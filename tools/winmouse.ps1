# Test helper: move or click the mouse inside Sojourn's window by posting window messages, without touching the real
# cursor. GLFW takes cursor position and buttons from these messages, as from a hand on the mouse.
#   powershell -File tools/winmouse.ps1 -X 507 -Y 330 [-Click] [-ProcessId PID]
param([int]$X, [int]$Y, [switch]$Click, [int]$ProcessId = 0)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
$p = if ($ProcessId) { Get-Process -Id $ProcessId } else { Get-Process dotnet | Where-Object { $_.MainWindowTitle -like "Sojourn*" } | Select-Object -First 1 }
if (-not $p) { throw "no Sojourn window" }
$h = $p.MainWindowHandle
$l = [IntPtr](($Y -shl 16) -bor ($X -band 0xffff))
[void][W]::PostMessage($h, 0x0200, [IntPtr]0, $l)          # WM_MOUSEMOVE
if ($Click) {
  Start-Sleep -Milliseconds 120
  [void][W]::PostMessage($h, 0x0201, [IntPtr]1, $l)        # WM_LBUTTONDOWN
  Start-Sleep -Milliseconds 90
  [void][W]::PostMessage($h, 0x0202, [IntPtr]0, $l)        # WM_LBUTTONUP
}
"moved to $X,$Y in $($p.MainWindowTitle.Substring(0, [Math]::Min(30, $p.MainWindowTitle.Length)))"
