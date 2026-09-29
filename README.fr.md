<p align="center">
  <a href="README.md">English</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português</a> · <b>Français</b> · <a href="README.de.md">Deutsch</a> · <a href="README.ru.md">Русский</a>
</p>

<p align="center">
  <img src="macos/Resources/AppIcon.png" width="128" alt="Icône d’OpenTypeless">
</p>

<h1 align="center">OpenTypeless</h1>

<p align="center">
  <b>La saisie vocale native pour macOS et Windows qui tient bon sur les longues dictées.</b><br>
  Maintenez une touche, parlez aussi longtemps qu’il le faut et obtenez un texte propre et structuré au curseur.
</p>

<p align="center">
  <img src="docs/images/recording.png" width="380" alt="La capsule d’enregistrement affichée pendant que vous parlez">
</p>

<p align="center">
  <img src="docs/images/menu-bar.png" width="330" alt="Le panneau de la barre des menus : rappel du raccourci et dictées récentes">
</p>

<p align="center">
  Deux apps natives, une seule conception : <b>macOS</b> (Swift / SwiftUI) et <b>Windows</b> (C# / WinUI 3).<br>
  Même chaîne de traitement, même prompt de nettoyage, mêmes réglages et même gestion des erreurs ; seuls l’intégration au système et l’apparence changent.
</p>

---

## Pourquoi ce projet

La voix est le moyen le plus rapide d’écrire de longs prompts pour les outils d’IA. Cela veut aussi dire de longues dictées : réfléchir à voix haute pendant une à trois minutes, c’est normal.

La plupart des apps de saisie vocale, libres comme commerciales, fonctionnent bien pour une phrase puis **échouent précisément sur ces longues dictées**. Enregistrer 40 s, 1 min ou 2 min se termine par un délai dépassé, un résultat vide ou du texte perdu. En lisant le code de plusieurs alternatives libres, on retrouve toujours les mêmes causes :

- **Tout l’enregistrement part en une seule requête de transcription.** Les fournisseurs coupent au bout d’environ 60 s de traitement (OpenRouter le documente explicitement) : plus vous parlez, plus la requête risque d’échouer.
- **L’appel au LLM de nettoyage a un délai *total* court.** Une limite de 30 s qui inclut le streaming coupe une longue réponse en plein milieu.
- **Un échec et tout est perdu.** Deux minutes de parole disparaissent, et il faut tout redire.

OpenTypeless est construit autour de ce problème.

## Comment il gère les longues dictées

| | Ce qui se passe |
|---|---|
| **Découpe aux pauses** | Pendant que vous parlez, l’audio est découpé en segments de 18 à 28 s, au niveau de la fenêtre de 0,4 s la plus silencieuse de chaque portion : aucun mot n’est coupé en deux et aucune requête n’approche la limite de 60 s du fournisseur. |
| **Transcrit pendant que vous parlez** | Chaque segment est transcrit en arrière-plan dès qu’il est découpé. Après une dictée de deux minutes, il ne reste que les dernières secondes à traiter quand vous relâchez la touche. |
| **Réessaie segment par segment** | Coupures réseau, 429 et erreurs 5xx sont réessayés avec un délai croissant. Clés invalides et problèmes de facturation échouent immédiatement. Un segment en échec n’affecte jamais les autres, et les segments en échec ont droit à une tournée complète de plus à la fin. |
| **Modèle de secours en cas de démarrage lent** | Si le modèle de nettoyage n’a pas commencé à répondre en 0,8 s, ou échoue, un modèle de secours d’un autre éditeur est aussi sollicité et le premier à répondre l’emporte. Un fournisseur lent coûte moins d’une seconde de plus, pas toute l’attente. |
| **Délai d’inactivité, pas délai total** | Le nettoyage reçoit sa réponse en streaming et n’est considéré comme bloqué que si *aucune* donnée n’arrive pendant 25 s : les longues sorties ne sont jamais tronquées. |
| **Aucun mot perdu** | L’audio est écrit sur le disque pendant que vous parlez. Chaque dictée est conservée dans l’historique ; une dictée en échec peut être relancée plus tard, et seuls ses segments en échec sont renvoyés. Si le nettoyage échoue, la transcription brute est insérée à la place. |

Une dictée synthétique de 121 s, par exemple, est découpée en 6 segments aux pauses naturelles. Quand la personne s’arrête de parler, 5 d’entre eux sont déjà transcrits.

## Un nettoyage qui se lit comme si vous l’aviez tapé

La transcription brute est brouillonne : tics de langage, reprises, « non attends, je veux dire… », réflexions à voix haute. Le nettoyage en fait ce que vous auriez tapé :

- **Les autocorrections sont résolues.** La dernière version l’emporte (« mercredi, non, jeudi » → jeudi). Cela vaut pour les corrections faites bien plus tard, les corrections implicites (« 50 000, euh, 60 000 pour être tranquille ») et les points retirés entièrement (« …le troisième, laisse tomber »).
- **Tics de langage et réflexions à voix haute supprimés.** um / uh / 嗯 / 那个 / « laisse-moi réfléchir » / « voilà, c’est à peu près tout » disparaissent.
- **Rien de réel n’est perdu.** Nombres, versions, noms et comparaisons sont conservés tels quels. Le modèle sait que des produits plus récents que lui existent bel et bien : « Gemini 3.5 » ne devient jamais « Gemini 2.5 ».
- **De la structure quand elle aide.** Trois points parallèles ou plus deviennent une liste numérotée ; le reste reste en paragraphes simples.
- **Ne vous répond jamais.** Les prompts dictés (« peux-tu expliquer pourquoi… ») sont nettoyés, pas traités ni exécutés.
- **Vos mots, vos langues.** Les modifications sont minimales : formulation, ordre et ton restent les vôtres. Quand vous mélangez chinois et anglais, chaque mot reste dans la langue où vous l’avez dit, y compris les mots courants (« shortcut », « dark mode »), avec une espace entre texte CJK et texte latin.

<p align="center">
  <img src="docs/images/history.png" width="720" alt="Historique : le texte nettoyé au-dessus de la transcription brute, avec la durée de chaque étape">
</p>

Le prompt est ajusté sur des jeux de développement et de test réservés (voir [eval/](eval/)), qui incluent de vraies dictées.

## Fonctionnalités

- **Raccourci global.** Maintenez **Fn** (macOS) ou **Ctrl droite** (Windows) par défaut, ou enregistrez n’importe quel modificateur seul (⌘ droite, ⌥ droite, Alt droite…) ou une combinaison (⌥ Espace, Alt + Espace, F5…).
- **Appuyer pour parler ou mains libres.** Maintenez pour parler ; appuyez une fois pour continuer à enregistrer mains libres, et une seconde fois pour terminer. **Échap** annule. Après 10 secondes de parole, une dictée annulée n’est pas perdue : elle est transcrite (sans être insérée) et conservée 24 heures dans l’historique.
- **Choisissez votre micro** dans **Réglages → Général**, avec un indicateur de niveau en direct pour vérifier qu’il vous entend. Les périphériques virtuels (applis de réunion et de streaming) sont signalés, et si le micro choisi est déconnecté, celui par défaut du système prend le relais.
- **Garder le micro prêt** (facultatif) : l’enregistrement démarre dès que vous appuyez sur la touche et inclut l’instant qui précède, pour que le premier mot ne soit pas coupé. Le micro reste allumé et les écouteurs Bluetooth passent en mode appel.
- **Aperçu en direct (bêta)** : voyez les mots au-dessus de la capsule d’enregistrement pendant que vous parlez, reconnus sur l’appareil (SpeechAnalyzer sur macOS ; reconnaissance vocale de Windows). Le texte inséré vient toujours de votre fournisseur.
- **Colle là où se trouve le curseur.** Dans un champ de texte, le texte est collé et votre presse-papiers restauré ; sans champ de texte actif, il va dans le presse-papiers. Les navigateurs et les apps Electron sont gérés aussi, ainsi que les terminaux sous Windows.
- **Apportez votre fournisseur.** OpenRouter, OpenAI, Groq, SiliconFlow, DeepSeek ou tout endpoint compatible OpenAI. La transcription, le nettoyage et le modèle de nettoyage de secours peuvent chacun utiliser un fournisseur différent. **Tester** vérifie une clé et affiche la latence aller-retour du fournisseur (médiane de trois).
- **Transcription de secours** (facultatif) : quand un segment prend bien plus de temps que ce dont cette route a d’habitude besoin pour sa durée, ou échoue, un second fournisseur est aussi sollicité et la première réponse l’emporte.
- **Vocabulaire personnalisé et préférences de style** pour les noms, les produits et le jargon.
- **Apprend de vos corrections.** Corrigez un mot mal reconnu après le collage (TypeList → Typeless) et il est ajouté automatiquement à votre vocabulaire, avec la façon dont il a été mal entendu. Seules les corrections de sonorité proche sont apprises, jamais les reformulations, les nombres modifiés ou les remplacements de mots courants, et un mot que vous retirez n’est plus jamais appris.
- **Page d’accueil** qui montre ce que la saisie vocale a fait pour vous : mots dictés, temps gagné par rapport à la frappe (100 mots/min par défaut, réglable), votre débit de parole, ce que cela a coûté aujourd’hui, ce mois-ci et au total, et une carte d’activité façon GitHub avec les séries de jours. Elle s’ouvre quand vous lancez l’app vous-même ; à la connexion, elle reste discrète sauf si vous activez **Afficher l’accueil à l’ouverture à la connexion**.
- **Sachez ce que vous dépensez.** Les requêtes OpenRouter comptent exactement ce qu’OpenRouter a facturé, et sa grille tarifaire en direct s’affiche à côté de chaque modèle. Pour tout autre fournisseur ou endpoint personnalisé, saisissez le prix du modèle dans **Modèles** (par million de jetons, ou par minute d’audio pour la transcription).
- **Historique** de chaque dictée, regroupé par jour et consultable par recherche, avec le texte brut et nettoyé, les durées, le coût, la copie et la retranscription. Choisissez combien de temps les enregistrements sont conservés : pas du tout, un jour, une semaine, un mois, un an ou pour toujours.
- **Neuf langues d’interface** : English, 简体中文, 日本語, 한국어, Español, Português, Français, Deutsch et Русский. L’app suit la langue du système, ou choisissez-en une dans **Réglages → Général → Langue**.
- **Design natif.** Liquid Glass sur macOS 26+ avec un panneau dans la barre des menus ; Mica et Acrylic sur Windows 11 avec un panneau dans la zone de notification. Les deux affichent une petite capsule d’enregistrement pendant que vous parlez.
- **Ouverture à la connexion.**
- **Se met à jour toute seule.** Vérifie GitHub Releases une fois par jour, télécharge la nouvelle version en arrière-plan et l’installe quand vous cliquez sur **Redémarrer pour mettre à jour**, jamais au milieu d’une dictée. Les téléchargements sont vérifiés avec le SHA-256 de GitHub avant tout remplacement. Désactivez-la, ou vérifiez manuellement, dans **Réglages → Général → Mises à jour**.
- **Légère et native.** Une app Swift/SwiftUI d’environ 3 Mo sur macOS et une app WinUI 3 autonome sur Windows. Pas d’Electron, pas de compte et pas de serveur à elle.

<table>
  <tr>
    <td width="50%"><img src="docs/images/models.png" alt="Modèles : un fournisseur et un modèle pour chaque étape, plus un modèle de nettoyage de secours"></td>
    <td width="50%"><img src="docs/images/vocabulary.png" alt="Vocabulaire : vos termes, y compris ceux appris de vos corrections"></td>
  </tr>
  <tr>
    <td align="center">Un fournisseur et un modèle pour chaque étape, avec un modèle de nettoyage de secours</td>
    <td align="center">Le vocabulaire, y compris les mots appris de vos corrections</td>
  </tr>
</table>


## Configuration requise

- **macOS :** macOS 26 ou ultérieur, Apple Silicon.
- **Windows :** Windows 10 (version 2004 ou ultérieure) ou Windows 11, x64 ou ARM64.
- Une clé API pour au moins un fournisseur ([OpenRouter](https://openrouter.ai/keys) est le plus simple : une seule clé couvre les deux étapes).

## Installer sur macOS

### Téléchargement

1. Téléchargez `OpenTypeless-<version>-macOS-arm64.zip` depuis [Releases](https://github.com/Tyler913/OpenTypeless/releases) et décompressez-le.
2. Déplacez **OpenTypeless.app** dans votre dossier **Applications**.
3. L’app n’est pas notarisée par Apple (cela demande un compte développeur payant) : macOS la bloque au premier lancement et peut même dire qu’elle « est endommagée et ne peut pas être ouverte ». Retirez une fois l’indicateur de quarantaine du téléchargement dans le Terminal :

   ```bash
   xattr -dr com.apple.quarantine /Applications/OpenTypeless.app
   ```

   Puis ouvrez-la normalement :

   ```bash
   open /Applications/OpenTypeless.app
   ```

   Vous pouvez aussi essayer de l’ouvrir une fois, puis aller dans **Réglages Système → Confidentialité et sécurité** et cliquer sur **Ouvrir quand même**.

OpenTypeless vit dans la barre des menus (icône de forme d’onde), pas dans le Dock.

Les versions suivantes s’installent depuis l’app (**Réglages → Général → Mises à jour**), sans passer par le Terminal : macOS ne pose la question que pour les apps téléchargées avec un navigateur.

### Compiler depuis les sources

Xcode 26+ doit être installé (les Command Line Tools suffisent pour compiler, mais la compilation emprunte le plugin de macros SwiftUI de `/Applications/Xcode.app`).

```bash
git clone https://github.com/Tyler913/OpenTypeless.git
cd OpenTypeless/macos
scripts/create-signing-cert.sh   # facultatif mais recommandé, une seule fois
scripts/build-app.sh             # compile, signe et installe /Applications/OpenTypeless.app
```

`create-signing-cert.sh` crée une identité locale de signature de code. Sans elle, l’app est signée ad hoc, et macOS redemande les autorisations Accessibilité et Micro après chaque recompilation.

Pour produire un zip de publication au lieu d’installer : `scripts/build-app.sh --package` écrit `macos/dist/OpenTypeless-<version>-macOS-arm64.zip` (signé ad hoc) et affiche son SHA-256.

`build-app.sh` garde exactement une copie de l’app sur la machine. Il assemble le paquet dans un dossier de préparation caché, le déplace dans `/Applications`, désenregistre les anciennes copies de LaunchServices et efface les entrées de confidentialité obsolètes quand la signature change.

### Premier lancement

1. Accordez l’accès au **Micro** et à l’**Accessibilité** (l’Accessibilité sert à détecter le raccourci et à coller le texte).
2. Ajoutez une clé API dans **Réglages → Fournisseurs**.
3. Recommandé si vous utilisez Fn : réglez **Réglages Système → Clavier → « Appuyer sur la touche 🌐 pour »** sur **Ne rien faire**, pour qu’un appui sur Fn n’ouvre pas le sélecteur d’emoji.

## Installer sur Windows

### Téléchargement

1. Téléchargez `OpenTypeless-<version>-windows-x64.zip` (ou `-arm64`) depuis [Releases](https://github.com/Tyler913/OpenTypeless/releases) et décompressez-le où vous voulez (par ex. `%LOCALAPPDATA%\Programs`).
2. Lancez **OpenTypeless.exe**. L’app est autonome : rien d’autre à installer.
3. L’app n’est pas signée, donc SmartScreen peut afficher « Windows a protégé votre ordinateur » : cliquez sur **Informations complémentaires → Exécuter quand même**.

Les versions suivantes s’installent depuis l’app (**Réglages → Général → Mises à jour**) dans le même dossier, sans alerte SmartScreen. Décompressez-la dans un emplacement où vous pouvez écrire, comme `%LOCALAPPDATA%\Programs` ; dans `Program Files`, l’app peut seulement vous renvoyer vers le téléchargement.

OpenTypeless vit dans la **zone de notification** (icône de forme d’onde à côté de l’horloge). Au début, Windows cache les nouvelles icônes dans le menu de dépassement (^) ; faites-la glisser sur la barre des tâches, ou activez-la dans **Paramètres → Personnalisation → Barre des tâches → Autres icônes de la barre d’état système**.

### Compiler depuis les sources

Nécessite le [SDK .NET 10](https://dotnet.microsoft.com/download). Visual Studio est facultatif.

```powershell
# depuis windows\ dans un clone de ce dépôt
powershell -ExecutionPolicy Bypass -File scripts\build.ps1            # teste, compile et installe dans %LOCALAPPDATA%\Programs\OpenTypeless
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package   # écrit windows\dist\OpenTypeless-<version>-windows-x64.zip
```

`build.ps1` garde exactement une copie installée : il arrête l’app en cours, remplace le dossier d’installation, met à jour le raccourci du menu Démarrer et lance la nouvelle version. Ajoutez `-Arch arm64` pour Windows sur ARM.

Nul besoin d’un PC Windows pour développer l’app Windows : GitHub Actions la compile à chaque modification (voir [Intégration continue](#intégration-continue)).

### Premier lancement

1. Ajoutez une clé API dans **Réglages → Fournisseurs**.
2. Vérifiez que **Paramètres → Confidentialité et sécurité → Microphone → Autoriser les applications de bureau à accéder à votre microphone** est activé.
3. Maintenez Ctrl droite et parlez. Windows ne demande aucune autorisation d’accessibilité ; la seule limite est qu’il n’autorise pas le collage dans les apps exécutées en administrateur, où le texte va donc dans le presse-papiers.

## Modèles par défaut

| Étape | Par défaut | Remarques |
|---|---|---|
| Transcription | `microsoft/mai-transcribe-2` (OpenRouter) | N’importe quel modèle de transcription OpenRouter, ou un `/audio/transcriptions` compatible Whisper ailleurs. |
| Nettoyage | `google/gemini-3.8-flash` (OpenRouter) | Le meilleur nettoyage dans nos tests, pour environ 0,005 $ par longue dictée. Options moins chères à essayer : `qwen/qwen3.7-flash`, `google/gemini-3.1-flash-lite`. |
| Nettoyage de secours | `deepseek/deepseek-v4.1-flash` (OpenRouter) | Sollicité seulement quand le modèle principal tarde à démarrer ou échoue. Choisissez un modèle rapide d’un autre éditeur. |

Le raisonnement du modèle de nettoyage est automatiquement désactivé ou réduit au minimum, pour limiter la latence.

## Confidentialité

- L’audio et le texte ne sont envoyés qu’aux fournisseurs que vous configurez. Avec l’**aperçu en direct** activé, macOS reconnaît la parole sur le Mac ; Windows utilise sa propre reconnaissance vocale, qui envoie votre voix à Microsoft lorsque la reconnaissance vocale en ligne est activée (le réglage le précise).
- Les clés API sont stockées dans le trousseau macOS, ou dans le Gestionnaire d’identification Windows (une entrée, `OpenTypeless/credentials`).
- Les totaux d’utilisation de la page d’accueil (mots, temps de parole et coût par jour, sans texte) sont conservés dans `usage.json`, dans le même dossier que les réglages et l’historique. La grille tarifaire d’OpenRouter est téléchargée depuis sa liste publique de modèles (sans clé, rien sur vous) quelques fois par jour.
- L’historique (audio + transcriptions) se trouve dans `~/Library/Application Support/OpenTypeless/Sessions/` sur macOS et dans `%LOCALAPPDATA%\OpenTypeless\` sur Windows. Les enregistrements sont conservés un mois par défaut (page Historique : pas du tout, un jour, une semaine, un mois, un an ou pour toujours) ; ensuite, le texte reste parmi les 200 entrées les plus récentes. Les dictées en échec conservent leur audio pour pouvoir être relancées.
- La recherche de mises à jour envoie une requête par jour à `api.github.com` (sans compte, rien sur vous ni sur vos dictées) ; désactivez-la dans **Réglages → Général → Mises à jour**.
- L’apprentissage à partir de vos corrections lit le champ de texte dans lequel vous avez dicté, uniquement sur votre ordinateur, pendant au plus deux minutes après le collage. Les champs de mot de passe sont ignorés. Il peut être désactivé dans **Vocabulaire et style**.

## Développement

Le dépôt contient les deux apps. Elles partagent la conception, les jeux d’évaluation et ce README ; chacune a son propre code, ses tests et ses scripts de compilation.

```
macos/                      L’app macOS (Swift Package)
  Sources/TypelessCore/       Logique de traitement, sans interface : découpage, WAV, client des fournisseurs, nouvelles tentatives, prompt de nettoyage
  Sources/OpenTypeless/       L’app : raccourci, enregistreur, HUD, réglages, historique, collage, outils CLI
  Tests/                      Tests swift-testing
  scripts/                    Scripts de compilation, de signature, d’icône et de test
windows/                    L’app Windows (solution .NET)
  src/TypelessCore/           La même logique de traitement, portée ligne par ligne
  src/OpenTypeless/           L’app WinUI : hook clavier, enregistreur WASAPI, HUD, zone de notification, réglages, historique, collage
  src/OpenTypeless.Cli/       Outils en ligne de commande (transcrire un fichier, analyse du découpage, évaluation des prompts)
  tests/                      Tests xUnit
  scripts/                    Scripts de compilation, de test et d’icône
eval/                       Jeux de test du nettoyage et guide d’évaluation, partagés par les deux apps
i18n/                       Traductions de l’interface (hors chinois et anglais), partagées par les deux apps
docs/DESIGN.md              Architecture et choix de conception
docs/WINDOWS-PORT.md        Correspondance de chaque fichier et API système macOS sous Windows
docs/images/                Captures d’écran du README
```

Le prompt de nettoyage (`Prompts.swift` / `Prompts.cs`) est identique octet pour octet dans les deux apps ; modifiez-les ensemble et vérifiez le résultat avec [eval/](eval/).

### Traductions

Chaque texte visible par l’utilisateur est écrit directement dans le code avec sa version chinoise et anglaise : `L("有新版本 \(version)", "Version \(version) is available")` en Swift, `L($"有新版本 {version}", $"Version {version} is available")` en C#. Les autres langues se trouvent dans [`i18n/strings.json`](i18n/strings.json), avec le texte anglais comme clé et chaque interpolation numérotée dans l’ordre :

```json
"Version {0} is available": { "ja": "バージョン {0} が利用可能です", "ko": "버전 {0} 사용 가능", … }
```

Les deux apps intègrent ce fichier à la compilation, et un texte sans traduction s’affiche en anglais. Une traduction peut déplacer les marqueurs, mais doit tous les conserver. Après avoir ajouté ou modifié un texte, ajoutez ses traductions et lancez `python3 i18n/check.py` : il liste les entrées manquantes, inutilisées ou mal formées, et la CI l’exécute sur chaque pull request. Pour relire une langue en contexte, faites le rendu de l’interface avec `--snapshot-ui` et `--lang fr` (ou tout autre code de langue).

### macOS

```bash
cd macos
swift build
scripts/test.sh                  # tests unitaires, dont un serveur simulé qui injecte des pannes dans un enregistrement de 130 s
scripts/perf.sh                  # budgets de performance dans un build release (chemin audio, travail sur le thread principal) ; la CI les exécute aussi
```

Modes en ligne de commande utiles du binaire compilé (depuis `macos/`) :

```bash
# Chaîne complète sur un fichier audio ; --realtime envoie l’audio au rythme de la parole, comme un micro en direct
.build/debug/OpenTypeless --transcribe-file speech.m4a --realtime

# Montre où le découpage coupe
.build/debug/OpenTypeless --transcribe-file speech.m4a --chunks-only

# Évalue les prompts et modèles de nettoyage (voir eval/README.md)
.build/debug/OpenTypeless --eval-polish ../eval/polish-holdout.json --model google/gemini-3.8-flash ...

# Rend les pages de réglages, le panneau de la barre des menus et le HUD en PNG (--live les affiche à l’écran, avec un vrai Liquid Glass).
# Les captures du README utilisent un historique d’exemple et --demo, qui considère les autorisations comme accordées.
OPENTYPELESS_SUPPORT_DIR=/path/to/sample-data .build/debug/OpenTypeless --snapshot-ui /tmp/shots --live --demo --lang en
```

### Windows

```powershell
cd windows
dotnet build OpenTypeless.slnx
scripts\test.ps1       # tests unitaires, dont un serveur simulé qui injecte des pannes dans un enregistrement de 130 s
scripts\perf.ps1       # budgets de performance dans un build Release (chemin audio, travail sur le thread d’interface) ; la CI les exécute aussi
```

Outils en ligne de commande (`OpenTypeless.Cli.exe`, livré avec l’app ; il utilise les réglages et les clés de l’app) :

```powershell
# Chaîne complète sur un fichier audio (WAV, MP3, M4A, WMA, FLAC…) ; --realtime envoie l’audio au rythme de la parole
OpenTypeless.Cli --transcribe-file speech.m4a --realtime

# Montre où le découpage coupe
OpenTypeless.Cli --transcribe-file speech.m4a --chunks-only

# Évalue les prompts et modèles de nettoyage (voir eval/README.md)
OpenTypeless.Cli --eval-polish ..\eval\polish-holdout.json --models google/gemini-3.8-flash --out ...

# Rend chaque page de réglages, le panneau de la zone de notification et les états du HUD en PNG
# (--lang en|zh|ja|… pour une seule langue, --demo considère les autorisations comme accordées ; à combiner avec OPENTYPELESS_DATA_DIR et un historique d’exemple)
OpenTypeless --snapshot-ui C:\temp\snapshots
```

Variables d’environnement de développement (Windows) :

| Variable | Effet |
|---|---|
| `OPENROUTER_API_KEY` | Remplace la clé OpenRouter enregistrée. |
| `OPENTYPELESS_DATA_DIR` | Utilise un autre dossier pour les réglages et l’historique (pratique pour les tests). |
| `OPENTYPELESS_DEBUG` | Écrit les événements de raccourci / session / focus dans `debug.log`, dans le dossier de données. |
| `OPENTYPELESS_TEST_AUDIO` | Diffuse en temps réel un WAV mono 16 kHz à la place du micro, pour les tests de bout en bout. |

### Contribuer

`main` est protégée : chaque modification passe par une pull request. Travaillez sur une branche, ouvrez une pull request vers `main` et fusionnez-la une fois la vérification **CI passed** au vert.

### Intégration continue

GitHub Actions ([`.github/workflows/`](.github/workflows/)) ne compile que l’app que vous avez modifiée :

| Vous modifiez | Ce qui s’exécute |
|---|---|
| `macos/**` | **macOS build** sur un runner macOS : les tests, puis le zip de l’app. |
| `windows/**` | **Windows build** sur un runner Windows : les tests, puis les zips x64 et ARM64. |
| `testdata/**` | Les deux compilations : les cas de test partagés (nombre de mots, prix, temps gagné) que lisent les deux suites, pour que les deux apps soient d’accord. |
| `i18n/**` | Les deux compilations : les traductions intégrées par les deux apps. |
| Seulement `docs/`, `eval/`, `README*.md` | Rien à compiler. |

Chaque exécution vérifie aussi les traductions (**Translations**, `python3 i18n/check.py`).

Téléchargez les zips depuis la section **Artifacts** d’une exécution dans l’onglet Actions. **Actions → macOS build / Windows build → Run workflow** lance une compilation manuellement.

Pour publier, augmentez la version dans les deux apps (`macos/scripts/build-app.sh`, `windows/Directory.Build.props`) et poussez un tag, `1.0.2` (ou `V1.0.2`). Cela compile les deux apps et crée une publication en **brouillon**, « OpenTypeless V1.0.2 », avec le zip macOS (signé avec le certificat de publication) et les zips Windows x64 et ARM64. La compilation échoue si la version d’un zip ne correspond pas au tag.

Relisez le brouillon et publiez-le manuellement ; il devient la dernière version. Les copies installées le trouvent en moins d’une journée : l’outil de mise à jour de l’app cherche la publication la plus récente, publiée et non préliminaire, qui contient un zip pour sa plateforme (`OpenTypeless-<version>-macOS-arm64.zip`, `-windows-x64.zip`, `-windows-arm64.zip`). Marquez une publication comme préversion pour qu’elle ne soit pas proposée.

**Signature des publications macOS (une seule fois).** macOS associe les autorisations Accessibilité et Micro à la signature de l’app : les publications doivent donc toujours être signées avec le même certificat, sinon les utilisateurs doivent réaccorder les deux autorisations à chaque mise à jour. Lancez `macos/scripts/create-release-cert.sh`, gardez en lieu sûr le `.p12` qu’il écrit et ajoutez les deux secrets de dépôt qu’il affiche (`MACOS_SIGNING_CERTIFICATE`, `MACOS_SIGNING_CERTIFICATE_PASSWORD`). Les compilations de publication sont alors signées avec lui ; sans ces secrets, elles sont signées ad hoc, avec un avertissement dans l’exécution.

## Remerciements

Inspiré par Typeless. L’approche de l’intégration au système a été apprise des projets libres [VoiceInk](https://github.com/Beingpax/VoiceInk), [OpenLess](https://github.com/Open-Less/openless) et [OpenTypeless (tover0314-w)](https://github.com/tover0314-w/opentypeless). Ce projet est indépendant et n’est affilié à aucun d’entre eux.

## Licence

[MIT](LICENSE)
