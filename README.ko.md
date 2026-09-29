<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <b>한국어</b> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="OpenTypeless 아이콘">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>긴 받아쓰기에도 무너지지 않는 macOS·Windows 네이티브 음성 입력.</b><br>
  키를 누른 채 원하는 만큼 말하면, 커서 위치에 깔끔하게 정리된 텍스트가 들어갑니다.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="말하는 동안 표시되는 녹음 캡슐">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="메뉴 막대 패널: 단축키 안내와 최근 받아쓰기">
</p>

<p align="center">
  두 개의 네이티브 앱, 하나의 설계: <b>macOS</b>(Swift / SwiftUI)와 <b>Windows</b>(C# / WinUI 3).<br>
  처리 과정, 다듬기 프롬프트, 설정, 실패 처리는 모두 같고, 시스템 연동과 모양만 다릅니다.
</p>

---

## 왜 만들었나

음성 입력은 AI 도구에 긴 프롬프트를 쓰는 가장 빠른 방법입니다. 그만큼 받아쓰기도 길어집니다. 생각하면서 1~3분 동안 말하는 건 흔한 일입니다.

오픈 소스든 상용이든 대부분의 음성 입력 앱은 한 문장은 잘 처리하지만, **바로 이런 긴 받아쓰기에서 실패합니다**. 40초, 1분, 2분을 녹음하면 시간 초과, 빈 결과, 또는 텍스트 손실로 끝납니다. 여러 오픈 소스 대안의 소스 코드를 읽어 보니 원인은 늘 같았습니다.

- **녹음 전체를 한 번의 음성 인식 요청으로 보냅니다.** 상위 제공업체는 처리 시간이 약 60초를 넘으면 시간 초과가 납니다(OpenRouter는 문서에 명시하고 있습니다). 오래 말할수록 요청이 실패할 가능성이 커집니다.
- **다듬기 LLM 호출의 *전체* 시간 제한이 짧습니다.** 스트리밍까지 포함해 30초로 제한하면 긴 답변이 중간에 잘립니다.
- **한 번 실패하면 전부 잃습니다.** 2분 동안 말한 내용이 사라지고, 처음부터 다시 말해야 합니다.

OpenTypeless는 바로 이 문제를 중심으로 만들어졌습니다.

## 긴 받아쓰기를 처리하는 방식

| | 동작 |
|---|---|
| **쉬는 지점에서 분할** | 말하는 동안 오디오는 각 구간에서 가장 조용한 0.4초 지점에서 18~28초 길이의 구간으로 잘립니다. 단어가 중간에 잘리지 않고, 어떤 요청도 상위의 60초 제한에 가까워지지 않습니다. |
| **말하는 동안 변환** | 각 구간은 잘리는 즉시 백그라운드에서 변환됩니다. 2분짜리 받아쓰기도 키를 뗄 때는 마지막 몇 초만 처리하면 됩니다. |
| **구간별 재시도** | 네트워크 끊김, 429, 5xx 오류는 백오프 후 재시도합니다. 잘못된 키나 결제 오류는 즉시 실패로 알립니다. 한 구간의 실패가 다른 구간에 영향을 주지 않으며, 실패한 구간은 마지막에 한 번 더 전체 재시도됩니다. |
| **느리게 시작할 때 백업 모델** | 다듬기 모델이 0.55초 안에 응답을 시작하지 않거나 실패하면, 다른 회사의 백업 모델에도 요청하고 먼저 응답한 쪽을 사용합니다. 상위 제공업체가 느려도 추가되는 시간은 1초 미만이며, 대기 시간 전체가 늘어나지 않습니다. |
| **전체 시간 제한이 아닌 유휴 시간 제한** | 다듬기 단계는 응답을 스트리밍으로 받으며, 25초 동안 데이터가 *전혀* 오지 않을 때만 멈춘 것으로 판단합니다. 그래서 긴 출력이 잘리지 않습니다. |
| **한 단어도 잃지 않음** | 오디오는 말하는 동안 디스크에 기록됩니다. 모든 받아쓰기는 기록에 남고, 실패한 것은 나중에 다시 시도할 수 있으며 실패한 구간만 다시 보냅니다. 다듬기에 실패하면 원본 변환 결과를 대신 삽입합니다. |

예를 들어 121초짜리 합성 받아쓰기는 자연스러운 쉬는 지점에서 6개 구간으로 나뉩니다. 말을 멈췄을 때 그중 5개는 이미 변환이 끝나 있습니다.

## 직접 타이핑한 것처럼 다듬기

음성 인식 결과를 그대로 쓰면 지저분합니다. 군더더기 말, 다시 말하기, "아니, 그러니까…", 생각하면서 하는 혼잣말. 다듬기 단계는 이것을 여러분이 원래 타이핑했을 문장으로 바꿉니다.

- **말 고침은 마지막 내용을 따릅니다.** "수요일, 아니 목요일" → 목요일. 한참 뒤에 고친 경우, 암묵적인 수정("5만, 음, 넉넉하게 6만"), 통째로 취소한 내용("…세 번째는, 그냥 빼 줘")도 처리합니다.
- **군더더기 말과 혼잣말을 없앱니다.** um / uh / 嗯 / 那个 / "잠깐 생각해 보면" / "대충 그 정도" 모두 사라집니다.
- **실제 내용은 잃지 않습니다.** 숫자, 버전, 이름, 비교는 그대로 유지합니다. 모델에게 자신이 모르는 최신 제품도 실제로 존재한다고 알려 주므로, "Gemini 3.5"가 "Gemini 2.5"로 바뀌지 않습니다.
- **필요할 때만 구조화합니다.** 나란한 항목이 세 개 이상이면 번호 목록으로, 나머지는 일반 문단으로 둡니다.
- **질문에 답하지 않습니다.** 받아쓴 프롬프트("왜 …인지 설명해 줄래")는 다듬기만 하고, 답하거나 실행하지 않습니다.
- **여러분의 말, 여러분의 언어.** 수정은 최소한으로 하며 표현, 순서, 말투는 그대로 둡니다. 중국어와 영어를 섞어 말하면 일상 단어("shortcut", "dark mode")까지 모든 단어가 말한 언어 그대로 남고, CJK 문자와 라틴 문자 사이에는 공백이 들어갑니다.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="기록: 날짜별로 묶이고 검색 가능하며, 다듬은 텍스트와 소요 시간, 비용 표시">
</p>

프롬프트는 개발용 세트와 홀드아웃 테스트 세트([eval/](eval/) 참고)로 조정했으며, 실제 받아쓰기도 포함되어 있습니다.

## 기능

- **전역 단축키.** 기본값은 **Fn**(macOS) 또는 **오른쪽 Ctrl**(Windows)을 누른 채 유지하는 것이며, 보조 키 하나(오른쪽 ⌘, 오른쪽 ⌥, 오른쪽 Alt…)나 조합(⌥ Space, Alt + Space, F5…)도 기록할 수 있습니다.
- **눌러서 말하기 또는 핸즈프리.** 누른 채로 말합니다. 한 번 탭하면 핸즈프리로 계속 녹음하고, 다시 탭하면 끝납니다. **Esc**로 취소합니다. 10초 이상 말한 뒤 취소하면 사라지지 않고, 텍스트로 변환되어(입력되지는 않음) 기록에 24시간 보관됩니다.
- **마이크 선택**: **설정 → 일반**에서 고르고, 실시간 레벨 미터로 목소리가 들어오는지 확인합니다. 가상 장치(회의·방송 앱)는 따로 표시되며, 선택한 장치가 연결 해제되면 시스템 기본값으로 돌아갑니다.
- **마이크 대기 유지**(선택): 키를 누르는 즉시 녹음이 시작되고 누르기 직전의 소리도 포함되어 첫 단어가 잘리지 않습니다. 마이크가 계속 켜져 있고 블루투스 헤드폰은 통화 모드로 전환됩니다.
- **실시간 미리보기(베타)**: 말하는 동안 녹음 캡슐 위에 인식된 단어가 표시되며, 기기에서 인식합니다(macOS는 SpeechAnalyzer, Windows는 음성 인식). 입력되는 텍스트는 여전히 제공업체에서 옵니다.
- **커서 위치에 붙여넣기.** 텍스트 입력란에서는 붙여넣은 뒤 클립보드를 원래대로 돌려놓고, 입력란에 포커스가 없으면 클립보드에 넣습니다. 브라우저와 Electron 앱, Windows의 터미널에서도 동작합니다.
- **원하는 제공업체 사용.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek 또는 OpenAI 호환 엔드포인트. 음성 인식, 다듬기, 백업 다듬기 모델에 각각 다른 제공업체를 쓸 수 있습니다. **테스트**는 키를 확인하고 제공업체의 왕복 지연 시간(세 번의 중앙값)을 보여 줍니다.
- **백업 음성 인식**(선택): 어떤 구간이 그 길이에 비해 평소보다 훨씬 오래 걸리거나 실패하면 두 번째 제공업체에도 요청하고 먼저 온 답을 사용합니다.
- **사용자 어휘와 스타일 선호**로 이름, 제품, 전문 용어를 지정할 수 있습니다.
- **여러분의 수정에서 학습합니다.** 붙여넣은 뒤 잘못 인식된 단어를 고치면(TypeList → Typeless) 어떻게 잘못 들었는지와 함께 자동으로 어휘에 추가됩니다. 발음이 비슷한 수정만 학습하며, 고쳐 쓰기, 숫자 변경, 일반 단어 교체는 학습하지 않습니다. 삭제한 단어는 다시 학습하지 않습니다.
- **홈 화면**에서는 음성 입력으로 얻은 것을 보여 줍니다. 받아쓴 단어 수, 타이핑 대비 절약한 시간(기본 분당 100단어, 조정 가능), 말하기 속도, 오늘·이번 달·전체 비용, 연속 일수가 표시되는 GitHub 스타일 활동 히트맵. 앱을 직접 열 때 표시되며, 로그인 시에는 **로그인 시 열릴 때 홈 표시**를 켜지 않는 한 방해하지 않습니다.
- **비용을 한눈에.** OpenRouter 요청은 OpenRouter가 실제로 청구한 금액으로 계산하며, 각 모델 옆에 실시간 가격을 보여 줍니다. 다른 제공업체나 사용자 지정 엔드포인트는 **모델**에서 가격(100만 토큰당, 음성 인식은 오디오 1분당)을 입력하세요.
- **기록**에는 모든 받아쓰기가 날짜별로 묶여 남고 검색할 수 있으며, 원본과 다듬은 텍스트, 소요 시간, 비용과 함께 복사와 다시 변환이 가능합니다. 녹음 보관 기간은 보관 안 함, 1일, 1주, 1개월, 1년, 영구 중에서 고릅니다.
- **9개 UI 언어**: English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский. 시스템 언어를 따르거나 **설정 → 일반 → 언어**에서 고를 수 있습니다.
- **네이티브 디자인.** macOS 26 이상에서는 Liquid Glass와 메뉴 막대 패널, Windows 11에서는 Mica와 Acrylic, 트레이 패널. 말하는 동안 두 플랫폼 모두 작은 녹음 캡슐을 보여 줍니다.
- **로그인 시 실행.**
- **자동 업데이트.** 하루에 한 번 GitHub Releases를 확인하고, 새 버전을 백그라운드에서 다운로드한 뒤 **재시작하여 업데이트**를 클릭할 때 설치합니다. 받아쓰기 도중에는 절대 설치하지 않습니다. 파일을 교체하기 전에 다운로드를 GitHub의 SHA-256과 대조합니다. **설정 → 일반 → 업데이트**에서 끄거나 직접 확인할 수 있습니다.
- **작고 네이티브.** macOS에서는 약 3MB의 Swift/SwiftUI 앱, Windows에서는 자체 포함형 WinUI 3 앱입니다. Electron도, 계정도, 자체 서버도 없습니다.

<p align="center">
  <img src="docs/images/home.png" width="720" alt="홈: 받아쓴 단어 수, 절약한 시간, 말하기 속도, 비용, GitHub 스타일 활동 히트맵">
</p>

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="모델: 단계별 제공업체와 모델, 실시간 가격, 그리고 백업"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="어휘: 수정에서 학습한 것을 포함한 나의 용어"></td>
  </tr>
  <tr>
    <td align="center">단계별로 제공업체와 모델 선택, 실시간 가격과 백업까지</td>
    <td align="center">수정에서 학습한 단어를 포함한 어휘</td>
  </tr>
</table>


## 요구 사항

- **macOS:** macOS 26 이상, Apple 실리콘.
- **Windows:** Windows 10(버전 2004 이상) 또는 Windows 11, x64 또는 ARM64.
- 제공업체 하나 이상의 API 키([OpenRouter](https://openrouter.ai/keys)가 가장 간편합니다. 키 하나로 두 단계를 모두 처리합니다).

## macOS에 설치

### 다운로드

1. [Releases](https://github.com/Tyler913/OpenTypeless/releases)에서 `OpenTypeless-<version>-macOS-arm64.zip`을 다운로드해 압축을 풉니다.
2. **OpenTypeless.app**을 **응용 프로그램** 폴더로 옮깁니다.
3. 이 앱은 Apple 공증을 받지 않았기 때문에(유료 개발자 계정이 필요합니다) macOS가 처음 실행을 막으며, "손상되었기 때문에 열 수 없습니다"라고 표시할 수도 있습니다. 터미널에서 다운로드 격리 플래그를 한 번 제거하세요.

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   그다음 평소처럼 엽니다.

   ```bash
   open /Applications/OpenTypeless.app
   ```

   또는 한 번 열어 본 뒤 **시스템 설정 → 개인정보 보호 및 보안**에서 **그래도 열기**를 클릭합니다.

OpenTypeless는 Dock이 아니라 메뉴 막대(파형 아이콘)에 있습니다.

이후 버전은 앱 안(**설정 → 일반 → 업데이트**)에서 설치되며 터미널 작업이 필요 없습니다. macOS는 브라우저로 다운로드한 앱만 확인하기 때문입니다.

### 소스에서 빌드

Xcode 26 이상이 필요합니다(명령줄 도구만으로도 컴파일되지만, 빌드 시 `/Applications/Xcode.app`의 SwiftUI 매크로 플러그인을 빌려 씁니다).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # 선택 사항이지만 권장, 한 번만
scripts/build-app.sh             # 빌드, 서명 후 /Applications/OpenTypeless.app에 설치
```

`create-signing-cert.sh`는 로컬 코드 서명 ID를 만듭니다. 이것이 없으면 앱은 임시(ad hoc) 서명되며, 다시 빌드할 때마다 macOS가 손쉬운 사용과 마이크 권한을 다시 요청합니다.

설치 대신 배포용 zip을 만들려면 `scripts/build-app.sh --package`를 실행하세요. `macos/dist/OpenTypeless-<version>-macOS-arm64.zip`(임시 서명)을 만들고 SHA-256을 출력합니다.

`build-app.sh`는 컴퓨터에 앱을 딱 하나만 유지합니다. 숨겨진 스테이징 폴더에서 번들을 조립해 `/Applications`로 옮기고, 오래된 사본을 LaunchServices에서 등록 해제하며, 서명이 바뀌면 오래된 개인정보 보호 항목을 지웁니다.

### 처음 실행

1. **마이크**와 **손쉬운 사용** 권한을 허용합니다(손쉬운 사용은 단축키 감지와 텍스트 붙여넣기에 쓰입니다).
2. **설정 → 제공업체**에서 API 키를 추가합니다.
3. Fn을 쓴다면 **시스템 설정 → 키보드 → '🌐 키를 눌러 다음 수행'**을 **동작 안 함**으로 설정하세요. 그러면 Fn을 탭해도 이모티콘 선택기가 열리지 않습니다.

## Windows에 설치

### 다운로드

1. [Releases](https://github.com/Tyler913/OpenTypeless/releases)에서 `OpenTypeless-<version>-windows-x64.zip`(또는 `-arm64`)을 다운로드해 아무 곳(예: `%LOCALAPPDATA%\Programs`)에나 압축을 풉니다.
2. **OpenTypeless.exe**를 실행합니다. 자체 포함형이라 따로 설치할 것이 없습니다.
3. 앱에 코드 서명이 없어서 SmartScreen이 "Windows의 PC 보호"를 표시할 수 있습니다. **추가 정보 → 실행**을 클릭하세요.

이후 버전은 앱 안(**설정 → 일반 → 업데이트**)에서 같은 폴더에 설치되며 SmartScreen 확인이 뜨지 않습니다. `%LOCALAPPDATA%\Programs`처럼 쓰기 권한이 있는 곳에 압축을 푸세요. `Program Files`에 두면 앱은 다운로드 링크만 안내할 수 있습니다.

OpenTypeless는 **알림 영역**(시계 옆 파형 아이콘)에 있습니다. Windows는 새 아이콘을 처음에는 오버플로(^)에 숨기므로, 작업 표시줄로 끌어다 놓거나 **설정 → 개인 설정 → 작업 표시줄 → 기타 시스템 트레이 아이콘**에서 켜세요.

### 소스에서 빌드

[.NET 10 SDK](https://dotnet.microsoft.com/download)가 필요합니다. Visual Studio는 선택 사항입니다.

```powershell
# 이 저장소를 클론한 폴더의 windows\ 에서
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # 테스트, 빌드 후 %LOCALAPPDATA%\Programs\OpenTypeless에 설치
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # windows\dist\OpenTypeless-<version>-windows-x64.zip 생성
```

`build.ps1`은 설치된 사본을 딱 하나만 유지합니다. 실행 중인 앱을 멈추고, 설치 폴더를 교체하고, 시작 메뉴 바로 가기를 새로 고친 뒤 새 빌드를 실행합니다. ARM용 Windows에서는 `-Arch arm64`를 추가하세요.

Windows 앱을 개발하는 데 Windows PC는 필요 없습니다. 변경할 때마다 GitHub Actions가 빌드합니다([지속적 통합](#지속적-통합) 참고).

### 처음 실행

1. **설정 → 제공업체**에서 API 키를 추가합니다.
2. **설정 → 개인 정보 및 보안 → 마이크 → 데스크톱 앱에서 마이크에 액세스하도록 허용**이 켜져 있는지 확인합니다.
3. 오른쪽 Ctrl을 누른 채 말합니다. Windows는 손쉬운 사용 권한이 필요 없습니다. 유일한 제한은 관리자 권한으로 실행 중인 앱에 붙여넣을 수 없다는 점이며, 이때 텍스트는 클립보드에 들어갑니다.

## 기본 모델

| 단계 | 기본값 | 참고 |
|---|---|---|
| 음성 인식 | `microsoft/mai-transcribe-2`(OpenRouter) | OpenRouter의 모든 변환 모델, 또는 다른 곳의 Whisper 호환 `/audio/transcriptions`. |
| 다듬기 | `google/gemini-3.8-flash`(OpenRouter) | 저희 테스트에서 다듬기 품질이 가장 좋았고, 긴 받아쓰기 한 번에 약 0.005달러입니다. 더 저렴한 선택지: `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| 백업 다듬기 | `deepseek/deepseek-v4.1-flash`(OpenRouter) | 기본 모델이 느리게 시작하거나 실패할 때만 요청합니다. 다른 회사의 빠른 모델을 고르세요. |

지연을 줄이기 위해 다듬기 모델의 추론(reasoning)은 자동으로 끄거나 최소로 설정합니다.

## 개인정보 보호

- 오디오와 텍스트는 여러분이 설정한 제공업체에만 전송됩니다. **실시간 미리보기**를 켜면 macOS는 Mac에서 음성을 인식합니다. Windows는 자체 음성 인식을 사용하며, 온라인 음성 인식이 켜져 있으면 음성이 Microsoft로 전송됩니다(설정에 안내되어 있습니다).
- API 키는 macOS 키체인 또는 Windows 자격 증명 관리자(항목 하나, `OpenTypeless/credentials`)에 저장됩니다.
- 홈 화면용 사용량(하루 단위 단어 수, 말한 시간, 비용이며 텍스트는 없음)은 설정과 기록이 있는 폴더의 `usage.json`에 저장됩니다. OpenRouter 가격표는 공개 모델 목록에서 하루에 몇 번 다운로드합니다(키 불필요, 여러분에 대한 정보 없음).
- 기록(오디오 + 변환 결과)은 macOS에서는 `~/Library/Application Support/OpenTypeless/Sessions/`, Windows에서는 `%LOCALAPPDATA%\OpenTypeless\`에 있습니다. 녹음은 기본적으로 한 달간 보관됩니다(기록 페이지에서 보관 안 함, 1일, 1주, 1개월, 1년, 영구 중 선택). 그 뒤에도 텍스트는 최신 200개 항목 안에 남습니다. 실패한 받아쓰기는 다시 시도할 수 있도록 오디오를 보관합니다.
- 업데이트 확인은 하루에 한 번 `api.github.com`에 요청을 보냅니다(계정 불필요, 여러분이나 받아쓰기에 대한 정보 없음). **설정 → 일반 → 업데이트**에서 끌 수 있습니다.
- 수정 학습은 받아쓴 입력란을 여러분의 컴퓨터에서만, 붙여넣은 뒤 최대 2분 동안 읽습니다. 암호 입력란은 건너뜁니다. **어휘 및 스타일**에서 끌 수 있습니다.

## 개발

이 저장소에는 두 앱이 모두 들어 있습니다. 설계, 평가 세트, 이 README를 공유하며, 코드와 테스트와 빌드 스크립트는 각자 가지고 있습니다.

```
macos/                      macOS 앱(Swift Package)
  Sources/TypelessCore/       UI 없는 처리 로직: 분할기, WAV, 제공업체 클라이언트, 재시도, 다듬기 프롬프트
  Sources/OpenTypeless/       앱: 단축키, 녹음기, HUD, 설정, 기록, 붙여넣기, CLI 도구
  Tests/                      swift-testing 테스트
  scripts/                    빌드, 서명, 아이콘, 테스트 스크립트
windows/                    Windows 앱(.NET 솔루션)
  src/TypelessCore/           같은 처리 로직을 한 줄씩 이식
  src/OpenTypeless/           WinUI 앱: 키보드 훅, WASAPI 녹음기, HUD, 트레이, 설정, 기록, 붙여넣기
  src/OpenTypeless.Cli/       명령줄 도구(파일 변환, 분할 분석, 프롬프트 평가)
  tests/                      xUnit 테스트
  scripts/                    빌드, 테스트, 아이콘 스크립트
eval/                       다듬기 테스트 세트와 평가 가이드(두 앱 공용)
i18n/                       UI 번역(중국어·영어 외, 두 앱 공용)
docs/DESIGN.md              아키텍처와 설계 결정
docs/WINDOWS-PORT.md        macOS의 각 파일과 시스템 API가 Windows에서 대응되는 방식
docs/images/                README 스크린샷
```

다듬기 프롬프트(`Prompts.swift` / `Prompts.cs`)는 두 앱에서 바이트 단위까지 같습니다. 바꿀 때는 둘을 함께 바꾸고 [eval/](eval/)로 결과를 확인하세요.

### 번역

사용자에게 보이는 모든 문자열은 중국어와 영어를 그 자리에 적습니다. Swift에서는 `L("有新版本 \(version)", "Version \(version) is available")`, C#에서는 `L($"有新版本 {version}", $"Version {version} is available")`입니다. 다른 언어는 [`i18n/strings.json`](i18n/strings.json)에 있으며, 영어 텍스트를 키로 하고 삽입되는 값에는 순서대로 번호를 붙입니다.

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

두 앱은 빌드할 때 이 파일을 포함하며, 번역이 없는 문자열은 영어로 표시됩니다. 번역에서 자리 표시자의 위치는 바꿔도 되지만 모두 남겨야 합니다. 문자열을 추가하거나 바꾼 뒤에는 번역을 추가하고 `python3 i18n/check.py`를 실행하세요. 누락되었거나, 쓰이지 않거나, 형식이 잘못된 항목을 보여 주며, CI도 모든 풀 리퀘스트에서 실행합니다. 실제 화면에서 언어를 확인하려면 `--snapshot-ui`와 `--lang ja`(또는 다른 언어 코드)로 UI를 렌더링하세요.

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # 단위 테스트(130초 녹음에 장애를 주입하는 모의 서버 포함)
scripts/perf.sh                  # 릴리스 빌드에서의 성능 예산(오디오 처리, 메인 스레드 작업). CI에서도 실행
```

빌드한 바이너리의 유용한 명령줄 모드(`macos/`에서 실행):

```bash
# 오디오 파일로 전체 처리 과정 실행. --realtime은 실제 마이크처럼 말하는 속도로 오디오를 보냄
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# 분할기가 자르는 위치 보기
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# 다듬기 프롬프트와 모델 평가(eval/README.md 참고)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# 설정 페이지, 메뉴 막대 패널, HUD를 PNG로 렌더링(--live는 화면에 표시해 실제 Liquid Glass로 촬영).
# README 스크린샷은 샘플 기록과, 권한을 허용된 것으로 취급하는 --demo를 사용합니다.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # 단위 테스트(130초 녹음에 장애를 주입하는 모의 서버 포함)
scripts\perf.ps1       # 릴리스 빌드에서의 성능 예산(오디오 처리, UI 스레드 작업). CI에서도 실행
```

명령줄 도구(`OpenTypeless.Cli.exe`, 앱과 함께 배포되며 앱의 설정과 키를 사용):

```powershell
# 오디오 파일(WAV, MP3, M4A, WMA, FLAC…)로 전체 처리 과정 실행. --realtime은 말하는 속도로 오디오를 보냄
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# 분할기가 자르는 위치 보기
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# 다듬기 프롬프트와 모델 평가(eval/README.md 참고)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# 모든 설정 페이지, 트레이 패널, HUD 상태를 PNG로 렌더링
# (--lang en|zh|ja|… 는 한 언어만, --demo는 권한을 허용된 것으로 취급. OPENTYPELESS_DATA_DIR와 샘플 기록과 함께 사용)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

개발용 환경 변수(Windows):

| 변수 | 효과 |
|---|---|
| `OPENROUTER_API_KEY` | 저장된 OpenRouter 키를 덮어씁니다. |
| `OPENTYPELESS_DATA_DIR` | 설정과 기록에 다른 폴더를 씁니다(테스트에 유용). |
| `OPENTYPELESS_DEBUG` | 단축키 / 세션 / 포커스 이벤트를 데이터 폴더의 `debug.log`에 기록합니다. |
| `OPENTYPELESS_TEST_AUDIO` | 마이크 대신 16kHz 모노 WAV를 실시간으로 흘려보냅니다(엔드 투 엔드 테스트용). |

### 기여하기

`main`은 보호되어 있어 모든 변경은 풀 리퀘스트를 거칩니다. 브랜치에서 작업하고 `main`으로 풀 리퀘스트를 연 뒤, **CI passed** 검사가 초록색이 되면 병합하세요.

### 지속적 통합

GitHub Actions([`.github/workflows/`](.github/workflows/))는 변경한 앱만 빌드합니다.

| 변경한 곳 | 실행되는 것 |
|---|---|
| `macos/**` | macOS 러너에서 **macOS build**: 테스트 후 앱 zip 생성. |
| `windows/**` | Windows 러너에서 **Windows build**: 테스트 후 x64와 ARM64 zip 생성. |
| `testdata/**` | 두 빌드 모두: 두 테스트 스위트가 함께 읽는 공용 테스트 케이스(단어 수, 가격, 절약한 시간). 두 앱의 결과를 일치시키기 위함입니다. |
| `i18n/**` | 두 빌드 모두: 두 앱이 포함하는 번역. |
| `docs/`, `eval/`, `README*.md`만 | 아무것도 빌드하지 않습니다. |

모든 실행에서 번역 검사(**Translations**, `python3 i18n/check.py`)도 함께 합니다.

zip은 Actions 탭에서 해당 실행의 **Artifacts** 섹션에서 다운로드합니다. **Actions → macOS build / Windows build → Run workflow**로 빌드를 직접 시작할 수 있습니다.

릴리스하려면 두 앱의 버전(`macos/scripts/build-app.sh`, `windows/Directory.Build.props`)을 올리고 태그 하나(`1.0.2` 또는 `V1.0.2`)를 푸시합니다. 두 앱을 빌드하고 "OpenTypeless V1.0.2"라는 **초안** 릴리스를 하나 만들며, macOS zip(릴리스 인증서로 서명)과 Windows x64, ARM64 zip이 들어갑니다. zip의 버전이 태그와 다르면 빌드가 실패합니다.

초안을 검토한 뒤 직접 게시하면 최신 릴리스가 됩니다. 설치된 사본은 하루 안에 이를 찾습니다. 앱 내 업데이터는 자기 플랫폼용 zip(`OpenTypeless-<version>-macOS-arm64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`)이 있는, 게시된 사전 릴리스가 아닌 최신 릴리스를 찾습니다. 배포하지 않으려면 사전 릴리스로 표시하세요.

**macOS 릴리스 서명(한 번만).** macOS는 손쉬운 사용과 마이크 권한을 앱의 서명에 연결하므로, 릴리스는 항상 같은 인증서로 서명해야 합니다. 그렇지 않으면 업데이트할 때마다 사용자가 두 권한을 다시 허용해야 합니다. `macos/scripts/create-release-cert.sh`를 실행하고, 만들어진 `.p12`를 안전한 곳에 보관한 뒤, 출력되는 두 저장소 시크릿(`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`)을 추가하세요. 이후 릴리스 빌드는 이것으로 서명됩니다. 시크릿이 없으면 임시 서명되며 실행에 경고가 표시됩니다.

## 감사의 말

Typeless에서 영감을 받았습니다. 시스템 연동 방식은 오픈 소스인 [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless), [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless)에서 배웠습니다. 이 프로젝트는 독립적이며 이들 중 어느 것과도 관련이 없습니다.

## 라이선스

[MIT](LICENSE)
