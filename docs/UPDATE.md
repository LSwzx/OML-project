# 单文件发布与本地更新测试

## 一、发布单文件 EXE

```powershell
cd D:\dshprogram\OML_test
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish
```

产物：`publish\OML.Desktop.exe`（约 71 MB，自包含 + 单文件 + 压缩）。

`OML.Desktop.csproj` 里的 Release 配置：

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<PublishTrimmed>false</PublishTrimmed>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
```

**为什么 `PublishTrimmed=false`**：WPF 大量依赖反射加载 XAML/BAML 与控件模板，
IL 裁剪会把它们判成"未使用"而删掉，表现是启动即 `XamlParseException`。
.NET 8 的 WPF 官方也不支持裁剪，所以这里必须关闭。

## 二、Velopack 打包（本地模拟发布）

```powershell
# 安装 CLI（一次）
dotnet tool install -g vpk

# 1.0.0 版本
vpk pack -u OML -v 1.0.0 -p publish -e OML.Desktop.exe

# 发布目录里会出现
#   Releases\OML-win-Setup.exe        ← 安装包
#   Releases\OML-1.0.0-full.nupkg     ← 完整包
#   Releases\releases.win.json        ← 更新索引
```

改一处代码，重新发布 1.0.1：

```powershell
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish-1.0.1
vpk pack -u OML -v 1.0.1 -p publish-1.0.1 -e OML.Desktop.exe -o releases-1.0.1
```

## 三、本地模拟更新（不依赖服务器）

Velopack 的更新源可以用本地目录（`SimpleFileSource`），也可以用 HTTP 静态服务。
启动器支持环境变量 `OML_UPDATE_SOURCE`：

| 值 | 含义 |
|:--|:--|
| `D:\updates\releases` | 本地目录（vpk 的 `Releases` 输出目录） |
| `http://127.0.0.1:8000` | 本地 HTTP 服务 |

完整流程：

```powershell
# 1. 安装 1.0.0（会装到 %LOCALAPPDATA%\OML\Current）
Start-Process .\Releases\OML-win-Setup.exe -Wait

# 2. 把 1.0.1 的更新包放到一个目录，作为更新源
$env:OML_UPDATE_SOURCE = "D:\dshprogram\OML_test\releases-1.0.1"

# 3. 启动已安装的 1.0.0，它会自动检查到 1.0.1，
#    顶部出现绿色提示条：发现新版本 1.0.1
Start-Process "$env:LOCALAPPDATA\OML\Current\OML.Desktop.exe"

# 4. 点"后台下载并重启更新"，重启后版本变成 1.0.1
```

用 HTTP 的方式等价：

```powershell
# 在 releases-1.0.1 目录起一个静态服务器
python -m http.server 8000
# 另一个窗口
$env:OML_UPDATE_SOURCE = "http://127.0.0.1:8000"
```

## 四、注意

- **开发期直接 `dotnet run` 不会检查更新**：`UpdateManager.IsInstalled` 为 false，
  Velopack 认为这不是安装版。必须走 `Setup.exe` 安装后再测。
- GitHub 正式更新：默认源是 `https://github.com/LSwzx/OML_test`，
  需要把 `vpk pack` 的 `Releases` 目录整体上传到 GitHub Release，
  且 tag 使用 Velopack 的版本号（如 `1.0.1`）。
- 版本号来自 `vpk pack -v`，不是 csproj 的 `<Version>`；两者最好保持一致。
