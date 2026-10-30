# OML · Onion Launcher

**Languages:** [简体中文](../../README.md) · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · **English (UK)** · [Français](README.fr.md) · [Deutsch](README.de.md) · [Русский](README.ru.md)

A Minecraft launcher for Windows 10/11 x64, built with .NET 8 and WPF.
OML installs vanilla, Forge, Fabric, NeoForge, OptiFine and modpacks, browses Modrinth,
repairs incomplete installations without overwriting existing files, provisions Java
automatically, and updates itself through Velopack.

> This project is developed mainly by DeepSeek-V4.1-Flash 1017.

Current version: **1.1.2** (stable)

## Features

- **Versions:** install vanilla, Forge, Fabric, NeoForge and OptiFine; import `.mrpack` modpacks
- **Compatibility:** recognises PCL2-managed `.minecraft` folders; a damaged version JSON is reported with a reason rather than silently skipped
- **Safety:** an existing `versions/<id>/<id>.json` is never deleted or overwritten — missing libraries and assets are repaired incrementally
- **Java:** detects installed runtimes, downloads the required major version when missing, and validates the configured path before launching
- **Content:** Modrinth search for mods, shaders and modpacks, with recursive dependency resolution
- **Interface:** three-column dashboard, 3D skin preview (Slim/Classic, two-layer skins), global task queue with cancel/retry and a stall watchdog
- **Updates:** installed builds check GitHub Releases at start-up and can download and restart in the background

## Installation

Download the latest release from
[GitHub Releases](https://github.com/LSwzx/OML-project/releases/latest):

| Package | Description |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | Setup wizard with a directory selection page (recommended) |
| `OML-win-1.1.2-Portable.zip` | Portable build, extract and run |

Choose a user-writable directory such as `D:\Apps\OML`; installing under `Program Files`
prevents the updater from writing files. The installed build updates itself automatically;
the portable build must be replaced manually.

Data locations:

| Item | Path |
|:--|:--|
| Launcher configuration | `%AppData%\OML\launcher_config.json` |
| Downloaded Java runtimes | `%AppData%\OML\runtimes\` |
| Default game directory | `%AppData%\.minecraft` (override with `--game-dir`) |

## Build from source

Requirements: Windows 10/11 x64 and the [.NET SDK 8.0+](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# Self-contained single-file build (about 71 MB)
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` must stay `false`: WPF does not support IL trimming.

## Licence

This project is licensed under [GPL-3.0-only](../../LICENSE), Copyright (C) 2026 LSwzx.

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

The bundled `SkinView` component remains MIT-licensed; see
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md).
