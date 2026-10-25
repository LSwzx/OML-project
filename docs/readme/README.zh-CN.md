# OML · Onion Minecraft Launcher

**语言：** **简体中文** · [繁體中文](README.zh-TW.md) · [简体文言](README.zh-classical.md) · [繁體文言](README.zh-classical-t.md) · [English (US)](README.en-US.md) · [English (UK)](README.en-GB.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Русский](README.ru.md)

面向 Windows 10/11 x64 的 Minecraft 启动器，基于 .NET 8 与 WPF。
支持安装原版、Forge、Fabric、NeoForge、OptiFine 与整合包，内置 Modrinth 搜索，
能识别并增量修复已有游戏目录（不覆盖用户文件），缺 Java 时自动下载，并通过 Velopack 自动更新。

> 本项目主要由 DeepSeek-V4.1-Flash 1017 开发。

当前版本：**1.1.2**（正式版）

## 功能

- **版本：** 安装原版、Forge、Fabric、NeoForge、OptiFine，导入 `.mrpack` 整合包
- **兼容：** 识别 PCL2 管理的 `.minecraft` 目录；损坏的版本 JSON 会给出具体原因
- **安全：** 已存在的 `versions/<id>/<id>.json` 绝不删除或覆盖，只增量补全缺失的库与资源
- **Java：** 自动探测、按版本要求下载运行时，启动前校验配置路径的主版本
- **内容：** Modrinth 模组 / 光影 / 整合包搜索，递归解析前置依赖
- **界面：** 三栏主页、3D 皮肤预览（Slim/Classic、双层皮肤）、全局任务队列（取消 / 重试 / 卡死看门狗）
- **更新：** 安装版启动时检查 GitHub Releases，可后台下载并重启升级

## 安装

从 [GitHub Releases](https://github.com/LSwzx/OML-project/releases/latest) 下载：

| 安装包 | 说明 |
|:--|:--|
| `OML-win-1.1.2-Installer.exe` | 带目录选择页的安装向导（推荐） |
| `OML-win-1.1.2-Portable.zip` | 免安装，解压即用 |

建议装到用户可写目录（如 `D:\Apps\OML`）；装在 `Program Files` 会导致自动更新失败。
安装版支持自动更新，便携版需手动替换。

数据位置：

| 内容 | 路径 |
|:--|:--|
| 启动器配置 | `%AppData%\OML\launcher_config.json` |
| Java 运行时 | `%AppData%\OML\runtimes\` |
| 默认游戏目录 | `%AppData%\.minecraft`（可用 `--game-dir` 覆盖） |

## 从源码构建

环境：Windows 10/11 x64、[.NET SDK 8.0+](https://dotnet.microsoft.com/download)。

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
dotnet run --project OML.Test -- --no-network

# 自包含单文件发布（约 71 MB）
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

`PublishTrimmed` 必须为 `false`：WPF 不支持 IL 裁剪。

## 许可

本项目基于 [GPL-3.0-only](../../LICENSE) 开源，Copyright (C) 2026 LSwzx。

> Note: Versions prior to v1.0.2 were released under the MIT License. Future versions are licensed under GPL-3.0.

内置的 `SkinView` 组件仍为 MIT 许可，详见
[THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md)。
