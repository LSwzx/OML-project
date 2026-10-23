# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

当前阶段：**阶段 3 — 镜像测速与多线程下载引擎**（控制台验证，不含 UI）。

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
│  ├─ DownloadUrls.cs           # URL 拼接与镜像重写
│  └─ DownloadIntegrityException.cs # SHA-1 不匹配
└─ Auth/
   ├─ IAuthService.cs  # AuthenticateAsync(type, credential)
   ├─ UserProfile.cs   # Username / Uuid / AccessToken / SkinUrl
   └─ AuthTypes.cs     # Microsoft / Offline / LittleSkin
```

## OML.Core 结构

```
OML.Core/
└─ Downloads/
   ├─ BmclApiPathMapper.cs      # 官方地址 → BMCLAPI 地址（实测规则）
   ├─ BmclApiSourceProvider.cs  # 双源 + 测速缓存 + 失败回退
   ├─ MirrorSpeedTester.cs      # 并发 HEAD 测速，3s 超时
   ├─ PclStyleDownloader.cs     # 8 块并发 / 断点续传 / SHA-1 / 重试
   └─ OmlHttpClientFactory.cs   # 无整体超时、连接超时 10s 的共享客户端
```

### BMCLAPI 路径规则（全部实测）

| 官方 Host | 映射方式 | 例 |
| :-- | :-- | :-- |
| `launchermeta.mojang.com` | 路径原样 | `/mc/game/version_manifest_v2.json` |
| `piston-meta.mojang.com` | 路径原样 | `/v1/packages/<sha>/1.21.1.json` |
| `piston-data.mojang.com` | 路径原样 | `/v1/objects/<sha>/client.jar` |
| `libraries.minecraft.net` | 插入 `maven/` | `/a/b.jar` → `/maven/a/b.jar` |
| `resources.download.minecraft.net` | 插入 `assets/` | `/b6/<hash>` → `/assets/b6/<hash>` |

> 注意：BMCLAPI **保留** `/mc/game/` 前缀，网上流传的"镜像没有该前缀"不成立。
> 另外 BMCLAPI 的 `/maven/` 会 302 到签名 CDN，必须允许自动重定向，且该 CDN 支持 Range。

### 下载引擎行为

- **分块**：`> 5MB` 且 `HttpClient` 可用 Range 时切 8 块；命中 200（服务端忽略 Range）自动退化单流。
- **续传**：每块一个 `{file}.part{i}`，续传起点 = 该文件当前长度；单流用 `{file}.part`。
- **进度**：8 块增量汇总，100ms 节流后上报 `0.0 - 1.0`；续传首帧即非零。
- **校验**：合并时流式算 SHA-1（不二次读盘），不匹配抛 `DownloadIntegrityException` 并清理。
- **重试**：默认 3 次指数退避；`429 / 503` 单独加长退避（3s 起）并尊重 `Retry-After`。
- **限流**：分块默认错峰 120ms 启动，避免 8 个请求同时触发镜像站 429。

## 构建与运行

```powershell
dotnet build OML.sln
dotnet run --project OML.Test              # 含联网验证（会真实下载 ~9MB）
dotnet run --project OML.Test -- --no-network   # 仅离线模型断言
```
