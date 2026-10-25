# OML · Onion Minecraft Launcher

**語言：** [简体中文](../../README.md) · **繁體中文** · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Русский](README.ru.md)

面向 Windows 10/11 x64 的 Minecraft 啟動器，基於 .NET 8 與 WPF。
支援安裝原版、Forge、Fabric、NeoForge、OptiFine 與整合包，內建 Modrinth 搜尋，
能辨識並增量修復既有的遊戲目錄（不覆寫使用者檔案），缺少 Java 時自動下載，並透過 Velopack 自動更新。

> 本專案主要由 DeepSeek-V4.1-Flash 1017 開發。

目前版本：**1.1.2**（正式版）

## 功能

- **版本：** 安裝原版、Forge、Fabric、NeoForge、OptiFine，匯入 `.mrpack` 整合包
- **相容：** 辨識 PCL2 管理的 `.minecraft` 目錄；損壞的版本 JSON 會顯示具體原因
- **安全：** 既有的 `versions/<id>/<id>.json` 絕不刪除或覆寫，只增量補齊缺少的函式庫與資源
- **Java：** 自動偵測、依版本需求下載執行環境，啟動前驗證設定路徑的主版本
- **內容：** Modrinth 模組／光影／整合包搜尋，遞迴解析前置依賴
- **介面：** 三欄首頁、3D 面板預覽（Slim/Classic、雙層面板）、全域任務佇列（取消／重試／卡死看門狗）
- **更新：** 安裝版啟動時檢查 GitHub Releases，可背景下載並重新啟動升級

## 安裝

從 [GitHub Releases](https://github.com/LSwzx/OML_test/releases/latest) 下載：

| 安裝包 | 說明 |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | 含目錄選擇頁的安裝精靈（建議） |
| `OML-win-1.1.2-Portable.zip` | 免安裝，解壓即用 |

建議安裝到使用者可寫入的目錄（例如 `D:\Apps\OML`）；装在 `Program Files` 會導致自動更新失敗。
安裝版支援自動更新，可攜版需手動替換。

資料位置：

| 內容 | 路徑 |
|:--|:--|
| 啟動器設定 | `%AppData%\OML\launcher_config.json` |
| Java 執行環境 | `%AppData%\OML\runtimes\` |
| 預設遊戲目錄 | `%AppData%\.minecraft`（可用 `--game-dir` 覆寫） |

## 從原始碼建置

環境：Windows 10/11 x64、[.NET SDK 8.0+](https://dotnet.microsoft.com/download)。

```powershell
git clone https://github.com/LSwzx/OML_test.git
cd OML_test

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# 自包含單檔發佈（約 71 MB）
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` 必須為 `false`：WPF 不支援 IL 裁剪。

## 授權

本專案依 [GPL-3.0-only](../../LICENSE) 開源，Copyright (C) 2026 LSwzx。

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

內建的 `SkinView` 元件仍為 MIT 授權，詳見
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md)。
