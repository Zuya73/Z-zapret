Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "artifacts\Z-zapret"
!endif
Name "Z-zapret"
OutFile "artifacts\Z-zapret-Setup-0.1.0-x64.exe"
InstallDir "$PROGRAMFILES64\Z-zapret"
InstallDirRegKey HKLM "Software\Z-zapret" "InstallDir"
RequestExecutionLevel admin
ManifestDPIAware true
SetCompressor /SOLID lzma
BrandingText "Z-zapret · Zuya73"
VIProductVersion "0.1.0.0"
VIAddVersionKey "ProductName" "Z-zapret"
VIAddVersionKey "FileDescription" "Z-zapret Windows installer"
VIAddVersionKey "FileVersion" "0.1.0"
VIAddVersionKey "LegalCopyright" "2026 Zuya73. Third-party copyrights retained."
!define MUI_ICON "Zapret.Desktop\Assets\app.ico"
!define MUI_UNICON "Zapret.Desktop\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Z-zapret.exe"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

!macro RequireClosed
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Global\Z-zapret.Desktop") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Закройте Z-zapret через команду «Выход» в меню трея и повторите установку или удаление." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Требуется 64-разрядная Windows 10 или 11." /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Требуется Windows 10 или Windows 11." /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
  !insertmacro RequireClosed
  ReadRegStr $0 HKLM "Software\Z-zapret" "InstallDir"
  StrCmp $0 "" ready
  IfFileExists "$0\Z-zapret.exe" 0 ready
  ClearErrors
  ExecWait '"$0\Z-zapret.exe" --stop-service-for-update' $1
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Не удалось запустить обслуживание Z-zapret. Обновление остановлено." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ${If} $1 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось остановить службу Z-zapret. Остановите её перед обновлением." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ready:
FunctionEnd

Section "Z-zapret" Main
  SetRegView 64
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Z-zapret"
  CreateShortcut "$SMPROGRAMS\Z-zapret\Z-zapret.lnk" "$INSTDIR\Z-zapret.exe"
  CreateShortcut "$DESKTOP\Z-zapret.lnk" "$INSTDIR\Z-zapret.exe"
  WriteRegStr HKLM "Software\Z-zapret" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayName" "Z-zapret"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayVersion" "0.1.0"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "Publisher" "Zuya73"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "URLInfoAbout" "https://github.com/Zuya73/Z-zapret"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayIcon" "$INSTDIR\Z-zapret.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "NoRepair" 1
SectionEnd

Function un.onInit
  SetRegView 64
  !insertmacro RequireClosed
FunctionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all
  ClearErrors
  ExecWait '"$INSTDIR\Z-zapret.exe" --uninstall-service' $0
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Не удалось запустить обслуживание Z-zapret. Файлы приложения сохранены." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось отключить службу или восстановить hosts. Проверьте журнал Z-zapret и повторите удаление." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ; Remove only files shipped by this build, preserving unrelated user files.
  !include "artifacts\uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\Z-zapret.lnk"
  Delete "$SMPROGRAMS\Z-zapret\Z-zapret.lnk"
  RMDir "$SMPROGRAMS\Z-zapret"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret"
  DeleteRegKey HKLM "Software\Z-zapret"
SectionEnd
