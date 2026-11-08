<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <b>Português</b> · <a href="README.fr.md">Français</a> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Segure uma tecla, fale e receba texto limpo no cursor.</b><br>
  Ditado nativo para macOS e Windows, feito para ditados longos.
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>Baixar</b></a> · <a href="#instalar">Instalar</a> · <a href="docs/DEVELOPMENT.md">Compilar do código-fonte</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="A cápsula de gravação, com a prévia das suas palavras acima dela">
</p>

## Por que OpenTypeless

A maioria dos apps de ditado vai bem com uma frase e falha em dois minutos de fala: tempo esgotado, resultado vazio, texto perdido. O OpenTypeless foi feito exatamente para isso.

- **Fale o quanto quiser.** O áudio é cortado nas suas pausas e transcrito enquanto você fala, então até um ditado longo fica pronto logo depois de soltar a tecla.
- **Nenhuma palavra se perde.** Cada trecho tenta de novo por conta própria, um modelo lento ganha um reserva e toda gravação é salva para poder ser refeita.
- **Parece digitado por você.** Vícios de linguagem e recomeços somem, “quarta, não, quinta” vira quinta e listas viram listas. Suas palavras, números e idiomas ficam.
- **Use sua própria chave.** OpenRouter, OpenAI, Groq, Gemini, Anthropic, DeepSeek e outros, ou modelos locais com Ollama e LM Studio. Sem conta e sem servidor próprio.

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="O painel da barra de menus: como ditar, o gasto do mês e os ditados recentes">
</p>

## Recursos

- **Segurar para falar ou mãos livres.** Segure **Fn** (macOS) ou **Alt direito** (Windows), ou toque uma vez para continuar falando. Qualquer tecla ou combinação serve.
- **Traduza enquanto fala.** Adicione **Shift** e suas palavras saem no idioma escolhido.
- **Edite com a voz.** Selecione um texto, adicione **Ctrl** e diga “deixe mais formal” ou “transforme numa lista”.
- **Vocabulário que aprende.** Adicione nomes e jargões; palavras que você corrige depois de colar são aprendidas sozinhas.
- **Aprende como você escreve (opcional).** Sua pontuação e seu tom, aprendidos dos seus ditados e correções numa nota curta que você pode editar.
- **Trechos prontos.** Diga “meu e-mail” e o texto salvo entra, palavra por palavra.
- **Bloco de notas.** Um bloco para ditar, onde também fica guardado um ditado que não tinha onde entrar.
- **Limpeza do seu jeito.** Desligada, Leve, Normal ou Polida, já ajustada para apps de chat, e-mail e código. Por app você também pode apertar Enter depois de inserir, como no Slack.
- **Histórico e arquivos.** Todo ditado pode ser buscado, exportado e ouvido de novo, e arquivos de áudio ou vídeo também podem ser transcritos.
- **Saiba quanto gasta.** Palavras, tempo economizado e custo na tela Início, com um limite mensal opcional.
- **No dispositivo, sem chave.** A transcrição também pode rodar no seu computador: gratuita, offline e privada, um pouco menos precisa que os modelos na nuvem. Também assume quando você está offline.
- **Prévia ao vivo (beta).** Veja suas palavras acima da cápsula enquanto fala.
- **Nove idiomas, atualizações automáticas.** English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский.

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="Início: palavras, tempo economizado, gasto e atividade"><br><sub>Início: palavras, tempo economizado, gasto e atividade</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="Histórico: todo ditado, com busca"><br><sub>Histórico: todo ditado, com busca</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="Modelos: um provedor e um modelo para cada etapa"><br><sub>Modelos: um provedor e um modelo para cada etapa</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="Vocabulário, incluindo palavras aprendidas com suas correções"><br><sub>Vocabulário, incluindo palavras aprendidas com suas correções</sub></td>
  </tr>
</table>

<sub>As capturas são do macOS. O Windows tem as mesmas páginas, com a aparência nativa dele.</sub>

## Instalar

### macOS

Requer macOS 26 ou posterior, com Apple silicon ou Intel.

```bash
brew install --cask tyler913/tap/opentypeless
```

Ou baixe o `.dmg` em [Releases](https://github.com/Tyler913/OpenTypeless/releases) (`arm64` para Apple silicon, `x64` para Intel) e arraste o app para Aplicativos. Ele não é notarizado pela Apple, então se o macOS disser que não pode abri-lo ou que ele “está danificado”, rode isto uma vez:

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Requer Windows 10 (2004 ou posterior) ou Windows 11, x64 ou ARM64.

Baixe `OpenTypeless-<version>-windows-x64-setup.exe` (`arm64` para Windows em ARM) em [Releases](https://github.com/Tyler913/OpenTypeless/releases) e execute; não precisa de permissão de administrador. Se o SmartScreen aparecer, clique em **Mais informações → Executar assim mesmo**. O `.zip` é uma versão portátil.

Depois disso, o app se atualiza sozinho.

## Primeiros passos

1. Abra o OpenTypeless. Ele fica na barra de menus (macOS) ou na área de notificação (Windows).
2. O guia de boas-vindas pede uma chave de API. O [OpenRouter](https://openrouter.ai/keys) é o mais fácil: uma chave serve para tudo. Ou escolha **Usar o reconhecimento no dispositivo** para começar sem nenhuma chave.
3. No macOS, permita o **Microfone** e a **Acessibilidade** quando for pedido.
4. Segure o atalho, fale e solte. O texto aparece no cursor.

> No macOS, defina **Ajustes do Sistema → Teclado → "Pressionar a tecla 🌐 para"** como **Não Fazer Nada**, para que tocar em Fn não abra o seletor de emojis.

## Privacidade

- Áudio e texto vão só para os provedores que você configurar. Fora isso, o app só procura atualizações no GitHub uma vez por dia e baixa a lista pública de preços do OpenRouter. A prévia ao vivo no Windows usa o reconhecimento de fala do Windows, que segue a configuração online dele.
- Chaves, ajustes e histórico ficam no seu computador. As gravações ficam guardadas por um mês por padrão; você escolhe por quanto tempo.
- Campos de senha nunca são lidos. Usar o texto perto do cursor para acertar nomes fica desligado até você ativar.

## Desenvolvimento

Dois apps nativos em um repositório: [`macos/`](macos) em Swift / SwiftUI e [`windows/`](windows) em C# / WinUI 3, com o mesmo processamento, o mesmo prompt de limpeza e os mesmos ajustes. Veja [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) para compilar e testar, e [docs/DESIGN.md](docs/DESIGN.md) para saber como funcionam.

## Agradecimentos

Inspirado no Typeless. A abordagem de integração com o sistema foi aprendida com os projetos livres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) e [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Este projeto é independente e não tem vínculo com nenhum deles. O reconhecimento no dispositivo do Windows usa [SenseVoice Small](https://github.com/FunAudioLLM/SenseVoice) (FunASR Model License) com [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0).

## Licença

[MIT](LICENSE)
