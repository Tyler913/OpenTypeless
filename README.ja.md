<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <b>日本語</b> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>キーを押して話すだけで、カーソル位置に整ったテキストが入ります。</b><br>
  macOS と Windows のネイティブ音声入力。長い口述のために作りました。
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>ダウンロード</b></a> · <a href="#インストール">インストール</a> · <a href="docs/DEVELOPMENT.md">ソースからビルド</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="録音カプセルと、その上に表示される話した言葉のプレビュー">
</p>

## OpenTypeless を選ぶ理由

多くの音声入力アプリは一文なら問題なくても、2 分の長い口述になるとタイムアウト、空の結果、テキストの消失で崩れます。OpenTypeless はまさにそのために作られています。

- **好きなだけ話せる。** 音声は話の区切りで分割され、話している間に文字起こしされるので、長い口述でもキーを離してすぐに完成します。
- **一語も失わない。** 区間ごとに個別に再試行し、遅いモデルにはバックアップを用意。録音はすべて保存され、失敗しても後から再試行できます。
- **自分でタイプしたように。** フィラーや言い直しは消え、「水曜、いや木曜」は木曜に、箇条書きは箇条書きに。言い回し、数字、言語はそのまま残ります。
- **自分のキーで使える。** OpenRouter、OpenAI、Groq、Gemini、Anthropic、DeepSeek など。Ollama や LM Studio でローカルモデルも使えます。アカウント不要、独自サーバーなし。

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="メニューバーのパネル：口述の方法、今月の利用額、最近の口述">
</p>

## 機能

- **押して話す、またはハンズフリー。** **Fn**（macOS）か **右 Alt**（Windows）を押したまま話すか、一度タップして話し続けます。どのキーや組み合わせでも設定できます。
- **話しながら翻訳。** **Shift** も押すと、選んだ言語で入力されます。
- **声で編集。** テキストを選択して **Ctrl** も押し、「もっと丁寧に」「箇条書きにして」と話します。
- **学習する語彙。** 名前や専門用語を登録できます。ペースト後に直した単語は自動で覚えます。
- **スニペット。** 「私のメール」と言えば、保存したテキストがそのまま入ります。
- **アプリごとの設定。** Slack では入力後に Return、メールでは丁寧な口調、ターミナルでは整形なし。
- **履歴とファイル。** すべての口述を検索・書き出しでき、音声や動画ファイルの文字起こしもできます。
- **利用額がわかる。** ホームに語数、節約した時間、費用を表示。月の上限も設定できます。
- **オンデバイス、キー不要。** 音声の文字起こしをこのコンピュータ上で行うこともできます。無料・オフラインで録音は外に出ません。精度はクラウドのモデルより少し低めです。
- **ライブプレビュー（ベータ）。** 話している間、カプセルの上に言葉が表示されます。
- **9 言語、自動アップデート。** English、简体中文、日本語、한국어、Español、Português、Français、Deutsch、Русский。

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="ホーム：語数、節約した時間、費用、アクティビティ"><br><sub>ホーム：語数、節約した時間、費用、アクティビティ</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="履歴：すべての口述を検索可能"><br><sub>履歴：すべての口述を検索可能</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="モデル：手順ごとにプロバイダとモデルを選択"><br><sub>モデル：手順ごとにプロバイダとモデルを選択</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="語彙：修正から学んだ単語も含む"><br><sub>語彙：修正から学んだ単語も含む</sub></td>
  </tr>
</table>

<sub>スクリーンショットは macOS のものです。Windows 版も同じページを Windows のネイティブな見た目で備えています。</sub>

## インストール

### macOS

macOS 26 以降（Apple シリコンまたは Intel）が必要です。

```bash
brew install --cask tyler913/tap/opentypeless
```

または [Releases](https://github.com/Tyler913/OpenTypeless/releases) から `.dmg` をダウンロードし（Apple シリコンは `arm64`、Intel は `x64`）、アプリを「アプリケーション」にドラッグします。Apple の公証を受けていないため、開けない、または「壊れている」と表示されたら一度だけ次を実行してください：

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Windows 10（2004 以降）または Windows 11、x64 または ARM64 が必要です。

[Releases](https://github.com/Tyler913/OpenTypeless/releases) から `OpenTypeless-<version>-windows-x64-setup.exe`（ARM 版 Windows は `arm64`）をダウンロードして実行します。管理者権限は不要です。SmartScreen が表示されたら **詳細情報 → 実行** をクリックしてください。`.zip` はポータブル版です。

以降、アプリは自動でアップデートされます。

## はじめに

1. OpenTypeless を開きます。メニューバー（macOS）または通知領域（Windows）に常駐します。
2. ようこそガイドで API キーを入力します。[OpenRouter](https://openrouter.ai/keys) が一番簡単で、キー 1 つですべてまかなえます。「オンデバイス認識を使う」を選べば、キーなしで始められます。
3. macOS では、求められたら **マイク** と **アクセシビリティ** を許可します。
4. ショートカットを押したまま話し、離します。カーソル位置にテキストが入ります。

> macOS では **システム設定 → キーボード →「🌐キーを押して」** を **何もしない** にすると、Fn をタップしても絵文字ピッカーが開きません。

## プライバシー

- 音声とテキストは設定したプロバイダにだけ送られます。それ以外の通信は、1 日 1 回の GitHub でのアップデート確認と、OpenRouter の公開価格表の取得だけです。Windows のライブプレビューは Windows の音声認識を使い、そのオンライン設定に従います。
- キー、設定、履歴はすべてお使いのコンピュータに残ります。録音はデフォルトで 1 か月保存され、期間は選べます。
- パスワード欄は読み取りません。カーソル周辺のテキストで名前の綴りを合わせる機能は、オンにしない限り無効です。

## 開発

1 つのリポジトリに 2 つのネイティブアプリ：[`macos/`](macos) は Swift / SwiftUI、[`windows/`](windows) は C# / WinUI 3 で、処理の流れ、整形プロンプト、設定は共通です。ビルドとテストは [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)、仕組みは [docs/DESIGN.md](docs/DESIGN.md) を参照してください。

## 謝辞

Typeless に着想を得ています。システム連携の手法は、オープンソースの [VoiceInk](https://github.com/Beingpax/VoiceInk)、[OpenLess](https://github.com/Open-Less/openless)、[OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless) から学びました。本プロジェクトは独立しており、これらのいずれとも関係はありません。

## ライセンス

[MIT](LICENSE)
