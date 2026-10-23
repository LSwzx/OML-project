# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

当前阶段：**阶段 4 — 版本清单管理与游戏目录布局**（控制台验证，不含 UI）。

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
├─ Downloads/
│  ├─ BmclApiPathMapper.cs      # 官方地址 → BMCLAPI 地址（实测规则）
│  ├─ BmclApiSourceProvider.cs  # 双源 + 测速缓存 + 失败回退
│  ├─ MirrorSpeedTester.cs      # 并发 HEAD 测速，3s 超时
│  ├─ PclStyleDownloader.cs     # 8 块并发 / 断点续传 / SHA-1 / 重试
│  └─ OmlHttpClientFactory.cs   # 无整体超时、连接超时 10s 的共享客户端
├─ Serialization/
│  └─ OmlJsonOptions.cs         # 大小写不敏感 / 枚举字符串 / 只读配置
└─ Versioning/
   ├─ GameDirectory.cs          # .minecraft 布局与版本隔离
   ├─ VersionRepository.cs      # 清单抓取 + 缓存 + 继承链解析
   └─ VersionInheritor.cs       # inheritsFrom 合并
```

### 版本缓存（离网可用）

| 内容 | 落点 | 新鲜期 |
| :-- | :-- | :-- |
| 版本清单 | `versions/version_manifest_v2.json` | 30 分钟（过期仍可作为断网兜底） |
| 版本详情 | `versions/<id>/<id>.json` | 永久（内容不可变） |
| 客户端 jar | `versions/<id>/<id>.jar` | — |

任何网络异常都会回退本地缓存；无缓存才抛异常。缓存目录与游戏目录同构，不做重复存储。

### inheritsFrom 合并规则

- **libraries**：以 `group:artifact[:classifier]` 为身份键（**不含版本号**），子级覆盖父级、保留父级位置，新库追加末尾。
  按完整 `name` 判重会让同一库的多个版本同时进 classpath，运行期直接 `NoSuchMethodError`。
- **arguments**：父在前、子在后的顺序拼接。
- **mainClass**：子级非空则覆盖。
- **assetIndex / assets / downloads / javaVersion / logging**：子级缺省时继承。
- 继承链有环检测与 16 层深度上限。

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

- **分块**：`> 5MB` 且服务端支持 Range 时切 8 块；命中 200（服务端忽略 Range）自动退化单流。
- **续传**：每块一个 `{file}.part{i}`，续传起点 = 该文件当前长度；单流用 `{file}.part`。
- **进度**：8 块增量汇总，100ms 节流后上报 `0.0 - 1.0`；续传首帧即非零。
- **校验**：合并时流式算 SHA-1（不二次读盘），不匹配抛 `DownloadIntegrityException` 并清理。
- **重试**：默认 3 次指数退避；`429 / 503` 单独加长退避（3s 起）并尊重 `Retry-After`。
- **限流**：分块默认错峰 120ms 启动，避免 8 个请求同时触发镜像站 429。

## 构建与运行

```powershell
dotnet build OML.sln
dotnet run --project OML.Test                    # 全量自检（会真实下载 ~50MB）
dotnet run --project OML.Test -- --no-network    # 仅离线断言
```
