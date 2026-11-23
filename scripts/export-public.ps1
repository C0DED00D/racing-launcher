# Refreshes the public repository (github.com/C0DED00D/racing-launcher) from this working folder.
# Copies an allowlist only: no ROMs, game files, emulators, personal config, notes (CLAUDE.md, TODO.md, DESIGN.md,
# docs/) or tile art. Public-only files (README, LICENSE, .gitignore, example configs) live in public/.
#   powershell -ExecutionPolicy Bypass -File scripts\export-public.ps1 [-Out <folder>]
# Then review and commit in the export folder (git status / git diff).
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path (Split-Path $root -Parent) 'racing-launcher' }
New-Item -ItemType Directory -Force $Out | Out-Null

# Everything but .git is rebuilt from scratch, so files dropped here disappear there too.
Get-ChildItem $Out -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force

function Copy-Tree($rel, [string[]]$include = @('*')) {
    $src = Join-Path $root $rel
    Get-ChildItem $src -Recurse -File -Include $include |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|x64|x86|\.vs)\\' } |
        ForEach-Object {
            $dst = Join-Path $Out $_.FullName.Substring($root.Length + 1)
            New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
            Copy-Item $_.FullName $dst
        }
}

foreach ($p in 'Launcher.App', 'Launcher.Core', 'BootSelect', 'AtGamesMouse') { Copy-Tree "src\$p" }
Copy-Item (Join-Path $root 'src\RacingLauncher.sln') (Join-Path $Out 'src\')
foreach ($t in 'xinput-inject', 'dinput-bg', 'seat-names') { Copy-Tree "tools\$t" '*.c', '*.h', '*.def', 'build.cmd' }
New-Item -ItemType Directory -Force (Join-Path $Out 'scripts'), (Join-Path $Out 'config') | Out-Null
foreach ($s in 'drive.ps1', 'padlog.ps1', 'xinput-press.ps1', 'xinput-axis.ps1', 'tp-seat-names.ps1',
               'boot-select.cmd', 'build-wheelhub.ps1', 'export-public.ps1') {
    Copy-Item (Join-Path $root "scripts\$s") (Join-Path $Out 'scripts\')
}
Copy-Item (Join-Path $root 'config\atgames-mouse.json') (Join-Path $Out 'config\')
Copy-Tree 'config\flycast-mappings'
Copy-Item (Join-Path $root 'public\*') $Out -Recurse -Force
Copy-Item (Join-Path $root 'public\.gitignore') $Out -Force

# The solution must not list projects that stay private.
$sln = Join-Path $Out 'src\RacingLauncher.sln'
if (Select-String -Path $sln -Pattern 'WheelHub' -Quiet) { throw 'RacingLauncher.sln still lists WheelHub' }
Write-Host "Exported to $Out"
