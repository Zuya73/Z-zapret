param([string]$OutputRoot = "$PSScriptRoot/../artifacts")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$version = (Get-Content "$root/legacy/flowseal/.service/version.txt" -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid upstream version.' }
$stage = Join-Path $OutputRoot 'flowseal-legacy-tools'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item "$stage/utils" -ItemType Directory -Force | Out-Null
Copy-Item "$root/legacy/flowseal/*.bat" $stage
Copy-Item "$root/legacy/flowseal/.service" $stage -Recurse
foreach ($name in @('test zapret.ps1', 'targets.txt', 'check_updates.enabled')) {
    Copy-Item "$root/legacy/flowseal/utils/$name" "$stage/utils/$name"
}
Copy-Item "$root/vendor/zapret/bin", "$root/resources/lists", "$root/vendor/zapret/licenses" $stage -Recurse
Copy-Item "$root/legacy/flowseal/README.md", "$root/LICENSE.txt" $stage
Copy-Item "$root/docs/THIRD-PARTY-NOTICES.txt" $stage
python "$root/tools/verify_package.py" --legacy $stage
if ($LASTEXITCODE -ne 0) { throw 'Legacy package validation failed.' }
$zip = Join-Path $OutputRoot "Flowseal-legacy-tools-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage/*" -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Optional legacy tools: $zip"
