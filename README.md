# Onion Minecraft Launcher (OML)

Windows 平台的 Minecraft 启动器。架构对标 HMCL（分层解耦），下载引擎对标 PCL2（多线程、断点续传、镜像测速）。

[注] 本启动器主要由 DeepSeek-V4.1-Flash 1017 负责开发。

当前阶段：**阶段 10 — LittleSkin / Yggdrasil 外置登录**（控制台验证，不含 UI）。

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
├─ Java/
│  ├─ JavaScanner.cs            # JAVA_HOME / 注册表 / 常见目录 / PATH 探测
│  └─ JavaRuntimeDownloader.cs  # 按 Mojang 清单逐文件下载 JRE
├─ Auth/
│  ├─ OfflineAuthService.cs     # 离线 UUID（UUIDv3）+ 随机令牌
│  ├─ MicrosoftAuthService.cs   # 设备码流 → XBL → XSTS → Minecraft
│  └─ YggdrasilAuthService.cs   # 外置登录（LittleSkin）
├─ Configuration/
│  └─ ConfigManager.cs          # launcher_config.json 读写（原子写入）
├─ Modrinth/
│  └─ ModrinthClient.cs         # 搜索 / 项目 / 版本 / 直链（无 API Key）
├─ Launching/
│  ├─ LaunchSession.cs          # 门面：检查→补全→解压→预检→启动→退出
│  ├─ LaunchOptions.cs          # 启动输入与构建结果
│  ├─ LaunchArgumentBuilder.cs  # JVM 参数 + 变量替换 + 条件参数
│  ├─ LaunchPrechecker.cs       # classpath / 主 jar / natives 预检
│  └─ JvmLauncher.cs            # 拉起进程 + 日志重定向
└─ Versioning/
   ├─ GameDirectory.cs          # .minecraft 布局与版本隔离
   ├─ LocalVersionScanner.cs    # 本地版本扫描 + Forge/Fabric 识别
   ├─ VersionInstaller.cs       # 一键安装：库/jar/资源/natives/Java
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

### 启动链路

1. **Java 探测**：`JAVA_HOME` → 注册表（`JavaSoft\JDK`、`Eclipse Adoptium`、`Microsoft\JDK` 等，64/32 位视图）
   → 常见安装目录 → `PATH`。版本用 `java -version` 实测解析：Java 8 的 `"1.8.0_381"` 折叠成 8，
   Java 9+ 直接取主版本号。绝不写死版本号（1.21.1 要 21，更新的版本已经要 25）。
2. **参数构建**：模板 JVM 参数 → 内存/品牌参数 → `-cp` → 主类 → 游戏参数。
   `${auth_player_name}`、`${game_directory}` 等全部替换；条件参数按 `rules` 求值，
   `--demo`/`--width` 由 features 决定。
3. **classpath 顺序**：客户端 jar **必须在最前面**。少了它 Java 会抛
   `ClassNotFoundException: net.minecraft.client.main.Main`，而库全都在时看起来"文件都齐了"。
4. **预检**：classpath 每个文件、客户端 jar、natives 目录里的 dll，缺任何一项立刻抛
   `LaunchPrecheckException` 并列出缺失清单。
5. **进程与日志**：`UseShellExecute=false` + `ArgumentList`（路径含空格也安全），
   stdout/stderr 同时重定向并实时落盘。

> **日志文件名是 `logs/oml-launch.log`，不是 `latest.log`。**
> 实测：启动器占住 `latest.log` 时，Minecraft 的 Log4j 会报
> `Unable to delete file ... latest.log: 另一个程序正在使用此文件`，日志轮转失败。
> `latest.log` 归游戏所有，启动器捕获的进程输出另存一份，两者互补。
> 确需覆盖可用 `new JvmLauncher(dir, "latest.log")`。

### Java 运行时下载

Mojang **不再以单个压缩包分发 Java**，而是给一份逐文件清单：

```
all.json (平台 × 组件)
  → 组件 manifest（~400 个文件）
    → 每个文件一个 piston-data 地址
```

所以 `JavaRuntimeDownloader` 的"解压"其实是按相对路径把 400 来个文件铺到
`runtimes/<component>/`（实测 `java-runtime-gamma`：403 个文件 / 87MB）。
平台键映射：`windows-x64` / `windows-x86` / `windows-arm64` / `linux` / `linux-i386` /
`mac-os` / `mac-os-arm64`。

### 版本安装与扫描

- `VersionInstaller`：解析 → 依赖库 → 客户端 jar → 资源 → natives →（可选）Java 运行时。
  用 `InstallOptions` 控制是否下载资源、`AssetLimit` 限额、是否 `EnsureJava`。
- `LocalVersionScanner`：遍历 `versions/*/<id>.json`；**坏 JSON 跳过而不是整体失败**
  （装 Forge 失败、手改文件都会留下残缺 JSON）。加载器识别靠特征库
  （`net.minecraftforge:` / `net.neoforged:` / `net.fabricmc:` / `org.quiltmc:`），
  没有特征库时退回 `inheritsFrom`。
- 规模参考：1.20.1 有 50 个依赖、3598 个资源对象（约 650MB）。测试里用
  `AssetLimit=200` 控制时长，但"完整任务列表"的断言仍然校验全量 3598 个。

