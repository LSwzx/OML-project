; OML 安装向导
;
; Velopack 的 Setup.exe / MSI 都没有“选择安装目录”的页面，所以这里用 NSIS
; 做一层真正的向导：目录页选好后，静默调用 Velopack Setup 的 --installto，
; 由 Velopack 完成实际安装（注册表、快捷方式、Update.exe 更新链路都保持不变）。
;
; 本安装器自身不写卸载项、不装文件：卸载仍然走 Velopack 的
; “设置 → 应用 → OML” 或 <安装目录>\Update.exe --uninstall。

Unicode true
!include "MUI2.nsh"
!include "FileFunc.nsh"

Name "OML 安装程序"
OutFile "OML-win-Installer.exe"
InstallDir "$LOCALAPPDATA\OML"
InstallDirRegKey HKCU "Software\OML" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!define MUI_ABORTWARNING
!define MUI_ICON "oml.ico"
!define MUI_UNICON "oml.ico"
Icon "oml.ico"
UninstallIcon "oml.ico"
!define MUI_WELCOMEPAGE_TITLE "安装 OML"
!define MUI_WELCOMEPAGE_TEXT "OML 会安装到你选择的目录。$\r$\n$\r$\n建议选择用户可写的目录（例如 D:\Apps\OML），这样自动更新才能正常写入。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "选择 OML 的安装目录。安装器会调用 Velopack 安装到该目录，并注册自动更新。"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\OML.exe"
!define MUI_FINISHPAGE_RUN_TEXT "运行 OML"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

Section "安装 OML"
  ; 目标目录必须可写：Velopack 以当前用户身份安装，Program Files 需要提权，
  ; 而且之后自动更新同样需要写权限。
  ClearErrors
  CreateDirectory "$INSTDIR"
  FileOpen $9 "$INSTDIR\.oml-write-test" w
  IfErrors 0 +3
    MessageBox MB_ICONSTOP "无法写入 $INSTDIR。$\r$\n$\r$\n请选择一个用户可写的目录（例如 D:\Apps\OML），不要选 Program Files。"
    Abort
  FileClose $9
  Delete "$INSTDIR\.oml-write-test"

  SetOutPath "$TEMP\oml-bootstrap"
  File "payload\OML-win-Setup.exe"

  DetailPrint "正在安装 OML 到 $INSTDIR ..."
  ExecWait '"$TEMP\oml-bootstrap\OML-win-Setup.exe" --installto "$INSTDIR" --silent' $0

  Delete "$TEMP\oml-bootstrap\OML-win-Setup.exe"
  RMDir "$TEMP\oml-bootstrap"

  ${If} $0 != 0
    MessageBox MB_ICONSTOP "安装失败（安装器退出码 $0）。$\r$\n$\r$\n请换一个用户可写目录后重试。"
    Abort
  ${EndIf}

  WriteRegStr HKCU "Software\OML" "InstallDir" "$INSTDIR"
  DetailPrint "安装完成：$INSTDIR"
SectionEnd
