# OML · Onion Launcher

**Langues :** [简体中文](../../README.md) · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · **Français** · [Deutsch](README.de.md) · [Русский](README.ru.md)

Un lanceur Minecraft pour Windows 10/11 x64, développé en .NET 8 et WPF.
OML installe les versions vanilla, Forge, Fabric, NeoForge, OptiFine et les modpacks,
recherche des contenus sur Modrinth, répare les installations incomplètes sans écraser
les fichiers existants, télécharge Java automatiquement et se met à jour via Velopack.

> Ce projet est principalement développé par DeepSeek-V4.1-Flash 1017.

Version actuelle : **1.1.2** (stable)

## Fonctionnalités

- **Versions :** installation de vanilla, Forge, Fabric, NeoForge et OptiFine ; import de modpacks `.mrpack`
- **Compatibilité :** reconnaît les dossiers `.minecraft` gérés par PCL2 ; un JSON de version corrompu est signalé avec sa cause au lieu d'être ignoré
- **Sécurité :** un fichier `versions/<id>/<id>.json` existant n'est jamais supprimé ni remplacé — seules les bibliothèques et ressources manquantes sont réparées
- **Java :** détection des runtimes installés, téléchargement de la version majeure requise et validation du chemin configuré avant le lancement
- **Contenus :** recherche Modrinth pour les mods, shaders et modpacks, avec résolution récursive des dépendances
- **Interface :** tableau de bord à trois colonnes, aperçu 3D du skin (Slim/Classic, deux couches), file de tâches globale avec annulation, reprise et chien de garde
- **Mises à jour :** la version installée vérifie les releases GitHub au démarrage, télécharge et redémarre en arrière-plan

## Installation

Téléchargez la dernière version depuis
[GitHub Releases](https://github.com/LSwzx/OML-project/releases/latest) :

| Paquet | Description |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | Assistant d'installation avec choix du dossier (recommandé) |
| `OML-win-1.1.2-Portable.zip` | Version portable, à décompresser et lancer |

Choisissez un dossier accessible en écriture, par exemple `D:\Apps\OML` ; une installation
dans `Program Files` empêcherait le programme de mise à jour d'écrire. La version installée
se met à jour automatiquement ; la version portable doit être remplacée manuellement.

Emplacements des données :

| Élément | Chemin |
|:--|:--|
| Configuration | `%AppData%\OML\launcher_config.json` |
| Runtimes Java téléchargés | `%AppData%\OML\runtimes\` |
| Dossier de jeu par défaut | `%AppData%\.minecraft` (modifiable avec `--game-dir`) |

## Compilation

Prérequis : Windows 10/11 x64 et le [.NET SDK 8.0+](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# Publication autonome en fichier unique (environ 71 Mo)
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` doit rester à `false` : WPF ne prend pas en charge le découpage IL.

## Licence

Ce projet est distribué sous [GPL-3.0-only](../../LICENSE), Copyright (C) 2026 LSwzx.

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

Le composant `SkinView` inclus reste sous licence MIT ; voir
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md).
