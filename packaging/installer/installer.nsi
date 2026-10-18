; ============================================================================
;  并川听澜 —— 一键安装包
;  NSIS 3.11 (Unicode) / Modern UI 2
;
;  设计取舍（每条都有原因）
;
;  1) 覆盖安装不打断正在输出的声音
;     做法：把旧文件改名顶替，而不是先结束进程再覆盖。
;     Windows 加载器以 FILE_SHARE_DELETE 打开 exe/dll，所以「正在运行的文件」
;     可以被改名、不能被删除或覆盖。先 Rename 再写新文件，既能升级成功，
;     又不会让用户的音频断掉。只有改名也失败（极少见）时才强制结束进程。
;
;  2) 装完立刻自检
;     写入后对比字节数与 SHA256（certutil），不一致就当场报错并中止安装，
;     而不是留一个「装上了但打不开」的坏程序让用户去猜。
;
;  3) 用户配置一个字都不动
;     程序把设置存在 %APPDATA%\MultiAudioOutput。安装器、卸载器都不碰它，
;     卸载时只提示「如需彻底清除请手动删除该目录」。
;
;  4) 自启项属于用户配置
;     程序内的齿轮面板会写 HKCU\...\Run。卸载时如果检测到，问一句要不要删，
;     不在安装/卸载流程里替用户做决定。
; ============================================================================

Unicode true
SetCompressor /SOLID lzma
SetCompress force
RequestExecutionLevel admin
SetDatablockOptimize on
CRCCheck on
XPStyle on

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

; ── 产品信息 ────────────────────────────────────────────────────────────────
!define PRODUCT_NAME      "MultiAudioOutput"
!define PRODUCT_DESC      "并川听澜"
!ifndef VER
  !define VER "1.2"
!endif
!define PRODUCT_VERSION   "${VER}"

; VIProductVersion 强制要求四段 X.X.X.X，而 VER 是 1.2 这种两段形式。
; 直接拼 "${PRODUCT_VERSION}.0" 只在版本号恰好是 1.0.0 三段时能凑成四段，
; 改成 1.2 后只有三段，makensis 会报 invalid VIProductVersion format。
; build.ps1 会算好四段值用 /DVER4= 传进来；直接跑 makensis 时用下面的兜底值。
!ifndef VER4
  !define VER4            "${VER}.0.0"
!endif
!define PRODUCT_EXE       "并川听澜.exe"
!define PRODUCT_DLL       "NAudio.dll"
!define PRODUCT_KEY       "Software\Microsoft\Windows\CurrentVersion\Uninstall\MultiAudioOutput"
!define RUN_KEY           "Software\Microsoft\Windows\CurrentVersion\Run"

; 机器级安装，与「所有用户」快捷方式语义一致
!define INSTALL_DIR       "$PROGRAMFILES64\${PRODUCT_NAME}"

; payload 与指纹：由 build.ps1 生成
!include "payload-fingerprint.nsh"

; ── 外观 ────────────────────────────────────────────────────────────────────
!define MUI_ICON                        "..\payload\app.ico"
!define MUI_UNICON                      "..\payload\app.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP          "..\assets\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP        "..\assets\header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP    "..\assets\welcome.bmp"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_NOAUTOCLOSE

; ── 页面 ────────────────────────────────────────────────────────────────────
!define MUI_WELCOMEPAGE_TITLE "欢迎安装 $(^NameDA)"
!define MUI_WELCOMEPAGE_TEXT  "本向导会把「并川听澜」安装到您的电脑上。$\r$\n$\r$\n所有运行组件（含 NAudio 音频库）都已在安装包内，不需要联网下载。$\r$\n$\r$\n您原有的输出设备选择、延迟、热键和方案都会完整保留。"
!insertmacro MUI_PAGE_WELCOME

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES

