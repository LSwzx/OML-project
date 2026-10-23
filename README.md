# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

当前阶段：**阶段 5 — 依赖解析与资源补全**（控制台验证，不含 UI）。

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
│  ├─ DownloadBatchRunner.cs    # 批量下载：并发上限 + 失败隔离
│  └─ OmlHttpClientFactory.cs   # 无整体超时、连接超时 10s 的共享客户端
├─ Serialization/
│  └─ OmlJsonOptions.cs         # 大小写不敏感 / 枚举字符串 / 只读配置
├─ Assets/
│  └─ AssetDownloader.cs        # 资源索引缓存 + 按 hash 补全
└─ Versioning/
   ├─ GameDirectory.cs          # .minecraft 布局与版本隔离
   ├─ PlatformInfo.cs           # os/arch 归一（windows / x86_64 ...）
   ├─ RuleEvaluator.cs          # rules 求值（os / arch / 版本正则 / features）
   ├─ LibraryResolver.cs        # classpath + 下载任务 + natives 选择
   ├─ NativesExtractor.cs       # 解压 .dll，跳过 META-INF，防 Zip Slip
   ├─ VersionRepository.cs      # 清单抓取 + 缓存 + 继承链解析
   └─ VersionInheritor.cs       # inheritsFrom 合并
```

### 规则求值

- 无 `rules` → 允许；有 `rules` → 初始拒绝，逐条评估，匹配规则按 `action` 改写结果。
- `features` 中未声明的键按 `false` 处理（Mojang 语义），留待 UI 勾选项接线。
- `os.version` 是正则，带 200ms 超时，坏正则视为不匹配而不是崩溃。

### natives 的架构陷阱（实测发现）

1.21.1 里三条本地库：

```
org.lwjgl:lwjgl:3.3.3:natives-windows        rules: os.name == windows
org.lwjgl:lwjgl:3.3.3:natives-windows-x86    rules: os.name == windows
org.lwjgl:lwjgl:3.3.3:natives-windows-arm64  rules: os.name == windows
```

**规则完全没提架构**。只按 rules 过滤会把 x64/x86/arm64 三种 dll 一起解压到
`versions/<id>/natives/`，同名文件互相覆盖 → `UnsatisfiedLinkError`。
因此 `LibraryResolver` 按分类器后缀二次筛选，同一 `group:artifact` 只保留一个变体：
精确匹配当前架构优先，其次回退通用版；旧格式的 `natives-windows-${arch}` 占位符按 64/32 替换。

解压还有第二个坑：LWJGL 3 的 natives jar 内部是 `windows/x64/org/lwjgl/lwjgl.dll` 这种嵌套结构，
按原路径解压后 `-Djava.library.path=<natives>` 不会递归查找，游戏照样找不到 dll。
所以 `NativesExtractor` 默认**压平**到目标目录根部；同时 natives jar 也会进 classpath——
官方启动器就是这么做的（LWJGL 的 `SharedLibraryLoader` 支持直接从 jar 内加载），两条路互为兜底。

### 资源补全

- 内容寻址：落点固定 `assets/objects/<hash 前两位>/<hash>`，跨版本共享。
- 已存在且大小一致的资源直接跳过（逐个算 SHA-1 会让 3911 个资源拖垮启动速度）。
- 批量下载并发默认 16（`DownloadBatchRunner`），单个失败不中断整批，失败清单可重试。
- `AssetDownloader.DownloadAsync` 默认带 2 轮失败重试：镜像站 429 是常态，
  单轮失败就放弃会让"补全几千个资源"必然留缺口。

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
dotnet run --project OML.Test                     # 全量自检（约 110MB 真实流量）
dotnet run --project OML.Test -- --no-network     # 仅离线断言
dotnet run --project OML.Test -- --stage5-only    # 只跑阶段 5（复用已下载的库文件）
```
