<#
.SYNOPSIS
    프린터 도우미를 빌드해서 설치하고 바탕화면에 바로 가기를 만듭니다.

.DESCRIPTION
    .NET SDK로 앱을 빌드해 %LOCALAPPDATA%\Programs\PrinterHelper 에 복사하고,
    바탕화면에 "프린터 도우미" 바로 가기를 만듭니다. 관리자 권한은 필요 없습니다.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
    프로그램과 바로 가기를 지웁니다. 스캔한 PDF는 지우지 않습니다.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\PrinterHelper'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '프린터 도우미.lnk'
$exe = Join-Path $InstallDir 'PrinterHelper.exe'

# The install folder is wiped on reinstall and uninstall, so never accept a folder that holds anything else.
$folderInUse = (Test-Path -LiteralPath $InstallDir) -and
    -not (Test-Path -LiteralPath $exe) -and
    (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1)
if ($folderInUse) {
    Write-Host "$InstallDir 에 다른 파일이 있어서 건드리지 않습니다. 비어 있거나 없는 폴더를 지정하세요." -ForegroundColor Red
    exit 1
}

if (Get-Process PrinterHelper -ErrorAction SilentlyContinue) {
    Write-Host '프린터 도우미가 실행 중입니다. 창을 닫고 다시 실행하세요.' -ForegroundColor Red
    exit 1
}

if ($Uninstall) {
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse }
    Write-Host '프린터 도우미를 지웠습니다. (스캔한 PDF와 설정은 남아 있습니다)' -ForegroundColor Green
    exit 0
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '.NET SDK가 필요합니다: https://dotnet.microsoft.com/download' -ForegroundColor Red
    exit 1
}

$publish = Join-Path $root 'artifacts\publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse }
Write-Host '빌드하는 중...'
& dotnet publish (Join-Path $root 'src\PrinterHelper\PrinterHelper.csproj') -c Release -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host '빌드에 실패했습니다.' -ForegroundColor Red
    exit 1
}

if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $publish '*') $InstallDir -Recurse

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $InstallDir
$link.IconLocation = "$exe,0"
$link.Description = '자동급지대 스캔, PDF 저장, 인쇄'
$link.Save()

Write-Host "설치했습니다: $InstallDir" -ForegroundColor Green
Write-Host "바탕화면 바로 가기: $shortcut" -ForegroundColor Green
