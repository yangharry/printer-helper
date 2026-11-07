# 프린터 도우미

네트워크 복합기의 **자동급지대(ADF)에 종이를 여러 장 넣고 한 번에 스캔해 PDF 한 파일로 저장**하는 Windows 앱입니다.
제조사 스캐너 드라이버 없이, 복합기가 지원하는 표준 네트워크 스캔(eSCL, AirScan/Mopria 스캔)으로 바로 통신합니다.

## 할 수 있는 것

- **급지대 연속 스캔**: 넣은 종이가 차례로 들어와 페이지로 쌓입니다. 유리판 1장 스캔도 됩니다.
- **양면 합치기**: 단면 급지대에서 앞면을 스캔한 뒤 종이를 뒷면이 위로 오게 다시 넣으면, 뒷면을 앞면 사이사이에 끼워 넣습니다.
- **페이지 정리**: 회전, 순서 바꾸기, 삭제, 크게 보기(더블클릭).
- **파일 합치기**: PDF와 사진(JPG, PNG, BMP, GIF, TIFF)을 끌어다 놓으면 페이지로 들어옵니다. 사진의 회전 정보는 따르고, 위치(GPS)·기기 정보 같은 메타데이터는 PDF에 넣지 않습니다.
- **PDF 저장**: 스캔한 JPEG를 다시 압축하지 않고 그대로 PDF에 넣어 화질이 떨어지지 않습니다.
- **인쇄**: 모은 페이지를 실제 크기로 인쇄합니다(PC에서 하는 복사). Windows에 설치된 프린터 드라이버를 씁니다.
- **프린터 상태**: 대기/작업 중, 토너 잔량, 용지 걸림·부족, 급지대에 종이가 있는지를 위쪽에 보여 줍니다.

## 필요한 것

- Windows 10 또는 11 (.NET Framework 4.8은 Windows에 기본으로 들어 있습니다)
- eSCL을 지원하는 네트워크 스캐너/복합기 (AirPrint·Mopria 스캔을 지원하는 대부분의 최근 기종)
- 빌드할 때만: [.NET SDK](https://dotnet.microsoft.com/download) 8 이상

Samsung Xpress M2085FW(급지대 40장, 단면)에서 시험했습니다.

## 설치

**내려받기**: [Releases](https://github.com/yangharry/printer-helper/releases/latest)에서 `PrinterHelper-버전-win.zip`을 받아 압축을 풀고 `PrinterHelper.exe`를 실행하세요. 빌드나 설치는 필요 없습니다.
처음 실행할 때 "Windows의 PC 보호" 창이 뜨면 **추가 정보 → 실행**을 누르세요. 서명하지 않은 개인 프로젝트라서 나오는 경고입니다.

**소스에서 빌드해 설치**:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

`%LOCALAPPDATA%\Programs\PrinterHelper`에 설치하고 바탕화면에 **프린터 도우미** 바로 가기를 만듭니다. 관리자 권한은 필요 없습니다.
지우려면 `install.ps1 -Uninstall`을 실행하세요.

앱은 Windows에 이미 설치된 네트워크 프린터 중에서 스캐너를 자동으로 찾습니다. 찾지 못하면 위쪽 **프린터 변경**에서 IP 주소를 넣으면 됩니다.

## 사용법

1. 급지대에 종이를 **글자가 위로 오게** 넣고 **스캔**을 누릅니다.
2. 양면 문서라면 앞면 스캔이 끝나고 나온 종이 묶음을 **뒷면이 위로 오게** 급지대에 다시 넣고 **뒷면 스캔해서 양면 합치기**를 누릅니다.
   M2085FW처럼 종이가 뒤집혀 나오는 기종은 나온 묶음을 그대로 넣으면 됩니다. 뒷면 순서가 어긋나면 **양면 순서 바꾸기**를 누르세요.
3. **PDF로 저장**을 누릅니다. 기본 저장 위치는 `문서\Scans`입니다.

| 단축키 | 동작 |
|---|---|
| Ctrl+S | PDF로 저장 |
| Ctrl+P | 인쇄 |
| Ctrl+O | 파일 추가 |
| Delete | 고른 페이지 삭제 |
| Esc | 스캔 멈추기 |

바탕화면 바로 가기 위로 PDF나 사진을 끌어다 놓으면 그 파일을 연 채로 앱이 시작됩니다.

## 명령줄로 스캔하기

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
- **상태**: IPP `Get-Printer-Attributes`(포트 631)로 토너와 오류를 읽고, eSCL `ScannerStatus`로 급지대 상태를 읽습니다.
- **PDF**: JPEG를 `DCTDecode` 이미지로 그대로 넣는 작은 PDF 작성기를 직접 구현했습니다. 다른 PDF를 불러올 때는 Windows에 들어 있는 `Windows.Data.Pdf`로 그립니다.

## 개발

```powershell
dotnet build src\PrinterHelper -c Release
dotnet run --project tests\PrinterHelper.Tests -c Release
dotnet run --project tests\PrinterHelper.Tests -c Release -- --live 192.168.0.10   # 실제 프린터 상태 읽기(스캔은 하지 않음)
```

테스트는 가짜 eSCL 서버로 스캔 흐름과 취소를 확인하고, 만든 PDF의 구조(xref 오프셋, 스트림 길이)와 인쇄 배치를 검사합니다.

## 라이선스

MIT. [LICENSE](LICENSE)를 보세요.

Samsung, Xpress는 각 소유자의 상표입니다. 이 프로젝트는 개인이 만든 것으로 삼성전자나 HP와 관계가 없습니다.
