Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"
!include "..\artifacts\version.nsh"

!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\artifacts\x2zapret"
!endif
Name "x2zapret"
OutFile "..\artifacts\x2zapret-Setup-${APP_VERSION}-x64.exe"
InstallDir "$PROGRAMFILES64\x2zapret"
InstallDirRegKey HKLM "Software\Z-zapret" "InstallDir"
RequestExecutionLevel admin
ManifestDPIAware true
SetCompressor /SOLID lzma
BrandingText "x2zapret · Zuya73"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "x2zapret"
VIAddVersionKey "FileDescription" "x2zapret Windows installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "2026 Zuya73. Third-party copyrights retained."
!define MUI_ICON "..\src\Zapret.Desktop\Assets\app.ico"
!define MUI_UNICON "..\src\Zapret.Desktop\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\x2zapret.exe"
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
    MessageBox MB_OK|MB_ICONEXCLAMATION "Нажмите правой кнопкой на значок x2zapret рядом с часами, выберите «Выход» и повторите установку или удаление." /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
!macroend

Function WaitForUpdatingProcess
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/UPDATEPID=" $1
  ${IfNot} ${Errors}
    IntOp $1 $1 + 0
    ${If} $1 > 0
      System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i r1) p.r2'
      ${If} $2 != 0
        System::Call 'kernel32::WaitForSingleObject(p r2, i 60000) i.r3'
        System::Call 'kernel32::CloseHandle(p r2)'
        ${If} $3 != 0
          MessageBox MB_OK|MB_ICONSTOP "Закройте x2zapret перед обновлением и повторите установку." /SD IDOK
          SetErrorLevel 6
          Abort
        ${EndIf}
      ${EndIf}
    ${EndIf}
  ${EndIf}
FunctionEnd

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
  Call WaitForUpdatingProcess
  !insertmacro RequireClosed
  ReadRegStr $0 HKLM "Software\Z-zapret" "InstallDir"
  StrCmp $0 "" ready
  StrCpy $2 "$0\x2zapret.exe"
  IfFileExists "$2" maintenance
  StrCpy $2 "$0\Z-zapret.exe"
  IfFileExists "$2" 0 ready
  maintenance:
  ClearErrors
  ExecWait '"$2" --stop-service-for-update' $1
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Не удалось подготовить x2zapret к обновлению. Полностью закройте программу через значок рядом с часами и повторите установку." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ${If} $1 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось остановить службу x2zapret. Остановите её перед обновлением." /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ready:
FunctionEnd

Section "x2zapret" Main
  SetRegView 64
  SetShellVarContext all
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"
  !include "..\artifacts\upgrade-cleanup.nsh"
  ; Known files and shortcuts from the former product name only.
  Delete "$INSTDIR\Z-zapret.exe"
  Delete "$INSTDIR\Z-zapret.dll"
  Delete "$INSTDIR\Z-zapret.deps.json"
  Delete "$INSTDIR\Z-zapret.runtimeconfig.json"
  Delete "$DESKTOP\Z-zapret.lnk"
  Delete "$SMPROGRAMS\Z-zapret\Z-zapret.lnk"
  RMDir "$SMPROGRAMS\Z-zapret"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ClearErrors
  ExecWait '"$INSTDIR\x2zapret.exe" --refresh-startup' $0
  ${If} ${Errors}
    SetErrorLevel 5
    Abort
  ${EndIf}
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось обновить настройки автозапуска. Откройте x2zapret и сохраните их повторно." /SD IDOK
    SetErrorLevel 5
    Abort
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\x2zapret"
  CreateShortcut "$SMPROGRAMS\x2zapret\x2zapret.lnk" "$INSTDIR\x2zapret.exe"
  CreateShortcut "$DESKTOP\x2zapret.lnk" "$INSTDIR\x2zapret.exe"
  WriteRegStr HKLM "Software\Z-zapret" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayName" "x2zapret"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "Publisher" "Zuya73"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "URLInfoAbout" "https://github.com/Zuya73/x2zapret"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret" "DisplayIcon" "$INSTDIR\x2zapret.exe"
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
  ExecWait '"$INSTDIR\x2zapret.exe" --uninstall-service' $0
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Не удалось запустить обслуживание x2zapret. Файлы приложения сохранены." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONSTOP "Не удалось отключить службу или восстановить hosts. Проверьте журнал x2zapret и повторите удаление." /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  ; Remove only files shipped by this build, preserving unrelated user files.
  !include "..\artifacts\uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\x2zapret.lnk"
  Delete "$SMPROGRAMS\x2zapret\x2zapret.lnk"
  RMDir "$SMPROGRAMS\x2zapret"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Z-zapret"
  DeleteRegKey HKLM "Software\Z-zapret"
SectionEnd
