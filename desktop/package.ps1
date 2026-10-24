param([string]$OutputRoot = "$PSScriptRoot/artifacts", [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = ([xml](Get-Content "$PSScriptRoot/Directory.Build.props" -Raw)).SelectSingleNode('//Version').InnerText
$publish = Join-Path $OutputRoot 'Z-zapret'
if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    New-Item $publish -ItemType Directory -Force | Out-Null
    dotnet publish "$PSScriptRoot/Zapret.Desktop/Zapret.Desktop.csproj" -c Release -r win-x64 --self-contained true -o $publish -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
}
if (-not (Test-Path "$publish/Z-zapret.exe")) { throw 'Published application is missing.' }
Copy-Item "$root/bin" $publish -Recurse
Copy-Item "$root/lists" $publish -Recurse
Copy-Item "$root/utils" $publish -Recurse
Copy-Item "$root/.service" $publish -Recurse
Copy-Item "$root/*.bat" $publish
Copy-Item "$root/LICENSE.txt" $publish
Copy-Item "$PSScriptRoot/THIRD-PARTY-NOTICES.txt" $publish
Copy-Item "$PSScriptRoot/README.md" "$publish/README-DESKTOP.md"
if (Test-Path "$PSScriptRoot/licenses") { Copy-Item "$PSScriptRoot/licenses" $publish -Recurse }
$packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
foreach ($package in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    if (-not (Test-Path (Join-Path $packageCache $package))) { continue }
    $latest = Get-ChildItem (Join-Path $packageCache $package) -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($latest) {
        foreach ($notice in @('LICENSE.TXT', 'LICENSE', 'THIRD-PARTY-NOTICES.TXT')) {
            $file = Join-Path $latest.FullName $notice
            if (Test-Path $file) { Copy-Item $file (Join-Path $publish "licenses/$package-$notice") }
        }
    }
}
$hashes = [ordered]@{}
Get-ChildItem "$root/bin" -File | Sort-Object Name | ForEach-Object {
    $hashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
$hashes | ConvertTo-Json | Set-Content "$publish/engine-manifest.json" -Encoding utf8NoBOM
$zip = Join-Path $OutputRoot "Z-zapret-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish/*" -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "Portable build: $zip"
