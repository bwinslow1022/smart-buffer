; Smart Buffer installer. Build: makensis /DVERSION=x.y.z installer\SmartBuffer.nsi
Unicode true
!define APP "Smart Buffer"
!define EXE "Smart Buffer.exe"
!define PUBLISHER "Brandon Winslow"
!define UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\SmartBuffer"
!ifndef VERSION
  !define VERSION "0.0.0"
!endif

Name "${APP}"
OutFile "..\bin\SmartBuffer-Setup.exe"
InstallDir "$PROGRAMFILES64\${APP}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "CompanyName" "${PUBLISHER}"
VIAddVersionKey "FileDescription" "${APP} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "MIT License"

!include "MUI2.nsh"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Open Smart Buffer to finish setting it up"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenApp
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; the installer runs as admin, so the app is opened through explorer to run as the normal user
Function OpenApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

; an update runs silently from the tray app, which it closed, so it starts the tray again as the normal user
Function .onInstSuccess
  IfSilent 0 +2
    nsExec::Exec '"$SYSDIR\schtasks.exe" /run /tn "Smart Buffer Tray"'
FunctionEnd

Section "Install"
  ; a running tray helper keeps the exe locked
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${EXE}"'
  Sleep 500
  SetOutPath "$INSTDIR"
  File "/oname=${EXE}" "..\bin\Release\net48\SmartBuffer.exe"
  File "..\LICENSE"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  SetShellVarContext all
  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0 SW_SHOWNORMAL "" "Clips, settings and setup"

  WriteRegStr HKLM "${UNINST}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINST}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST}" "URLInfoAbout" "https://github.com/bwinslow1022/smart-buffer"
  WriteRegStr HKLM "${UNINST}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST}" "NoRepair" 1
  WriteRegDWORD HKLM "${UNINST}" "EstimatedSize" 400
SectionEnd

Section "Uninstall"
  ; puts OBS settings back, deletes the startup tasks and the separate OBS copy; clips are kept
  ExecWait '"$INSTDIR\${EXE}" --remove --quiet --yes'
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM "${EXE}"'
  Sleep 500
  Delete /REBOOTOK "$INSTDIR\${EXE}"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  SetShellVarContext all
  Delete "$SMPROGRAMS\${APP}.lnk"
  DeleteRegKey HKLM "${UNINST}"
SectionEnd
