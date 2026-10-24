# Run only on the disposable Windows CI desktop. Always restore its original work area.
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowBoundsDesktop {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref Rect rect, uint flags);
}
'@
$original = [WindowBoundsDesktop+Rect]::new()
if (-not [WindowBoundsDesktop]::SystemParametersInfo(0x0030, 0, [ref]$original, 0)) { throw 'Cannot read desktop work area.' }

function Check-Window([string]$name) {
    $receipt = Join-Path $PWD "artifacts/window-bounds-$name.json"
    $arguments = 'artifacts/x2zapret/x2zapret.dll --window-bounds-check "' + $receipt + '"'
    $process = Start-Process dotnet -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Window bounds test timed out.' }
    if ($process.ExitCode -ne 0 -or -not (Test-Path $receipt)) {
        if (Test-Path smoke-error.txt) { Get-Content smoke-error.txt }
        throw "Window bounds test failed: $name"
    }
    Get-Content $receipt
}

try {
    Check-Window 'desktop'
    $compact = $original
    $compact.Right = $compact.Left + [Math]::Min(1280, $original.Right - $original.Left)
    $compact.Bottom = $compact.Top + [Math]::Min(680, $original.Bottom - $original.Top)
    if (-not [WindowBoundsDesktop]::SystemParametersInfo(0x002F, 0, [ref]$compact, 2)) { throw 'Cannot set compact work area.' }
    Check-Window 'compact'
    # Also reserve space along the top and left, not only along the bottom.
    $compact.Left += 48; $compact.Top += 32
    if (-not [WindowBoundsDesktop]::SystemParametersInfo(0x002F, 0, [ref]$compact, 2)) { throw 'Cannot move work area.' }
    Check-Window 'offset'
}
finally {
    if (-not [WindowBoundsDesktop]::SystemParametersInfo(0x002F, 0, [ref]$original, 2)) { throw 'Cannot restore desktop work area.' }
}
