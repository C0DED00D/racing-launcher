# Gives a seat's TeknoParrot copy its own names for the pipe and shared memory that connect the TeknoParrot UI to
# an OpenParrot game, so two copies (one per seat) don't share wheel input or fight over the control pipe:
#   TeknoParrotPipe -> TeknoParrotPip<N>, TeknoParrot_JvsState -> TeknoParrot_JvsStat<N>,
#   TeknoParrot_JvsExtendedState -> TeknoParrot_JvsExtendedStat<N>, TeknoParrot_JVS -> TeknoParrot_JV<N>
#   (same length, so a byte patch works).
# Patches <seat>\tp\OpenParrotWin32\OpenParrot.dll and <seat>\tp\TeknoParrotUi.exe, and writes an unpacked, patched
# TeknoParrotUi.Common.dll next to the exe (the exe carries it Costura-packed; .NET loads a copy beside the exe first).
# The games' TeknoParrot core is packed, so the game side is renamed at run time by tools/seat-names instead.
# Originals are kept as *.orig. Seat 1 keeps the stock names.
# usage: tp-seat-names.ps1 -Seat 2
param([Parameter(Mandatory)][ValidateRange(2, 9)][int]$Seat)
$ErrorActionPreference = 'Stop'
$tp = (Resolve-Path (Join-Path $PSScriptRoot "..\runtime\link\teknoparrot\seat$Seat\tp")).Path
$names = [ordered]@{
  'TeknoParrotPipe'              = "TeknoParrotPip$Seat"
  'TeknoParrot_JVS'              = "TeknoParrot_JV$Seat"
  'TeknoParrot_JvsExtendedState' = "TeknoParrot_JvsExtendedStat$Seat"
  'TeknoParrot_JvsState'         = "TeknoParrot_JvsStat$Seat"
}

function Patch([byte[]]$bytes, [string]$label) {
  $total = 0
  foreach ($from in $names.Keys) {
    $to = $names[$from]
    foreach ($enc in [Text.Encoding]::ASCII, [Text.Encoding]::Unicode) {
      $f = $enc.GetBytes($from); $t = $enc.GetBytes($to); $n = 0
      for ($i = 0; $i -le $bytes.Length - $f.Length; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $f.Length; $j++) { if ($bytes[$i + $j] -ne $f[$j]) { $hit = $false; break } }
        # Don't touch JvsExtendedState when looking for JvsState (it isn't a prefix, but be safe about overlaps).
        if ($hit) { [Array]::Copy($t, 0, $bytes, $i, $t.Length); $n++; $i += $f.Length - 1 }
      }
      if ($n) { "  $label : $from -> $to ($($enc.WebName)) x$n"; $total += $n }
    }
  }
  if (-not $total) { throw "$label : nothing to patch" }
}

# OpenParrot.dll (the game side).
$op = Join-Path $tp "OpenParrotWin32\OpenParrot.dll"
if (-not (Test-Path "$op.orig")) { Copy-Item $op "$op.orig" }
$b = [IO.File]::ReadAllBytes("$op.orig"); Patch $b "OpenParrot.dll"; [IO.File]::WriteAllBytes($op, $b)

# TeknoParrotUi.Common.dll, unpacked from the exe's Costura resources.
$ui = Join-Path $tp "TeknoParrotUi.exe"
$src = if (Test-Path "$ui.orig") { "$ui.orig" } else { $ui }
$asm = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($src)) # from bytes: LoadFrom locks the file
$s = $asm.GetManifestResourceStream('costura.teknoparrotui.common.dll.compressed')
$d = New-Object IO.Compression.DeflateStream($s, [IO.Compression.CompressionMode]::Decompress)
$ms = New-Object IO.MemoryStream; $d.CopyTo($ms); $b = $ms.ToArray()
Patch $b "TeknoParrotUi.Common.dll"
[IO.File]::WriteAllBytes((Join-Path $tp "TeknoParrotUi.Common.dll"), $b)

# TeknoParrotUi.exe itself names the I/O board pipe (TeknoParrot_JVS, used by ElfLdr2 games such as OutRun 2).
if (-not (Test-Path "$ui.orig")) { Copy-Item $ui "$ui.orig" }
$b = [IO.File]::ReadAllBytes("$ui.orig"); Patch $b "TeknoParrotUi.exe"; [IO.File]::WriteAllBytes($ui, $b)
"done: seat $Seat"
