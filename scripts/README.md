# 选择安装目录（Velopack 一键安装器没有目录选择页，用这个脚本包一层）

Velopack 的 `Setup.exe` 默认装到 `%LocalAppData%\OML`，官方尚未提供“选择目录”的向导
（[issue #30](https://github.com/velopack/velopack/issues/30)），但安装器本身支持：

```cmd
OML-win-Setup.exe --installto "D:\Apps\OML"
```

所以这里提供一个图形化的目录选择脚本：把本文件与 `OML-win-Setup.exe` 放在同一目录，
双击 `安装到指定目录.cmd` 即可。

## 参数

```powershell
# 图形选择
.\install-oml.ps1

# 直接指定（自动化/静默）
.\install-oml.ps1 -InstallDir "D:\Apps\OML"
```
