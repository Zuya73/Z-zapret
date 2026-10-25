param([string]$OutputRoot = "$PSScriptRoot/../artifacts", [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$version = ([xml](Get-Content "$root/Directory.Build.props" -Raw)).SelectSingleNode('//Version').InnerText
$publish = Join-Path $OutputRoot 'x2zapret'
if (-not $SkipPublish) {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    New-Item $publish -ItemType Directory -Force | Out-Null
    dotnet publish "$root/src/Zapret.Desktop/Zapret.Desktop.csproj" -c Release -r win-x64 --self-contained true -o $publish -p:PublishSingleFile=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
}
if (-not (Test-Path "$publish/x2zapret.exe")) { throw 'Published application is missing.' }
Copy-Item "$root/vendor/zapret/bin" $publish -Recurse
Copy-Item "$root/resources/lists" $publish -Recurse
Copy-Item "$root/LICENSE.txt" $publish
Copy-Item "$root/docs/THIRD-PARTY-NOTICES.txt" $publish
Copy-Item "$root/docs/USER-GUIDE.md" "$publish/README.md"
Copy-Item "$root/vendor/zapret/licenses" $publish -Recurse
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
python "$root/tools/package_hardware_licenses.py" $publish
if ($LASTEXITCODE -ne 0) { throw 'Hardware dependency licences could not be packaged.' }
$hashes = [ordered]@{}
Get-ChildItem "$root/vendor/zapret/bin" -File | Sort-Object Name | ForEach-Object {
    $hashes[$_.Name] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
$hashes | ConvertTo-Json | Set-Content "$publish/engine-manifest.json" -Encoding utf8NoBOM
python "$root/tools/verify_package.py" $publish
if ($LASTEXITCODE -ne 0) { throw 'Runtime package validation failed.' }
$zip = Join-Path $OutputRoot "x2zapret-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$publish/*" -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "Portable build: $zip"
