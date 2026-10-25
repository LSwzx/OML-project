# OML · 洋蔥 Minecraft 啟動器

**語言：** [简体中文](../../README.md) · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · **繁體文言** · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Русский](README.ru.md)

OML 者，Windows 十、十一 之 Minecraft 啟動器也。其架構法乎 HMCL，下載之術效乎 PCL2，
以 .NET 八與 WPF 成之。

可裝原版、Forge、Fabric、NeoForge、OptiFine 與整合包；能識 PCL2 所治之目錄；
版本有闕，則補其缺而不覆其舊；Java 不足，則自下之；安裝版更能自新。

> 此專案多由 DeepSeek-V4.1-Flash 1017 所為。

今本：**1.1.2**（正版）

## 功用

- 原版、Forge、Fabric、NeoForge、OptiFine、整合包，皆可安裝，亦可納 `.mrpack` 之包。
- 能識 PCL2 所治之 `.minecraft` 目錄；版本 JSON 有損，必告其由，不默棄之。
- 既有 `versions/<id>/<id>.json`，不刪不覆，唯補其闕。
- Java 缺則自下之，啟動之前先驗其版本。
- Modrinth 之中，可搜模組、光影、整合包，並究其前置。
- 首頁三欄，皮膚三維可觀；諸務列於一隊，可止、可復、可察其滯。
- 安裝版可自新，便攜版則須手易。

## 安裝

自 [GitHub Releases](https://github.com/LSwzx/OML-project/releases/latest) 下之：

| 包 | 說 |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | 有目錄選擇之安裝嚮導（宜用） |
| `OML-win-1.1.2-Portable.zip` | 免安裝，解壓即用 |

宜置於使用者可寫之處，如 `D:\Apps\OML`；若置於 `Program Files`，自新必敗。
安裝版可自新，便攜版須手易。

數據所在：

| 物 | 路 |
|:--|:--|
| 啟動器之設定 | `%AppData%\OML\launcher_config.json` |
| Java 執行時 | `%AppData%\OML\runtimes\` |
| 預設遊戲目錄 | `%AppData%\.minecraft`（可用 `--game-dir` 改之） |

## 構建

需 Windows 十、十一 x64 與 [.NET SDK 八以上](https://dotnet.microsoft.com/download)。

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# 自足單檔（約 71 MB）
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` 必為 `false`，蓋 WPF 不容 IL 裁剪也。

## 許可

本專案以 [GPL-3.0-only](../../LICENSE) 行於世，Copyright (C) 2026 LSwzx。

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

所納 `SkinView` 仍為 MIT，詳見 [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md)。
