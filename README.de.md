<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <b>Deutsch</b> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="OpenTypeless-Symbol">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Native Spracheingabe für macOS und Windows, die auch bei langen Diktaten nicht einknickt.</b><br>
  Taste halten, so lange sprechen, wie du willst, und sauberen, gegliederten Text am Cursor bekommen.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="Die Aufnahme-Kapsel, die beim Sprechen erscheint">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="Das Menüleisten-Panel: Hinweis zum Tastenkürzel und letzte Diktate">
</p>

<p align="center">
  Zwei native Apps, ein Design: <b>macOS</b> (Swift / SwiftUI) und <b>Windows</b> (C# / WinUI 3).<br>
  Gleiche Verarbeitung, gleicher Bereinigungs-Prompt, gleiche Einstellungen und gleiche Fehlerbehandlung; nur die Systemintegration und das Aussehen unterscheiden sich.
</p>

---

## Warum es das gibt

Sprechen ist der schnellste Weg, lange Prompts für KI-Tools zu schreiben. Das heißt aber auch: lange Diktate. Ein bis drei Minuten laut nachdenken ist ganz normal.

Die meisten Spracheingabe-Apps, ob Open Source oder kommerziell, kommen mit einem Satz gut klar und **scheitern dann genau an diesen langen Diktaten**. 40 s, 1 min oder 2 min Aufnahme enden mit einer Zeitüberschreitung, einem leeren Ergebnis oder verlorenem Text. Beim Lesen des Codes mehrerer Open-Source-Alternativen tauchten immer wieder dieselben Ursachen auf:

- **Die ganze Aufnahme geht als eine einzige Spracherkennungs-Anfrage raus.** Anbieter brechen nach etwa 60 s Verarbeitung ab (OpenRouter dokumentiert das ausdrücklich). Je länger du sprichst, desto wahrscheinlicher scheitert die Anfrage.
- **Der LLM-Aufruf für die Bereinigung hat ein kurzes *Gesamt*-Timeout.** Ein Limit von 30 s, das das Streaming einschließt, schneidet eine lange Antwort mittendrin ab.
- **Ein Fehler, und alles ist weg.** Zwei Minuten Sprechen sind verloren, und du musst alles noch einmal sagen.

OpenTypeless ist um genau dieses Problem herum gebaut.

## Wie lange Diktate verarbeitet werden

| | Was passiert |
|---|---|
| **Schnitt an Pausen** | Während du sprichst, wird das Audio in Abschnitte von 18–28 s geschnitten, jeweils im leisesten 0,4-s-Fenster. So wird kein Wort zerteilt, und keine Anfrage kommt in die Nähe des 60-s-Limits des Anbieters. |
| **Transkription schon beim Sprechen** | Jeder Abschnitt wird im Hintergrund transkribiert, sobald er geschnitten ist. Nach einem zweiminütigen Diktat sind beim Loslassen der Taste nur noch die letzten Sekunden übrig. |
| **Wiederholung pro Abschnitt** | Netzabbrüche, 429 und 5xx-Fehler werden mit wachsender Wartezeit wiederholt. Ungültige Schlüssel und Abrechnungsfehler schlagen sofort fehl. Ein fehlgeschlagener Abschnitt beeinflusst nie die anderen, und fehlgeschlagene Abschnitte bekommen am Ende noch eine komplette Runde. |
| **Backup-Modell bei langsamem Start** | Hat das Bereinigungsmodell nach 0,55 s noch nicht zu antworten begonnen oder schlägt es fehl, wird zusätzlich ein Backup-Modell eines anderen Herstellers gefragt, und die erste Antwort gewinnt. Ein langsamer Anbieter kostet so weniger als eine Sekunde extra, nicht die ganze Wartezeit. |
| **Leerlauf-Timeout statt Gesamt-Timeout** | Die Bereinigung empfängt ihre Antwort gestreamt und gilt erst als hängend, wenn 25 s lang *gar keine* Daten ankommen. Lange Ausgaben werden nie abgeschnitten. |
| **Kein Wort geht verloren** | Das Audio wird schon beim Sprechen auf die Festplatte geschrieben. Jedes Diktat landet im Verlauf; ein fehlgeschlagenes lässt sich später wiederholen, und nur seine fehlgeschlagenen Abschnitte werden neu gesendet. Schlägt die Bereinigung fehl, wird stattdessen das Rohtranskript eingefügt. |

Ein synthetisches Diktat von 121 s wird zum Beispiel an natürlichen Pausen in 6 Abschnitte geteilt. Wenn die sprechende Person aufhört, sind 5 davon bereits transkribiert.

## Eine Bereinigung, die klingt, als hättest du getippt

Rohe Spracherkennung ist unordentlich: Füllwörter, Neuanfänge, „nein, warte, ich meine …“, lautes Nachdenken. Die Bereinigung macht daraus, was du getippt hättest:

- **Selbstkorrekturen werden aufgelöst.** Die letzte Version gewinnt („Mittwoch, nein, Donnerstag“ → Donnerstag). Das gilt auch für Korrekturen viel später, für implizite („50.000, äh, sicherheitshalber 60.000“) und für ganz zurückgenommene Punkte („… der dritte, vergiss es“).
- **Füllwörter und lautes Nachdenken fliegen raus.** um / uh / 嗯 / 那个 / „lass mich überlegen“ / „das war’s eigentlich“ verschwinden.
- **Nichts Echtes geht verloren.** Zahlen, Versionen, Namen und Vergleiche bleiben exakt erhalten. Dem Modell wird gesagt, dass Produkte, die neuer sind als sein Wissen, real sind. Aus „Gemini 3.5“ wird also nie „Gemini 2.5“.
- **Struktur, wo sie hilft.** Drei oder mehr gleichrangige Punkte werden zur nummerierten Liste; alles andere bleibt normaler Fließtext.
- **Antwortet dir nie.** Diktierte Prompts („kannst du erklären, warum …“) werden bereinigt, nicht beantwortet oder ausgeführt.
- **Deine Worte, deine Sprachen.** Änderungen bleiben minimal: Wortwahl, Reihenfolge und Ton bleiben deine. Wenn du Chinesisch und Englisch mischst, bleibt jedes Wort in der Sprache, in der du es gesagt hast, auch Alltagswörter („shortcut“, „dark mode“), mit Leerzeichen zwischen CJK- und lateinischem Text.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="Verlauf: der bereinigte Text über dem Rohtranskript, mit der Dauer jedes Schritts">
</p>

Der Prompt ist auf Entwicklungs- und zurückgehaltenen Testsets abgestimmt (siehe [eval/](eval/)), darunter echte Diktate.

## Funktionen

- **Globales Tastenkürzel.** Standardmäßig **Fn** (macOS) oder **rechte Strg-Taste** (Windows) halten, oder eine beliebige einzelne Sondertaste (rechte ⌘, rechte ⌥, rechte Alt …) oder eine Kombination (⌥ Leertaste, Alt + Leertaste, F5 …) aufnehmen.
- **Halten zum Sprechen oder freihändig.** Halten zum Sprechen; einmal tippen, um freihändig weiter aufzunehmen, und nochmal tippen zum Beenden. **Esc** bricht ab. Nach 10 Sekunden Sprechen geht ein abgebrochenes Diktat nicht verloren: Es wird transkribiert (nicht eingefügt) und 24 Stunden im Verlauf aufbewahrt.
- **Mikrofon wählen** unter **Einstellungen → Allgemein**, mit einer Live-Pegelanzeige, die zeigt, ob es dich hört. Virtuelle Geräte (Meeting- und Streaming-Apps) sind markiert, und ist das gewählte getrennt, wird der Systemstandard verwendet.
- **Mikrofon bereithalten** (optional): Die Aufnahme beginnt, sobald du die Taste drückst, und enthält den Moment davor, damit das erste Wort nicht abgeschnitten wird. Das Mikrofon bleibt an, und Bluetooth-Kopfhörer wechseln in den Anrufmodus.
- **Live-Vorschau (Beta)**: Sieh die Wörter beim Sprechen über der Aufnahmekapsel, erkannt auf dem Gerät (SpeechAnalyzer unter macOS; Windows-Spracherkennung). Der eingefügte Text kommt weiterhin von deinem Anbieter.
- **Fügt am Cursor ein.** In einem Textfeld wird der Text eingefügt und deine Zwischenablage wiederhergestellt; ohne aktives Textfeld landet er in der Zwischenablage. Browser und Electron-Apps funktionieren auch, unter Windows außerdem Terminals.
- **Eigener Anbieter.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek oder jeder OpenAI-kompatible Endpunkt. Spracherkennung, Bereinigung und das Backup-Bereinigungsmodell können jeweils einen anderen Anbieter nutzen. **Testen** prüft einen Schlüssel und zeigt die Round-Trip-Latenz des Anbieters (Median aus drei).
- **Ersatz-Spracherkennung** (optional): Braucht ein Abschnitt deutlich länger, als diese Route für seine Länge sonst braucht, oder schlägt fehl, wird zusätzlich ein zweiter Anbieter gefragt, und die erste Antwort gewinnt.
- **Eigenes Vokabular und Stilvorlieben** für Namen, Produkte und Fachbegriffe.
- **Lernt aus deinen Korrekturen.** Korrigierst du nach dem Einfügen ein falsch erkanntes Wort (TypeList → Typeless), wird es automatisch deinem Vokabular hinzugefügt, samt der Art, wie es falsch verstanden wurde. Gelernt werden nur ähnlich klingende Korrekturen, nie Umformulierungen, geänderte Zahlen oder normale Wortwechsel, und ein Wort, das du entfernst, wird nie wieder gelernt.
- **Startseite** mit dem, was die Spracheingabe für dich getan hat: diktierte Wörter, gesparte Zeit gegenüber dem Tippen (standardmäßig 100 Wörter/Min., einstellbar), dein Sprechtempo, die Kosten von heute, diesem Monat und insgesamt, und eine Aktivitäts-Heatmap im GitHub-Stil mit Serien. Sie öffnet sich, wenn du die App selbst startest; bei der Anmeldung bleibt sie im Hintergrund, außer du aktivierst **Startseite bei der Anmeldung anzeigen**.
- **Wisse, was du ausgibst.** OpenRouter-Anfragen zählen genau das, was OpenRouter dafür berechnet hat, und die aktuelle Preisliste steht neben jedem Modell. Für andere Anbieter oder eigene Endpunkte trägst du den Preis des Modells unter **Modelle** ein (pro Million Tokens, bei der Spracherkennung pro Minute Audio).
- **Verlauf** jedes Diktats, nach Tagen gruppiert und durchsuchbar, mit Roh- und bereinigtem Text, Zeiten, Kosten, Kopieren und Neu-Transkribieren. Du bestimmst, wie lange Aufnahmen behalten werden: gar nicht, einen Tag, eine Woche, einen Monat, ein Jahr oder für immer.
- **Neun UI-Sprachen**: English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch und Русский. Die App folgt der Systemsprache, oder du wählst eine unter **Einstellungen → Allgemein → Sprache**.
- **Natives Design.** Liquid Glass ab macOS 26 mit einem Menüleisten-Panel; Mica und Acrylic unter Windows 11 mit einem Panel im Infobereich. Beide zeigen beim Sprechen eine kleine Aufnahme-Kapsel.
- **Start bei der Anmeldung.**
- **Aktualisiert sich selbst.** Prüft einmal täglich GitHub Releases, lädt eine neue Version im Hintergrund und installiert sie, wenn du auf **Neu starten und aktualisieren** klickst, nie mitten in einem Diktat. Downloads werden vor jedem Austausch gegen GitHubs SHA-256 geprüft. Abschalten oder manuell prüfen kannst du unter **Einstellungen → Allgemein → Updates**.
- **Klein und nativ.** Eine ca. 3 MB große Swift/SwiftUI-App auf macOS und eine eigenständige WinUI-3-App auf Windows. Kein Electron, kein Konto und kein eigener Server.

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="Modelle: ein Anbieter und ein Modell für jeden Schritt, dazu ein Backup-Bereinigungsmodell"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="Vokabular: deine Begriffe, auch die aus deinen Korrekturen gelernten"></td>
  </tr>
  <tr>
    <td align="center">Anbieter und Modell für jeden Schritt, mit Backup-Bereinigungsmodell</td>
    <td align="center">Vokabular, auch mit aus deinen Korrekturen gelernten Wörtern</td>
  </tr>
</table>


## Voraussetzungen

- **macOS:** macOS 26 oder neuer, Apple Silicon.
- **Windows:** Windows 10 (Version 2004 oder neuer) oder Windows 11, x64 oder ARM64.
- Ein API-Schlüssel für mindestens einen Anbieter ([OpenRouter](https://openrouter.ai/keys) ist am einfachsten: ein Schlüssel deckt beide Schritte ab).

## Installation auf macOS

### Download

1. Lade `OpenTypeless-<version>-macOS-arm64.zip` von [Releases](https://github.com/Tyler913/OpenTypeless/releases) herunter und entpacke es.
2. Verschiebe **OpenTypeless.app** in deinen Ordner **Programme**.
3. Die App ist nicht von Apple notarisiert (dafür braucht man ein kostenpflichtiges Entwicklerkonto), daher blockiert macOS sie beim ersten Start und meldet eventuell sogar, sie sei „beschädigt und kann nicht geöffnet werden“. Entferne einmalig im Terminal die Quarantäne-Markierung des Downloads:

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   Danach öffnest du sie ganz normal:

   ```bash
   open /Applications/OpenTypeless.app
   ```

   Alternativ versuchst du einmal, sie zu öffnen, gehst dann zu **Systemeinstellungen → Datenschutz & Sicherheit** und klickst auf **Dennoch öffnen**.

OpenTypeless lebt in der Menüleiste (Wellenform-Symbol), nicht im Dock.

Spätere Versionen werden direkt in der App installiert (**Einstellungen → Allgemein → Updates**), ohne Terminal: macOS fragt nur bei Apps nach, die über einen Browser geladen wurden.

### Aus dem Quellcode bauen

Xcode 26+ muss installiert sein (die Command Line Tools reichen zum Kompilieren, aber der Build leiht sich das SwiftUI-Makro-Plug-in aus `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # optional, aber empfohlen, einmalig
scripts/build-app.sh             # baut, signiert und installiert /Applications/OpenTypeless.app
```

`create-signing-cert.sh` erstellt eine lokale Codesignatur-Identität. Ohne sie wird die App ad hoc signiert, und macOS fragt nach jedem neuen Build erneut nach den Berechtigungen für Bedienungshilfen und Mikrofon.

Für ein Release-Zip statt einer Installation: `scripts/build-app.sh --package` schreibt `macos/dist/OpenTypeless-<version>-macOS-arm64.zip` (ad hoc signiert) und gibt dessen SHA-256 aus.

`build-app.sh` sorgt dafür, dass genau eine Kopie der App auf dem Rechner liegt. Es baut das Bundle in einem versteckten Staging-Ordner zusammen, verschiebt es nach `/Applications`, meldet veraltete Kopien bei LaunchServices ab und löscht veraltete Datenschutzeinträge, wenn sich die Signatur ändert.

### Erster Start

1. Erlaube den Zugriff auf **Mikrofon** und **Bedienungshilfen** (Bedienungshilfen werden genutzt, um das Tastenkürzel zu erkennen und Text einzufügen).
2. Füge unter **Einstellungen → Anbieter** einen API-Schlüssel hinzu.
3. Empfohlen, wenn du Fn nutzt: Stelle **Systemeinstellungen → Tastatur → „🌐-Taste drücken für“** auf **Keine Aktion**, damit ein Tippen auf Fn nicht die Emoji-Auswahl öffnet.

## Installation auf Windows

### Download

1. Lade `OpenTypeless-<version>-windows-x64.zip` (oder `-arm64`) von [Releases](https://github.com/Tyler913/OpenTypeless/releases) herunter und entpacke es an einen beliebigen Ort (z. B. `%LOCALAPPDATA%\Programs`).
2. Starte **OpenTypeless.exe**. Die App ist eigenständig: Es muss nichts weiter installiert werden.
3. Die App ist nicht codesigniert, daher meldet SmartScreen eventuell „Der Computer wurde durch Windows geschützt“: Klicke auf **Weitere Informationen → Trotzdem ausführen**.

Spätere Versionen werden direkt in der App (**Einstellungen → Allgemein → Updates**) in denselben Ordner installiert, ohne SmartScreen-Abfrage. Entpacke sie an einen Ort mit Schreibrechten, etwa `%LOCALAPPDATA%\Programs`; unter `Program Files` kann die App dich nur zum Download verweisen.

OpenTypeless lebt im **Infobereich** (Wellenform-Symbol neben der Uhr). Windows versteckt neue Symbole anfangs im Überlaufbereich (^); zieh es auf die Taskleiste oder schalte es unter **Einstellungen → Personalisierung → Taskleiste → Andere Infobereichssymbole** ein.

### Aus dem Quellcode bauen

Benötigt das [.NET 10 SDK](https://dotnet.microsoft.com/download). Visual Studio ist optional.

```powershell
# aus windows\ in einem Klon dieses Repositorys
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # testet, baut und installiert nach %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # schreibt windows\dist\OpenTypeless-<version>-windows-x64.zip
```

`build.ps1` sorgt für genau eine installierte Kopie: Es beendet die laufende App, ersetzt den Installationsordner, aktualisiert die Startmenü-Verknüpfung und startet den neuen Build. Für Windows auf ARM `-Arch arm64` anhängen.

Für die Entwicklung der Windows-App brauchst du keinen Windows-PC: GitHub Actions baut sie bei jeder Änderung (siehe [Continuous Integration](#continuous-integration)).

### Erster Start

1. Füge unter **Einstellungen → Anbieter** einen API-Schlüssel hinzu.
2. Stelle sicher, dass **Einstellungen → Datenschutz und Sicherheit → Mikrofon → Desktop-Apps den Zugriff auf Ihr Mikrofon erlauben** eingeschaltet ist.
3. Halte die rechte Strg-Taste und sprich. Windows braucht keine Bedienungshilfen-Berechtigung; die einzige Grenze ist, dass es kein Einfügen in Apps erlaubt, die als Administrator laufen. Dort landet der Text in der Zwischenablage.

## Standardmodelle

| Schritt | Standard | Hinweise |
|---|---|---|
| Spracherkennung | `microsoft/mai-transcribe-2` (OpenRouter) | Jedes Transkriptionsmodell von OpenRouter oder ein Whisper-kompatibles `/audio/transcriptions` anderswo. |
| Bereinigung | `google/gemini-3.8-flash` (OpenRouter) | Die beste Bereinigung in unseren Tests, für etwa 0,005 $ pro langem Diktat. Günstigere Alternativen: `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| Backup-Bereinigung | `deepseek/deepseek-v4.1-flash` (OpenRouter) | Wird nur gefragt, wenn das Hauptmodell langsam startet oder fehlschlägt. Wähle ein schnelles Modell eines anderen Herstellers. |

Das Reasoning des Bereinigungsmodells wird automatisch abgeschaltet oder minimiert, damit die Latenz niedrig bleibt.

## Datenschutz

- Audio und Text werden nur an die Anbieter gesendet, die du einrichtest. Mit eingeschalteter **Live-Vorschau** erkennt macOS die Sprache auf dem Mac; Windows nutzt seine eigene Spracherkennung, die deine Stimme an Microsoft sendet, wenn die Online-Spracherkennung aktiviert ist (die Einstellung weist darauf hin).
- API-Schlüssel liegen im macOS-Schlüsselbund oder in der Windows-Anmeldeinformationsverwaltung (ein Eintrag, `OpenTypeless/credentials`).
- Die Nutzungssummen für die Startseite (Wörter, Sprechzeit und Kosten pro Tag, kein Text) liegen in `usage.json` im selben Ordner wie Einstellungen und Verlauf. Die OpenRouter-Preisliste wird ein paar Mal am Tag aus der öffentlichen Modellliste geladen (ohne Schlüssel, nichts über dich).
- Der Verlauf (Audio + Transkripte) liegt unter macOS in `~/Library/Application Support/OpenTypeless/Sessions/` und unter Windows in `%LOCALAPPDATA%\OpenTypeless\`. Aufnahmen werden standardmäßig einen Monat behalten (Seite „Verlauf“: gar nicht, einen Tag, eine Woche, einen Monat, ein Jahr oder für immer); danach bleibt der Text unter den neuesten 200 Einträgen. Fehlgeschlagene Diktate behalten ihr Audio, damit sie wiederholt werden können.
- Die Update-Prüfung sendet einmal täglich eine Anfrage an `api.github.com` (ohne Konto, nichts über dich oder deine Diktate); abschalten kannst du sie unter **Einstellungen → Allgemein → Updates**.
- Das Lernen aus deinen Korrekturen liest das Textfeld, in das du diktiert hast, nur auf deinem Computer und höchstens zwei Minuten nach dem Einfügen. Passwortfelder werden übersprungen. Es lässt sich unter **Vokabular & Stil** abschalten.

## Entwicklung

Das Repository enthält beide Apps. Sie teilen sich das Design, die Evaluierungssets und diese README; jede hat ihren eigenen Code, eigene Tests und Build-Skripte.

```
macos/                      Die macOS-App (Swift Package)
  Sources/TypelessCore/       Verarbeitungslogik ohne UI: Zerteilung, WAV, Anbieter-Client, Wiederholungen, Bereinigungs-Prompt
  Sources/OpenTypeless/       Die App: Tastenkürzel, Aufnahme, HUD, Einstellungen, Verlauf, Einfügen, CLI-Werkzeuge
  Tests/                      swift-testing-Tests
  scripts/                    Skripte für Build, Signatur, Symbol und Tests
windows/                    Die Windows-App (.NET-Projektmappe)
  src/TypelessCore/           Dieselbe Verarbeitungslogik, Zeile für Zeile portiert
  src/OpenTypeless/           Die WinUI-App: Tastatur-Hook, WASAPI-Aufnahme, HUD, Infobereich, Einstellungen, Verlauf, Einfügen
  src/OpenTypeless.Cli/       Kommandozeilenwerkzeuge (Datei transkribieren, Zerteilungsanalyse, Prompt-Evaluierung)
  tests/                      xUnit-Tests
  scripts/                    Skripte für Build, Tests und Symbol
eval/                       Bereinigungs-Testsets und der Evaluierungsleitfaden, von beiden Apps genutzt
i18n/                       UI-Übersetzungen (außer Chinesisch und Englisch), von beiden Apps genutzt
docs/DESIGN.md              Architektur und Designentscheidungen
docs/WINDOWS-PORT.md        Wie jede macOS-Datei und System-API unter Windows abgebildet wird
docs/images/                README-Screenshots
```

Der Bereinigungs-Prompt (`Prompts.swift` / `Prompts.cs`) ist in beiden Apps byte-identisch; ändere beide gemeinsam und prüfe das Ergebnis mit [eval/](eval/).

### Übersetzungen

Jeder sichtbare Text steht direkt im Code, mit chinesischer und englischer Fassung: `L("有新版本 \(version)", "Version \(version) is available")` in Swift, `L($"有新版本 {version}", $"Version {version} is available")` in C#. Die übrigen Sprachen liegen in [`i18n/strings.json`](i18n/strings.json), mit dem englischen Text als Schlüssel und jeder Interpolation der Reihe nach nummeriert:

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

Beide Apps betten die Datei beim Build ein, und ein Text ohne Übersetzung erscheint auf Englisch. Eine Übersetzung darf die Platzhalter umstellen, muss aber jeden davon behalten. Nachdem du einen Text hinzugefügt oder geändert hast, ergänze die Übersetzungen und führe `python3 i18n/check.py` aus: Es listet fehlende, ungenutzte und fehlerhafte Einträge auf, und die CI führt es bei jedem Pull Request aus. Um eine Sprache im Zusammenhang zu prüfen, rendere die UI mit `--snapshot-ui` und `--lang de` (oder einem anderen Sprachcode).

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # Unit-Tests, einschließlich eines Mock-Servers, der Fehler in eine 130-s-Aufnahme einschleust
```

Nützliche Kommandozeilenmodi des gebauten Programms (aus `macos/`):

```bash
# Komplette Verarbeitung einer Audiodatei; --realtime liefert das Audio in Sprechgeschwindigkeit wie ein echtes Mikrofon
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Zeigt, wo die Zerteilung schneidet
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Bereinigungs-Prompts und -Modelle evaluieren (siehe eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Einstellungsseiten, Menüleisten-Panel und HUD als PNG rendern (--live zeigt sie auf dem Bildschirm, mit echtem Liquid Glass).
# Die README-Screenshots nutzen einen Beispielverlauf und --demo, das Berechtigungen als erteilt behandelt.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # Unit-Tests, einschließlich eines Mock-Servers, der Fehler in eine 130-s-Aufnahme einschleust
```

Kommandozeilenwerkzeuge (`OpenTypeless.Cli.exe`, wird mit der App ausgeliefert und nutzt deren Einstellungen und Schlüssel):

```powershell
# Komplette Verarbeitung einer Audiodatei (WAV, MP3, M4A, WMA, FLAC …); --realtime liefert das Audio in Sprechgeschwindigkeit
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# Zeigt, wo die Zerteilung schneidet
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# Bereinigungs-Prompts und -Modelle evaluieren (siehe eval/README.md)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# Jede Einstellungsseite, das Infobereich-Panel und die HUD-Zustände als PNG rendern
# (--lang en|zh|ja|… für nur eine Sprache, --demo behandelt Berechtigungen als erteilt; mit OPENTYPELESS_DATA_DIR und Beispielverlauf kombinieren)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Umgebungsvariablen für die Entwicklung (Windows):

| Variable | Wirkung |
|---|---|
| `OPENROUTER_API_KEY` | Überschreibt den gespeicherten OpenRouter-Schlüssel. |
| `OPENTYPELESS_DATA_DIR` | Nutzt einen anderen Ordner für Einstellungen und Verlauf (praktisch für Tests). |
| `OPENTYPELESS_DEBUG` | Schreibt Tastenkürzel-, Sitzungs- und Fokus-Ereignisse in `debug.log` im Datenordner. |
| `OPENTYPELESS_TEST_AUDIO` | Spielt statt des Mikrofons ein 16-kHz-Mono-WAV in Echtzeit ein, für End-to-End-Tests. |

### Mitmachen

`main` ist geschützt: Jede Änderung läuft über einen Pull Request. Arbeite auf einem Branch, öffne einen Pull Request nach `main` und merge ihn, sobald die Prüfung **CI passed** grün ist.

### Continuous Integration

GitHub Actions ([`.github/workflows/`](.github/workflows/)) baut nur die App, die du geändert hast:

| Du änderst | Was läuft |
|---|---|
| `macos/**` | **macOS build** auf einem macOS-Runner: die Tests, dann das App-Zip. |
| `windows/**` | **Windows build** auf einem Windows-Runner: die Tests, dann die x64- und ARM64-Zips. |
| `testdata/**` | Beide Builds: die gemeinsamen Testfälle (Wortzahlen, Preise, gesparte Zeit), die beide Testsuiten lesen, damit beide Apps übereinstimmen. |
| `i18n/**` | Beide Builds: die Übersetzungen, die beide Apps einbetten. |
| Nur `docs/`, `eval/`, `README*.md` | Nichts zu bauen. |

Jeder Lauf prüft außerdem die Übersetzungen (**Translations**, `python3 i18n/check.py`).

Die Zips findest du im Abschnitt **Artifacts** eines Laufs im Actions-Tab. **Actions → macOS build / Windows build → Run workflow** startet einen Build von Hand.

Für ein Release erhöhst du die Version in beiden Apps (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) und pushst ein Tag, `1.0.2` (oder `V1.0.2`). Das baut beide Apps und legt ein **Entwurfs**-Release „OpenTypeless V1.0.2“ an, mit dem macOS-Zip (mit dem Release-Zertifikat signiert) und den Windows-Zips für x64 und ARM64. Der Build schlägt fehl, wenn die Version eines Zips nicht zum Tag passt.

Prüfe den Entwurf und veröffentliche ihn von Hand; dann ist er das neueste Release. Installierte Kopien finden es innerhalb eines Tages: Der Updater in der App sucht das neueste veröffentlichte Release, das kein Vorab-Release ist und ein Zip für seine Plattform enthält (`OpenTypeless-<version>-macOS-arm64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`). Markiere ein Release als Vorab-Release, damit es nicht angeboten wird.

**macOS-Release-Signatur (einmalig).** macOS bindet die Berechtigungen für Bedienungshilfen und Mikrofon an die Signatur der App. Releases sollten deshalb immer mit demselben Zertifikat signiert werden, sonst müssen Nutzer nach jedem Update beide Berechtigungen neu erteilen. Führe `macos/scripts/create-release-cert.sh` aus, bewahre die erzeugte `.p12` an einem privaten Ort auf und lege die beiden ausgegebenen Repository-Secrets an (`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`). Release-Builds werden dann damit signiert; ohne die Secrets werden sie ad hoc signiert, mit einer Warnung im Lauf.

## Danksagung

Inspiriert von Typeless. Der Ansatz für die Systemintegration stammt aus den Open-Source-Projekten [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) und [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Dieses Projekt ist unabhängig und mit keinem davon verbunden.

## Lizenz

[MIT](LICENSE)
