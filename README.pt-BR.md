<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <b>Português</b> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="Ícone do OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Digitação por voz nativa para macOS e Windows que não quebra em ditados longos.</b><br>
  Segure uma tecla, fale o quanto precisar e receba um texto limpo e organizado no cursor.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="A cápsula de gravação exibida enquanto você fala">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="O painel da barra de menus: dica do atalho e ditados recentes">
</p>

<p align="center">
  Dois apps nativos, um só design: <b>macOS</b> (Swift / SwiftUI) e <b>Windows</b> (C# / WinUI 3).<br>
  O mesmo processo, o mesmo prompt de limpeza, as mesmas configurações e o mesmo tratamento de falhas; só mudam a integração com o sistema e a aparência.
</p>

---

## Por que ele existe

Falar é a forma mais rápida de escrever prompts longos para ferramentas de IA. Isso também significa ditados longos: pensar em voz alta por um a três minutos é normal.

A maioria dos apps de digitação por voz, livres e comerciais, funciona bem com uma frase e **falha justamente nesses ditados longos**. Gravar 40 s, 1 min ou 2 min termina em tempo esgotado, resultado vazio ou texto perdido. Lendo o código de várias alternativas livres, as mesmas causas apareciam sempre:

- **A gravação inteira é enviada como uma única solicitação de voz para texto.** Os provedores estouram o tempo depois de uns 60 s de processamento (o OpenRouter documenta isso explicitamente), então quanto mais você fala, maior a chance de a solicitação morrer.
- **A chamada ao LLM de limpeza tem um tempo *total* curto.** Um limite de 30 s que inclui o streaming corta uma resposta longa no meio.
- **Uma falha joga tudo fora.** Dois minutos de fala se perdem e você precisa dizer tudo de novo.

O OpenTypeless foi construído em torno desse problema.

## Como ele lida com ditados longos

| | O que acontece |
|---|---|
| **Corta nas pausas** | Enquanto você fala, o áudio é cortado em trechos de 18–28 s na janela de 0,4 s mais silenciosa de cada intervalo, então nenhuma palavra é cortada ao meio e nenhuma solicitação chega perto do limite de 60 s do provedor. |
| **Transcreve enquanto você fala** | Cada trecho é transcrito em segundo plano assim que é cortado. Depois de um ditado de dois minutos, só os últimos segundos ficam para processar quando você solta a tecla. |
| **Tenta de novo por trecho** | Quedas de rede, 429 e erros 5xx são repetidos com espera progressiva. Chaves inválidas e problemas de cobrança falham na hora. Um trecho com falha nunca afeta os outros, e os trechos com falha ganham mais uma rodada completa no final. |
| **Modelo reserva para inícios lentos** | Se o modelo de limpeza não começar a responder em 0,8 s, ou falhar, um modelo reserva de outro fornecedor também é consultado e vence quem responder primeiro. Um provedor lento custa menos de um segundo a mais, não a espera inteira. |
| **Tempo ocioso, não tempo total** | A limpeza recebe a resposta por streaming e só é considerada travada quando *nenhum* dado chega por 25 s, então saídas longas nunca são cortadas. |
| **Nunca perde palavras** | O áudio é gravado em disco enquanto você fala. Todo ditado fica no Histórico; um que falhou pode ser repetido depois, e só os trechos com falha são reenviados. Se a limpeza falhar, a transcrição bruta é inserida no lugar. |

Um ditado sintético de 121 s, por exemplo, é dividido em 6 trechos em pausas naturais. Quando a pessoa para de falar, 5 deles já estão transcritos.

## Uma limpeza que parece digitada por você

A transcrição bruta é bagunçada: vícios de linguagem, recomeços, "não, espera, quero dizer…", pensar em voz alta. A limpeza transforma isso no que você teria digitado:

- **Autocorreções resolvidas.** Vale a versão final ("quarta, não, quinta" → quinta). Isso inclui correções feitas bem depois, implícitas ("50 mil, hã, 60 mil por segurança") e pontos retirados por completo ("…o terceiro, esquece").
- **Vícios de linguagem e pensamentos em voz alta removidos.** um / uh / 嗯 / 那个 / "deixa eu pensar" / "é isso" somem.
- **Nada real se perde.** Números, versões, nomes e comparações são mantidos exatamente. O modelo é avisado de que produtos mais novos do que ele conhece são reais, então "Gemini 3.5" nunca vira "Gemini 2.5".
- **Estrutura quando ajuda.** Três ou mais pontos paralelos viram uma lista numerada; o resto fica em parágrafos normais.
- **Nunca responde a você.** Prompts ditados ("você pode explicar por que…") são limpos, não respondidos nem executados.
- **Suas palavras, seus idiomas.** As edições são mínimas: palavras, ordem e tom continuam seus. Quando você mistura chinês e inglês, cada palavra fica no idioma em que foi dita, inclusive as do dia a dia ("shortcut", "dark mode"), com espaço entre o texto CJK e o latino.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="Histórico: o texto limpo acima da transcrição bruta, com o tempo de cada etapa">
</p>

O prompt é ajustado com conjuntos de desenvolvimento e de teste reservados (veja [eval/](eval/)), incluindo ditados reais.

## Recursos

- **Atalho global.** Segure **Fn** (macOS) ou **Ctrl direito** (Windows) por padrão, ou grave qualquer modificador sozinho (⌘ direito, ⌥ direito, Alt direito…) ou uma combinação (⌥ Espaço, Alt + Espaço, F5…).
- **Segurar para falar ou mãos livres.** Segure para falar; toque uma vez para continuar gravando com as mãos livres e toque de novo para terminar. **Esc** cancela.
- **Cola onde está o cursor.** Em um campo de texto, o texto é colado e sua área de transferência é restaurada; sem campo de texto em foco, ele vai para a área de transferência. Funciona também em navegadores e apps Electron, e em terminais no Windows.
- **Use seu próprio provedor.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek ou qualquer endpoint compatível com a OpenAI. Voz para texto, limpeza e o modelo reserva de limpeza podem usar provedores diferentes.
- **Vocabulário personalizado e preferências de estilo** para nomes, produtos e jargões.
- **Aprende com suas correções.** Corrija uma palavra mal reconhecida depois de colada (TypeList → Typeless) e ela é adicionada automaticamente ao seu vocabulário, junto com como foi ouvida errado. Só correções de som parecido são aprendidas, nunca reescritas, números alterados ou trocas de palavras comuns, e uma palavra que você remover nunca é aprendida de novo.
- **Página Início** com o que a digitação por voz fez por você: palavras ditadas, tempo economizado em relação a digitar (100 ppm por padrão, ajustável), sua velocidade de fala, quanto custou hoje, neste mês e no total, e um mapa de atividade no estilo do GitHub com sequências. Ela abre quando você mesmo abre o app; no login ela não atrapalha, a menos que você ative **Mostrar o Início ao abrir no login**.
- **Saiba quanto você gasta.** Solicitações ao OpenRouter contam exatamente o que o OpenRouter cobrou, e a tabela de preços ao vivo aparece ao lado de cada modelo. Para qualquer outro provedor ou endpoint personalizado, informe o preço do modelo em **Modelos** (por milhão de tokens, ou por minuto de áudio para voz para texto).
- **Histórico** de todos os ditados, com texto bruto e limpo, tempos, copiar e transcrever de novo. Escolha por quanto tempo as gravações ficam guardadas: nada, um dia, uma semana, um mês, um ano ou para sempre.
- **Nove idiomas de interface**: English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch e Русский. O app segue o idioma do sistema, ou escolha um em **Configurações → Geral → Idioma**.
- **Design nativo.** Liquid Glass no macOS 26+ com um painel na barra de menus; Mica e Acrylic no Windows 11 com um painel na bandeja. Ambos mostram uma pequena cápsula de gravação enquanto você fala.
- **Abre ao iniciar sessão.**
- **Atualiza sozinho.** Verifica o GitHub Releases uma vez por dia, baixa a nova versão em segundo plano e instala quando você clica em **Reiniciar para atualizar**, nunca no meio de um ditado. Os downloads são conferidos com o SHA-256 do GitHub antes de qualquer substituição. Desative, ou verifique manualmente, em **Configurações → Geral → Atualizações**.
- **Pequeno e nativo.** Um app Swift/SwiftUI de ~3 MB no macOS e um app WinUI 3 independente no Windows. Sem Electron, sem conta e sem servidor próprio.

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="Modelos: um provedor e um modelo para cada etapa, mais um modelo reserva de limpeza"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="Vocabulário: seus termos, incluindo os aprendidos com suas correções"></td>
  </tr>
  <tr>
    <td align="center">Escolha um provedor e um modelo para cada etapa, com um modelo reserva de limpeza</td>
    <td align="center">Vocabulário, incluindo palavras aprendidas com suas correções</td>
  </tr>
</table>


## Requisitos

- **macOS:** macOS 26 ou posterior, Apple Silicon.
- **Windows:** Windows 10 (versão 2004 ou posterior) ou Windows 11, x64 ou ARM64.
- Uma chave de API de pelo menos um provedor (o [OpenRouter](https://openrouter.ai/keys) é o mais fácil: uma chave cobre as duas etapas).

## Instalar no macOS

### Download

1. Baixe `OpenTypeless-<version>-macOS-arm64.zip` em [Releases](https://github.com/Tyler913/OpenTypeless/releases) e descompacte.
2. Mova o **OpenTypeless.app** para a pasta **Aplicativos**.
3. O app não é notarizado pela Apple (isso exige uma conta de desenvolvedor paga), então o macOS o bloqueia na primeira abertura e pode até dizer que ele "está danificado e não pode ser aberto". Remova uma vez a marca de quarentena do download no Terminal:

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   Depois abra normalmente:

   ```bash
   open /Applications/OpenTypeless.app
   ```

   Outra opção: tente abri-lo uma vez, depois vá em **Ajustes do Sistema → Privacidade e Segurança** e clique em **Abrir Mesmo Assim**.

O OpenTypeless fica na barra de menus (ícone de forma de onda), não no Dock.

As próximas versões são instaladas de dentro do app (**Configurações → Geral → Atualizações**), sem passar pelo Terminal: o macOS só pergunta sobre apps baixados por um navegador.

### Compilar a partir do código

Precisa do Xcode 26+ instalado (as Command Line Tools bastam para compilar, mas a compilação usa o plugin de macros do SwiftUI de `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # opcional, mas recomendado, uma única vez
scripts/build-app.sh             # compila, assina e instala /Applications/OpenTypeless.app
```

`create-signing-cert.sh` cria uma identidade local de assinatura de código. Sem ela, o app é assinado ad hoc, e o macOS pede de novo as permissões de Acessibilidade e Microfone após cada recompilação.

Para gerar um zip de lançamento em vez de instalar: `scripts/build-app.sh --package` grava `macos/dist/OpenTypeless-<version>-macOS-arm64.zip` (assinado ad hoc) e mostra seu SHA-256.

`build-app.sh` mantém exatamente uma cópia do app na máquina. Ele monta o pacote em uma pasta temporária oculta, move para `/Applications`, remove o registro de cópias antigas no LaunchServices e apaga entradas de privacidade desatualizadas quando a assinatura muda.

### Primeira execução

1. Conceda acesso ao **Microfone** e à **Acessibilidade** (a Acessibilidade é usada para detectar o atalho e colar o texto).
2. Adicione uma chave de API em **Configurações → Provedores**.
3. Recomendado se você usa Fn: defina **Ajustes do Sistema → Teclado → "Pressionar a tecla 🌐 para"** como **Não Fazer Nada**, para que tocar em Fn não abra o seletor de emojis.

## Instalar no Windows

### Download

1. Baixe `OpenTypeless-<version>-windows-x64.zip` (ou `-arm64`) em [Releases](https://github.com/Tyler913/OpenTypeless/releases) e descompacte onde quiser (por exemplo, `%LOCALAPPDATA%\Programs`).
2. Execute o **OpenTypeless.exe**. Ele é independente: não há mais nada para instalar.
3. O app não tem assinatura de código, então o SmartScreen pode dizer "O Windows protegeu o computador": clique em **Mais informações → Executar assim mesmo**.

As próximas versões são instaladas de dentro do app (**Configurações → Geral → Atualizações**) na mesma pasta, sem aviso do SmartScreen. Descompacte em um lugar onde você possa gravar, como `%LOCALAPPDATA%\Programs`; em `Program Files`, o app só consegue mostrar o link para o download.

O OpenTypeless fica na **área de notificação** (ícone de forma de onda ao lado do relógio). No começo, o Windows esconde ícones novos no menu de estouro (^); arraste-o para a barra de tarefas ou ative-o em **Configurações → Personalização → Barra de Tarefas → Outros ícones da bandeja do sistema**.

### Compilar a partir do código

Precisa do [SDK do .NET 10](https://dotnet.microsoft.com/download). O Visual Studio é opcional.

```powershell
# a partir de windows\ em um clone deste repositório
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # testa, compila e instala em %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # grava windows\dist\OpenTypeless-<version>-windows-x64.zip
```

`build.ps1` mantém exatamente uma cópia instalada: para o app em execução, substitui a pasta de instalação, atualiza o atalho do menu Iniciar e abre a nova versão. Adicione `-Arch arm64` para Windows em ARM.

Não é preciso um PC com Windows para desenvolver o app do Windows: o GitHub Actions o compila a cada alteração (veja [Integração contínua](#integração-contínua)).

### Primeira execução

1. Adicione uma chave de API em **Configurações → Provedores**.
2. Confirme que **Configurações → Privacidade e segurança → Microfone → Permitir que aplicativos da área de trabalho acessem seu microfone** está ativado.
3. Segure o Ctrl direito e fale. O Windows não precisa de permissão de acessibilidade; o único limite é que ele não permite colar em apps executados como administrador, então nesses casos o texto vai para a área de transferência.

## Modelos padrão

| Etapa | Padrão | Observações |
|---|---|---|
| Voz para texto | `microsoft/mai-transcribe-2` (OpenRouter) | Qualquer modelo de transcrição do OpenRouter, ou um `/audio/transcriptions` compatível com Whisper em outro lugar. |
| Limpeza | `google/gemini-3.8-flash` (OpenRouter) | A melhor limpeza nos nossos testes, por cerca de US$ 0,005 por ditado longo. Opções mais baratas para testar: `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| Limpeza reserva | `deepseek/deepseek-v4.1-flash` (OpenRouter) | Só é consultado quando o modelo principal demora a começar ou falha. Escolha um modelo rápido de outro fornecedor. |

O raciocínio (reasoning) do modelo de limpeza é desligado ou reduzido ao mínimo automaticamente, para manter a latência baixa.

## Privacidade

- Áudio e texto só são enviados aos provedores que você configurar.
- As chaves de API ficam no Keychain do macOS ou no Gerenciador de Credenciais do Windows (uma entrada, `OpenTypeless/credentials`).
- Os totais de uso da página Início (palavras, tempo de fala e custo por dia, sem texto) ficam em `usage.json`, na mesma pasta das configurações e do histórico. A tabela de preços do OpenRouter é baixada da lista pública de modelos (sem chave, nada sobre você) algumas vezes por dia.
- O histórico (áudio + transcrições) fica em `~/Library/Application Support/OpenTypeless/Sessions/` no macOS e em `%LOCALAPPDATA%\OpenTypeless\` no Windows. As gravações ficam guardadas por um mês por padrão (na página Histórico: nada, um dia, uma semana, um mês, um ano ou para sempre); depois disso, o texto continua entre as 200 entradas mais recentes. Ditados com falha mantêm o áudio para que possam ser repetidos.
- A verificação de atualizações envia uma solicitação por dia para `api.github.com` (sem conta, nada sobre você ou seus ditados); desative em **Configurações → Geral → Atualizações**.
- Aprender com suas correções lê o campo de texto em que você ditou, só no seu computador, por no máximo dois minutos após colar. Campos de senha são ignorados. Pode ser desativado em **Vocabulário e estilo**.

## Desenvolvimento

O repositório contém os dois apps. Eles compartilham o design, os conjuntos de avaliação e este README; cada um tem seu próprio código, testes e scripts de compilação.

```
macos/                      O app para macOS (Swift Package)
  Sources/TypelessCore/       Lógica do processo, sem interface: divisão, WAV, cliente de provedores, novas tentativas, prompt de limpeza
  Sources/OpenTypeless/       O app: atalho, gravação, HUD, configurações, histórico, colagem, ferramentas CLI
  Tests/                      Testes com swift-testing
  scripts/                    Scripts de compilação, assinatura, ícone e testes
windows/                    O app para Windows (solução .NET)
  src/TypelessCore/           A mesma lógica do processo, portada linha a linha
  src/OpenTypeless/           O app WinUI: hook de teclado, gravação WASAPI, HUD, bandeja, configurações, histórico, colagem
  src/OpenTypeless.Cli/       Ferramentas de linha de comando (transcrever um arquivo, análise da divisão, avaliação de prompts)
  tests/                      Testes com xUnit
  scripts/                    Scripts de compilação, testes e ícone
eval/                       Conjuntos de teste da limpeza e o guia de avaliação, compartilhados pelos dois apps
i18n/                       Traduções da interface (exceto chinês e inglês), compartilhadas pelos dois apps
docs/DESIGN.md              Arquitetura e decisões de design
docs/WINDOWS-PORT.md        Como cada arquivo e API de sistema do macOS corresponde no Windows
docs/images/                Capturas de tela do README
```

O prompt de limpeza (`Prompts.swift` / `Prompts.cs`) é idêntico byte a byte nos dois apps; altere os dois juntos e confira o resultado com [eval/](eval/).

### Traduções

Todo texto visível ao usuário é escrito no próprio código com a versão em chinês e em inglês: `L("有新版本 \(version)", "Version \(version) is available")` em Swift, `L($"有新版本 {version}", $"Version {version} is available")` em C#. Os demais idiomas ficam em [`i18n/strings.json`](i18n/strings.json), com o texto em inglês como chave e cada interpolação numerada em ordem:

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

Os dois apps incorporam o arquivo na compilação, e um texto sem tradução aparece em inglês. Uma tradução pode mudar a posição dos marcadores, mas precisa manter todos. Depois de adicionar ou alterar um texto, adicione as traduções e execute `python3 i18n/check.py`: ele lista entradas ausentes, não usadas e malformadas, e a CI o executa em todo pull request. Para revisar um idioma no contexto, renderize a interface com `--snapshot-ui` e `--lang pt` (ou qualquer outro código de idioma).

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # testes unitários, incluindo um servidor simulado que injeta falhas em uma gravação de 130 s
```

Modos úteis de linha de comando do binário compilado (a partir de `macos/`):

```bash
# Processo completo em um arquivo de áudio; --realtime envia o áudio na velocidade da fala, como um microfone ao vivo
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Mostra onde a divisão corta
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Avalia prompts e modelos de limpeza (veja eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Renderiza as páginas de configurações, o painel da barra de menus e o HUD em PNG (--live mostra na tela, com Liquid Glass de verdade).
# As capturas do README usam um histórico de exemplo e --demo, que trata as permissões como concedidas.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # testes unitários, incluindo um servidor simulado que injeta falhas em uma gravação de 130 s
```

Ferramentas de linha de comando (`OpenTypeless.Cli.exe`, distribuída junto com o app; usa as configurações e chaves do app):

```powershell
# Processo completo em um arquivo de áudio (WAV, MP3, M4A, WMA, FLAC…); --realtime envia o áudio na velocidade da fala
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# Mostra onde a divisão corta
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# Avalia prompts e modelos de limpeza (veja eval/README.md)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# Renderiza todas as páginas de configurações, o painel da bandeja e os estados do HUD em PNG
# (--lang en|zh|ja|… para um só idioma, --demo trata as permissões como concedidas; use com OPENTYPELESS_DATA_DIR e um histórico de exemplo)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Variáveis de ambiente para desenvolvimento (Windows):

| Variável | Efeito |
|---|---|
| `OPENROUTER_API_KEY` | Substitui a chave do OpenRouter salva. |
| `OPENTYPELESS_DATA_DIR` | Usa outra pasta para configurações e histórico (útil em testes). |
| `OPENTYPELESS_DEBUG` | Grava eventos de atalho / sessão / foco em `debug.log`, na pasta de dados. |
| `OPENTYPELESS_TEST_AUDIO` | Transmite em tempo real um WAV mono de 16 kHz no lugar do microfone, para testes de ponta a ponta. |

### Como contribuir

A `main` é protegida: toda alteração passa por um pull request. Trabalhe em um branch, abra um pull request para a `main` e faça o merge quando a verificação **CI passed** estiver verde.

### Integração contínua

O GitHub Actions ([`.github/workflows/`](.github/workflows/)) só compila o app que você alterou:

| Se você alterar | O que roda |
|---|---|
| `macos/**` | **macOS build** em um runner macOS: os testes e depois o zip do app. |
| `windows/**` | **Windows build** em um runner Windows: os testes e depois os zips x64 e ARM64. |
| `testdata/**` | As duas compilações: os casos de teste compartilhados (contagem de palavras, preços, tempo economizado) que as duas suítes leem, para que os dois apps concordem. |
| `i18n/**` | As duas compilações: as traduções que os dois apps incorporam. |
| Só `docs/`, `eval/`, `README*.md` | Nada para compilar. |

Toda execução também verifica as traduções (**Translations**, `python3 i18n/check.py`).

Baixe os zips na seção **Artifacts** de uma execução na aba Actions. **Actions → macOS build / Windows build → Run workflow** inicia uma compilação manualmente.

Para lançar uma versão, aumente a versão nos dois apps (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) e envie uma tag, `1.0.2` (ou `V1.0.2`). Ela compila os dois apps e cria um lançamento em **rascunho**, "OpenTypeless V1.0.2", com o zip do macOS (assinado com o certificado de lançamento) e os zips x64 e ARM64 do Windows. A compilação falha se a versão de um zip não bater com a tag.

Revise o rascunho e publique manualmente; ele passa a ser o lançamento mais recente. As cópias instaladas o encontram em até um dia: o atualizador do app procura o lançamento publicado mais recente, que não seja pré-lançamento, com um zip para a sua plataforma (`OpenTypeless-<version>-macOS-arm64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`). Marque um lançamento como pré-lançamento para que ele não seja oferecido.

**Assinatura de lançamentos no macOS (uma única vez).** O macOS vincula as permissões de Acessibilidade e Microfone à assinatura do app, então os lançamentos devem sempre ser assinados com o mesmo certificado; caso contrário, os usuários precisam conceder as duas permissões de novo a cada atualização. Execute `macos/scripts/create-release-cert.sh`, guarde em um lugar privado o `.p12` gerado e adicione os dois segredos do repositório que ele mostra (`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`). A partir daí, as compilações de lançamento são assinadas com ele; sem os segredos, são assinadas ad hoc, com um aviso na execução.

## Agradecimentos

Inspirado no Typeless. A abordagem de integração com o sistema foi aprendida com os projetos livres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) e [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Este projeto é independente e não tem vínculo com nenhum deles.

## Licença

[MIT](LICENSE)
