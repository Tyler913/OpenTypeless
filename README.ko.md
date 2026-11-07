<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <b>한국어</b> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>키를 누르고 말하면, 커서 위치에 깔끔한 텍스트가 입력됩니다.</b><br>
  macOS와 Windows용 네이티브 음성 입력. 긴 받아쓰기를 위해 만들었습니다.
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>다운로드</b></a> · <a href="#설치">설치</a> · <a href="docs/DEVELOPMENT.md">소스에서 빌드</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="녹음 캡슐과 그 위에 미리 보이는 말한 내용">
</p>

## 왜 OpenTypeless인가

대부분의 음성 입력 앱은 한 문장은 잘 받아쓰지만 2분짜리 긴 말에서는 시간 초과, 빈 결과, 텍스트 유실로 무너집니다. OpenTypeless는 바로 그 상황을 위해 만들었습니다.

- **원하는 만큼 말하세요.** 오디오를 말이 멈추는 곳에서 나누고 말하는 동안 받아쓰기 때문에, 긴 받아쓰기도 키를 놓자마자 완성됩니다.
- **한 단어도 잃지 않습니다.** 구간마다 따로 재시도하고, 느린 모델에는 백업이 붙으며, 모든 녹음이 저장되어 실패한 것도 나중에 다시 시도할 수 있습니다.
- **직접 타이핑한 것처럼.** 군말과 말 바꿈은 사라지고 "수요일, 아니 목요일"은 목요일로, 목록은 목록으로 정리됩니다. 표현, 숫자, 언어는 그대로입니다.
- **내 키로 사용.** OpenRouter, OpenAI, Groq, Gemini, Anthropic, DeepSeek 등을 지원하고, Ollama와 LM Studio로 로컬 모델도 쓸 수 있습니다. 계정도 자체 서버도 없습니다.

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="메뉴 막대 패널: 받아쓰기 방법, 이번 달 비용, 최근 받아쓰기">
</p>

## 기능

- **누르고 말하기 또는 핸즈프리.** **Fn**(macOS)이나 **오른쪽 Alt**(Windows)를 누른 채 말하거나, 한 번 탭해서 계속 말하세요. 어떤 키나 조합도 쓸 수 있습니다.
- **말하면서 번역.** **Shift**도 누르면 선택한 언어로 입력됩니다.
- **음성으로 편집.** 텍스트를 선택하고 **Ctrl**도 누른 채 "더 격식 있게", "목록으로 바꿔 줘"라고 말하세요.
- **학습하는 어휘.** 이름과 전문 용어를 추가하세요. 붙여넣은 뒤 고친 단어는 자동으로 학습합니다.
- **스니펫.** "내 이메일"이라고 말하면 저장한 텍스트가 그대로 들어갑니다.
- **앱별 설정.** Slack에서는 입력 후 Return, 메일에서는 격식 있는 어조, 터미널에서는 다듬기 없음.
- **기록과 파일.** 모든 받아쓰기를 검색하고 내보낼 수 있으며, 오디오와 동영상 파일도 받아쓸 수 있습니다.
- **비용을 한눈에.** 홈에 단어 수, 절약한 시간, 비용을 보여 주고 월 한도도 설정할 수 있습니다.
- **온디바이스, 키 없이.** 음성 인식을 컴퓨터에서 직접 할 수도 있습니다. 무료이고 오프라인이며 녹음이 밖으로 나가지 않습니다. 정확도는 클라우드 모델보다 조금 낮습니다.
- **실시간 미리 보기(베타).** 말하는 동안 캡슐 위에 내용이 표시됩니다.
- **9개 언어, 자동 업데이트.** English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский.

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="홈: 단어 수, 절약한 시간, 비용, 활동"><br><sub>홈: 단어 수, 절약한 시간, 비용, 활동</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="기록: 모든 받아쓰기를 검색"><br><sub>기록: 모든 받아쓰기를 검색</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="모델: 단계마다 제공업체와 모델 선택"><br><sub>모델: 단계마다 제공업체와 모델 선택</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="어휘: 수정에서 학습한 단어 포함"><br><sub>어휘: 수정에서 학습한 단어 포함</sub></td>
  </tr>
