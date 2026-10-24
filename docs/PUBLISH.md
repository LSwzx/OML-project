## 发布与更新

`powershell
# 单文件 EXE（自包含，约 71MB）
dotnet publish OML.Desktop\OML.Desktop.csproj -c Release -o publish

# Velopack 安装包 + 更新包
vpk pack -u OML -v 1.0.0 -p publish -e OML.Desktop.exe
`

本地模拟更新：把生成的 Releases 目录设为 OML_UPDATE_SOURCE，
启动已安装的旧版本即可看到顶部更新提示。完整步骤见 [docs/UPDATE.md](docs/UPDATE.md)。
