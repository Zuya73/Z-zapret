param([string]$OutputRoot = "$PSScriptRoot/../artifacts/root-executable")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path $OutputRoot) { Remove-Item $OutputRoot -Recurse -Force }
New-Item $OutputRoot -ItemType Directory -Force | Out-Null
dotnet publish "$root/src/Zapret.Desktop/Zapret.Desktop.csproj" -c Release -r win-x64 --self-contained true -o $OutputRoot -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Root executable publish failed.' }
python "$root/tools/root_executable.py" "$OutputRoot/x2zapret.exe" "$OutputRoot/root-executable.json"
if ($LASTEXITCODE -ne 0) { throw 'Root executable validation failed.' }
