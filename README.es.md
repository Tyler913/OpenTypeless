<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <b>Español</b> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Mantén pulsada una tecla, habla y obtén texto limpio en el cursor.</b><br>
  Dictado nativo para macOS y Windows, pensado para dictados largos.
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>Descargar</b></a> · <a href="#instalar">Instalar</a> · <a href="docs/DEVELOPMENT.md">Compilar desde el código</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="La cápsula de grabación, con tus palabras en vista previa encima">
</p>

## Por qué OpenTypeless

La mayoría de las apps de dictado van bien con una frase y fallan con dos minutos seguidos de habla: tiempos agotados, resultados vacíos, texto perdido. OpenTypeless está hecho justo para eso.

- **Habla todo lo que quieras.** El audio se corta en tus pausas y se transcribe mientras hablas, así que hasta un dictado largo está listo un momento después de soltar la tecla.
- **No se pierde ni una palabra.** Cada fragmento se reintenta por separado, un modelo lento tiene respaldo y cada grabación se guarda para poder reintentarla.
- **Como si lo hubieras escrito.** Desaparecen las muletillas y los titubeos, «miércoles, no, jueves» queda en jueves y las listas salen como listas. Tus palabras, cifras e idiomas se mantienen.
- **Usa tu propia clave.** OpenRouter, OpenAI, Groq, Gemini, Anthropic, DeepSeek y más, o modelos locales con Ollama y LM Studio. Sin cuenta y sin servidor propio.

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="El panel de la barra de menús: cómo dictar, el gasto del mes y los dictados recientes">
</p>

## Funciones

- **Pulsar para hablar o manos libres.** Mantén **Fn** (macOS) o **Alt derecha** (Windows), o toca una vez para seguir hablando. Sirve cualquier tecla o combinación.
- **Traduce mientras hablas.** Añade **Mayús** y tus palabras salen en el idioma que elegiste.
- **Edita con la voz.** Selecciona texto, añade **Ctrl** y di «hazlo más formal» o «conviértelo en una lista».
- **Un vocabulario que aprende.** Añade nombres y jerga; las palabras que corriges tras pegar se aprenden solas.
- **Fragmentos.** Di «mi correo» y entra el texto que guardaste, palabra por palabra.
- **Ajustes por app.** Pulsar Intro tras insertar en Slack, tono formal en Mail, sin limpieza en Terminal.
- **Historial y archivos.** Cada dictado se puede buscar y exportar, y también puedes transcribir archivos de audio o vídeo.
- **Controla el gasto.** Palabras, tiempo ahorrado y coste en Inicio, con un límite mensual opcional.
- **Vista previa en directo (beta).** Ve tus palabras sobre la cápsula mientras hablas.
- **Nueve idiomas, actualizaciones automáticas.** English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский.

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="Inicio: palabras, tiempo ahorrado, gasto y actividad"><br><sub>Inicio: palabras, tiempo ahorrado, gasto y actividad</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="Historial: cada dictado, con búsqueda"><br><sub>Historial: cada dictado, con búsqueda</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="Modelos: un proveedor y un modelo para cada paso"><br><sub>Modelos: un proveedor y un modelo para cada paso</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="Vocabulario, con las palabras aprendidas de tus correcciones"><br><sub>Vocabulario, con las palabras aprendidas de tus correcciones</sub></td>
  </tr>
</table>

<sub>Las capturas son de macOS. Windows tiene las mismas páginas, con su propio aspecto nativo.</sub>

## Instalar

### macOS

Requiere macOS 26 o posterior, con Apple silicon o Intel.

```bash
brew install --cask tyler913/tap/opentypeless
```

O descarga el `.dmg` desde [Releases](https://github.com/Tyler913/OpenTypeless/releases) (`arm64` para Apple silicon, `x64` para Intel) y arrastra la app a Aplicaciones. No está notarizada por Apple, así que si macOS dice que no se puede abrir o que «está dañada», ejecuta esto una vez:

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Requiere Windows 10 (2004 o posterior) o Windows 11, x64 o ARM64.

Descarga `OpenTypeless-<version>-windows-x64-setup.exe` (`arm64` para Windows en ARM) desde [Releases](https://github.com/Tyler913/OpenTypeless/releases) y ejecútalo; no hacen falta permisos de administrador. Si aparece SmartScreen, pulsa **Más información → Ejecutar de todas formas**. El `.zip` es una versión portable.

Después, la app se actualiza sola.

## Primeros pasos

1. Abre OpenTypeless. Vive en la barra de menús (macOS) o en el área de notificación (Windows).
2. La guía de bienvenida te pide una clave de API. [OpenRouter](https://openrouter.ai/keys) es lo más fácil: una clave sirve para todo.
3. En macOS, permite el acceso al **Micrófono** y a **Accesibilidad** cuando se te pida.
4. Mantén el atajo, habla y suelta. El texto aparece en el cursor.

> En macOS, pon **Ajustes del Sistema → Teclado → «Pulsar la tecla 🌐 para»** en **No hacer nada**, para que tocar Fn no abra el selector de emojis.

## Privacidad

- El audio y el texto solo van a los proveedores que configures. Aparte de eso, la app solo busca actualizaciones en GitHub una vez al día y descarga la lista pública de precios de OpenRouter. La vista previa en directo de Windows usa el reconocimiento de voz de Windows, que sigue su propio ajuste en línea.
- Las claves, los ajustes y el historial se quedan en tu ordenador. Las grabaciones se guardan un mes por defecto; tú eliges cuánto.
- Los campos de contraseña nunca se leen. Usar el texto junto al cursor para escribir bien los nombres está desactivado salvo que lo actives.

## Desarrollo

Dos apps nativas en un repositorio: [`macos/`](macos) en Swift / SwiftUI y [`windows/`](windows) en C# / WinUI 3, con el mismo proceso, el mismo prompt de limpieza y los mismos ajustes. Consulta [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) para compilarlas y probarlas, y [docs/DESIGN.md](docs/DESIGN.md) para saber cómo funcionan.

## Agradecimientos

Inspirado en Typeless. El enfoque de integración con el sistema se aprendió de los proyectos libres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) y [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Este proyecto es independiente y no está afiliado a ninguno de ellos.

## Licencia

[MIT](LICENSE)
