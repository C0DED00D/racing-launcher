<#
Downloads emulators from their official GitHub releases into emulators\<name>\, checking each download against the
SHA-256 digest GitHub publishes for it (a file without a published digest, or with a wrong one, is refused).

  powershell -ExecutionPolicy Bypass -File scripts\install-emulators.ps1 -List
  powershell -ExecutionPolicy Bypass -File scripts\install-emulators.ps1 supermodel flycast
  powershell -ExecutionPolicy Bypass -File scripts\install-emulators.ps1 -All
  ... -Latest     the newest release instead of the version the launcher was tested with
  ... -Update     install over an existing copy (settings files that already exist are kept)
  ... -DryRun     show what would be downloaded (release, file, size, SHA-256) without downloading

Downloads are kept in tools\downloads\<name>\. Nothing is installed system-wide.
daytona-arcade-recomp is built from source with your own ROM, so it isn't here (see its README).
#>
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)][string[]]$Name,
    [switch]$All, [switch]$List, [switch]$Latest, [switch]$Update, [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is very slow with the progress bar on
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent

# name: GitHub repo, tested release tag ($null = latest), asset name pattern, folder, exe to check, notes.
$table = [ordered]@{
    'supermodel'    = @{ Repo = 'trzy/Supermodel'; Tag = 'v0.3a-20261029-git-036af1c'; Asset = '-windows\.zip$'
                         Exe = 'supermodel.exe'; Note = 'Sega Model 3' }
    'supermodel-m2' = @{ Repo = 'trzy/Supermodel'; Tag = 'v0.4-20261116-git-0b82a98'; Asset = '-windows\.zip$'
                         Exe = 'supermodel.exe'; Note = 'Supermodel with Model 2 support (games with "emulator": "supermodel-m2")' }
    'flycast'       = @{ Repo = 'flyinghead/flycast'; Tag = 'v2.7'; Asset = '^flycast-win64-[\d.]+\.zip$'
                         Exe = 'flycast.exe'; Note = 'Dreamcast / NAOMI' }
    'vcthunder'     = @{ Repo = 'mourix/VCThunder'; Tag = 'v0.4'; Asset = '-windows\.zip$'
                         Exe = 'VCThunder.exe'; Note = 'Midway Vegas' }
    'xenia'         = @{ Repo = 'AdrianCassar/xenia-canary'; Tag = 'v6.0.0'; Asset = '^xenia_canary_netplay_windows\.zip$'
                         Exe = 'xenia_canary_netplay.exe'; Note = 'Xbox 360 (made portable)'; Portable = 'portable.txt' }
    'xemu'          = @{ Repo = 'xemu-project/xemu'; Tag = 'v0.8.139'; Asset = '^xemu-[\d.]+-windows-x86_64\.zip$'
                         Exe = 'xemu.exe'; Note = 'original Xbox (needs your own Xbox BIOS files in bios\)' }
    'teknoparrot'   = @{ Repo = 'teknogods/TeknoParrot'; Tag = 'TeknoParrotUi'; Asset = '^TeknoParrotUi\.zip$'
                         Exe = 'TeknoParrotUi.exe'; Note = 'PC-based arcade (open it once to fetch components)'; NoLatest = $true }
}

if ($List -or (-not $All -and -not $Name)) {
    Write-Host "Emulators this script can install (emulators\<name>\):"
    foreach ($k in $table.Keys) {
        $e = $table[$k]; $have = Test-Path (Join-Path $root "emulators\$k\$($e.Exe)")
        Write-Host ("  {0,-14} {1,-28} {2}{3}" -f $k, $e.Tag, $e.Note, $(if ($have) { '  [installed]' } else { '' }))
    }
    Write-Host "Install some:  scripts\install-emulators.ps1 supermodel flycast    (or -All; -Latest for newest)"
    return
}
$names = if ($All) { @($table.Keys) } else { $Name }
foreach ($n in $names) { if (-not $table.Contains($n)) { throw "Unknown emulator '$n'. Known: $($table.Keys -join ', ')" } }

# Files never overwritten by -Update: the emulator's settings, saves and mappings.
$keep = '\.(ini|cfg|toml|config|json|xml|nv|nvmem|txt)$|\\(Config|NVRAM|Saves|data|mappings|UserProfiles|bios|content|cache)\\'

function Get-Release($e) {
    $headers = @{ 'User-Agent' = 'racing-launcher-installer'; 'Accept' = 'application/vnd.github+json' }
    $uri = if ($e.Tag -and (-not $Latest -or $e.NoLatest)) { "https://api.github.com/repos/$($e.Repo)/releases/tags/$($e.Tag)" }
           else { "https://api.github.com/repos/$($e.Repo)/releases/latest" }
    Invoke-RestMethod -Uri $uri -Headers $headers
}

foreach ($n in $names) {
    $e = $table[$n]
    $dest = Join-Path $root "emulators\$n"
    if ((Test-Path (Join-Path $dest $e.Exe)) -and -not $Update) {
        Write-Host "$n : already installed in emulators\$n (use -Update to install over it)"; continue
    }
    $rel = Get-Release $e
    $asset = @($rel.assets | Where-Object { $_.name -match $e.Asset -and $_.name -notmatch 'pdb|arm64' }) | Select-Object -First 1
    if (-not $asset) { throw "$n : no asset matching '$($e.Asset)' in release $($rel.tag_name)" }
    if (-not $asset.digest -or $asset.digest -notmatch '^sha256:([0-9a-f]{64})$') {
        throw "$n : GitHub publishes no SHA-256 for $($asset.name); not downloading it unchecked. Get it by hand from $($rel.html_url)"
    }
    $want = $Matches[1]
    if ($DryRun) { Write-Host "$n : would download $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB, $($rel.tag_name)), SHA-256 $want"; continue }
    $dl = Join-Path $root "tools\downloads\$n"
    New-Item -ItemType Directory -Force $dl | Out-Null
    $file = Join-Path $dl $asset.name
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLower() -ne $want) {
        Write-Host "$n : downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB) from $($rel.tag_name) ..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing -Headers @{ 'User-Agent' = 'racing-launcher-installer' }
    }
    $got = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    if ($got -ne $want) { Remove-Item $file -Force; throw "$n : SHA-256 mismatch (got $got, GitHub says $want); download deleted" }
    Write-Host "$n : SHA-256 OK ($want)"

    $tmp = Join-Path $dl 'unpacked'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $file -DestinationPath $tmp
    # A zip with one top folder: install that folder's contents.
    $src = $tmp
    $top = @(Get-ChildItem $tmp -Force)
    if ($top.Count -eq 1 -and $top[0].PSIsContainer) { $src = $top[0].FullName }
    New-Item -ItemType Directory -Force $dest | Out-Null
    $copied = 0; $kept = 0
    foreach ($f in Get-ChildItem $src -Recurse -File -Force) {
        $relPath = $f.FullName.Substring($src.Length)
        $to = Join-Path $dest $relPath
        if ((Test-Path $to) -and ($relPath -match $keep)) { $kept++; continue }
        New-Item -ItemType Directory -Force (Split-Path $to) | Out-Null
        Copy-Item $f.FullName $to -Force; $copied++
    }
    Remove-Item $tmp -Recurse -Force
    if ($e.Portable -and -not (Test-Path (Join-Path $dest $e.Portable))) { New-Item -ItemType File (Join-Path $dest $e.Portable) | Out-Null }
    if (-not (Test-Path (Join-Path $dest $e.Exe))) { Write-Warning "$n : $($e.Exe) not found after unpacking; check emulators\$n and config\emulators.json" }
    Write-Host "$n : installed $($rel.tag_name) to emulators\$n ($copied files$(if ($kept) { ", kept $kept existing settings files" }))"
}
Write-Host "Done. Paths are already in config\emulators.example.json; run scripts\check-setup.ps1 next."
