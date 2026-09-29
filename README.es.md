<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <b>Español</b> · <a href="README.pt-BR.md">Português</a> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="Icono de OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Dictado por voz nativo para macOS y Windows que no se rompe con dictados largos.</b><br>
  Mantén una tecla, habla todo lo que necesites y obtén texto limpio y estructurado en el cursor.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="La cápsula de grabación que aparece mientras hablas">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="El panel de la barra de menús: aviso del atajo y dictados recientes">
</p>

<p align="center">
  Dos apps nativas, un mismo diseño: <b>macOS</b> (Swift / SwiftUI) y <b>Windows</b> (C# / WinUI 3).<br>
  El mismo proceso, el mismo prompt de limpieza, los mismos ajustes y el mismo manejo de errores; solo cambian la integración con el sistema y el aspecto.
</p>

---

## Por qué existe

Dictar es la forma más rápida de escribir prompts largos para herramientas de IA. Eso también significa dictados largos: pensar en voz alta durante uno a tres minutos es lo normal.

La mayoría de las apps de dictado, libres y comerciales, funcionan bien con una frase y **fallan justo con esos dictados largos**. Grabar 40 s, 1 min o 2 min termina en un tiempo de espera agotado, un resultado vacío o texto perdido. Al leer el código de varias alternativas libres aparecían una y otra vez las mismas causas:

- **Toda la grabación se envía como una sola solicitud de voz a texto.** Los proveedores cortan tras unos 60 s de procesamiento (OpenRouter lo documenta explícitamente), así que cuanto más hablas, más probable es que la solicitud muera.
- **La llamada al LLM de limpieza tiene un tiempo *total* corto.** Un límite de 30 s que incluye el streaming corta una respuesta larga a la mitad.
- **Un fallo lo tira todo.** Dos minutos de habla desaparecen y tienes que repetirlo todo.

OpenTypeless está construido alrededor de ese problema.

## Cómo maneja los dictados largos

| | Qué pasa |
|---|---|
| **Corta en las pausas** | Mientras hablas, el audio se corta en segmentos de 18–28 s en la ventana de 0,4 s más silenciosa de cada tramo, así que nunca se parten palabras y ninguna solicitud se acerca al límite de 60 s del proveedor. |
| **Transcribe mientras hablas** | Cada segmento se transcribe en segundo plano en cuanto se corta. Tras un dictado de dos minutos, al soltar la tecla solo quedan por procesar los últimos segundos. |
| **Reintenta por segmento** | Las caídas de red, los 429 y los errores 5xx se reintentan con espera progresiva. Las claves incorrectas y los problemas de pago fallan de inmediato. Un segmento fallido nunca afecta a los demás, y los segmentos fallidos tienen una ronda completa más al final. |
| **Modelo de respaldo si tarda en arrancar** | Si el modelo de limpieza no ha empezado a responder en 0,8 s, o falla, se consulta también a un modelo de respaldo de otro fabricante y gana el que responda primero. Un proveedor lento cuesta menos de un segundo extra, no toda la espera. |
| **Tiempo de inactividad, no tiempo total** | La limpieza recibe su respuesta en streaming y solo se considera atascada cuando no llega *ningún* dato durante 25 s, así que las salidas largas nunca se cortan. |
| **Nunca pierde palabras** | El audio se escribe en disco mientras hablas. Cada dictado se guarda en el historial; uno fallido se puede reintentar después y solo se reenvían sus segmentos fallidos. Si la limpieza falla, se inserta la transcripción original. |

Un dictado sintético de 121 s, por ejemplo, se divide en 6 segmentos en pausas naturales. Cuando quien habla se detiene, 5 de ellos ya están transcritos.

## Una limpieza que parece escrita por ti

La transcripción en bruto es desordenada: muletillas, frases que se reinician, «no, espera, quiero decir…», pensar en voz alta. La limpieza lo convierte en lo que habrías escrito:

- **Las autocorrecciones se resuelven.** Gana la versión final («el miércoles, no, el jueves» → jueves). Eso incluye correcciones hechas mucho después, correcciones implícitas («50 000, eh, 60 000 por si acaso») y puntos retirados por completo («…el tercero, olvídalo»).
- **Se eliminan las muletillas y el pensar en voz alta.** um / uh / 嗯 / 那个 / «déjame pensar» / «y eso es todo» desaparecen.
- **No se pierde nada real.** Números, versiones, nombres y comparaciones se mantienen exactos. Al modelo se le dice que los productos más nuevos que él existen de verdad, así que «Gemini 3.5» nunca se convierte en «Gemini 2.5».
- **Estructura cuando ayuda.** Tres o más puntos paralelos se convierten en una lista numerada; todo lo demás queda como párrafos normales.
- **Nunca te responde.** Los prompts dictados («¿puedes explicar por qué…?») se limpian, no se responden ni se ejecutan.
- **Tus palabras, tus idiomas.** Los cambios son mínimos: la redacción, el orden y el tono siguen siendo tuyos. Si mezclas chino e inglés, cada palabra se queda en el idioma en que la dijiste, también las cotidianas («shortcut», «dark mode»), con espacio entre el texto CJK y el latino.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="Historial: el texto limpio sobre la transcripción original, con el tiempo de cada paso">
</p>

El prompt está ajustado con conjuntos de desarrollo y de prueba reservados (ver [eval/](eval/)), que incluyen dictados reales.

## Funciones

- **Atajo global.** Mantén **Fn** (macOS) o **Ctrl derecha** (Windows) por defecto, o graba cualquier modificador suelto (⌘ derecha, ⌥ derecha, Alt derecha…) o una combinación (⌥ Espacio, Alt + Espacio, F5…).
- **Mantener para hablar o manos libres.** Mantén para hablar; toca una vez para seguir grabando con las manos libres y vuelve a tocar para terminar. **Esc** cancela. Si ya llevas 10 segundos hablando, un dictado cancelado no se pierde: se transcribe (sin insertarse) y se guarda en el historial 24 horas.
- **Elige tu micrófono** en **Ajustes → General**, con un medidor de nivel en directo para comprobar que te oye. Los dispositivos virtuales (apps de reuniones y streaming) aparecen marcados, y si el elegido se desconecta se usa el predeterminado del sistema.
- **Mantener el micrófono listo** (opcional): la grabación empieza en cuanto pulsas la tecla e incluye el instante anterior, así que la primera palabra no se corta. El micrófono queda encendido y los auriculares Bluetooth pasan al modo llamada.
- **Vista previa en directo (beta)**: ve las palabras encima de la cápsula de grabación mientras hablas, reconocidas en el dispositivo (SpeechAnalyzer en macOS; reconocimiento de voz de Windows). El texto insertado sigue viniendo de tu proveedor.
- **Pega donde está el cursor.** En un campo de texto se pega el texto y se restaura tu portapapeles; sin un campo de texto enfocado va al portapapeles. También funciona en navegadores y apps de Electron, y en terminales en Windows.
- **Trae tu propio proveedor.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek o cualquier endpoint compatible con OpenAI. La voz a texto, la limpieza y el modelo de limpieza de respaldo pueden usar cada uno un proveedor distinto. **Probar** comprueba una clave y muestra la latencia de ida y vuelta del proveedor (mediana de tres).
- **Voz a texto de respaldo** (opcional): cuando un fragmento tarda mucho más de lo que esa ruta suele necesitar para su duración, o falla, también se pide a un segundo proveedor y gana la primera respuesta.
- **Vocabulario personalizado y preferencias de estilo** para nombres, productos y jerga.
- **Aprende de tus correcciones.** Corrige una palabra mal reconocida después de pegarla (TypeList → Typeless) y se añade automáticamente a tu vocabulario, junto con cómo se oyó mal. Solo se aprenden correcciones de sonido parecido, nunca reescrituras, números cambiados ni sustituciones de palabras corrientes, y una palabra que quites no se vuelve a aprender.
- **Página de inicio** con lo que el dictado ha hecho por ti: palabras dictadas, tiempo ahorrado frente a teclear (100 ppm por defecto, ajustable), tu velocidad al hablar, lo que ha costado hoy, este mes y en total, y un mapa de actividad al estilo de GitHub con rachas. Se abre cuando lanzas la app tú mismo; al iniciar sesión no molesta, salvo que actives **Mostrar Inicio al abrirse al iniciar sesión**.
- **Sabe lo que gastas.** Las solicitudes a OpenRouter cuentan exactamente lo que OpenRouter cobró, y su lista de precios en vivo aparece junto a cada modelo. Para cualquier otro proveedor o endpoint personalizado, introduce el precio del modelo en **Modelos** (por millón de tokens, o por minuto de audio para la voz a texto).
- **Historial** de cada dictado, agrupado por día y con búsqueda, con el texto original y el limpio, tiempos, coste, copiar y volver a transcribir. Elige cuánto tiempo se guardan las grabaciones: nada, un día, una semana, un mes, un año o para siempre.
- **Nueve idiomas de interfaz**: English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch y Русский. La app sigue el idioma del sistema, o elige uno en **Ajustes → General → Idioma**.
- **Diseño nativo.** Liquid Glass en macOS 26+ con un panel en la barra de menús; Mica y Acrylic en Windows 11 con un panel en la bandeja. Ambas muestran una pequeña cápsula de grabación mientras hablas.
- **Se abre al iniciar sesión.**
- **Se actualiza sola.** Busca en GitHub Releases una vez al día, descarga la nueva versión en segundo plano y la instala cuando pulsas **Reiniciar para actualizar**, nunca en mitad de un dictado. Las descargas se comprueban con el SHA-256 de GitHub antes de reemplazar nada. Desactívalo, o búscalas a mano, en **Ajustes → General → Actualizaciones**.
- **Pequeña y nativa.** Una app Swift/SwiftUI de ~3 MB en macOS y una app WinUI 3 autónoma en Windows. Sin Electron, sin cuenta y sin servidor propio.

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="Modelos: un proveedor y un modelo para cada paso, más un modelo de limpieza de respaldo"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="Vocabulario: tus términos, incluidos los aprendidos de tus correcciones"></td>
  </tr>
  <tr>
    <td align="center">Elige un proveedor y un modelo para cada paso, con un modelo de limpieza de respaldo</td>
    <td align="center">Vocabulario, incluidas las palabras aprendidas de tus correcciones</td>
  </tr>
</table>


## Requisitos

- **macOS:** macOS 26 o posterior, Apple Silicon.
- **Windows:** Windows 10 (versión 2004 o posterior) o Windows 11, x64 o ARM64.
- Una clave de API de al menos un proveedor ([OpenRouter](https://openrouter.ai/keys) es lo más fácil: una sola clave cubre ambos pasos).

## Instalar en macOS

### Descarga

1. Descarga `OpenTypeless-<version>-macOS-arm64.zip` desde [Releases](https://github.com/Tyler913/OpenTypeless/releases) y descomprímelo.
2. Mueve **OpenTypeless.app** a tu carpeta **Aplicaciones**.
3. La app no está notarizada por Apple (eso requiere una cuenta de desarrollador de pago), así que macOS la bloquea en el primer arranque e incluso puede decir que «está dañada y no se puede abrir». Quita una vez la marca de cuarentena de la descarga en Terminal:

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   Después ábrela con normalidad:

   ```bash
   open /Applications/OpenTypeless.app
   ```

   También puedes intentar abrirla una vez y luego ir a **Ajustes del Sistema → Privacidad y seguridad** y pulsar **Abrir igualmente**.

OpenTypeless vive en la barra de menús (icono de forma de onda), no en el Dock.

Las versiones siguientes se instalan desde la propia app (**Ajustes → General → Actualizaciones**), sin pasar por Terminal: macOS solo pregunta por apps descargadas con un navegador.

### Compilar desde el código

Necesita Xcode 26+ instalado (las Command Line Tools bastan para compilar, pero la compilación toma prestado el plugin de macros de SwiftUI de `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # opcional pero recomendado, una sola vez
scripts/build-app.sh             # compila, firma e instala /Applications/OpenTypeless.app
```

`create-signing-cert.sh` crea una identidad local de firma de código. Sin ella la app se firma ad hoc, y macOS vuelve a pedir los permisos de Accesibilidad y Micrófono tras cada recompilación.

Para crear un zip de publicación en lugar de instalar: `scripts/build-app.sh --package` escribe `macos/dist/OpenTypeless-<version>-macOS-arm64.zip` (firmado ad hoc) y muestra su SHA-256.

`build-app.sh` mantiene exactamente una copia de la app en el equipo. Monta el paquete en una carpeta temporal oculta, lo mueve a `/Applications`, da de baja las copias antiguas en LaunchServices y borra las entradas de privacidad obsoletas cuando cambia la firma.

### Primer arranque

1. Concede acceso al **Micrófono** y a **Accesibilidad** (Accesibilidad se usa para detectar el atajo y pegar texto).
2. Añade una clave de API en **Ajustes → Proveedores**.
3. Recomendado si usas Fn: pon **Ajustes del Sistema → Teclado → «Pulsar la tecla 🌐 para»** en **No hacer nada**, para que tocar Fn no abra el selector de emojis.

## Instalar en Windows

### Descarga

1. Descarga `OpenTypeless-<version>-windows-x64.zip` (o `-arm64`) desde [Releases](https://github.com/Tyler913/OpenTypeless/releases) y descomprímelo donde quieras (p. ej., `%LOCALAPPDATA%\Programs`).
2. Ejecuta **OpenTypeless.exe**. Es autónoma: no hay que instalar nada más.
3. La app no tiene firma de código, así que SmartScreen puede decir «Windows protegió su PC»: pulsa **Más información → Ejecutar de todas formas**.

Las versiones siguientes se instalan desde la propia app (**Ajustes → General → Actualizaciones**) en la misma carpeta, sin aviso de SmartScreen. Descomprímela en un lugar donde puedas escribir, como `%LOCALAPPDATA%\Programs`; en `Program Files` la app solo puede enlazarte a la descarga.

OpenTypeless vive en el **área de notificación** (icono de forma de onda junto al reloj). Al principio Windows oculta los iconos nuevos en el desbordamiento (^); arrástralo a la barra de tareas o actívalo en **Configuración → Personalización → Barra de tareas → Otros iconos de la bandeja del sistema**.

### Compilar desde el código

Necesita el [SDK de .NET 10](https://dotnet.microsoft.com/download). Visual Studio es opcional.

```powershell
# desde windows\ en un clon de este repositorio
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # prueba, compila e instala en %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # escribe windows\dist\OpenTypeless-<version>-windows-x64.zip
```

`build.ps1` mantiene exactamente una copia instalada: detiene la app en ejecución, reemplaza la carpeta de instalación, actualiza el acceso directo del menú Inicio y abre la nueva compilación. Añade `-Arch arm64` para Windows en ARM.

No hace falta un PC con Windows para desarrollar la app de Windows: GitHub Actions la compila con cada cambio (ver [Integración continua](#integración-continua)).

### Primer arranque

1. Añade una clave de API en **Ajustes → Proveedores**.
2. Comprueba que **Configuración → Privacidad y seguridad → Micrófono → Permitir que las aplicaciones de escritorio accedan al micrófono** está activado.
3. Mantén Ctrl derecha y habla. Windows no necesita permiso de accesibilidad; el único límite es que no permite pegar en apps que se ejecutan como administrador, así que ahí el texto va al portapapeles.

## Modelos por defecto

| Paso | Por defecto | Notas |
|---|---|---|
| Voz a texto | `microsoft/mai-transcribe-2` (OpenRouter) | Cualquier modelo de transcripción de OpenRouter, o un `/audio/transcriptions` compatible con Whisper en otro sitio. |
| Limpieza | `google/gemini-3.8-flash` (OpenRouter) | La mejor limpieza en nuestras pruebas, a unos 0,005 $ por dictado largo. Opciones más baratas: `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| Limpieza de respaldo | `deepseek/deepseek-v4.1-flash` (OpenRouter) | Solo se consulta cuando el modelo principal tarda en arrancar o falla. Elige un modelo rápido de otro fabricante. |

El razonamiento del modelo de limpieza se desactiva o se pone al mínimo automáticamente para mantener baja la latencia.

## Privacidad

- El audio y el texto solo se envían a los proveedores que configures. Con la **vista previa en directo** activada, macOS reconoce la voz en el Mac; Windows usa su propio reconocimiento de voz, que envía tu voz a Microsoft cuando el reconocimiento de voz en línea está activado (el ajuste lo indica).
- Las claves de API se guardan en el Llavero de macOS o en el Administrador de credenciales de Windows (una entrada, `OpenTypeless/credentials`).
- Los totales de uso de la página de inicio (palabras, tiempo hablado y coste por día, sin texto) se guardan en `usage.json`, en la misma carpeta que los ajustes y el historial. La lista de precios de OpenRouter se descarga de su lista pública de modelos (sin clave, nada sobre ti) unas cuantas veces al día.
- El historial (audio + transcripciones) está en `~/Library/Application Support/OpenTypeless/Sessions/` en macOS y en `%LOCALAPPDATA%\OpenTypeless\` en Windows. Las grabaciones se guardan un mes por defecto (en la página Historial: nada, un día, una semana, un mes, un año o para siempre); después, el texto sigue entre las 200 entradas más recientes. Los dictados fallidos conservan su audio para poder reintentarlos.
- La búsqueda de actualizaciones envía una solicitud al día a `api.github.com` (sin cuenta, nada sobre ti ni tus dictados); desactívala en **Ajustes → General → Actualizaciones**.
- Aprender de tus correcciones lee el campo de texto en el que dictaste, solo en tu ordenador, durante como mucho dos minutos tras pegar. Los campos de contraseña se omiten. Se puede desactivar en **Vocabulario y estilo**.

## Desarrollo

El repositorio contiene ambas apps. Comparten el diseño, los conjuntos de evaluación y este README; cada una tiene su propio código, pruebas y scripts de compilación.

```
macos/                      La app de macOS (Swift Package)
  Sources/TypelessCore/       Lógica del proceso, sin interfaz: troceado, WAV, cliente de proveedores, reintentos, prompt de limpieza
  Sources/OpenTypeless/       La app: atajo, grabación, HUD, ajustes, historial, pegado, herramientas CLI
  Tests/                      Pruebas con swift-testing
  scripts/                    Scripts de compilación, firma, icono y pruebas
windows/                    La app de Windows (solución .NET)
  src/TypelessCore/           La misma lógica del proceso, portada línea a línea
  src/OpenTypeless/           La app WinUI: hook de teclado, grabación WASAPI, HUD, bandeja, ajustes, historial, pegado
  src/OpenTypeless.Cli/       Herramientas de línea de comandos (transcribir un archivo, análisis del troceado, evaluación de prompts)
  tests/                      Pruebas con xUnit
  scripts/                    Scripts de compilación, pruebas e icono
eval/                       Conjuntos de prueba de la limpieza y guía de evaluación, compartidos por ambas apps
i18n/                       Traducciones de la interfaz (salvo chino e inglés), compartidas por ambas apps
docs/DESIGN.md              Arquitectura y decisiones de diseño
docs/WINDOWS-PORT.md        Cómo se corresponde cada archivo y API de sistema de macOS en Windows
docs/images/                Capturas del README
```

El prompt de limpieza (`Prompts.swift` / `Prompts.cs`) es idéntico byte a byte en ambas apps; cámbialos juntos y comprueba el resultado con [eval/](eval/).

### Traducciones

Cada texto visible para el usuario se escribe en línea con su versión en chino y en inglés: `L("有新版本 \(version)", "Version \(version) is available")` en Swift, `L($"有新版本 {version}", $"Version {version} is available")` en C#. Los demás idiomas están en [`i18n/strings.json`](i18n/strings.json), con el texto en inglés como clave y cada interpolación numerada en orden:

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

Ambas apps incrustan el archivo al compilar, y un texto sin traducción se muestra en inglés. Una traducción puede mover los marcadores, pero debe conservarlos todos. Tras añadir o cambiar un texto, añade sus traducciones y ejecuta `python3 i18n/check.py`: muestra las entradas que faltan, las que no se usan y las mal formadas, y la CI lo ejecuta en cada pull request. Para revisar un idioma en contexto, renderiza la interfaz con `--snapshot-ui` y `--lang es` (o cualquier otro código de idioma).

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # pruebas unitarias, incluido un servidor simulado que inyecta fallos en una grabación de 130 s
scripts/perf.sh                  # presupuestos de rendimiento en una compilación de release (ruta de audio, trabajo en el hilo principal); la CI también los ejecuta
```

Modos de línea de comandos útiles del binario compilado (desde `macos/`):

```bash
# Proceso completo sobre un archivo de audio; --realtime envía el audio a velocidad de habla, como un micrófono en vivo
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Muestra dónde corta el troceado
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Evalúa prompts y modelos de limpieza (ver eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Renderiza las páginas de ajustes, el panel de la barra de menús y el HUD a PNG (--live los muestra en pantalla con Liquid Glass real).
# Las capturas del README usan un historial de ejemplo y --demo, que da los permisos por concedidos.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # pruebas unitarias, incluido un servidor simulado que inyecta fallos en una grabación de 130 s
scripts\perf.ps1       # presupuestos de rendimiento en una compilación Release (ruta de audio, trabajo en el hilo de la interfaz); la CI también los ejecuta
```

Herramientas de línea de comandos (`OpenTypeless.Cli.exe`, distribuida junto a la app; usa los ajustes y las claves de la app):

```powershell
# Proceso completo sobre un archivo de audio (WAV, MP3, M4A, WMA, FLAC…); --realtime envía el audio a velocidad de habla
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# Muestra dónde corta el troceado
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# Evalúa prompts y modelos de limpieza (ver eval/README.md)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# Renderiza todas las páginas de ajustes, el panel de la bandeja y los estados del HUD a PNG
# (--lang en|zh|ja|… para un solo idioma, --demo da los permisos por concedidos; úsalo con OPENTYPELESS_DATA_DIR e historial de ejemplo)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Variables de entorno para desarrollo (Windows):

| Variable | Efecto |
|---|---|
| `OPENROUTER_API_KEY` | Sustituye la clave de OpenRouter guardada. |
| `OPENTYPELESS_DATA_DIR` | Usa otra carpeta para los ajustes y el historial (útil para pruebas). |
| `OPENTYPELESS_DEBUG` | Escribe los eventos de atajo / sesión / foco en `debug.log`, en la carpeta de datos. |
| `OPENTYPELESS_TEST_AUDIO` | Envía en tiempo real un WAV mono de 16 kHz en lugar del micrófono, para pruebas de extremo a extremo. |

### Contribuir

`main` está protegida: cada cambio pasa por una pull request. Trabaja en una rama, abre una pull request hacia `main` y fusiónala cuando la comprobación **CI passed** esté en verde.

### Integración continua

GitHub Actions ([`.github/workflows/`](.github/workflows/)) solo compila la app que cambiaste:

| Si cambias | Qué se ejecuta |
|---|---|
| `macos/**` | **macOS build** en un runner de macOS: las pruebas y luego el zip de la app. |
| `windows/**` | **Windows build** en un runner de Windows: las pruebas y luego los zips x64 y ARM64. |
| `testdata/**` | Ambas compilaciones: los casos de prueba compartidos (recuento de palabras, precios, tiempo ahorrado) que leen ambas suites, para que las dos apps coincidan. |
| `i18n/**` | Ambas compilaciones: las traducciones que incrustan las dos apps. |
| Solo `docs/`, `eval/`, `README*.md` | Nada que compilar. |

Cada ejecución también comprueba las traducciones (**Translations**, `python3 i18n/check.py`).

Descarga los zips desde la sección **Artifacts** de una ejecución en la pestaña Actions. **Actions → macOS build / Windows build → Run workflow** lanza una compilación a mano.

Para publicar, sube la versión en ambas apps (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) y envía una etiqueta, `1.0.2` (o `V1.0.2`). Compila ambas apps y crea una publicación en **borrador**, «OpenTypeless V1.0.2», con el zip de macOS (firmado con el certificado de publicación) y los zips de Windows x64 y ARM64. La compilación falla si la versión de un zip no coincide con la etiqueta.

Revisa el borrador y publícalo a mano; pasa a ser la última versión. Las copias instaladas la encuentran en menos de un día: el actualizador de la app busca la publicación más reciente, publicada y que no sea preliminar, que tenga un zip para su plataforma (`OpenTypeless-<version>-macOS-arm64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`). Marca una publicación como preliminar para que no se ofrezca.

**Firma de publicaciones en macOS (una sola vez).** macOS asocia los permisos de Accesibilidad y Micrófono a la firma de la app, así que las publicaciones deberían firmarse siempre con el mismo certificado; si no, se piden ambos permisos otra vez tras cada actualización. Ejecuta `macos/scripts/create-release-cert.sh`, guarda en un sitio privado el `.p12` que genera y añade los dos secretos del repositorio que muestra (`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`). A partir de ahí las compilaciones de publicación se firman con él; sin los secretos se firman ad hoc, con un aviso en la ejecución.

## Agradecimientos

Inspirado en Typeless. El enfoque de integración con el sistema se aprendió de los proyectos libres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) y [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Este proyecto es independiente y no está afiliado a ninguno de ellos.

## Licencia

[MIT](LICENSE)
