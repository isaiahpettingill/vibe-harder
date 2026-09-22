Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
Var CreateDesktopShortcut
Name "Vibe Harder"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\CodexManager"
InstallDirRegKey HKCU "Software\CodexManager" "InstallDir"
RequestExecutionLevel user
SetCompressor zlib
Icon "${PUBLISH}\Assets\app.ico"
UninstallIcon "${PUBLISH}\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\VibeHarder.exe"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Vibe Harder"
  SetShellVarContext current
  ; Preserve a removed desktop shortcut on upgrades, including older installs.
  StrCpy $CreateDesktopShortcut 1
  ReadRegStr $0 HKCU "Software\CodexManager" "InstallDir"
  ${If} $0 != ""
  ${OrIf} ${FileExists} "$INSTDIR\VibeHarder.exe"
  ${OrIf} ${FileExists} "$INSTDIR\CodexManager.exe"
    StrCpy $CreateDesktopShortcut 0
  ${EndIf}
  ${If} ${FileExists} "$DESKTOP\Vibe Harder.lnk"
  ${OrIf} ${FileExists} "$DESKTOP\Codex Manager.lnk"
    StrCpy $CreateDesktopShortcut 1
  ${EndIf}
  SetOutPath "$INSTDIR"
  RMDir /r "$INSTDIR\runtime"
  RMDir /r "$INSTDIR\node_modules"
  Delete "$INSTDIR\remote-server.cjs"
  Delete "$INSTDIR\package.json"
  Delete "$INSTDIR\package-lock.json"
  File /r "${PUBLISH}\*"
  Delete "$SMPROGRAMS\Codex Manager.lnk"
  Delete "$DESKTOP\Codex Manager.lnk"
  Delete "$INSTDIR\CodexManager.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Vibe Harder.lnk" "$INSTDIR\VibeHarder.exe" "" "$INSTDIR\Assets\app.ico" 0
  ${If} $CreateDesktopShortcut == 1
    CreateShortcut "$DESKTOP\Vibe Harder.lnk" "$INSTDIR\VibeHarder.exe" "" "$INSTDIR\Assets\app.ico" 0
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexManager"
  StrCmp $0 "" +2
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexManager" '"$INSTDIR\VibeHarder.exe" --startup'
  WriteRegStr HKCU "Software\CodexManager" "InstallDir" "$INSTDIR"
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\register-cli.ps1"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayName" "Vibe Harder"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayIcon" "$INSTDIR\VibeHarder.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "NoRepair" 1
  ; Invalidate cached executable/shortcut icons after replacing their resources.
  ; SHCNE_ASSOCCHANGED / SHCNF_IDLIST, then SHCNE_UPDATEITEM / SHCNF_PATHW.
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$INSTDIR\VibeHarder.exe", p 0)'
  System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$INSTDIR\Assets\app.ico", p 0)'
  System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$SMPROGRAMS\Vibe Harder.lnk", p 0)'
  ${If} $CreateDesktopShortcut == 1
    System::Call 'shell32::SHChangeNotify(i 0x00002000, i 0x0005, w "$DESKTOP\Vibe Harder.lnk", p 0)'
  ${EndIf}
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\register-cli.ps1" -Remove'
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexManager"
  Delete "$SMPROGRAMS\Vibe Harder.lnk"
  Delete "$DESKTOP\Vibe Harder.lnk"
  ; Remove only shipped application files; user sessions live elsewhere.
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.exe"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\INSTALL.md"
  Delete "$INSTDIR\runtime.txt"
  Delete "$INSTDIR\vibe-harder.cmd"
  Delete "$INSTDIR\register-cli.ps1"
  Delete "$INSTDIR\remote-server.cjs"
  Delete "$INSTDIR\LICENSE"
  RMDir /r "$INSTDIR\node_modules"
  RMDir /r "$INSTDIR\Assets"
  RMDir /r "$INSTDIR\runtime"
  RMDir /r "$INSTDIR\runtimes"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\CodexManager"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager"
SectionEnd
