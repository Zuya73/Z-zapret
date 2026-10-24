# The app checks the real monitor, then injects compact/offset work areas into the
# same native window behavior. This avoids Explorer/RDP reverting SPI_SETWORKAREA.
$ErrorActionPreference = 'Stop'
$receipt = Join-Path $PWD 'artifacts/window-bounds-desktop.json'
$arguments = 'artifacts/x2zapret/x2zapret.dll --window-bounds-check "' + $receipt + '"'
$process = Start-Process dotnet -ArgumentList $arguments -PassThru
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Window bounds test timed out.' }
if ($process.ExitCode -ne 0 -or -not (Test-Path $receipt)) {
    if (Test-Path smoke-error.txt) { Get-Content smoke-error.txt }
    throw 'Window bounds test failed.'
}
$checks = @(Get-Content $receipt -Raw | ConvertFrom-Json)
if ($checks.Count -ne 9 -or @($checks.work.height | Select-Object -Unique).Count -lt 2) {
    throw 'Expected nine checks with distinct work area sizes.'
}
Get-Content $receipt
