# Publishes the wheel hub (github.com/C0DED00D/x360-wheel-ffb, checked out beside this folder by default) to
# tools\wheelhub\, where the launcher looks for it. Stops a running hub first.
#   powershell -ExecutionPolicy Bypass -File scripts\build-wheelhub.ps1 [-HubRepo <path to x360-wheel-ffb>]
param([string]$HubRepo)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $HubRepo) { $HubRepo = Join-Path (Split-Path $root -Parent) 'x360-wheel-ffb' }
$publish = Join-Path $HubRepo 'scripts\publish.ps1'
if (-not (Test-Path $publish)) { throw "x360-wheel-ffb not found at $HubRepo (git clone https://github.com/C0DED00D/x360-wheel-ffb, or pass -HubRepo)" }
& $publish -Out (Join-Path $root 'tools\wheelhub')
