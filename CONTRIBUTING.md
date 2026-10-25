# 贡献指南

感谢你愿意为 OML 做贡献。本文说明开发环境、提交规范与合并流程，尽量让每一次 PR 都能被快速审阅。

## 行为准则

- 讨论对事不对人，问题描述尽量附带复现步骤与日志；
- 不接受用于绕过正版验证、分发盗版账号或游戏本体的改动；
- 涉及第三方代码/资源时，必须在 PR 中说明来源与许可证，并同步更新 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。

## 开发环境

| 依赖 | 版本 |
|:--|:--|
| .NET SDK | 8.0 或更高（开发时使用 8.0 / 10.0 SDK 均可） |
| 操作系统 | Windows 10 / 11 x64 |
| IDE | Visual Studio 2022、Rider 或 VS Code + C# Dev Kit |
| 打包（可选） | NSIS 3.x、Velopack CLI（`dotnet tool install -g vpk`） |

> WPF 只能在 Windows 上构建；Linux/macOS 可以编译 `OML.Core` 与 `OML.Abstractions`，但无法运行界面。

## 构建与运行

```powershell
git clone https://github.com/LSwzx/OML-project.git
cd OML-project

dotnet build OML.sln -c Release
dotnet run --project OML.Desktop
```

运行自检（必须通过）：

```powershell
dotnet run --project OML.Test -- --no-network
```

自检覆盖版本解析、下载引擎、依赖解析、启动参数等纯本地逻辑；联网测试会访问 Mojang / Modrinth，按需执行。

## 项目结构约定

```
OML.Abstractions/   纯模型与接口，不依赖任何项目
OML.Core/           业务逻辑，只能依赖 Abstractions
OML.Desktop/        WPF 界面，依赖 Core
OML.Test/           控制台自检宿主
```

- **依赖方向严格单向**：`Desktop / Test → Core → Abstractions`，不允许反向引用；
- 网络、文件、进程等副作用放在 `OML.Core`，UI 层只做编排与展示；
- 新增功能优先补 `OML.Test` 的本地检查，再写 UI；
- 公共 API 需要 XML 注释，注释与界面文案统一使用中文。

## 代码风格

- 启用 `<Nullable>enable</Nullable>`，不要用 `!` 压制可空性警告；
- 私有字段 `_camelCase`，异步方法以 `Async` 结尾；
- 避免静默 `catch {}`：要么记录日志，要么把异常上抛；
- 不引入新的第三方依赖前，先在 Issue 中讨论；
- 不要提交 `publish-*`、`Releases*`、安装包等构建产物（`.gitignore` 已覆盖）。

## 提交规范

提交信息使用中文，格式：

```
<type>: <一句话说明>

<可选：为什么改、影响范围、验证方式>
```

`type` 取值：

| type | 含义 |
|:--|:--|
| `feat` | 新功能 |
| `fix` | 修复 bug |
| `docs` | 文档 |
| `refactor` | 重构（不改变行为） |
| `test` | 测试 |
| `build` / `ci` | 构建、依赖、工作流 |
| `chore` | 杂项 |

示例：

```
feat: 下载页支持按加载器筛选版本

fix: 修复 OptiFine 库缺少下载信息时未进入 classpath
```

一个提交只做一件事；功能改动请附带验证说明（命令、截图或日志）。

## 分支与 PR

1. 从 `main` 切出分支：`feat/简短描述`、`fix/简短描述`；
2. 提交前本地执行 `dotnet build OML.sln -c Release` 与 `dotnet run --project OML.Test -- --no-network`；
3. PR 描述包含：改动目的、行为变化、验证方式、相关 Issue；
4. 涉及 UI 的改动请附截图（可用 `--screenshot` 离屏渲染）；
5. 维护者合并后删除分支。

## 调试技巧

```powershell
# 指定游戏目录 / 页面
dotnet run --project OML.Desktop -- --game-dir "D:\mc\.minecraft" --nav settings

# 离屏截图，适合在无人值守环境检查界面
dotnet run --project OML.Desktop -- --nav home --screenshot out.png --screenshot-exit

# 扫描第三方启动器目录，输出跳过的版本及原因
dotnet run --project OML.Test -- --scan "E:\mc\.minecraft"

# 启动并自动进入游戏（需要本地已安装版本）
dotnet run --project OML.Desktop -- --auto-launch
```

日志位置：

- 启动器日志：界面底部「启动日志」面板；
- 游戏日志：`versions/<id>/logs/oml-launch.log`（版本隔离时为 `versions/<id>/logs/`）。

## 发布流程（维护者）

1. 修改 `OML.Desktop.csproj` 的 `<Version>` 与 README 版本历史；
2. 提交并打标签：`git tag v1.1.x && git push origin v1.1.x`；
3. Release 工作流会构建安装包、便携版与 Velopack 更新源并创建 Release；
4. 更新源文件（`releases.win.json`、`*.nupkg`）不能遗漏，否则自动更新失效。

## 许可

贡献的代码默认按仓库的 [GPL-3.0-only](LICENSE) 授权；提交即表示同意以该许可证分发。
