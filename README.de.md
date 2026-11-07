<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <b>Deutsch</b> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Taste halten, sprechen, und sauberer Text steht am Cursor.</b><br>
  Native Spracheingabe für macOS und Windows, gebaut für lange Diktate.
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>Download</b></a> · <a href="#installation">Installation</a> · <a href="docs/DEVELOPMENT.md">Aus dem Quellcode bauen</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="Die Aufnahmekapsel, darüber die Vorschau deiner Worte">
</p>

## Warum OpenTypeless

Die meisten Diktier-Apps schaffen einen Satz und scheitern an zwei Minuten am Stück: Zeitüberschreitungen, leere Ergebnisse, verlorener Text. OpenTypeless ist genau dafür gebaut.

- **Sprich, so lange du willst.** Das Audio wird an deinen Pausen geteilt und schon beim Sprechen transkribiert, sodass selbst ein langes Diktat kurz nach dem Loslassen fertig ist.
- **Kein Wort geht verloren.** Jeder Abschnitt wird einzeln wiederholt, ein langsames Modell bekommt eine Ausweichlösung, und jede Aufnahme wird gespeichert, damit eine fehlgeschlagene wiederholt werden kann.
- **Als hättest du getippt.** Füllwörter und Neuansätze verschwinden, „Mittwoch, nein, Donnerstag“ wird zu Donnerstag, und Listen werden Listen. Deine Worte, Zahlen und Sprachen bleiben.
- **Dein eigener Schlüssel.** OpenRouter, OpenAI, Groq, Gemini, Anthropic, DeepSeek und mehr, oder lokale Modelle mit Ollama und LM Studio. Kein Konto, kein eigener Server.

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="Das Menüleisten-Panel: wie man diktiert, die Ausgaben des Monats und die letzten Diktate">
</p>

## Funktionen

- **Halten zum Sprechen oder freihändig.** Halte **Fn** (macOS) oder die **rechte Alt-Taste** (Windows), oder tippe einmal, um weiterzusprechen. Jede Taste oder Kombination geht.
- **Beim Sprechen übersetzen.** Nimm **Umschalt** dazu, und deine Worte kommen in der gewählten Sprache heraus.
- **Per Stimme bearbeiten.** Text markieren, **Strg** dazunehmen und „formeller“ oder „mach eine Liste daraus“ sagen.
- **Ein Wortschatz, der lernt.** Füge Namen und Fachbegriffe hinzu; Wörter, die du nach dem Einfügen korrigierst, lernt die App selbst.
- **Textbausteine.** Sag „meine E-Mail“, und dein gespeicherter Text wird Wort für Wort eingefügt.
- **Einstellungen pro App.** Eingabetaste nach dem Einfügen in Slack, förmlicher Ton in Mail, keine Bereinigung im Terminal.
- **Verlauf und Dateien.** Jedes Diktat ist durchsuchbar und exportierbar, und Audio- oder Videodateien lassen sich auch transkribieren.
- **Kosten im Blick.** Wörter, gesparte Zeit und Kosten auf der Startseite, mit optionalem Monatslimit.
- **Live-Vorschau (Beta).** Sieh deine Worte über der Kapsel, während du sprichst.
- **Neun Sprachen, automatische Updates.** English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский.

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="Start: Wörter, gesparte Zeit, Ausgaben und Aktivität"><br><sub>Start: Wörter, gesparte Zeit, Ausgaben und Aktivität</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="Verlauf: jedes Diktat, durchsuchbar"><br><sub>Verlauf: jedes Diktat, durchsuchbar</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="Modelle: ein Anbieter und ein Modell für jeden Schritt"><br><sub>Modelle: ein Anbieter und ein Modell für jeden Schritt</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="Wortschatz, auch mit Wörtern aus deinen Korrekturen"><br><sub>Wortschatz, auch mit Wörtern aus deinen Korrekturen</sub></td>
  </tr>
</table>

<sub>Die Screenshots stammen von macOS. Windows hat dieselben Seiten in seinem eigenen nativen Look.</sub>

## Installation

### macOS

Benötigt macOS 26 oder neuer, auf Apple Silicon oder Intel.

```bash
brew install --cask tyler913/tap/opentypeless
```

Oder lade die `.dmg` von [Releases](https://github.com/Tyler913/OpenTypeless/releases) (`arm64` für Apple Silicon, `x64` für Intel) und ziehe die App in „Programme“. Sie ist nicht von Apple notarisiert: Wenn macOS meldet, dass sie sich nicht öffnen lässt oder „beschädigt“ ist, führe einmal Folgendes aus:

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Benötigt Windows 10 (2004 oder neuer) oder Windows 11, x64 oder ARM64.

Lade `OpenTypeless-<version>-windows-x64-setup.exe` (`arm64` für Windows auf ARM) von [Releases](https://github.com/Tyler913/OpenTypeless/releases) und starte sie; Administratorrechte sind nicht nötig. Erscheint SmartScreen, klicke auf **Weitere Informationen → Trotzdem ausführen**. Die `.zip` ist eine portable Version.

Danach aktualisiert sich die App selbst.

## Erste Schritte

1. Öffne OpenTypeless. Es sitzt in der Menüleiste (macOS) bzw. im Infobereich (Windows).
2. Der Willkommensassistent fragt nach einem API-Schlüssel. [OpenRouter](https://openrouter.ai/keys) ist am einfachsten: Ein Schlüssel deckt alles ab.
3. Erlaube auf macOS **Mikrofon** und **Bedienungshilfen**, wenn danach gefragt wird.
4. Kürzel halten, sprechen, loslassen. Der Text erscheint am Cursor.

> Stelle auf macOS **Systemeinstellungen → Tastatur → „🌐-Taste drücken für“** auf **Keine Aktion**, damit ein Tippen auf Fn nicht die Emoji-Auswahl öffnet.

## Datenschutz

- Audio und Text gehen nur an die Anbieter, die du einrichtest. Sonst prüft die App nur einmal täglich bei GitHub auf Updates und lädt die öffentliche Preisliste von OpenRouter. Die Live-Vorschau unter Windows nutzt die Windows-Spracherkennung, die ihrer eigenen Online-Einstellung folgt.
- Schlüssel, Einstellungen und Verlauf bleiben auf deinem Computer. Aufnahmen werden standardmäßig einen Monat aufbewahrt; wie lange, entscheidest du.
- Passwortfelder werden nie gelesen. Den Text um den Cursor zum Schreiben von Namen zu nutzen ist aus, bis du es einschaltest.

## Entwicklung

Zwei native Apps in einem Repository: [`macos/`](macos) in Swift / SwiftUI und [`windows/`](windows) in C# / WinUI 3, mit derselben Verarbeitung, demselben Bereinigungs-Prompt und denselben Einstellungen. Bauen und Testen: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md); wie sie funktionieren: [docs/DESIGN.md](docs/DESIGN.md).

## Danksagung

Inspiriert von Typeless. Der Ansatz für die Systemintegration stammt aus den Open-Source-Projekten [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) und [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Dieses Projekt ist unabhängig und mit keinem davon verbunden.

## Lizenz

[MIT](LICENSE)
