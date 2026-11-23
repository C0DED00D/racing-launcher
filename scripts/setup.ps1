<#
One-time setup after cloning. Safe to run again: it never overwrites your config files.
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
  ... -Emulators supermodel,flycast   also install those emulators (scripts\install-emulators.ps1; -AllEmulators = every one)
  ... -BuildTools                     also build the helper DLLs in tools\ (needs Visual Studio C++ build tools; Linked mode)
  ... -Configuration Debug            build Debug instead of Release
Steps: check the .NET 10 SDK, create config\*.json from the examples, build the launcher, optionally install
emulators and build the helper DLLs, install the git pre-commit guard, then run scripts\check-setup.ps1.
#>
param([string[]]$Emulators, [switch]$AllEmulators, [switch]$BuildTools, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "== .NET 10 SDK"
$sdk = @(& dotnet --list-sdks 2>$null) -match '^10\.'
if (-not $sdk) {
    throw ".NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download (or: winget install Microsoft.DotNet.SDK.10), then run this again."
}
Write-Host "   $($sdk[0])"

Write-Host "== Config files"
foreach ($n in 'emulators', 'games', 'seats') {
    $dst = "config\$n.json"
    if (Test-Path $dst) { Write-Host "   $dst exists, left as is" }
    else { Copy-Item "config\$n.example.json" $dst; Write-Host "   created $dst from the example: edit it (CONFIG.md explains every field)" }
}
New-Item -ItemType Directory -Force 'runtime\logs', 'emulators', 'games', 'art' | Out-Null

Write-Host "== Build ($Configuration)"
dotnet build src\RacingLauncher.sln -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Write-Host "   src\Launcher.App\bin\$Configuration\net10.0-windows\RacingLauncher.exe"

if ($AllEmulators -or $Emulators) {
    Write-Host "== Emulators"
    if ($AllEmulators) { & "$PSScriptRoot\install-emulators.ps1" -All } else { & "$PSScriptRoot\install-emulators.ps1" @Emulators }
}

if ($BuildTools) {
    Write-Host "== Helper DLLs"
    foreach ($t in 'xinput-inject', 'dinput-bg', 'seat-names') {
        Write-Host "   tools\$t"
        cmd /c "tools\$t\build.cmd" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "tools\$t\build.cmd failed" }
    }
}

if ((Test-Path '.git') -and -not (Test-Path '.git\hooks\pre-commit')) {
    $u = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $root '.git\hooks\pre-commit'), ([IO.File]::ReadAllText((Join-Path $root 'scripts\pre-commit')) -replace "`r`n", "`n"), $u)
    Write-Host "== Installed the git pre-commit guard (refuses ROMs, binaries and private files)"
}

Write-Host "== Check"
& "$PSScriptRoot\check-setup.ps1"
Write-Host "`nNext: edit config\games.json and config\seats.json (CONFIG.md), then:"
Write-Host "   dotnet run --project src\Launcher.App -c $Configuration"
