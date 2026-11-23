<#
Checks the setup before you start the launcher: configs present and readable, each emulator installed, each game's
files in place, seats matched to attached controllers, audio outputs and monitors, the wheel hub, Windows settings.
  powershell -ExecutionPolicy Bypass -File scripts\check-setup.ps1
Prints OK / WARN / FAIL lines; exits 1 if anything FAILed. The launcher's tiles show the same game problems as
SETUP NEEDED; this also covers seats, audio and the hub.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$script:fails = 0; $script:warns = 0
function Ok($m) { Write-Host "  OK    $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN  $m" -ForegroundColor Yellow; $script:warns++ }
function Fail($m) { Write-Host "  FAIL  $m" -ForegroundColor Red; $script:fails++ }

# The configs allow // and /* */ comments and trailing commas (the launcher reads them that way); PowerShell 5.1's
# ConvertFrom-Json doesn't, so strip them outside strings first.
function Read-Jsonc($path) {
    $t = [IO.File]::ReadAllText($path); $sb = New-Object Text.StringBuilder; $i = 0; $inStr = $false
    while ($i -lt $t.Length) {
        $c = $t[$i]
        if ($inStr) { [void]$sb.Append($c); if ($c -eq '\') { [void]$sb.Append($t[$i + 1]); $i += 2; continue }; if ($c -eq '"') { $inStr = $false }; $i++; continue }
        if ($c -eq '"') { $inStr = $true; [void]$sb.Append($c); $i++; continue }
        if ($c -eq '/' -and $i + 1 -lt $t.Length -and $t[$i + 1] -eq '/') { while ($i -lt $t.Length -and $t[$i] -ne "`n") { $i++ }; continue }
        if ($c -eq '/' -and $i + 1 -lt $t.Length -and $t[$i + 1] -eq '*') { $e = $t.IndexOf('*/', $i + 2); $i = if ($e -lt 0) { $t.Length } else { $e + 2 }; continue }
        [void]$sb.Append($c); $i++
    }
    $clean = [regex]::Replace($sb.ToString(), ',(\s*[}\]])', '$1')
    return $clean | ConvertFrom-Json
}
function Resolve-Root($p) { if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $root $p } }
function Load($name) {
    $f = Join-Path $root "config\$name"
    if (-not (Test-Path $f)) { return $null }
    try { $j = Read-Jsonc $f; Ok "config\$name read"; return $j } catch { Fail "config\$name is not valid JSON: $($_.Exception.Message)"; return $null }
}

Write-Host "`n== Tools"
$sdk = @(& dotnet --list-sdks 2>$null) -match '^10\.'
if ($sdk) { Ok ".NET 10 SDK ($($sdk[0].Split(' ')[0]))" } else { Fail ".NET 10 SDK not found (https://dotnet.microsoft.com/download)" }
$exe = Get-ChildItem (Join-Path $root 'src\Launcher.App\bin') -Recurse -Filter RacingLauncher.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if ($exe) { Ok "launcher built ($($exe.FullName.Substring($root.Length + 1)))" } else { Warn "launcher not built yet (scripts\setup.ps1 or dotnet build src\RacingLauncher.sln -c Release)" }

Write-Host "`n== Config files"
foreach ($n in 'emulators.json', 'games.json', 'seats.json') {
    if (-not (Test-Path (Join-Path $root "config\$n"))) { Fail "config\$n missing: copy config\$($n -replace '\.json$', '.example.json') (scripts\setup.ps1 does it)" }
}
$emus = Load 'emulators.json'; $games = Load 'games.json'; $seats = Load 'seats.json'

Write-Host "`n== Emulators"
$emuPath = @{}
if ($emus) {
    foreach ($p in $emus.emulators.PSObject.Properties) {
        $full = Resolve-Root $p.Value.path; $emuPath[$p.Name] = $full
        $how = switch ($p.Name) {
            'daytona-recomp' { 'build it from source with your ROM (its README)' }
            'xenia-link' { 'patched build for Xenia Linked, not published yet (Solo works without it)' }
            default { "scripts\install-emulators.ps1 $($p.Name)" }
        }
        if (Test-Path $full) { Ok "$($p.Name): $($p.Value.path)" } else { Warn "$($p.Name): not installed ($($p.Value.path)); $how" }
    }
}

Write-Host "`n== Games"
$runners = @{ supermodel = 'supermodel'; flycast = 'flycast'; vcthunder = 'vcthunder'; xenia = 'xenia'; xemu = 'xemu'; teknoparrot = 'teknoparrot'; 'daytona-recomp' = 'daytona-recomp'; exe = $null; steam = $null }
$wantsLinked = $false
if ($games) {
    $ids = @{}
    foreach ($g in $games.games) {
        $label = "$($g.id) ($($g.title))"
        if ($ids.ContainsKey($g.id)) { Fail "${label}: duplicate id" }; $ids[$g.id] = 1
        if (-not $runners.ContainsKey([string]$g.runner)) { Fail "${label}: unknown runner '$($g.runner)' (known: $($runners.Keys -join ', '))"; continue }
        $problems = @()
        $key = if ($g.emulator) { $g.emulator } else { $runners[[string]$g.runner] }
        if ($key) {
            if (-not $emuPath.ContainsKey($key)) { $problems += "no '$key' entry in emulators.json" }
            elseif (-not (Test-Path $emuPath[$key])) { $problems += "$key not installed" }
        }
        if ($g.runner -eq 'exe') { if (-not $g.exe -or -not (Test-Path (Resolve-Root $g.exe))) { $problems += "exe not found: $($g.exe)" } }
        elseif ($g.runner -eq 'steam') {
            if (-not $g.steamAppId) { $problems += 'steamAppId missing' }
            if (-not (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamExe) { $problems += 'Steam not installed' }
        }
        elseif ($g.rom -and -not (Test-Path (Resolve-Root $g.rom))) { $problems += "game file not found: $($g.rom)" }
        elseif (-not $g.rom -and $g.runner -ne 'daytona-recomp') { $problems += 'no "rom"' }
        if ($g.runner -eq 'flycast' -and $g.rom -match '\.(zip|7z)$' -and $emuPath['flycast'] -and -not (Test-Path (Join-Path (Split-Path $emuPath['flycast']) 'data\naomi.zip'))) { $problems += 'NAOMI BIOS missing (emulators\flycast\data\naomi.zip)' }
        if ($g.runner -eq 'xemu' -and $emuPath['xemu']) {
            foreach ($b in 'mcpx_1.0.bin', 'bios.bin', 'xbox_hdd.qcow2') { if (-not (Test-Path (Join-Path (Split-Path $emuPath['xemu']) "bios\$b"))) { $problems += "xemu needs your bios\$b" } }
        }
        if ($g.runner -eq 'teknoparrot') {
            if (-not $g.tpProfile) { $problems += 'no "tpProfile"' }
            elseif ($emuPath['teknoparrot'] -and -not (Test-Path (Join-Path (Split-Path $emuPath['teknoparrot']) "UserProfiles\$($g.tpProfile).xml"))) { $problems += "UserProfiles\$($g.tpProfile).xml missing: add the game in TeknoParrotUi first" }
        }
        if ($g.modes -contains 'Linked') {
            $wantsLinked = $true
            if ($g.runner -eq 'xenia') {
                if (-not ($emuPath['xenia-link'] -and (Test-Path $emuPath['xenia-link']))) { $problems += 'Linked needs the patched xenia-link build' }
                if (@($g.linkProfiles).Count -lt 2) { $problems += 'Linked needs two "linkProfiles"' }
            }
            if ($g.runner -in 'xemu', 'exe', 'steam') { $problems += "runner $($g.runner) has no Linked mode" }
        }
        if ($problems) { Warn "${label}: $($problems -join '; ')" } else { Ok $label }
    }
}

Write-Host "`n== Seats"
$controllers = @(Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction SilentlyContinue | Where-Object FriendlyName -match 'game controller' |
    ForEach-Object { $_.InstanceId.ToLower().Replace('\', '#') })
$audio = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render' | ForEach-Object {
    $st = [int]((Get-ItemProperty $_.PSPath).DeviceState) -band 0xF
    $n = (Get-ItemProperty "$($_.PSPath)\Properties" -ErrorAction SilentlyContinue).'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
    [pscustomobject]@{ Name = $n; Active = ($st -eq 1) } })
Add-Type -AssemblyName System.Windows.Forms
$screens = [System.Windows.Forms.Screen]::AllScreens.Count
if ($seats) {
    $list = @($seats.seats)
    if ($list.Count -eq 0) { Fail 'seats.json has no seats' }
    if ($wantsLinked -and $list.Count -lt 2) { Warn 'some games offer Linked, but seats.json has only one seat' }
    if ($list.Count -ge 2 -and $screens -lt 2 -and -not $seats.devMode) { Warn "two seats but $screens monitor: set ""devMode"": true to tile both seats on one screen" }
    foreach ($s in $list) {
        $who = "seat $($s.id)"
        if ($s.joystick) {
            $m = @($controllers | Where-Object { $_.Contains($s.joystick.ToLower()) })
            if ($m.Count -eq 1) { Ok "$who joystick '$($s.joystick)' -> $($m[0])" }
            elseif ($m.Count -gt 1) { Warn "$who joystick '$($s.joystick)' matches $($m.Count) controllers; use a more specific part (scripts\list-devices.ps1)" }
            else { Warn "$who joystick '$($s.joystick)' matches no attached controller (off, or wheel hub not running?); scripts\list-devices.ps1" }
        } else { Warn "$who has no ""joystick"": games pick the first wheel" }
        if ($s.audio) {
            $a = @($audio | Where-Object { $_.Name -and $_.Name.ToLower().Contains($s.audio.ToLower()) })
            if ($a | Where-Object Active) { Ok "$who audio '$($s.audio)'" }
            elseif ($a) { Warn "$who audio '$($s.audio)' is known but not active now (TV off?)" }
            else { Warn "$who audio '$($s.audio)' matches no output Windows knows (scripts\list-devices.ps1 -AllAudio)" }
        }
        if ($s.monitor -eq 'secondary' -and $screens -lt 2 -and -not $seats.devMode) { Warn "$who wants the secondary monitor, but $screens is attached" }
    }
}

Write-Host "`n== Wheel hub (Xbox 360 wheels)"
if (Test-Path (Join-Path $root 'config\wheels.json')) {
    if (Test-Path (Join-Path $root 'tools\wheelhub\WheelHub.exe')) { Ok 'config\wheels.json + tools\wheelhub\WheelHub.exe' }
    else { Fail 'config\wheels.json exists but tools\wheelhub\WheelHub.exe is missing: scripts\build-wheelhub.ps1' }
} else { Ok 'not used (no config\wheels.json)' }

Write-Host "`n== Helper DLLs (Linked mode)"
foreach ($d in @(@('tools\xinput-inject\x64\xinput1_4.dll', 'Flycast Linked'), @('tools\dinput-bg\x64\version.dll', 'Supermodel Linked'))) {
    if (Test-Path (Join-Path $root $d[0])) { Ok "$($d[0])" } else { Warn "$($d[0]) not built ($($d[1]) needs it): scripts\setup.ps1 -BuildTools" }
}

Write-Host "`n== Windows"
$gb = (Get-ItemProperty 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue).UseNexusForGameBarEnabled
if ($gb -eq 0) { Ok 'Game Bar does not open on the Guide button' }
else { Warn 'Game Bar opens on the Guide button: Settings > Gaming > Game Bar > turn off "Open Game Bar using this button on a controller"' }

Write-Host ""
if ($script:fails) { Write-Host "$($script:fails) problem(s), $($script:warns) warning(s)." -ForegroundColor Red; exit 1 }
Write-Host "No blocking problems; $($script:warns) warning(s)." -ForegroundColor Green
