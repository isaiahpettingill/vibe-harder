Unicode True
!include "MUI2.nsh"
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
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH}\*"
  Delete "$SMPROGRAMS\Codex Manager.lnk"
  Delete "$DESKTOP\Codex Manager.lnk"
  Delete "$INSTDIR\CodexManager.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Vibe Harder.lnk" "$INSTDIR\VibeHarder.exe"
  CreateShortcut "$DESKTOP\Vibe Harder.lnk" "$INSTDIR\VibeHarder.exe"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexManager"
  StrCmp $0 "" +2
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CodexManager" '"$INSTDIR\VibeHarder.exe" --startup'
  WriteRegStr HKCU "Software\CodexManager" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayName" "Vibe Harder"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "DisplayIcon" "$INSTDIR\VibeHarder.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexManager" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
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
