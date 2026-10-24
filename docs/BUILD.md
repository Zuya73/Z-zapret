# Сборка x2zapret

Откройте корневой `x2zapret.slnx` в IDE с поддержкой .NET 10.
Версия продукта задаётся только в `Directory.Build.props`; минимальная линия SDK - в `global.json`.

## Ядро и регрессионные проверки

Из корня репозитория, на Windows или Linux с .NET SDK 10:

```powershell
python tools/import_flowseal_strategies.py --check
dotnet run --project tests/Zapret.Tests -c Release -- .
```

Полная сборка решения включает WPF, поэтому выполняется на Windows:

```powershell
dotnet build x2zapret.slnx -c Release
```

## Приложение и установщик

Нужны Windows, .NET SDK 10, Python 3 и NSIS 3. Команды запускаются из корня:

```powershell
./build/package.ps1
python tools/generate_installer_files.py
Push-Location build
try {
    & "${env:ProgramFiles(x86)}\NSIS\makensis.exe" installer.nsi
} finally {
    Pop-Location
}
```

Результаты находятся в `artifacts/`: каталог приложения, portable ZIP, установщик
и сгенерированные списки файлов NSIS. Эти файлы не хранятся в Git.
Пакет приложения не содержит BAT/PowerShell-сценариев, `utils` и `.service`.
Проверка `tools/verify_package.py` контролирует ресурсы всех 22 профилей,
обязательные файлы и контрольные суммы движка перед созданием ZIP.

При обновлении установщик удаляет только прежние файлы из `build/obsolete-files.json`.
Удаление каталогов не рекурсивное: посторонние файлы пользователя сохраняются.

## Старые консольные инструменты

```powershell
./build/package-legacy.ps1
```

Это отдельный `Flowseal-legacy-tools-<версия>.zip`; он не нужен приложению.
Подробности - в [LEGACY.md](LEGACY.md).

## GitHub Actions и выпуск

Workflow `Windows desktop` проверяет импорт стратегий и синтаксис PowerShell,
собирает решение и оба пакета, запускает регрессионные тесты, рендерит WPF,
проверяет установку, обновление, автозапуск и удаление.
Проверка границ окна запускает настоящий WPF в обычном, развёрнутом
и восстановленном состоянии: на текущем рабочем столе и при уменьшенной
рабочей области с резервом под панель задач (подстановка rcWork в том же
обработчике окна; настройки рабочего стола не меняются). Размеры при DPI 100–200%
и расположение панели задач по четырём краям проверяются отдельно.
Артефакт называется `x2zapret-Windows-x64`.

После проверки версии создайте тег `desktop-v<версия из Directory.Build.props>`.
Workflow проверит совпадение версии и опубликует GitHub Release.
Для совместимости с обновлятором 0.1 публикуется копия установщика с прежним именем.
Старый workflow выпусков Flowseal сохранён в `legacy/flowseal/workflows` и не запускается.

## Готовый EXE в корне

`./build/build-root.ps1` собирает сжатый self-contained single-file EXE со значком X2.
.NET находится внутри файла. Для запуска из репозитория рядом нужны только
`vendor` и `resources`, устанавливать SDK пользователю не требуется.
CI проверяет запуск в отдельной папке с пробелами без соседних DLL.
После успешного прогона main отдельная задача записывает проверенный EXE
в корень репозитория. В `build/root-executable.json` сохраняются версия, хэш
EXE и отпечаток исходников. Если приложение не изменилось, повторного коммита нет.

После изменения сторонних бинарников обновите манифест:
`python tools/generate_engine_manifest.py`. CI проверяет его с `--check`.
