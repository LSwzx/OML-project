# 安装位置

## 默认行为

Velopack 的一键安装器（`OML-win-Setup.exe`）**没有目录选择页**，
默认按用户安装到 `%LocalAppData%\OML`（即 `C:\Users\<你>\AppData\Local\OML`）。
好处是不需要管理员权限；代价是没法在安装界面里改路径。

官方还没有实现带目录选择的向导安装器（[velopack#30](https://github.com/velopack/velopack/issues/30)），
但安装器本身支持指定路径。

## 方案一：用自带脚本选目录（推荐）

把 `install-oml.ps1` 和 `安装到指定目录.cmd` 与 `OML-win-Setup.exe` 放在同一目录，
双击 `安装到指定目录.cmd`，会弹出文件夹选择框：

```
选择目录 → Setup.exe --installto "<你选的目录>" → 完成
```

也可以命令行直接指定：

```powershell
.\install-oml.ps1 -InstallDir "D:\Apps\OML"
```

说明：

- 目录就是安装根目录，里面会出现 `current\`、`packages\`、`OML.exe`、`Update.exe`；
- **仍然按用户安装，不需要管理员权限**；
- 之后自动更新会继续装在这个目录（`Update.exe` 记录的安装位置就是它）。

## 方案二：MSI 机器级安装

发布里同时生成了 `OML-win.msi`，可以用 MSI 属性指定目录：

```cmd
msiexec /i OML-win.msi /qn VELOPACK_INSTALLDIR="D:\Apps\OML"
```

MSI 是机器级安装，需要管理员权限，适合多用户/受管环境。

## 配置文件位置

配置不在安装目录里，避免更新/卸载被误删：

```
%AppData%\OML\launcher_config.json
```

从旧版本升级时，如果 `%LocalAppData%\OML\launcher_config.json` 存在且新位置没有配置，
启动器会自动复制一次。

## 已经把装到 C 盘的版本移到别的盘

1. 先卸载：`%LocalAppData%\OML\Update.exe --uninstall`（或设置 → 应用 → OML → 卸载）；
2. 用方案一/方案二装到目标目录；
3. 配置会自动从 `%AppData%\OML` 读取，不用手动搬。

> 注意：游戏存档/模组在游戏目录（`.minecraft`），与安装位置无关，不受影响。
