# 安装位置

## 结论先说

想要**安装时自己选目录**，请用 **MSI 安装包（`OML-win.msi`）**。

`OML-win-Setup.exe` 是 Velopack 的一键安装器：它没有目录选择页，
默认固定装到 `%LocalAppData%\OML`，只能通过命令行 `--installto` 改路径
（[Velopack 文档](https://docs.velopack.io/packaging/installer)）。
它按用户安装、不弹 UAC，但这**不是限制，也不该当成优点**——如果你的目标是装到
`D:\Apps\OML` 或 `C:\Program Files\OML`，用 MSI 更直接。

## 方案一：MSI（推荐，安装向导里可选目录）

双击 `OML-win.msi`：

- 安装向导带目录选择页（MSI 里包含 WiX 的 `BrowseDlg` 与 `INSTALLFOLDER`）；
- 选择 `C:\Program Files\...` 这类受保护目录时，Windows 会正常弹出 UAC；
- 选择用户可写目录（如 `D:\Apps\OML`）时可能免提权，由 MSI 的 dual-mode 逻辑决定。

静默安装 / 自动化：

```cmd
msiexec /i OML-win.msi /qn VELOPACK_INSTALLDIR="D:\Apps\OML"
```

注意：`/qn` 是静默模式，不会显示向导，必须显式给 `VELOPACK_INSTALLDIR`。

## 方案二：Setup.exe + `--installto`

如果不想用 MSI，Setup.exe 支持指定目录：

```cmd
OML-win-Setup.exe --installto "D:\Apps\OML"
```

仓库附带图形化选目录脚本（与 `OML-win-Setup.exe` 放同一目录）：

- `scripts/安装到指定目录.cmd`（双击）
- `scripts/install-oml.ps1`（`-InstallDir` 可直接指定）

限制：Setup.exe 是**按用户**安装器，装到 `C:\Program Files` 这类目录会遇到权限问题
（它自己不请求提权）。要装到受保护目录，用 MSI。

## 卸载

**支持**，而且两种安装包都能正常卸载。

### Setup.exe 安装的版本

三种入口等价：

1. Windows 设置 → 应用 → 已安装的应用 → OML → 卸载；
2. 运行安装目录下的 `Update.exe --uninstall`；
3. 控制面板 → 程序和功能（按用户安装的条目在 HKCU）。

卸载过程会弹一个"卸载完成"对话框，确认后：

- 删除安装目录（`current`、`packages`、`OML.exe`、`Update.exe`）；
- 删除开始菜单/桌面快捷方式；
- 删除注册表卸载项（HKCU Uninstall）。

**不会删除**：

- `%AppData%\OML\launcher_config.json`（启动器配置）；
- 游戏目录里的 `.minecraft`（存档、模组、资源）。

### MSI 安装的版本

标准 MSI 卸载：

```cmd
msiexec /x OML-win.msi
```

或设置 → 应用 → OML → 卸载。机器级安装的卸载会要求管理员权限。

## 配置文件位置

配置不在安装目录里，避免更新/卸载被误删：

```
%AppData%\OML\launcher_config.json
```

从旧版本升级时，如果 `%LocalAppData%\OML\launcher_config.json` 存在且新位置没有配置，
启动器会自动复制一次。

## 已经从 C 盘装过的怎么办

```powershell
# 卸载旧的一键安装版本
& "$env:LOCALAPPDATA\OML\Update.exe" --uninstall
```

然后用 MSI 装到目标目录。游戏存档/模组在 `.minecraft`，与安装位置无关。
