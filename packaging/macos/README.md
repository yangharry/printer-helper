# Mac 앱 만들기

`PrinterHelper.app`(한국어 Mac의 Finder·Dock에서는 **프린터 도우미**로 보임)은 GitHub Actions(`.github/workflows/build.yml`)가 Apple 실리콘용(`osx-arm64`)과 Intel용(`osx-x64`)으로 따로 만들어 zip으로 올립니다. Mac 판은 아직 시험판입니다.

## 파일

| 파일 | 하는 일 |
|---|---|
| `Info.plist` | 번들 정보. 기본 이름(`CFBundleName`, `CFBundleDisplayName`)은 번들 파일 이름과 같은 `PrinterHelper`입니다. 버전(`CFBundleShortVersionString`, `CFBundleVersion`)은 `make-app.sh`가 채웁니다. |
| `ko.lproj/InfoPlist.strings` | 한국어 Mac의 Finder·Dock·메뉴 막대에 보이는 이름 "프린터 도우미"(UTF-8). macOS는 `Info.plist`의 기본 `CFBundleDisplayName`이 번들 파일 이름(`PrinterHelper.app`의 `PrinterHelper`)과 같을 때만 이 이름을 보여 주므로, 기본 이름을 한국어로 바꾸면 안 됩니다. |
| `icon-1024.png` | 아이콘 원본. Windows의 `app.ico`와 같은 그림을 Apple 격자(1024 캔버스 가운데 824 px 둥근 사각형)에 맞춘 것으로, Windows에서 `tools\make-icon.ps1`이 함께 그립니다. |
| `make-icns.sh` | `icon-1024.png` → `PrinterHelper.icns` (sips, iconutil) |
| `make-app.sh` | publish 결과로 번들을 만들고 ad-hoc 서명 |
| `../copy-notices.py` | 함께 들어가는 구성 요소의 라이선스·고지 파일을 `licenses` 폴더로 모음(Windows zip에도 씀) |
| `../licenses/pdfium/` | PDFium과 그 안에 빌드된 코드의 라이선스 원문. PDFium 패키지에는 라이선스 파일이 없어 저장소에 둡니다. |

## Mac에서 직접 만들기

.NET 10 SDK와 Xcode 명령줄 도구(`xcode-select --install`)가 필요합니다.

```sh
version=$(dotnet msbuild src/PrinterHelper/PrinterHelper.csproj -getProperty:Version)
dotnet publish src/PrinterHelper/PrinterHelper.csproj -c Release -r osx-arm64 --self-contained \
  -p:PublishReadyToRun=true -p:DebugType=none -o out/pub      # Intel Mac용은 -r osx-x64
packaging/macos/make-app.sh out/pub out "$version"
open out/PrinterHelper.app
```

`make-app.sh`가 하는 일:

1. `Contents/MacOS`에 publish 결과 전체, `Contents/Resources`에 아이콘, `ko.lproj`, `LICENSE`, `THIRD-PARTY-NOTICES.md`, `licenses` 폴더를 넣고 `Info.plist`에 버전을 적습니다(`plutil -lint`로 확인). `licenses`에는 `copy-notices.py`가 PDFium 라이선스 원문과, restore한 NuGet 패키지(SkiaSharp, HarfBuzzSharp, .NET 런타임 팩 등)에 든 라이선스·고지 파일을 그대로 복사합니다. 그래서 같은 체크아웃에서 publish한 뒤에 실행해야 합니다.
2. 실행 파일 `PrinterHelper`의 Mach-O UUID(LC_UUID)가 .NET의 apphost 원본과 같으면 새 값으로 바꿉니다. 같은 버전의 .NET 앱은 모두 같은 UUID를 갖는데, macOS는 로컬 네트워크 권한을 이 UUID로 구분하므로 다른 .NET 앱과 권한이 섞일 수 있습니다. 새 UUID는 번들 ID·버전·아키텍처로 정해지므로 같은 빌드는 늘 같은 값입니다.
3. 안쪽부터 ad-hoc 서명(`codesign --sign -`)을 하고 `codesign --verify --deep --strict`로 확인합니다. `Contents/MacOS`의 파일은 `.dll`, `.json`까지 모두 서명합니다. codesign은 그 폴더의 모든 파일을 코드로 보기 때문입니다(Mach-O가 아닌 파일의 서명은 확장 속성에 들어갑니다). Hardened Runtime은 켜지 않습니다(.NET의 JIT에 따로 권한이 필요해집니다).

zip은 `ditto -c -k --sequesterRsrc --keepParent`로 만듭니다. 확장 속성이 zip에 함께 들어가고, Finder로 풀면 되살아납니다.

최소 macOS는 14(Sonoma)입니다. .NET 10은 macOS 14를 지원하며 나왔고(지금 목록은 15, 26, 27), 함께 들어가는 PDFium은 macOS 13 이상용으로 빌드되어 있습니다. CI는 macOS 15와 26에서 실제로 실행해 봅니다.

## 처음 실행하기 (사용자 안내)

1. [Releases](https://github.com/yangharry/printer-helper/releases)에서 가장 위에 있는 버전(2.0.0은 "Pre-release")의 zip 중 Mac에 맞는 것을 받습니다. Apple 메뉴 > **이 Mac에 관하여**에서 칩이 Apple M으로 시작하면 `mac-arm64`, Intel이면 `mac-x64`입니다.
2. 받은 zip을 **Finder에서 더블클릭해** 풉니다. 다른 압축 프로그램으로 풀면 서명 정보가 빠져 "손상되었기 때문에 열 수 없습니다"가 뜰 수 있습니다.
3. 나온 **프린터 도우미**(파일 이름은 `PrinterHelper.app`)를 응용 프로그램 폴더로 옮기고 더블클릭합니다.
4. "Apple은 '프린터 도우미'에 악성 코드가 없음을 확인할 수 없습니다" 창이 뜨면 **완료**를 누릅니다. Apple 개발자 인증서로 서명·공증하지 않은 개인 프로젝트라서 나오는 경고입니다.
5. **시스템 설정 > 개인정보 보호 및 보안**을 열고 아래쪽 보안 항목의 '프린터 도우미' 옆 **그래도 열기**를 누른 뒤 암호(또는 Touch ID)를 넣고, 다시 뜨는 창에서 **열기**를 누릅니다. 한 번만 하면 됩니다.

   터미널이 편하면 4–5 대신 이렇게 해도 됩니다(Finder에 보이는 이름과 달리 실제 폴더 이름은 `PrinterHelper.app`입니다).

   ```sh
   xattr -dr com.apple.quarantine /Applications/PrinterHelper.app
   ```

6. "'프린터 도우미'이(가) 로컬 네트워크에서 기기를 찾고 연결하려고 합니다"가 뜨면 **허용**을 누릅니다. 이걸 거부하면 복합기를 찾지도, 스캔하지도 못합니다. 이미 거부했다면 **시스템 설정 > 개인정보 보호 및 보안 > 로컬 네트워크**에서 프린터 도우미를 켜세요.
7. PDF를 처음 저장할 때 '문서' 폴더 접근을 물으면 **허용**을 누릅니다.
