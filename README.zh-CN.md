<p align="center">
  <a href="README.md">English</a> · <b>简体中文</b> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>按住一个键，说话，光标处就出现整理好的文字。</b><br>
  macOS 和 Windows 的原生语音输入，专为长段口述打造。
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>下载</b></a> · <a href="#安装">安装</a> · <a href="docs/DEVELOPMENT.md">从源码构建</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="录音胶囊，上方实时预览你说的话">
</p>

## 为什么选 OpenTypeless

大多数语音输入应用说一句话没问题，一到两分钟的长段口述就出错：超时、空白、文字丢失。OpenTypeless 就是为这种场景做的。

- **想说多久说多久。** 音频在你停顿处切开，边说边转写，再长的口述，松开按键后片刻就好。
- **一个字都不丢。** 每个片段单独重试，模型慢了有备用，每段录音都会保存，失败了可以稍后重试。
- **像你亲手打的。** 口头禅和重说都去掉，“周三，不对，周四”只留周四，该分条的分条。你的措辞、数字和中英混说都原样保留。
- **用你自己的 Key。** 支持 OpenRouter、OpenAI、Groq、Gemini、Anthropic、DeepSeek 等，也能用 Ollama、LM Studio 跑本地模型。无需账号，没有自己的服务器。

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="菜单栏面板：怎么听写、本月花费和最近的听写">
</p>

## 功能

- **按住说话或免提。** 按住 **Fn**（macOS）或 **右 Alt**（Windows），也可以轻点一下持续录音。任何键或组合键都可以。
- **边说边翻译。** 再按住 **Shift**，说出的话就变成你选的语言。
- **用语音改文字。** 选中文字，再按住 **Ctrl**，说“改正式一点”或“整理成列表”。
- **会学习的词汇表。** 加入人名和术语；粘贴后你手动改正的词会自动学会。
- **文本片段。** 说“我的邮箱”，就插入你保存好的文字，一字不差。
- **按应用设置。** 在 Slack 里插入后按回车，在邮件里语气正式些，在终端里不整理。
- **历史和文件。** 每次听写都能搜索和导出，也能转写音频和视频文件。
- **花费心里有数。** 首页显示字数、节省的时间和花费，还可以设每月上限。
- **设备端识别，不用 Key。** 语音转文字也可以在你的电脑上完成：免费、离线、录音不出电脑，准确率比云端模型略低。
- **实时预览（测试版）。** 说话时在胶囊上方看到你说的话。
- **九种界面语言，自动更新。** English、简体中文、日本語、한국어、Español、Português、Français、Deutsch、Русский。

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="首页：字数、节省的时间、花费和活跃度"><br><sub>首页：字数、节省的时间、花费和活跃度</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="历史：每次听写，可搜索"><br><sub>历史：每次听写，可搜索</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="模型：每一步选服务商和模型"><br><sub>模型：每一步选服务商和模型</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="词汇表，包括从你的改正中学到的词"><br><sub>词汇表，包括从你的改正中学到的词</sub></td>
  </tr>
</table>

<sub>截图来自 macOS。Windows 版页面相同，采用 Windows 自己的原生外观。</sub>

## 安装

### macOS

需要 macOS 26 或更高版本，Apple 芯片或 Intel 均可。

```bash
brew install --cask tyler913/tap/opentypeless
```

也可以从 [Releases](https://github.com/Tyler913/OpenTypeless/releases) 下载 `.dmg`（Apple 芯片选 `arm64`，Intel 选 `x64`），把应用拖进“应用程序”。应用没有经过 Apple 公证，如果 macOS 提示无法打开或“已损坏”，运行一次：

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

需要 Windows 10（2004 或更高版本）或 Windows 11，x64 或 ARM64。

从 [Releases](https://github.com/Tyler913/OpenTypeless/releases) 下载 `OpenTypeless-<version>-windows-x64-setup.exe`（ARM 设备选 `arm64`）并运行，不需要管理员权限。如果出现 SmartScreen，点 **更多信息 → 仍要运行**。`.zip` 是免安装版。

之后应用会自动更新。

## 开始使用

1. 打开 OpenTypeless。它在菜单栏（macOS）或通知区域（Windows）里。
2. 欢迎引导会让你填一个 API Key。[OpenRouter](https://openrouter.ai/keys) 最省事：一个 Key 全部搞定。也可以选「使用设备端识别」，不填 Key 直接开始。
3. 在 macOS 上，按提示允许 **麦克风** 和 **辅助功能**。
4. 按住快捷键，说话，松开。文字就出现在光标处。

> 在 macOS 上，把 **系统设置 → 键盘 → “按下 🌐 键时”** 设为 **不执行任何操作**，这样轻点 Fn 不会弹出表情面板。

## 隐私

- 音频和文字只发给你配置的服务商。除此之外，应用只会每天到 GitHub 检查一次更新，并下载 OpenRouter 公开的价格表。Windows 上的实时预览使用 Windows 语音识别，是否联网取决于它自己的在线设置。
- Key、设置和历史都留在你的电脑上。录音默认保留一个月，时长由你决定。
- 从不读取密码框。用光标附近的文字校正名字拼写默认关闭，需要你自己打开。

## 开发

一个仓库里两个原生应用：[`macos/`](macos) 用 Swift / SwiftUI，[`windows/`](windows) 用 C# / WinUI 3，处理流程、整理提示词和设置都相同。构建和测试见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)，工作原理见 [docs/DESIGN.md](docs/DESIGN.md)。

## 致谢

灵感来自 Typeless。系统集成的做法借鉴了开源的 [VoiceInk](https://github.com/Beingpax/VoiceInk)、[OpenLess](https://github.com/Open-Less/openless) 和 [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless)。本项目独立开发，与它们均无关联。Windows 上的本机识别使用 [SenseVoice Small](https://github.com/FunAudioLLM/SenseVoice)（FunASR Model License）和 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx)（Apache-2.0）。

## 许可证

[MIT](LICENSE)
