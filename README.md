# 프린터 도우미

네트워크 복합기의 **자동급지대(ADF)에 종이를 여러 장 넣고 한 번에 스캔해 PDF 한 파일로 저장**하는 앱입니다. **Windows와 macOS**에서 씁니다.
제조사 스캐너 드라이버 없이, 복합기가 지원하는 표준 네트워크 스캔(eSCL, AirScan/Mopria 스캔)으로 바로 통신합니다.

> macOS 버전은 2.0에서 처음 나온 **시험판**입니다. 쓰다가 이상한 점이 있으면 [Issues](https://github.com/yangharry/printer-helper/issues)에 알려 주세요.

## 할 수 있는 것

- **급지대 연속 스캔**: 넣은 종이가 차례로 들어와 페이지로 쌓입니다. 유리판 1장 스캔도 됩니다.
- **양면 합치기**: 단면 급지대에서 앞면을 스캔한 뒤 종이를 뒷면이 위로 오게 다시 넣으면, 뒷면을 앞면 사이사이에 끼워 넣습니다.
- **페이지 정리**: 회전, 순서 바꾸기, 삭제, 크게 보기(더블클릭).
- **파일 합치기**: PDF와 사진(JPG, PNG, BMP, GIF, TIFF, WEBP)을 끌어다 놓으면 페이지로 들어옵니다. 사진의 회전 정보는 따르고, 위치(GPS)·기기 정보 같은 메타데이터는 PDF에 넣지 않습니다.
- **PDF 저장**: 스캔한 JPEG를 다시 압축하지 않고 그대로 PDF에 넣어 화질이 떨어지지 않습니다.
- **인쇄**: 모은 페이지를 실제 크기로 인쇄합니다(PC에서 하는 복사). 용지보다 큰 쪽만 줄이고, 가로로 긴 쪽은 돌려서 찍습니다.
- **프린터 상태**: 대기/작업 중, 토너 잔량, 용지 걸림·부족, 급지대에 종이가 있는지를 위쪽에 보여 줍니다.

## 필요한 것

- Windows 10 또는 11 (64비트), 또는 macOS
- eSCL을 지원하는 네트워크 스캐너/복합기 (AirPrint·Mopria 스캔을 지원하는 대부분의 최근 기종)
- 빌드할 때만: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Samsung Xpress M2085FW(급지대 40장, 단면, 스캔 최대 300dpi)에서 시험했습니다.

## 설치

[Releases](https://github.com/yangharry/printer-helper/releases)에서 가장 위에 있는 버전의 zip 중 내 컴퓨터에 맞는 것을 받으세요. (2.0.0은 시험판이라 "Latest"가 아니라 "Pre-release"로 표시됩니다.)

### Windows

`PrinterHelper-버전-win-x64.zip`의 압축을 풀고 `PrinterHelper.exe`를 실행하세요. .NET을 따로 설치할 필요는 없습니다.
처음 실행할 때 "Windows의 PC 보호" 창이 뜨면 **추가 정보 → 실행**을 누르세요. 서명하지 않은 개인 프로젝트라서 나오는 경고입니다.

소스에서 빌드해 설치하려면(바탕화면에 **프린터 도우미** 바로 가기도 만듭니다):

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

`%LOCALAPPDATA%\Programs\PrinterHelper`에 설치합니다. 관리자 권한은 필요 없고, 지우려면 `install.ps1 -Uninstall`을 실행하세요.

### macOS

Apple 실리콘(M1 이후) Mac은 `PrinterHelper-버전-mac-arm64.zip`, Intel Mac은 `-mac-x64.zip`을 받으세요. (메뉴 막대의 Apple 메뉴 → 이 Mac에 관하여에서 확인할 수 있습니다.)

1. zip을 풀고 나온 **PrinterHelper**(프린터 도우미)를 **응용 프로그램** 폴더로 옮깁니다.
2. 더블클릭합니다. "Apple에서 악성 코드가 없음을 확인할 수 없습니다"가 뜨면 **완료**를 누릅니다.
3. **시스템 설정 → 개인정보 보호 및 보안**을 열고 아래쪽의 **그래도 열기**를 누른 뒤, 한 번 더 **열기**를 누릅니다.
   Apple 개발자 서명을 받지 않은 개인 프로젝트라서 나오는 경고입니다. 터미널을 쓸 줄 알면 `xattr -dr com.apple.quarantine /Applications/PrinterHelper.app` 로 대신할 수 있습니다.
4. "로컬 네트워크의 기기를 찾고 연결하려고 합니다"가 뜨면 **허용**을 누릅니다. 허용하지 않으면 복합기를 찾지 못합니다.
   나중에 바꾸려면 **시스템 설정 → 개인정보 보호 및 보안 → 로컬 네트워크**에서 **프린터 도우미**를 켜세요.
5. 처음 PDF를 저장할 때 문서 폴더 접근을 물으면 **허용**을 누릅니다.

새 버전으로 바꿀 때는 예전 앱을 지우고 같은 순서를 다시 하면 됩니다.

## 사용법

앱은 같은 네트워크의 스캐너를 자동으로 찾습니다(Windows는 설치된 프린터 먼저). 찾지 못하면 위쪽 **프린터 변경**에서 IP 주소를 넣으면 됩니다.

1. 급지대에 종이를 **글자가 위로 오게** 넣고 **스캔**을 누릅니다.
2. 양면 문서라면 앞면 스캔이 끝나고 나온 종이 묶음을 **뒷면이 위로 오게** 급지대에 다시 넣고 **뒷면 스캔해서 양면 합치기**를 누릅니다.
   M2085FW처럼 종이가 뒤집혀 나오는 기종은 나온 묶음을 그대로 넣으면 됩니다. 뒷면 순서가 어긋나면 **양면 순서 바꾸기**를 누르세요.
3. **PDF로 저장**을 누릅니다. 기본 저장 위치는 `문서/Scans`입니다.

| Windows | macOS | 동작 |
|---|---|---|
| Ctrl+S | ⌘S | PDF로 저장 |
| Ctrl+P | ⌘P | 인쇄 |
| Ctrl+O | ⌘O | 파일 추가 |
| Delete | delete | 고른 페이지 삭제 |
| Esc | Esc | 스캔 멈추기 |

Windows에서는 바탕화면 바로 가기 위로 PDF나 사진을 끌어다 놓으면 그 파일을 연 채로 앱이 시작됩니다.

## 명령줄로 스캔하기 (Windows)

설치 없이 PowerShell 스크립트만으로도 급지대 스캔을 할 수 있습니다. `cli\scan.bat`을 더블클릭하면 급지대의 종이 전부를 회색조 200dpi로 스캔해 `문서\Scans\scan_날짜.pdf`로 저장합니다.

| 옵션 | 뜻 |
|---|---|
| `-Color` | 컬러로 스캔 |
| `-Dpi 100`, `200`, `300` | 해상도 |
| `-Source Platen` | 유리판에서 1장 |
| `-PrinterIp 192.168.0.10` | 스캐너 주소를 직접 지정 (기본은 Windows에 설치된 네트워크 프린터에서 찾음) |
| `-OutDir D:\스캔` | 저장 폴더 |

```bat
cli\scan.bat -Color -Dpi 300
```

## 동작 방식

- **스캔**: eSCL `POST /eSCL/ScanJobs`로 작업을 만들고 `NextDocument`를 반복해서 한 쪽씩 JPEG로 받습니다. 503(아직 스캔 중)이면 기다렸다 다시 묻고, 404가 오면 끝입니다. 중간에 멈추거나 창을 닫으면 작업을 `DELETE`로 취소해 급지대가 계속 종이를 먹지 않게 합니다.
- **스캐너 찾기**: mDNS(Bonjour)로 `_uscan._tcp`를 찾고, Windows에서는 설치된 프린터의 TCP/IP 포트도 확인합니다.
- **상태**: IPP `Get-Printer-Attributes`(포트 631)로 토너와 오류를 읽고, eSCL `ScannerStatus`로 급지대 상태를 읽습니다.
- **PDF**: JPEG를 `DCTDecode` 이미지로 그대로 넣는 작은 PDF 작성기를 직접 구현했습니다. 다른 PDF를 불러올 때는 PDFium으로 그리고, 그림은 SkiaSharp, TIFF는 LibTiff.NET으로 읽습니다.
- **인쇄**: Windows는 시스템 인쇄 창과 프린터 드라이버로, macOS는 페이지를 용지에 배치한 PDF를 만들어 CUPS(`lp`)로 보냅니다.
- **화면**: [Avalonia](https://avaloniaui.net/)로 Windows와 macOS에서 같은 화면을 씁니다.

## 개발

```powershell
dotnet build PrinterHelper.slnx -c Release
dotnet run --project tests\PrinterHelper.Tests -c Release
dotnet run --project tests\PrinterHelper.Tests -c Release -- --live 192.168.0.10   # 실제 프린터 상태 읽기(스캔은 하지 않음)
dotnet run --project src\PrinterHelper -c Release -- --smoke-test out\smoke        # 앱을 열어 네이티브 라이브러리를 확인하고 닫음
```

- `src/PrinterHelper.Core`: 스캔(eSCL), IPP, PDF 쓰기·읽기, 그림 가져오기, 스캐너 찾기, 인쇄. 화면과 무관해서 테스트로 확인합니다.
- `src/PrinterHelper`: Avalonia 화면.
- `tests/PrinterHelper.Tests`: 가짜 eSCL 서버로 스캔 흐름과 취소를 확인하고, 1.0.1이 같은 파일을 어떻게 불러왔는지 기록해 둔 값(`fixtures/golden-v1.json`)과 비교합니다.
- `packaging/macos`: Mac 앱 묶음을 만드는 스크립트. GitHub Actions가 Windows와 macOS에서 빌드·테스트하고, Mac에서 앱을 실제로 띄워 확인한 뒤 zip을 만듭니다.

## 라이선스

MIT. [LICENSE](LICENSE)를 보세요. 함께 쓰는 라이브러리의 라이선스는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)에 있습니다.

Samsung, Xpress는 각 소유자의 상표입니다. 이 프로젝트는 개인이 만든 것으로 삼성전자나 HP와 관계가 없습니다.
