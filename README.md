# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

当前阶段：**阶段 1 — 项目初始化**（控制台骨架，不含 UI）。

## 解决方案结构

```
OML.sln
├─ OML.Abstractions/   # 接口与模型（不依赖任何项目）
├─ OML.Core/           # 核心逻辑，引用 Abstractions
└─ OML.Test/           # 控制台验证宿主，引用 Core + Abstractions
```

依赖方向严格单向：`OML.Test → OML.Core → OML.Abstractions`。

| 项目 | 类型 | TFM |
| :-- | :-- | :-- |
| OML.Abstractions | classlib | net8.0 |
| OML.Core | classlib | net8.0 |
| OML.Test | console (Exe) | net8.0 |

## 构建与运行

```powershell
dotnet build OML.sln
dotnet run --project OML.Test
```
