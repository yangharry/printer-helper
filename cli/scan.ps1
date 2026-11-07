<#
.SYNOPSIS
    네트워크 복합기의 자동급지대(ADF)에 넣은 종이를 차례로 스캔해 PDF 한 파일로 저장합니다.

.DESCRIPTION
    스캐너 드라이버 없이 프린터의 네트워크 스캔 기능(eSCL)을 직접 씁니다.
    프린터에서 쪽마다 JPEG를 받아 여러 쪽짜리 PDF 하나로 묶습니다.
    Samsung Xpress M2085FW(급지대 40장, 단면)에서 시험했습니다.

.EXAMPLE
    .\scan.ps1
    급지대에 있는 종이 전부를 회색조 200dpi로 스캔합니다.

.EXAMPLE
    .\scan.ps1 -Color -Dpi 300
    컬러 300dpi로 스캔합니다.

.EXAMPLE
    .\scan.ps1 -Source Platen
    유리판에 올린 1장을 스캔합니다.
#>
[CmdletBinding()]
param(
    # Auto: 급지대에 종이가 있으면 급지대를 쓰고, 없으면 유리판으로 스캔할지 묻습니다.
    [ValidateSet('Auto', 'Feeder', 'Platen')]
    [string]$Source = 'Auto',

    # 컬러로 스캔합니다. 기본은 회색조입니다.
    [switch]$Color,

    [ValidateSet(100, 200, 300)]
    [int]$Dpi = 200,

    [ValidateSet('A4', 'Letter', 'Legal')]
    [string]$PaperSize = 'A4',

    # 비워 두면 Windows에 설치된 네트워크 프린터 중에서 스캐너를 찾습니다.
    [string]$PrinterIp,

    # 비워 두면 문서\Scans 폴더에 저장합니다.
    [string]$OutDir,

    # 저장한 PDF를 자동으로 열지 않습니다.
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

# 콘솔 창을 X로 닫으면 finally가 실행되지 않으므로, 그때도 프린터의 스캔 작업을 취소합니다.
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Runtime.InteropServices;
public static class EsclCloseGuard {
    public static volatile string JobUrl;
    delegate bool HandlerRoutine(int ctrlType);
    static readonly HandlerRoutine Handler = OnCtrl;
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(HandlerRoutine h, bool add);
    public static void Install() { SetConsoleCtrlHandler(Handler, true); }
    static bool OnCtrl(int ctrlType) {
        string url = JobUrl;
        // 2 = 창 닫기, 6 = 시스템 종료. Ctrl+C는 PowerShell의 finally가 처리합니다.
        if ((ctrlType == 2 || ctrlType == 6) && url != null) {
            try {
                var r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "DELETE"; r.Proxy = null; r.Timeout = 3000;
                r.GetResponse().Close();
            } catch { }
        }
        return false;
    }
}
'@
[EsclCloseGuard]::Install()

# ValidateSet은 대소문자를 가리지 않으므로 프린터에 보낼 값은 정해진 표기로 바꿉니다.
$Source = @{ auto = 'Auto'; feeder = 'Feeder'; platen = 'Platen' }[$Source]

# 저장 폴더는 스캔 전에 확인합니다. 종이가 다 지나간 뒤에 실패하면 스캔이 사라지기 때문입니다.
if (-not $OutDir) { $OutDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Scans' }
try {
    $OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
    New-Item -ItemType Directory -Force -Path $OutDir -ErrorAction Stop | Out-Null
} catch {
    Write-Host "저장 폴더를 만들 수 없습니다: $OutDir" -ForegroundColor Red
    Write-Host '경로 끝의 \ 를 빼고 다시 실행해 보세요.' -ForegroundColor Red
    exit 1
}

$Invariant = [Globalization.CultureInfo]::InvariantCulture

# 용지 크기(1/300인치 단위). 최대 스캔 크기는 프린터에서 읽어 오고, 못 읽으면 이 값을 씁니다.
$PaperSizes = @{
    A4     = @{ Width = 2480; Height = 3508 }
    Letter = @{ Width = 2550; Height = 3300 }
    Legal  = @{ Width = 2550; Height = 4200 }
}
$MaxWidth = @{ Feeder = 2550; Platen = 2550 }
$MaxHeight = @{ Feeder = 4200; Platen = 3507 }

$AdfStateMessages = @{
    ScannerAdfEmpty     = '급지대에 종이가 없습니다.'
    ScannerAdfJam       = '급지대에 종이가 걸렸습니다. 걸린 종이를 빼고 다시 넣어 주세요.'
    ScannerAdfDoorOpen  = '급지대 덮개가 열려 있습니다.'
    ScannerAdfHatchOpen = '급지대 덮개가 열려 있습니다.'
}

function Stop-WithError([string]$Message) {
    Write-Host $Message -ForegroundColor Red
    exit 1
}

# Windows에 설치된 네트워크 프린터 중에서 eSCL(네트워크 스캔)에 응답하는 첫 번째 주소를 찾습니다.
function Find-ScannerIp {
    try {
        $hosts = @(Get-PrinterPort -ErrorAction SilentlyContinue |
            Where-Object { $_.PrinterHostAddress } |
            ForEach-Object { $_.PrinterHostAddress } |
            Select-Object -Unique)
    } catch {
        $hosts = @()
    }
    $probe = New-Object System.Net.Http.HttpClient
    $probe.Timeout = [TimeSpan]::FromSeconds(3)
    # 꺼진 프린터를 기다리느라 늦어지지 않도록 모두 동시에 물어봅니다.
    $tasks = @{}
    foreach ($h in $hosts) { $tasks[$h] = $probe.GetAsync("http://$h/eSCL/ScannerCapabilities") }
    foreach ($h in $hosts) {
        try {
            if ($tasks[$h].GetAwaiter().GetResult().IsSuccessStatusCode) { return $h }
        } catch { }
    }
    return $null
}

# 프린터가 알려 주는 최대 스캔 크기를 읽습니다. 못 읽으면 기본값을 그대로 둡니다.
function Read-MaxScanSize {
    try {
        $response = Invoke-Escl GET "$Base/ScannerCapabilities"
        [xml]$caps = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        foreach ($pair in @(@('Platen', 'PlatenInputCaps'), @('Feeder', 'AdfSimplexInputCaps'))) {
            $w = $caps.SelectSingleNode("//*[local-name()='$($pair[1])']/*[local-name()='MaxWidth']")
            $h = $caps.SelectSingleNode("//*[local-name()='$($pair[1])']/*[local-name()='MaxHeight']")
            if ($w) { $MaxWidth[$pair[0]] = [int]$w.InnerText }
            if ($h) { $MaxHeight[$pair[0]] = [int]$h.InnerText }
        }
    } catch { }
}

$Http = New-Object System.Net.Http.HttpClient
$Http.Timeout = [TimeSpan]::FromSeconds(120)

function Invoke-Escl([string]$Method, [string]$Uri, [string]$Body) {
    $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList (New-Object System.Net.Http.HttpMethod $Method), $Uri
    if ($Body) {
        $request.Content = New-Object System.Net.Http.StringContent -ArgumentList $Body, ([Text.Encoding]::UTF8), 'text/xml'
    }
    return $Http.SendAsync($request).GetAwaiter().GetResult()
}

function Get-ScannerStatus {
    $response = Invoke-Escl GET "$Base/ScannerStatus"
    if (-not $response.IsSuccessStatusCode) { throw "스캐너 상태를 읽지 못했습니다 (HTTP $([int]$response.StatusCode))" }
    [xml]$xml = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return $xml.ScannerStatus
}

# JPEG 헤더(SOF)에서 가로·세로 픽셀 수와 색 채널 수를 읽습니다.
function Get-JpegInfo([byte[]]$Bytes) {
    if ($Bytes.Length -lt 4 -or $Bytes[0] -ne 0xFF -or $Bytes[1] -ne 0xD8) {
        throw '프린터가 JPEG가 아닌 데이터를 보냈습니다.'
    }
    $i = 2
    while ($i + 9 -lt $Bytes.Length) {
        if ($Bytes[$i] -ne 0xFF) { $i++; continue }
        $marker = [int]$Bytes[$i + 1]
        if ($marker -eq 0xFF) { $i++; continue }
        if ($marker -eq 0x01 -or ($marker -ge 0xD0 -and $marker -le 0xD8)) { $i += 2; continue }
        $length = [int]$Bytes[$i + 2] * 256 + [int]$Bytes[$i + 3]
        if ($marker -ge 0xC0 -and $marker -le 0xCF -and $marker -notin 0xC4, 0xC8, 0xCC) {
            return [pscustomobject]@{
                Height     = [int]$Bytes[$i + 5] * 256 + [int]$Bytes[$i + 6]
                Width      = [int]$Bytes[$i + 7] * 256 + [int]$Bytes[$i + 8]
                Components = [int]$Bytes[$i + 9]
            }
        }
        $i += 2 + $length
    }
    throw 'JPEG에서 이미지 크기를 찾지 못했습니다.'
}

# JPEG 쪽들을 그대로 넣어 여러 쪽짜리 PDF를 만듭니다(다시 압축하지 않음).
function Write-ImagePdf([string]$Path, $Pages, [int]$Dpi) {
    $ascii = [Text.Encoding]::ASCII
    $stream = [IO.File]::Create($Path)
    try {
        function Out-Text([string]$Text) {
            $b = $ascii.GetBytes($Text)
            $stream.Write($b, 0, $b.Length)
        }

        $objectCount = 2 + 3 * $Pages.Count
        $offsets = New-Object 'long[]' ($objectCount + 1)

        Out-Text "%PDF-1.4`n"
        $stream.Write([byte[]](0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A), 0, 6)

        $kids = (0..($Pages.Count - 1) | ForEach-Object { "$(3 + 3 * $_) 0 R" }) -join ' '
        $offsets[1] = $stream.Position
        Out-Text "1 0 obj`n<< /Type /Catalog /Pages 2 0 R >>`nendobj`n"
        $offsets[2] = $stream.Position
        Out-Text "2 0 obj`n<< /Type /Pages /Kids [$kids] /Count $($Pages.Count) >>`nendobj`n"

        for ($p = 0; $p -lt $Pages.Count; $p++) {
            $page = $Pages[$p]
            $pageObj = 3 + 3 * $p
            $contentObj = $pageObj + 1
            $imageObj = $pageObj + 2
            $w = ($page.Width * 72.0 / $Dpi).ToString('0.###', $Invariant)
            $h = ($page.Height * 72.0 / $Dpi).ToString('0.###', $Invariant)
            $colorSpace = switch ($page.Components) {
                1 { '/DeviceGray' }
                3 { '/DeviceRGB' }
                default { throw "지원하지 않는 JPEG 색 형식입니다 (채널 $($page.Components)개)." }
            }

            $offsets[$pageObj] = $stream.Position
            Out-Text "$pageObj 0 obj`n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 $w $h] /Resources << /XObject << /Im0 $imageObj 0 R >> >> /Contents $contentObj 0 R >>`nendobj`n"

            $content = "q $w 0 0 $h 0 0 cm /Im0 Do Q"
            $offsets[$contentObj] = $stream.Position
            Out-Text "$contentObj 0 obj`n<< /Length $($content.Length) >>`nstream`n$content`nendstream`nendobj`n"

            $offsets[$imageObj] = $stream.Position
            Out-Text "$imageObj 0 obj`n<< /Type /XObject /Subtype /Image /Width $($page.Width) /Height $($page.Height) /ColorSpace $colorSpace /BitsPerComponent 8 /Filter /DCTDecode /Length $($page.Bytes.Length) >>`nstream`n"
            $stream.Write($page.Bytes, 0, $page.Bytes.Length)
            Out-Text "`nendstream`nendobj`n"
        }

        $xref = $stream.Position
        Out-Text "xref`n0 $($objectCount + 1)`n0000000000 65535 f `n"
        for ($n = 1; $n -le $objectCount; $n++) {
            Out-Text ("{0:D10} 00000 n `n" -f $offsets[$n])
        }
        Out-Text "trailer`n<< /Size $($objectCount + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n"
    } finally {
        $stream.Dispose()
    }
}

# --- 프린터 확인 ---

$ip = if ($PrinterIp) { $PrinterIp } else { Find-ScannerIp }
if (-not $ip) {
    Stop-WithError "네트워크 스캐너를 찾지 못했습니다. 프린터가 켜져 있는지 확인하거나, -PrinterIp 로 주소를 지정하세요.`n(예: scan.bat -PrinterIp 192.168.0.10)"
}
$Base = "http://$ip/eSCL"
Write-Host "프린터: $ip"

try {
    $status = Get-ScannerStatus
} catch {
    Stop-WithError "프린터($ip)에 연결하지 못했습니다. 프린터 전원과 네트워크를 확인하세요.`n($($_.Exception.GetBaseException().Message))"
}

# 복사 같은 다른 작업이 끝날 때까지 최대 1분 기다립니다.
$waited = 0
while ($status.State -ne 'Idle') {
    if ($waited -ge 60) { Stop-WithError "프린터가 다른 작업 중입니다 (상태: $($status.State)). 잠시 후 다시 실행하세요." }
    if ($waited -eq 0) { Write-Host '프린터가 다른 작업 중이라 끝날 때까지 기다립니다...' }
    Start-Sleep -Seconds 2
    $waited += 2
    $status = Get-ScannerStatus
}

$adfState = [string]$status.AdfState
if ($Source -eq 'Auto') {
    if ($adfState -eq 'ScannerAdfLoaded') {
        $Source = 'Feeder'
    } elseif ($adfState -ne 'ScannerAdfEmpty' -and $AdfStateMessages[$adfState]) {
        # 종이 걸림이나 덮개 열림은 "종이 없음"과 다르게 알려 줍니다.
        Stop-WithError $AdfStateMessages[$adfState]
    } else {
        $answer = Read-Host '급지대에 종이가 없습니다. 유리판에 올린 1장을 스캔할까요? (Y/N)'
        # [string]: 입력이 끊기면(EOF) Read-Host가 $null을 돌려주는데, 그때도 "아니요"로 봅니다.
        if ([string]$answer -notmatch '^\s*(y|yes|ㅛ)\s*$') { exit 0 }
        $Source = 'Platen'
    }
}
if ($Source -eq 'Feeder' -and $adfState -ne 'ScannerAdfLoaded') {
    $message = $AdfStateMessages[$adfState]
    if (-not $message) { $message = "급지대를 쓸 수 없는 상태입니다 ($adfState)." }
    Stop-WithError $message
}

# --- 스캔 작업 시작 ---

$size = $PaperSizes[$PaperSize]
Read-MaxScanSize
$width = [Math]::Min($size.Width, $MaxWidth[$Source])
$height = [Math]::Min($size.Height, $MaxHeight[$Source])
$colorMode = if ($Color) { 'RGB24' } else { 'Grayscale8' }

$settings = @"
<?xml version="1.0" encoding="UTF-8"?>
<scan:ScanSettings xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03" xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
  <pwg:Version>2.63</pwg:Version>
  <scan:Intent>Document</scan:Intent>
  <pwg:ScanRegions>
    <pwg:ScanRegion>
      <pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>
      <pwg:XOffset>0</pwg:XOffset>
      <pwg:YOffset>0</pwg:YOffset>
      <pwg:Width>$width</pwg:Width>
      <pwg:Height>$height</pwg:Height>
    </pwg:ScanRegion>
  </pwg:ScanRegions>
  <pwg:InputSource>$Source</pwg:InputSource>
  <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
  <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>
  <scan:ColorMode>$colorMode</scan:ColorMode>
  <scan:XResolution>$Dpi</scan:XResolution>
  <scan:YResolution>$Dpi</scan:YResolution>
</scan:ScanSettings>
"@

$sourceName = if ($Source -eq 'Feeder') { '급지대' } else { '유리판' }
$modeName = if ($Color) { '컬러' } else { '회색조' }
Write-Host "스캔 시작: $sourceName, $modeName, ${Dpi}dpi, $PaperSize"

$response = Invoke-Escl POST "$Base/ScanJobs" $settings
if ([int]$response.StatusCode -ne 201 -or -not $response.Headers.Location) {
    $detail = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Stop-WithError "스캔 작업을 시작하지 못했습니다 (HTTP $([int]$response.StatusCode)).`n$detail"
}

# Location이 프린터 호스트명(.local)일 수 있어 경로만 쓰고 주소는 IP로 고정합니다.
$location = $response.Headers.Location
$jobPath = if ($location.IsAbsoluteUri) { $location.AbsolutePath } else { $location.OriginalString }
$jobUrl = "http://$ip$jobPath"
[EsclCloseGuard]::JobUrl = $jobUrl

# --- 쪽마다 받기 ---

$pages = New-Object System.Collections.Generic.List[object]
$jobDone = $false
$failure = $null
try {
    :nextPage while ($true) {
        # 503은 "아직 스캔 중"이라는 뜻이라 잠시 후 다시 묻습니다.
        $giveUpAt = (Get-Date).AddSeconds(90)
        while ($true) {
            $response = Invoke-Escl GET "$jobUrl/NextDocument"
            $code = [int]$response.StatusCode
            if ($code -ne 503) { break }
            if ((Get-Date) -gt $giveUpAt) { throw '프린터가 90초 동안 다음 쪽을 보내지 않았습니다.' }
            Start-Sleep -Milliseconds 500
        }
        if ($code -eq 404) { $jobDone = $true; break nextPage }
        if ($code -ne 200) { throw "프린터가 오류를 보냈습니다 (HTTP $code)." }

        $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $info = Get-JpegInfo $bytes
        $pages.Add([pscustomobject]@{
            Bytes      = $bytes
            Width      = $info.Width
            Height     = $info.Height
            Components = $info.Components
        })
        Write-Host ("  {0}쪽 스캔됨" -f $pages.Count)
    }
} catch {
    $failure = $_.Exception.GetBaseException().Message
} finally {
    # 중간에 멈췄거나 Ctrl+C로 끊었으면 프린터의 작업도 취소합니다.
    if (-not $jobDone) {
        try { Invoke-Escl DELETE $jobUrl | Out-Null } catch { }
    }
    [EsclCloseGuard]::JobUrl = $null
}

if ($pages.Count -eq 0) {
    $reason = $failure
    if (-not $reason) {
        $reason = '프린터가 아무 쪽도 보내지 않았습니다.'
        # 급지대 상태는 급지대 스캔일 때만 원인이 됩니다. 유리판 스캔 뒤에 붙이면 엉뚱한 안내가 됩니다.
        if ($Source -eq 'Feeder') {
            try {
                $adfState = [string](Get-ScannerStatus).AdfState
                if ($AdfStateMessages[$adfState]) { $reason += ' ' + $AdfStateMessages[$adfState] }
            } catch { }
        }
    }
    Stop-WithError "스캔하지 못했습니다. $reason"
}
if ($failure) {
    Write-Host "스캔이 중간에 멈췄습니다: $failure" -ForegroundColor Yellow
    Write-Host "지금까지 스캔한 $($pages.Count)쪽만 저장합니다." -ForegroundColor Yellow
}

# --- PDF로 저장 ---

$file = Join-Path $OutDir ('scan_{0:yyyyMMdd_HHmmss}.pdf' -f (Get-Date))
Write-ImagePdf $file $pages $Dpi
Write-Host "저장했습니다: $file ($($pages.Count)쪽)" -ForegroundColor Green

if (-not $NoOpen) { Invoke-Item $file }
if ($failure) { exit 1 }
