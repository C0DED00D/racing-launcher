<#
Lists what config\seats.json needs: game controllers (for "joystick" / "twinStick"), audio outputs (for "audio")
and monitors (for "monitor").
  powershell -ExecutionPolicy Bypass -File scripts\list-devices.ps1 [-AllAudio]
Turn the TVs/monitors and controllers on first: devices that are off don't show (or show as "not present").
#>
param([switch]$AllAudio)
$ErrorActionPreference = 'Stop'

Write-Host "`n== Game controllers (seats.json ""joystick"" = a unique part of the path below) =="
$pads = @(Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction SilentlyContinue | Where-Object FriendlyName -match 'game controller')
if (-not $pads) { Write-Host "  none found (is the wheel plugged in and switched on?)" }
foreach ($p in $pads) {
    $parent = (Get-PnpDeviceProperty -InstanceId $p.InstanceId -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
    $name = if ($parent) { (Get-PnpDeviceProperty -InstanceId $parent -KeyName DEVPKEY_Device_BusReportedDeviceDesc -ErrorAction SilentlyContinue).Data }
    $id = $p.InstanceId.ToLower()
    $suggest = $null
    if ($id -match 'hidclass&(col\d\d)') { $name = "vJoy device $([int]$Matches[1].Substring(3)) (e.g. from the wheel hub)"; $suggest = $Matches[1] }
    elseif ($id -match '(vid_[0-9a-f]{4}&pid_[0-9a-f]{4})(&mi_\d\d)?') { $suggest = $Matches[0] }
    if (-not $name) { $name = $p.FriendlyName }
    Write-Host ("  {0}`n      path part: {1}   (full: {2})" -f $name, $suggest, $id.Replace('\', '#'))
}
Write-Host "  Two identical wheels: use the part that differs (vJoy: col01 / col02; a USB adapter with two ports: &mi_00 / &mi_01)."
Write-Host "  Button numbers and axes: Windows' 'Set up USB game controllers' (joy.cpl) > Properties shows them, 1-based."

Write-Host "`n== Audio outputs (seats.json ""audio"" = part of the name) =="
$states = @{ 1 = 'active'; 2 = 'disabled'; 4 = 'not present'; 8 = 'unplugged' }
$seen = @{}
foreach ($k in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render') {
    $state = [int]((Get-ItemProperty $k.PSPath).DeviceState) -band 0xF
    if ($state -ne 1 -and -not $AllAudio) { continue }
    $props = Get-ItemProperty "$($k.PSPath)\Properties" -ErrorAction SilentlyContinue
    $n = $props.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'; $d = $props.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
    $line = "  {0,-28} {1,-34} {2}" -f $n, "($d)", $states[$state]
    if (-not $seen.ContainsKey($line)) { $seen[$line] = 1; Write-Host $line }
}
if ($seen.Count -eq 0) { Write-Host "  no active outputs (TVs off?). -AllAudio lists every output Windows remembers." }
elseif (-not $AllAudio) { Write-Host "  (only active outputs; -AllAudio lists every output Windows remembers, e.g. a TV that is off)" }

Write-Host "`n== Monitors (seats.json ""monitor"": ""primary"", ""secondary"" or the index) =="
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DpiAware { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][DpiAware]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
$i = 0
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    Write-Host ("  {0}: {1}  {2}x{3} at ({4},{5}){6}" -f $i, $s.DeviceName, $s.Bounds.Width, $s.Bounds.Height, $s.Bounds.X, $s.Bounds.Y,
        $(if ($s.Primary) { '  PRIMARY (seat 1)' } else { '' }))
    $i++
}
$names = @(Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID -ErrorAction SilentlyContinue |
    ForEach-Object { -join ($_.UserFriendlyName | Where-Object { $_ } | ForEach-Object { [char]$_ }) })
if ($names) { Write-Host "  connected displays: $($names -join ', ')" }
Write-Host ""