!define MUI_FINISHPAGE_TITLE "「并川听澜」已安装完成"
!define MUI_FINISHPAGE_TEXT  "程序位置：$\r$\n$INSTDIR$\r$\n$\r$\n桌面和开始菜单都已生成快捷方式。$\r$\n$\r$\n小提示：点窗口右上角的「✕」只是收进托盘，音频会继续输出；要彻底退出请用托盘右键菜单里的「退出」。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动「并川听澜」"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

; ── 安装器元信息（右键属性里能看到）────────────────────────────────────────
Name "${PRODUCT_DESC} ${PRODUCT_VERSION}"
OutFile "..\dist\并川听澜-${PRODUCT_VERSION}-Setup.exe"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "${PRODUCT_KEY}" "InstallLocation"
ShowInstDetails show
ShowUninstDetails show
BrandingText "并川听澜 · 多路音频输出"
SilentInstall normal
SilentUnInstall normal

VIProductVersion "${VER4}"
VIAddVersionKey /LANG=2052 "ProductName"     "${PRODUCT_DESC}"
VIAddVersionKey /LANG=2052 "FileDescription" "${PRODUCT_DESC} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"     "${VER4}"
VIAddVersionKey /LANG=2052 "ProductVersion"  "${VER4}"
VIAddVersionKey /LANG=2052 "CompanyName"     "并川听澜"
VIAddVersionKey /LANG=2052 "LegalCopyright"  "© 2026 并川听澜"
VIAddVersionKey /LANG=2052 "Comments"        "一键安装包：内置主程序与 NAudio 运行库"

; ============================================================================
;  变量
; ============================================================================
Var IsUpgrade

; ============================================================================
;  初始化
; ============================================================================
Function .onInit
  ; Win10 是硬门槛：程序用 WASAPI 环路采集，Win7/8 上跑不起来。
  ${IfNot} ${AtLeastWin10}
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP|MB_OK "本程序需要 Windows 10 或更高版本。"
    ${EndIf}
    SetErrorLevel 3
    Abort
  ${EndIf}

  ; 机器级安装：快捷方式进「所有用户」。
  SetShellVarContext all

  ; NSIS 生成的是 32 位安装器，默认会因 WOW64 重定向把卸载登记项写到
  ; HKLM\Software\Wow6432Node\... 下。切到 64 位视图，登记项落在
  ; 控制面板/「应用和功能」正常读取的 HKLM\Software\... 位置。
  SetRegView 64

  ; 必须先建插件临时目录。NSIS 的 $PLUGINSDIR 是惰性初始化的：在第一个插件
  ; 调用之前它是空串。HashFile 里拼 "$PLUGINSDIR\hash.txt" 时若还没初始化，
  ; 路径会变成 "\hash.txt"（指向当前盘根目录）→ 文件打不开 → 自检失败。
  InitPluginsDir

  ; 升级判定：注册表里已有登记项就是覆盖安装
  StrCpy $IsUpgrade 0
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "InstallLocation"
  ${If} $0 != ""
    StrCpy $IsUpgrade 1
  ${EndIf}
FunctionEnd

