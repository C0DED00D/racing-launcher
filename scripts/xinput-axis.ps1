# Holds the left stick X and/or the triggers on a controller slot for a moment, in programs that load the stand-in
# xinput DLL (tools/xinput-inject); the axis counterpart of xinput-press.ps1 (same shared memory, InjectBlock).
#   xinput-axis.ps1 -Slot 0 -LX 32000 -Ms 300        steer right for 0.3 s (-32767..32767)
#   xinput-axis.ps1 -Slot 1 -RT 255 -Ms 500          gas
# Slots must be kept "plugged in" by xinput-press.ps1 -Connect.
param([int]$Slot = 0, [int]$LX = 0, [int]$LT = 0, [int]$RT = 0, [int]$Ms = 300)

$Size = 48; $Magic = 0x52584931
$OffActive = 4; $OffLT = 28; $OffRT = 32; $OffLX = 36; $OffSequence = 44

Add-Type -AssemblyName System.Core
$map = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateOrOpen("Local\RacingXInputInject", $Size)
$view = $map.CreateViewAccessor(0, $Size)
function Bump { $view.Write($OffSequence, [uint32]($view.ReadUInt32($OffSequence) + 1)) }
try {
  $view.Write(0, [uint32]$Magic)
  $view.Write($OffActive + 4 * $Slot, [uint32]1)
  $view.Write($OffLT + $Slot, [byte]$LT)
  $view.Write($OffRT + $Slot, [byte]$RT)
  $view.Write($OffLX + 2 * $Slot, [int16]$LX)
  Bump
  Start-Sleep -Milliseconds $Ms
} finally {
  $view.Write($OffLT + $Slot, [byte]0)
  $view.Write($OffRT + $Slot, [byte]0)
  $view.Write($OffLX + 2 * $Slot, [int16]0)
  Bump
  $view.Dispose(); $map.Dispose()
}
