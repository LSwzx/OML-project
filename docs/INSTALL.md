# 安装位置

## 结论

**想安装时选目录，用 `OML-win-Installer.exe`（NSIS 向导）。**

Velopack 自己的两个安装包都没有目录页，这是实测结论：

| 安装包 | 目录页 | 说明 |
|:--|:--|:--|
| **`OML-win-Installer.exe`** | **有**（NSIS 向导，可选任意目录） | 内部调用 Velopack `Setup --installto`，自动更新链路不变 |
| `OML-win.msi` | **没有**，只有"安装范围"（仅为我 / 所有用户） | MSI 属性 `VELOPACK_INSTALLDIR` 只能在命令行指定 |
| `OML-win-Setup.exe` | 没有 | 默认 `%LocalAppData%\OML`，仅支持 `--installto` |
| `OML-win-Portable.zip` | — | 免安装，解压即用 |

之前文档里写"MSI 安装向导可选目录"是错的：MSI 包内确实带 `BrowseDlg` 表，
但它的 UI 序列里没有目录页，实际只显示安装范围。这一版已改。

## OML-win-Installer.exe

1. 双击运行，选择目录（默认 `%LocalAppData%\OML`）；
2. 安装器检查目标目录可写（不可写会直接报错，例如选了 `C:\Program Files`）；
3. 静默调用 Velopack `Setup.exe --installto "<你选的目录>"`；
4. Velopack 完成实际安装：注册表卸载项、快捷方式、`Update.exe` 更新链路都在。

所以安装完成后：

- 自动更新仍然可用（更新写回同一个目录）；
- 卸载仍在"设置 → 应用 → OML"，或 `<安装目录>\Update.exe --uninstall`；
- 本 NSIS 向导自身不写卸载项、不留文件，避免出现两个 OML 卸载条目。

静默安装（自动化）：

```cmd
OML-win-Installer.exe /S /D=D:\Apps\OML
```

注意 `/D=` 必须是最后一个参数，路径不要加引号（NSIS 语法）。

## 为什么不让选 Program Files

Velopack 是按用户安装 + 按用户更新：更新进程以当前用户身份写安装目录。
装到 `Program Files` 后普通用户没有写权限，自动更新会失败。
所以向导会拒绝不可写目录，并提示改用 `D:\Apps\OML` 这类用户可写位置。

如果你确实需要全机器安装（Program Files + 所有用户），那要换掉 Velopack 的更新机制，
成本明显更高，当前版本没有做。

## 其它安装方式

```cmd
:: 一键安装（固定路径，按用户）
OML-win-Setup.exe

:: Setup 指定目录
OML-win-Setup.exe --installto "D:\Apps\OML"

:: MSI 静默指定目录（机器级，需要管理员）
msiexec /i OML-win.msi /qn VELOPACK_INSTALLDIR="D:\Apps\OML"
```

仓库还附带 PowerShell 图形脚本（`scripts/install-oml.ps1`），等价于 Setup + 目录选择，
但需要用户手动把它和 Setup.exe 放在一起，不如 `OML-win-Installer.exe` 方便。

## 配置文件位置

配置不在安装目录里，避免更新/卸载被误删：

```
%AppData%\OML\launcher_config.json
```

从旧版本升级时，如果 `%LocalAppData%\OML\launcher_config.json` 存在且新位置没有配置，
启动器会自动复制一次。

## 卸载

- 设置 → 应用 → OML → 卸载；
- 或 `<安装目录>\Update.exe --uninstall`。

删除安装目录、快捷方式和卸载项，**保留** `%AppData%\OML` 配置和 `.minecraft` 游戏数据。