; ============================================================================
;  安装主体
; ============================================================================
Section "-主程序" SecMain
  SectionIn RO

  SetOutPath "$INSTDIR"
  CreateDirectory "$INSTDIR"

  ; ── 1. 主程序 ────────────────────────────────────────────────────────────
  DetailPrint "正在安装主程序…"
  StrCpy $R7 "$INSTDIR\${PRODUCT_EXE}"
  ${If} ${FileExists} "$R7"
    Call BackupOld
    ${If} $R8 == ""
      DetailPrint "旧文件无法改名，尝试结束正在运行的实例…"
      nsExec::ExecToStack 'cmd /c taskkill /F /IM "${PRODUCT_EXE}" /T 2>nul'
      Pop $0
      Pop $0
      Sleep 800
      Call BackupOld
      ${If} $R8 == ""
        ${IfNot} ${Silent}
          MessageBox MB_ICONSTOP|MB_OK "无法替换主程序：$\r$\n$R7$\r$\n$\r$\n请先关闭「并川听澜」再重试安装。"
        ${EndIf}
        SetErrorLevel 4
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}
  File "/oname=${PRODUCT_EXE}" "${PAYLOAD_EXE}"

  ; ── 1b. 清掉英文文件名时代的旧主程序 ────────────────────────────────────
  ; exe 改名为「并川听澜.exe」后，升级用户的安装目录里会同时留着旧的
  ; MultiAudioOutput.exe（还能双击运行、显示的还是旧图标），必须收拾掉。
  ; 先 taskkill 再删：它可能正被旧版本进程占用，删不掉就挂重启删除。
  ${If} ${FileExists} "$INSTDIR\MultiAudioOutput.exe"
    nsExec::ExecToStack 'cmd /c taskkill /F /IM "MultiAudioOutput.exe" /T 2>nul'
    Pop $0
    Pop $0
    Sleep 500
    Delete /REBOOTOK "$INSTDIR\MultiAudioOutput.exe"
    Delete /REBOOTOK "$INSTDIR\MultiAudioOutput.exe.old1"
    Delete /REBOOTOK "$INSTDIR\MultiAudioOutput.exe.old2"
    Delete /REBOOTOK "$INSTDIR\MultiAudioOutput.exe.old3"
  ${EndIf}

  ; ── 2. 音频运行库（必须与 exe 同目录，.NET 不往父目录找）───────────────
  DetailPrint "正在安装音频运行库…"
  StrCpy $R7 "$INSTDIR\${PRODUCT_DLL}"
  ${If} ${FileExists} "$R7"
    Call BackupOld
    ${If} $R8 == ""
      nsExec::ExecToStack 'cmd /c taskkill /F /IM "${PRODUCT_EXE}" /T 2>nul'
      Pop $0
      Pop $0
      Sleep 800
      Call BackupOld
      ${If} $R8 == ""
        ${IfNot} ${Silent}
          MessageBox MB_ICONSTOP|MB_OK "无法替换音频运行库：$\r$\n$R7$\r$\n$\r$\n请先关闭「并川听澜」再重试安装。"
        ${EndIf}
        SetErrorLevel 4
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}
  File "/oname=${PRODUCT_DLL}" "${PAYLOAD_DLL}"

  ; ── 3. 装完自检 ──────────────────────────────────────────────────────────
  Call VerifyPayload

  ; ── 4. 清理历史备份（正在用的删不掉，静默失败即可，下次安装再清）───────
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_EXE}.old1"
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_EXE}.old2"
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_EXE}.old3"
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_DLL}.old1"
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_DLL}.old2"
  Delete /REBOOTOK "$INSTDIR\${PRODUCT_DLL}.old3"

  ; ── 5. 卸载器 ────────────────────────────────────────────────────────────
  DetailPrint "正在生成卸载程序…"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; ── 6. 快捷方式 ──────────────────────────────────────────────────────────
  DetailPrint "正在创建快捷方式…"
  ; 桌面上只放一个快捷方式，不要中英文各来一个
  CreateShortCut "$DESKTOP\${PRODUCT_DESC}.lnk" \
                 "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\${PRODUCT_EXE}" 0 SW_SHOWNORMAL \
                 "" "把声音同时送到多个输出设备"
  ; 清掉早期版本可能留在桌面上的旧名快捷方式（升级时顺手收拾干净）。
  ; 改名后旧快捷方式指向的仍是同一个 exe，但不删会留下两个同名同物的图标。
  Delete "$DESKTOP\MultiAudioOutput.lnk"
  Delete "$DESKTOP\多路音频输出.lnk"

  CreateDirectory "$SMPROGRAMS\${PRODUCT_DESC}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_DESC}\${PRODUCT_DESC}.lnk" \
                 "$INSTDIR\${PRODUCT_EXE}" "" "$INSTDIR\${PRODUCT_EXE}" 0 SW_SHOWNORMAL \
                 "" "把声音同时送到多个输出设备"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_DESC}\卸载 ${PRODUCT_DESC}.lnk" \
                 "$INSTDIR\Uninstall.exe" "" "$INSTDIR\Uninstall.exe" 0 SW_SHOWNORMAL \
                 "" "卸载「并川听澜」"

  ; ── 7. 卸载登记项：控制面板 /「应用和功能」里可见 ───────────────────────
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegStr   HKLM "${PRODUCT_KEY}" "DisplayName"     "${PRODUCT_DESC}"
  WriteRegStr   HKLM "${PRODUCT_KEY}" "DisplayVersion"  "${PRODUCT_VERSION}"
  WriteRegStr   HKLM "${PRODUCT_KEY}" "Publisher"       "并川听澜"
  WriteRegStr   HKLM "${PRODUCT_KEY}" "DisplayIcon"     "$INSTDIR\${PRODUCT_EXE},0"
  WriteRegStr   HKLM "${PRODUCT_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKLM "${PRODUCT_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${PRODUCT_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr   HKLM "${PRODUCT_KEY}" "DisplayCategory" "音频工具"
  WriteRegDWORD HKLM "${PRODUCT_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${PRODUCT_KEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${PRODUCT_KEY}" "EstimatedSize" $0

  ; 让「应用和功能」立刻刷新，不用等它自己重读
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'

  ${If} $IsUpgrade == 1
    DetailPrint "覆盖安装完成。若旧版本仍在运行，关闭后重新打开即为新版本（正在输出的音频不会中断）。"
  ${EndIf}
SectionEnd

; ============================================================================
;  把旧文件改名顶替。正在运行的文件也能改名，所以不会打断音频输出。
;  入参：$R7 = 目标路径    出参：$R8 = 备份名，失败为空串
; ============================================================================
Function BackupOld
  StrCpy $R8 "$R7.old1"
  ClearErrors
  Rename "$R7" "$R8"
  ${IfNot} ${Errors}
    Return
  ${EndIf}

  StrCpy $R8 "$R7.old2"
  ClearErrors
  Rename "$R7" "$R8"
  ${IfNot} ${Errors}
    Return
  ${EndIf}

  StrCpy $R8 "$R7.old3"
  ClearErrors
  Rename "$R7" "$R8"
  ${IfNot} ${Errors}
    Return
  ${EndIf}

  StrCpy $R8 ""
FunctionEnd

; ============================================================================
;  安装后自检：字节数 + SHA256
;  失败时置错误码 5 并 Abort，不会留下一个「装上了但打不开」的程序
; ============================================================================
Function VerifyPayload
  Push "$INSTDIR\${PRODUCT_EXE}"
  Call FileSize
  Pop $0
  StrCmp $0 "${PAYLOAD_EXE_SIZE}" 0 _vp_exe_size_bad

  Push "$INSTDIR\${PRODUCT_DLL}"
  Call FileSize
  Pop $0
  StrCmp $0 "${PAYLOAD_DLL_SIZE}" 0 _vp_dll_size_bad

  Push "$INSTDIR\${PRODUCT_EXE}"
  Call HashFile
  Pop $0
  StrCmp $0 "${PAYLOAD_EXE_SHA}" 0 _vp_exe_hash_bad

  Push "$INSTDIR\${PRODUCT_DLL}"
  Call HashFile
  Pop $0
  StrCmp $0 "${PAYLOAD_DLL_SHA}" 0 _vp_dll_hash_bad

  DetailPrint "自检通过：主程序 ${PAYLOAD_EXE_SIZE} 字节、运行库 ${PAYLOAD_DLL_SIZE} 字节，SHA256 全部一致。"
  Return

  _vp_exe_size_bad:
    DetailPrint "自检失败：主程序字节数不符（实际 $0，应为 ${PAYLOAD_EXE_SIZE}）。"
    Goto _vp_fail
  _vp_dll_size_bad:
    DetailPrint "自检失败：运行库字节数不符（实际 $0，应为 ${PAYLOAD_DLL_SIZE}）。"
    Goto _vp_fail
  _vp_exe_hash_bad:
    DetailPrint "自检失败：主程序 SHA256 不符。"
    DetailPrint "  实际：$0"
    DetailPrint "  应为：${PAYLOAD_EXE_SHA}"
    Goto _vp_fail
  _vp_dll_hash_bad:
    DetailPrint "自检失败：运行库 SHA256 不符。"
    DetailPrint "  实际：$0"
    DetailPrint "  应为：${PAYLOAD_DLL_SHA}"
    Goto _vp_fail

  _vp_fail:
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP|MB_OK \
        "安装后自检未通过，文件可能已损坏。$\r$\n$\r$\n请重新下载安装包后再试。$\r$\n详细信息见安装日志。"
    ${EndIf}
    SetErrorLevel 5
    Abort
FunctionEnd

; ============================================================================
;  文件字节数
;  入参（栈顶）：文件完整路径    出参（栈顶）：十进制字节数，失败为空串
;  说明：FileFunc 的 ${GetSize} 只能统计目录，对单个文件不可用，故自备。
; ============================================================================
Function FileSize
  Exch $1
  Push $2
  Push $3

  ClearErrors
  FileOpen $2 "$1" r
  ${If} ${Errors}
    StrCpy $1 ""
  ${Else}
    FileSeek $2 0 END $3
    FileClose $2
    StrCpy $1 $3
  ${EndIf}

  Pop $3
  Pop $2
  Exch $1
FunctionEnd

; ============================================================================
;  SHA256
;  入参（栈顶）：文件完整路径    出参（栈顶）：64 位小写十六进制，失败为空串
;  说明：certutil 的输出是多行中文提示，用 findstr 只留十六进制那行，
;        落到临时文件再读，避免依赖 nsExec 的文本裁剪行为。
; ============================================================================
Function HashFile
  Exch $1
  Push $2
  Push $3
  Push $4

  StrCpy $3 "$PLUGINSDIR\hash.txt"
  Delete "$3"
  ; $$ 是 NSIS 里输出字面量 $ 的写法；正则末尾的行尾锚点必须写成 $$
  nsExec::ExecToStack 'cmd /c certutil -hashfile "$1" SHA256 | findstr /R "^[0-9a-f][0-9a-f]*$$" > "$3"'
  Pop $2
  Pop $2

  ClearErrors
  FileOpen $2 "$3" r
  ${If} ${Errors}
    StrCpy $1 ""
  ${Else}
    FileRead $2 $1
    FileClose $2
    Delete "$3"
    Call StripEOL
  ${EndIf}

  Pop $4
  Pop $3
  Pop $2
  Exch $1
FunctionEnd

; 就地去掉 $1 尾部的 CR、LF、空格（FileRead 会连换行一起读进来）
Function StripEOL
  _strip_head:
    StrCpy $4 $1 1 -1
    ${If} $4 == "$\r"
    ${OrIf} $4 == "$\n"
    ${OrIf} $4 == " "
      StrCpy $1 $1 -1
      Goto _strip_head
    ${EndIf}
  Return
FunctionEnd

; ============================================================================
;  卸载
; ============================================================================
Function un.onInit
  SetShellVarContext all
  ; 与安装时保持一致：读 64 位视图下的登记项
  SetRegView 64
  ReadRegStr $INSTDIR HKLM "${PRODUCT_KEY}" "InstallLocation"
  ${If} $INSTDIR == ""
    StrCpy $INSTDIR "${INSTALL_DIR}"
  ${EndIf}
FunctionEnd

Section "Uninstall"
  ; ── 1. 开机自启是用户配置，问一句再动 ───────────────────────────────────
  ReadRegStr $0 HKCU "${RUN_KEY}" "${PRODUCT_NAME}"
  ${If} $0 != ""
    ${IfNot} ${Silent}
      MessageBox MB_YESNO|MB_ICONQUESTION \
        "检测到「并川听澜」已设为开机自动启动。$\r$\n$\r$\n卸载后该自启项将失效，是否现在一并删除？" \
        IDNO +2
      DeleteRegValue HKCU "${RUN_KEY}" "${PRODUCT_NAME}"
    ${EndIf}
  ${EndIf}

  ; ── 2. 结束进程，否则 exe/dll 删不掉 ────────────────────────────────────
  DetailPrint "正在关闭「并川听澜」…"
  nsExec::ExecToStack 'cmd /c taskkill /F /IM "${PRODUCT_EXE}" /T 2>nul'
  Pop $0
  Pop $0
  ; 英文文件名时代的旧版本可能还开着，一并结束，否则删不掉它的 exe
  nsExec::ExecToStack 'cmd /c taskkill /F /IM "MultiAudioOutput.exe" /T 2>nul'
  Pop $0
  Pop $0
  Sleep 500

  ; ── 3. 文件 ──────────────────────────────────────────────────────────────
  SetOutPath "$INSTDIR"
  Delete "$INSTDIR\${PRODUCT_EXE}"
  Delete "$INSTDIR\${PRODUCT_DLL}"
  Delete "$INSTDIR\${PRODUCT_EXE}.old1"
  Delete "$INSTDIR\${PRODUCT_EXE}.old2"
  Delete "$INSTDIR\${PRODUCT_EXE}.old3"
  Delete "$INSTDIR\${PRODUCT_DLL}.old1"
  Delete "$INSTDIR\${PRODUCT_DLL}.old2"
  Delete "$INSTDIR\${PRODUCT_DLL}.old3"
  ; 英文文件名时代的旧主程序：升级时若因占用没能删掉（挂了 /REBOOTOK 但用户
  ; 没重启），卸载时要一并清干净，否则安装目录撤走后它还孤零零留在原地。
  Delete "$INSTDIR\MultiAudioOutput.exe"
  Delete "$INSTDIR\MultiAudioOutput.exe.old1"
  Delete "$INSTDIR\MultiAudioOutput.exe.old2"
  Delete "$INSTDIR\MultiAudioOutput.exe.old3"
  ; 卸载器无法删除正在运行的自己：挂 /REBOOTOK，重启时由系统清掉
  Delete /REBOOTOK "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\*.log"

  ; RMDir 删不掉「当前工作目录」。上面 SetOutPath 把 CWD 设成了 $INSTDIR，
  ; 必须先离开，否则目录删不掉、留下一片空壳。
  SetOutPath "$TEMP"
  Sleep 300
  RMDir "$INSTDIR"
  Sleep 300
  RMDir /REBOOTOK "$INSTDIR"

  ; ── 4. 快捷方式 ──────────────────────────────────────────────────────────
  Delete "$DESKTOP\${PRODUCT_DESC}.lnk"
  Delete "$DESKTOP\MultiAudioOutput.lnk"
  Delete "$DESKTOP\多路音频输出.lnk"
  RMDir /r "$SMPROGRAMS\${PRODUCT_DESC}"
  ; 旧版按「多路音频输出」建的开始菜单目录 —— 升级覆盖装到新名之后，
  ; 这里要一并清掉，否则开始菜单里会留一个指向已卸载目录的空文件夹。
  RMDir /r "$SMPROGRAMS\多路音频输出"

  ; ── 5. 注册表 ────────────────────────────────────────────────────────────
  DeleteRegKey HKLM "${PRODUCT_KEY}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, i 0, i 0)'

  ; ── 6. 用户配置：保留 ───────────────────────────────────────────────────
  DetailPrint "用户设置保留在 %APPDATA%\${PRODUCT_NAME}；如需彻底清除，手动删除该文件夹即可。"
SectionEnd