</table>

<sub>스크린샷은 macOS입니다. Windows에도 같은 페이지가 Windows 고유의 모습으로 있습니다.</sub>

## 설치

### macOS

macOS 26 이상(Apple 실리콘 또는 Intel)이 필요합니다.

```bash
brew install --cask tyler913/tap/opentypeless
```

또는 [Releases](https://github.com/Tyler913/OpenTypeless/releases)에서 `.dmg`를 내려받아(Apple 실리콘은 `arm64`, Intel은 `x64`) 앱을 응용 프로그램 폴더로 드래그하세요. Apple 공증을 받지 않았으므로, 열 수 없다거나 "손상되었다"는 메시지가 나오면 한 번만 다음을 실행하세요:

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Windows 10(2004 이상) 또는 Windows 11, x64 또는 ARM64가 필요합니다.

[Releases](https://github.com/Tyler913/OpenTypeless/releases)에서 `OpenTypeless-<version>-windows-x64-setup.exe`(ARM용 Windows는 `arm64`)를 내려받아 실행하세요. 관리자 권한은 필요 없습니다. SmartScreen이 나타나면 **추가 정보 → 실행**을 클릭하세요. `.zip`은 포터블 버전입니다.

이후에는 앱이 스스로 업데이트됩니다.

## 시작하기

1. OpenTypeless를 엽니다. 메뉴 막대(macOS)나 알림 영역(Windows)에 있습니다.
2. 환영 안내에서 API 키를 입력합니다. [OpenRouter](https://openrouter.ai/keys)가 가장 쉽습니다. 키 하나로 모두 됩니다. ‘온디바이스 인식 사용’을 고르면 키 없이 바로 시작할 수 있습니다.
3. macOS에서는 요청이 뜨면 **마이크**와 **손쉬운 사용**을 허용합니다.
4. 단축키를 누른 채 말하고 놓으세요. 커서 위치에 텍스트가 입력됩니다.

> macOS에서는 **시스템 설정 → 키보드 → '🌐 키를 눌러 다음 수행'**을 **동작 안 함**으로 설정하면 Fn을 탭해도 이모티콘 선택기가 열리지 않습니다.

## 개인정보 보호

- 오디오와 텍스트는 설정한 제공업체에만 전송됩니다. 그 밖에는 하루 한 번 GitHub에서 업데이트를 확인하고 OpenRouter의 공개 가격표를 내려받는 것뿐입니다. Windows의 실시간 미리 보기는 Windows 음성 인식을 쓰며, 그 온라인 설정을 따릅니다.
- 키, 설정, 기록은 모두 내 컴퓨터에 남습니다. 녹음은 기본 한 달 보관되며 기간은 직접 고를 수 있습니다.
- 암호 입력란은 읽지 않습니다. 커서 주변 텍스트로 이름 철자를 맞추는 기능은 직접 켜기 전까지 꺼져 있습니다.

## 개발

한 저장소에 두 개의 네이티브 앱: [`macos/`](macos)는 Swift / SwiftUI, [`windows/`](windows)는 C# / WinUI 3이며 처리 과정, 다듬기 프롬프트, 설정이 같습니다. 빌드와 테스트는 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md), 동작 방식은 [docs/DESIGN.md](docs/DESIGN.md)를 보세요.

## 감사의 말

Typeless에서 영감을 받았습니다. 시스템 연동 방식은 오픈 소스인 [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless), [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless)에서 배웠습니다. 이 프로젝트는 독립적이며 이들 중 어느 것과도 관련이 없습니다. Windows의 온디바이스 인식은 [SenseVoice Small](https://github.com/FunAudioLLM/SenseVoice)(FunASR Model License)를 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)(Apache-2.0)로 실행합니다.

## 라이선스

[MIT](LICENSE)
