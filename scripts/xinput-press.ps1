# Presses controller buttons in programs that load the stand-in xinput1_4.dll (tools/xinput-inject), for testing
# without anyone at the wheels. The DLL merges what this script writes into XInputGetState for that slot.
#   xinput-press.ps1 -Connect "0 1" -Seconds 600        keep slots 0 and 1 "plugged in" (start before the program:
#                                                       programs often only pick controllers present at start)
#   xinput-press.ps1 -Slot 0 -Buttons Back              tap Back on controller slot 0 (200 ms)
#   xinput-press.ps1 -Slot 1 -Buttons "X X X Back"      several taps in a row
#   xinput-press.ps1 -Slot 0 -Buttons Start -Hold 1000  hold for a second
# Buttons: A B X Y LB RB Back Start Up Down Left Right LS RS. Nothing is injected outside a press or -Connect.
param([int]$Slot = 0, [string]$Buttons = "", [int]$Hold = 200, [int]$Gap = 400, [string]$Connect = "",
      [int]$Seconds = 600)

$bits = @{ Up = 0x0001; Down = 0x0002; Left = 0x0004; Right = 0x0008; Start = 0x0010; Back = 0x0020; LS = 0x0040;
           RS = 0x0080; LB = 0x0100; RB = 0x0200; A = 0x1000; B = 0x2000; X = 0x4000; Y = 0x8000 }

# Layout of InjectBlock in tools/xinput-inject/xinput_inject.c.
$Size = 48; $Magic = 0x52584931
$OffActive = 4; $OffButtons = 20; $OffSequence = 44

Add-Type -AssemblyName System.Core
$map = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateOrOpen("Local\RacingXInputInject", $Size)
$view = $map.CreateViewAccessor(0, $Size)
function Bump { $view.Write($OffSequence, [uint32]($view.ReadUInt32($OffSequence) + 1)) }
try {
  $view.Write(0, [uint32]$Magic)
  if ($Connect) {
    $slots = $Connect -split '\s+' | Where-Object { $_ } | ForEach-Object { [int]$_ }
    foreach ($s in $slots) { $view.Write($OffActive + 4 * $s, [uint32]1) }
    Bump
    "slots $($slots -join ',') connected for $Seconds s"
    Start-Sleep -Seconds $Seconds
    foreach ($s in $slots) { $view.Write($OffActive + 4 * $s, [uint32]0) }
    Bump
    return
  }
  # A press on a slot nobody holds connected: connect it just for the press.
  $own = $view.ReadUInt32($OffActive + 4 * $Slot) -eq 0
  if ($own) { $view.Write($OffActive + 4 * $Slot, [uint32]1); Bump; Start-Sleep -Milliseconds 300 }
  foreach ($name in ($Buttons -split '\s+' | Where-Object { $_ })) {
    if (-not $bits.ContainsKey($name)) { throw "unknown button $name" }
    $view.Write($OffButtons + 2 * $Slot, [uint16]$bits[$name]); Bump
    Start-Sleep -Milliseconds $Hold
    $view.Write($OffButtons + 2 * $Slot, [uint16]0); Bump
    Start-Sleep -Milliseconds $Gap
  }
  if ($own) { $view.Write($OffActive + 4 * $Slot, [uint32]0); Bump }
} finally {
  if (-not $Connect) { $view.Write($OffButtons + 2 * $Slot, [uint16]0) }
  $view.Dispose(); $map.Dispose()
}
