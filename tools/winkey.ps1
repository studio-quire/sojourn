# Test helper: press a key in Sojourn's window by posting window messages (GLFW takes keys from them), held for HoldMs.
#   powershell -File tools/winkey.ps1 -ProcessId PID -Vk 84 [-HoldMs 3000]      (84 = T; virtual-key codes)
#   powershell -File tools/winkey.ps1 -ProcessId PID -Text "words"              (typed as characters, WM_CHAR)
param([int]$ProcessId, [int]$Vk, [int]$HoldMs = 100, [string]$Text)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class K {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
}
"@
$p = Get-Process -Id $ProcessId
$h = $p.MainWindowHandle
if ($Text) {
  foreach ($ch in $Text.ToCharArray()) {
    [void][K]::PostMessage($h, 0x0102, [IntPtr][int]$ch, [IntPtr]1)
    Start-Sleep -Milliseconds 5
  }
  "typed $($Text.Length) characters"
  exit
}
$scan = [K]::MapVirtualKey([uint32]$Vk, 0)
$down = [IntPtr](1 -bor ($scan -shl 16))
$up = [IntPtr]([int64]1 -bor ([int64]$scan -shl 16) -bor (3L -shl 30))
$t0 = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
[void][K]::PostMessage($h, 0x0100, [IntPtr]$Vk, $down)
Start-Sleep -Milliseconds $HoldMs
$t1 = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
[void][K]::PostMessage($h, 0x0101, [IntPtr]$Vk, $up)
"key $Vk down $t0 up $t1"