### 微软登录与 Modrinth

**微软正版**（`MicrosoftAuthService`）：设备代码流，链路
`devicecode → token（轮询）→ Xbox Live → XSTS → Minecraft login → profile`。

- `Client ID` 必须是你自己注册的 Azure 应用（Entra ID → 应用注册 → 允许公共客户端流），
  通过 `OML_MICROSOFT_CLIENT_ID` 环境变量或构造参数注入。项目**不预置**任何第三方 ID。
- 轮询正确处理 `authorization_pending`（继续等）、`slow_down`（间隔 +5s）、
  `authorization_declined` / `expired_token`（终止）。
- XSTS 失败码翻译成人话：`2148916233` 无 Xbox 档案、`2148916235` 地区不支持、`2148916238` 未成年家庭组。
- `RefreshAsync` 用 refresh token 静默续期，微软轮换时保存新的 refresh token。

> **重要：Mojang 对第三方应用有白名单。**
> 自建 Azure 应用可以拿到微软令牌、也能通过 Xbox / XSTS，但调用
> `login_with_xbox` 时会返回 `403 Invalid app registration`。
> 需要在 https://aka.ms/mce-reviewappid 提交审核，通过后同一个 Client ID 才能使用 Minecraft API。
> 代码已把这个错误翻译成可执行的提示（`MicrosoftAuthService.DescribeMinecraftApiError`）。
>
> 实测链路（用真实账号授权后）：设备码 ✅ → Xbox Live ✅ → XSTS ✅ → Minecraft 登录 ❌（白名单）。
> 交互式验证：`dotnet run --project OML.Test -- --ms-login`。

**Modrinth**（`ModrinthClient`）：无 API Key，`snake_case` JSON，请求带 UA。

```csharp
var hits    = await modrinth.SearchAsync("JEI", limit: 5, projectType: "mod", gameVersion: "1.20.1", loader: "fabric");
var latest  = await modrinth.GetLatestVersionAsync("jei", "1.20.1", "fabric");
var file    = ModrinthClient.GetPrimaryFile(latest);   // file.Url / file.Hashes.Sha1
```

facets 形如 `[["project_type:mod"],["versions:1.20.1"],["categories:fabric"]]`；
429/5xx 自动重试并尊重 `Retry-After`。

**外置登录**（`YggdrasilAuthService`）：默认 `https://littleskin.cn/api/yggdrasil`。

```csharp
using var ygg = new YggdrasilAuthService();
var session = await ygg.LoginAsync(email, password);   // accessToken + 角色 + 皮肤
await ygg.RefreshAsync(session.AccessToken);           // 静默续期
var ok = await ygg.ValidateAsync(session.AccessToken); // true/false
```

- **错误码坑**：Yggdrasil 的"密码错误"和"令牌失效"都返回 **403**（`ForbiddenOperationException`），
  不是 401。判断用 `YggdrasilException.IsCredentialError`。
- 响应里的 UUID 是 32 位无连字符形态，转 `UserProfile` 时统一格式化。
- 登录后会额外拉 `/sessionserver/session/minecraft/profile/{uuid}` 解析皮肤 URL 与模型
  （`classic`/`slim`）；皮肤失败不影响登录。

### 门面与配置（UI 层契约）

```csharp
var manager = new ConfigManager(launcherRoot);          // 默认程序所在目录
var config  = await manager.LoadAsync();                // 坏文件自动备份并回退默认
config.UserProfile = await new OfflineAuthService()
        .AuthenticateAsync(AuthTypes.Offline, "Player");

using var session = new LaunchSession();
session.LogReceived += line => ui.AppendLog(line);
var info = await session.LaunchGameAsync(config, progress);
var exit = await info.WaitForExitAsync();               // ExitCode + CrashReportPath
```

- `LaunchProgress` 的 `Progress` 是**阶段内**百分比：下载 54 个库与几千个资源耗时差几个数量级，
  硬凑全局百分比只会误导用户。UI 按 `Stage` 切换文案与进度条即可。
- 进程起来立即返回；退出码与崩溃报告（`crash-reports/*.txt` 中本次启动后生成的那份）通过
  `WaitForExitAsync()` 获取。只认启动时间之后的文件，避免把上次的旧报告当成本次崩溃。
- 配置写在**启动器根目录**的 `launcher_config.json`，不进 `.minecraft`：游戏目录会被玩家
  清理或迁移，配置不该跟着丢。写入走临时文件替换，断电不会留下半截 JSON。

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
- **读超时**：单次读取/等响应头 30s 无数据即抛出并重试。`HttpClient` 整体超时设为无限，
  没有这一层兜底，僵死连接会让"正在下载"永久卡住（实测出现过 6/8 分块完成后另两块停住）。
- **限流**：分块默认错峰 120ms 启动，避免 8 个请求同时触发镜像站 429。

## 构建与运行

```powershell
dotnet build OML.sln
dotnet run --project OML.Test                     # 全量自检（约 110MB 真实流量）
dotnet run --project OML.Test -- --no-network     # 仅离线断言
dotnet run --project OML.Test -- --stage5-only    # 只跑阶段 5（复用已下载的库文件）
```
