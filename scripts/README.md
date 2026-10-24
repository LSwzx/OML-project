# 选择安装目录

`OML-win-Setup.exe` 是 Velopack 的一键安装器：按用户安装、没有目录选择页，
默认固定装到 `%LocalAppData%\OML`。它支持命令行参数：

```cmd
OML-win-Setup.exe --installto "D:\Apps\OML"
```

本目录下的脚本只是给这个参数套一层图形界面，适合"装到 D 盘用户目录"这种场景。

如果你要装到 `C:\Program Files` 这类受保护目录，或者希望安装向导里直接浏览目录，
请用 MSI 安装包（`OML-win.msi`）——它有目录选择页，并按需触发 UAC。

| 安装包 | 目录选择 | 提权 |
|:--|:--|:--|
| `OML-win.msi` | 向导内可浏览选择 | 选受保护目录时 UAC |
| `OML-win-Setup.exe` | 无（只能 `--installto`） | 不提权，受保护目录会失败 |
| 本脚本 | 弹文件夹选择框 | 依赖 Setup.exe，同左 |

使用：

```powershell
.\install-oml.ps1                     # 弹框选目录
.\install-oml.ps1 -InstallDir "D:\Apps\OML"
```

详见仓库 [docs/INSTALL.md](../docs/INSTALL.md)。
