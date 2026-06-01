; Project Dashboard - NSIS installer (per-user, no elevation, no signing)
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WordFunc.nsh"

!define APPNAME   "Project Dashboard"
!define COMPANY   "Jason Ulbright"
!define VERSION   "1.1.1.1"
!define EXE       "ProjectDashboard.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectDashboard"

Name "${APPNAME}"
OutFile "ProjectDashboard-Setup-${VERSION}.exe"
RequestExecutionLevel user
InstallDir "$LOCALAPPDATA\Programs\Project Dashboard"
InstallDirRegKey HKCU "Software\Project Dashboard" "InstallDir"
SetCompressor /SOLID lzma

; --- Branding ---
!define MUI_ICON                        "..\branding\app.ico"
!define MUI_UNICON                      "..\branding\app.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP          "..\branding\installer-header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP        "..\branding\installer-header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP    "..\branding\installer-sidebar.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP  "..\branding\installer-sidebar.bmp"
!define MUI_ABORTWARNING

; --- Pages ---
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN       "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT  "Launch ${APPNAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; Prerequisite: the .NET Desktop RUNTIME (shared framework Microsoft.WindowsDesktop.App),
; major version 10 or newer. This checks the runtime under \shared\, NOT the SDK (\sdk\),
; and never bundles it - first-party Microsoft runtimes are OS prerequisites.
Function .onInit
  ${If} ${RunningX64}
    StrCpy $R0 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App"
  ${Else}
    StrCpy $R0 "$PROGRAMFILES\dotnet\shared\Microsoft.WindowsDesktop.App"
  ${EndIf}

  StrCpy $R3 0  ; 1 once a runtime with major >= 10 is found
  FindFirst $R1 $R2 "$R0\*"
  rt_loop:
    StrCmp $R2 "" rt_endloop
    StrCmp $R2 "." rt_next
    StrCmp $R2 ".." rt_next
    ${WordFind} "$R2" "." "+1" $R4   ; major component, e.g. "10" from "10.0.3"
    IntCmp $R4 10 rt_found rt_next rt_found
    rt_found:
      StrCpy $R3 1
      Goto rt_endloop
    rt_next:
      FindNext $R1 $R2
      Goto rt_loop
  rt_endloop:
  FindClose $R1

  ${If} $R3 == 0
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "Project Dashboard requires the .NET Desktop Runtime 10.0 or newer (x64).$\n$\nThis is the runtime, not the SDK. Download it from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0$\n$\nContinue installing anyway?" IDYES rt_continue
    Abort
    rt_continue:
  ${EndIf}
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "payload\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0
  CreateShortCut "$DESKTOP\${APPNAME}.lnk"    "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0

  WriteRegStr   HKCU "Software\Project Dashboard" "InstallDir" "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayName"     "${APPNAME}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKCU "${UNINSTKEY}" "Publisher"       "${COMPANY}"
  WriteRegStr   HKCU "${UNINSTKEY}" "DisplayIcon"     "$INSTDIR\${EXE}"
  WriteRegStr   HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTKEY}" "UninstallString" "$INSTDIR\Uninstall.exe"
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize"   9500
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify"        1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair"        1
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTKEY}"
  DeleteRegKey HKCU "Software\Project Dashboard"
  ; Note: user data in %APPDATA%\ProjectDashboard is intentionally left in place.
SectionEnd
