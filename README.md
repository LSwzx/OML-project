# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

当前阶段：**阶段 2 — 数据模型与核心接口**（控制台骨架，不含 UI）。

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

## OML.Abstractions 结构

```
OML.Abstractions/
├─ Models/
│  ├─ VersionManifest.cs   # latest + versions[]（version_manifest_v2）
│  ├─ VersionDetail.cs     # id/type/mainClass/assets/arguments/libraries/downloads/logging…
│  ├─ Library.cs           # maven 坐标、artifact/classifiers、natives、rules、extract
│  └─ MavenCoordinate.cs   # 坐标解析与相对路径推导
├─ Downloads/
│  ├─ IDownloadEngine.cs        # 单任务下载契约
│  ├─ IDownloadSourceProvider.cs# 多源测速与择优
│  ├─ DownloadTask.cs           # Url / SavePath / ExpectedSize / Sha1
│  ├─ DownloadSource.cs         # 源描述（名称、基地址、优先级、是否官方）
│  └─ DownloadUrls.cs           # URL 拼接与镜像重写
└─ Auth/
   ├─ IAuthService.cs  # AuthenticateAsync(type, credential)
   ├─ UserProfile.cs   # Username / Uuid / AccessToken / SkinUrl
   └─ AuthTypes.cs     # Microsoft / Offline / LittleSkin
```

设计约定：

- **抽象层只描述，不决策**。`arguments` 保留为 `JsonElement`，参数组装规则交给 Core。
- **接口接受 `CancellationToken`**，且允许 `progress` 为 `null`。
- **测速失败不得抛异常**，必须回退官方源（写在接口契约里）。

## 构建与运行

```powershell
dotnet build OML.sln
dotnet run --project OML.Test
```
