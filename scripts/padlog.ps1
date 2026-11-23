# Records both XInput slots to runtime/logs/padlog.txt whenever something changes (10 ms polling).
# Shows steering (LX), pedals (LT/RT, LY), and pressed buttons, so wheel mappings can be checked from the log.
#   powershell -File scripts/padlog.ps1 [seconds]
param([int]$seconds = 900)

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class XI {
  [StructLayout(LayoutKind.Sequential)] public struct STATE { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
  // Ordinal 100 (XInputGetStateEx) also reports the Guide button.
  [DllImport("xinput1_4.dll", EntryPoint = "#100")] public static extern uint GetStateEx(uint i, out STATE s);
}
'@
$names = [ordered]@{ 0x0001='Up'; 0x0002='Down'; 0x0004='Left'; 0x0008='Right'; 0x0010='Start'; 0x0020='Back'; 0x0040='LS'; 0x0080='RS';
                     0x0100='LB'; 0x0200='RB'; 0x0400='Guide'; 0x1000='A'; 0x2000='B'; 0x4000='X'; 0x8000='Y' }

$out = Join-Path $PSScriptRoot '..\runtime\logs\padlog.txt'
"---- padlog started $(Get-Date -Format 'HH:mm:ss') ----" | Out-File $out -Encoding utf8
$last = @{}
$end = (Get-Date).AddSeconds($seconds)
while ((Get-Date) -lt $end) {
  foreach ($slot in 0, 1) {
    $s = New-Object XI+STATE
    $r = [XI]::GetStateEx($slot, [ref]$s)
    if ($r -ne 0) { $line = 'disconnected' }
    else {
      # Look names up with [object] keys: an int index on an ordered dictionary means "position", not "key".
      $b = ($names.Keys | Where-Object { $s.Buttons -band $_ } | ForEach-Object { $names[[object]$_] }) -join '+'
      # Round axes a little so tiny jitter doesn't flood the log.
      $line = 'LX={0,6} LY={1,6} LT={2,3} RT={3,3} RX={4,6} RY={5,6} raw=0x{7:X4} btn={6}' -f ([int]($s.LX / 512) * 512), ([int]($s.LY / 512) * 512), ([int]($s.LT / 4) * 4), ([int]($s.RT / 4) * 4), ([int]($s.RX / 512) * 512), ([int]($s.RY / 512) * 512), $b, $s.Buttons
    }
    if ($last[$slot] -ne $line) { $last[$slot] = $line; "$(Get-Date -Format 'HH:mm:ss.ff') wheel$($slot + 1) $line" | Out-File $out -Append -Encoding utf8 }
  }
  Start-Sleep -Milliseconds 10
}
