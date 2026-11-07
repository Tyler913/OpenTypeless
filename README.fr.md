<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <b>Français</b> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="112" alt="OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>Maintenez une touche, parlez, et le texte propre arrive au curseur.</b><br>
  La dictée native pour macOS et Windows, pensée pour les longues dictées.
</p>

<p align="center">
  <a href="https://github.com/Tyler913/OpenTypeless/releases/latest"><b>Télécharger</b></a> · <a href="#installer">Installer</a> · <a href="docs/DEVELOPMENT.md">Compiler depuis les sources</a>
</p>

<p align="center">
  <img src="docs/images/recording.png" width="420" alt="La capsule d’enregistrement, avec l’aperçu de vos mots au-dessus">
</p>

## Pourquoi OpenTypeless

La plupart des apps de dictée s’en sortent pour une phrase et cèdent sur deux minutes de parole : délais dépassés, résultats vides, texte perdu. OpenTypeless est conçu précisément pour ça.

- **Parlez aussi longtemps que vous voulez.** L’audio est coupé à vos pauses et transcrit pendant que vous parlez : même une longue dictée est prête un instant après avoir relâché la touche.
- **Aucun mot perdu.** Chaque segment est réessayé à part, un modèle lent a une solution de secours, et chaque enregistrement est conservé pour pouvoir le relancer.
- **Comme si vous l’aviez tapé.** Les tics de langage et les faux départs disparaissent, « mercredi, non, jeudi » devient jeudi, et les listes deviennent des listes. Vos mots, chiffres et langues restent.
- **Votre propre clé.** OpenRouter, OpenAI, Groq, Gemini, Anthropic, DeepSeek et d’autres, ou des modèles locaux avec Ollama et LM Studio. Pas de compte, pas de serveur maison.

<p align="center">
  <img src="docs/images/menu-bar.png" width="300" alt="Le panneau de la barre des menus : comment dicter, la dépense du mois et les dictées récentes">
</p>

## Fonctionnalités

- **Appuyer pour parler ou mains libres.** Maintenez **Fn** (macOS) ou **Alt droite** (Windows), ou appuyez une fois pour continuer. N’importe quelle touche ou combinaison convient.
- **Traduire en parlant.** Ajoutez **Maj** et vos mots sortent dans la langue choisie.
- **Modifier à la voix.** Sélectionnez du texte, ajoutez **Ctrl** et dites « plus formel » ou « fais-en une liste ».
- **Un vocabulaire qui apprend.** Ajoutez noms et jargon ; les mots corrigés après le collage sont appris tout seuls.
- **Extraits.** Dites « mon e-mail » et le texte enregistré est inséré, mot pour mot.
- **Réglages par app.** Entrée après l’insertion dans Slack, ton formel dans Mail, pas de nettoyage dans Terminal.
- **Historique et fichiers.** Chaque dictée se recherche et s’exporte, et les fichiers audio ou vidéo se transcrivent aussi.
- **Maîtrisez vos dépenses.** Mots, temps gagné et coût sur l’Accueil, avec une limite mensuelle facultative.
- **Sur l’appareil, sans clé.** La transcription peut aussi se faire sur votre ordinateur : gratuite, hors ligne et privée, un peu moins précise que les modèles dans le cloud.
- **Aperçu en direct (bêta).** Voyez vos mots au-dessus de la capsule pendant que vous parlez.
- **Neuf langues, mises à jour automatiques.** English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch, Русский.

<table>
  <tr>
    <td width="50%" align="center"><img src="docs/images/home.png" alt="Accueil : mots, temps gagné, dépenses et activité"><br><sub>Accueil : mots, temps gagné, dépenses et activité</sub></td>
    <td width="50%" align="center"><img src="docs/images/history.png" alt="Historique : chaque dictée, avec recherche"><br><sub>Historique : chaque dictée, avec recherche</sub></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/images/models.png" alt="Modèles : un fournisseur et un modèle par étape"><br><sub>Modèles : un fournisseur et un modèle par étape</sub></td>
    <td width="50%" align="center"><img src="docs/images/vocabulary.png" alt="Vocabulaire, y compris les mots appris de vos corrections"><br><sub>Vocabulaire, y compris les mots appris de vos corrections</sub></td>
  </tr>
</table>

<sub>Les captures viennent de macOS. Windows a les mêmes pages, avec son propre style natif.</sub>

## Installer

### macOS

Nécessite macOS 26 ou ultérieur, sur Apple silicon ou Intel.

```bash
brew install --cask tyler913/tap/opentypeless
```

Ou téléchargez le `.dmg` depuis [Releases](https://github.com/Tyler913/OpenTypeless/releases) (`arm64` pour Apple silicon, `x64` pour Intel) et glissez l’app dans Applications. Elle n’est pas notarisée par Apple : si macOS dit qu’elle ne peut pas être ouverte ou qu’elle « est endommagée », lancez ceci une fois :

```bash
xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
```

### Windows

Nécessite Windows 10 (2004 ou ultérieur) ou Windows 11, x64 ou ARM64.

Téléchargez `OpenTypeless-<version>-windows-x64-setup.exe` (`arm64` pour Windows sur ARM) depuis [Releases](https://github.com/Tyler913/OpenTypeless/releases) et lancez-le ; aucun droit administrateur n’est nécessaire. Si SmartScreen s’affiche, cliquez sur **Informations complémentaires → Exécuter quand même**. Le `.zip` est une version portable.

Ensuite, l’app se met à jour toute seule.

## Prise en main

1. Ouvrez OpenTypeless. Il se trouve dans la barre des menus (macOS) ou la zone de notification (Windows).
2. Le guide de bienvenue demande une clé API. [OpenRouter](https://openrouter.ai/keys) est le plus simple : une clé suffit pour tout. Ou choisissez **Utiliser la reconnaissance sur l’appareil** pour commencer sans aucune clé.
3. Sur macOS, autorisez le **Micro** et l’**Accessibilité** quand c’est demandé.
4. Maintenez le raccourci, parlez, relâchez. Le texte apparaît au curseur.

> Sur macOS, réglez **Réglages Système → Clavier → « Appuyer sur la touche 🌐 pour »** sur **Ne rien faire**, pour qu’un appui sur Fn n’ouvre pas le sélecteur d’emoji.

## Confidentialité

- L’audio et le texte ne vont qu’aux fournisseurs que vous configurez. À part ça, l’app vérifie les mises à jour sur GitHub une fois par jour et télécharge la liste de prix publique d’OpenRouter. L’aperçu en direct sur Windows utilise la reconnaissance vocale de Windows, qui suit son propre réglage en ligne.
- Clés, réglages et historique restent sur votre ordinateur. Les enregistrements sont gardés un mois par défaut ; vous choisissez la durée.
- Les champs de mot de passe ne sont jamais lus. Utiliser le texte autour du curseur pour orthographier les noms est désactivé tant que vous ne l’activez pas.

## Développement

Deux apps natives dans un même dépôt : [`macos/`](macos) en Swift / SwiftUI et [`windows/`](windows) en C# / WinUI 3, avec le même traitement, le même prompt de nettoyage et les mêmes réglages. Voir [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) pour les compiler et les tester, et [docs/DESIGN.md](docs/DESIGN.md) pour leur fonctionnement.

## Remerciements

Inspiré par Typeless. L’approche de l’intégration au système a été apprise des projets libres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) et [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Ce projet est indépendant et n’est affilié à aucun d’entre eux.

## Licence

[MIT](LICENSE)
