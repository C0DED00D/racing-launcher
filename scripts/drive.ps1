# Test helper: screenshots and simulated keypresses, so games can be checked without someone at the cabinet.
#   drive.ps1 shot [1|2]             screenshot of a monitor -> runtime/shots/, prints the path (half size)
#   drive.ps1 shotfull [1|2]         the same at full resolution
#   drive.ps1 key F2 [holdMs]        press a key (scan code, so emulators that read raw input see it)
#   drive.ps1 keys "UP UP ENTER"     several keys in a row
#   drive.ps1 down 1 / up 1          hold / release a key (for "hold Start and press ..." combos)
#   drive.ps1 focus daytona          bring a process's window to the front (name part, or a process id)
param([string]$cmd, [string]$arg = "", [int]$hold = 80)

Add-Type -ReferencedAssemblies System.Drawing, System.Windows.Forms @'
using System; using System.Drawing; using System.Runtime.InteropServices; using System.Windows.Forms;
public static class Drive {
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
  public static void DpiAware() { SetProcessDpiAwarenessContext((IntPtr)(-4)); }
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  // Windows refuses SetForegroundWindow from a background process; the same F24 + AttachThreadInput
  // trick the launcher uses gets around that.
  public static bool Focus(IntPtr h) {
    Key(Keys.F24, 1);
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    bool att = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
    try { BringWindowToTop(h); return SetForegroundWindow(h); }
    finally { if (att) AttachThreadInput(me, fg, false); }
  }
  public static void Down(Keys k) { Send(k, false); }
  public static void Up(Keys k) { Send(k, true); }
  static void Send(Keys k, bool up) {
    uint sc = MapVirtualKey((uint)k, 0);
    bool ext = k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right;
    uint f = 0x0008u | (ext ? 0x0001u : 0u) | (up ? 0x0002u : 0u);
    var i = new INPUT { type = 1, ki = new KEYBDINPUT { vk = (ushort)k, scan = (ushort)sc, flags = f } };
    SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Key(Keys k, int holdMs) {
    uint sc = MapVirtualKey((uint)k, 0);
    // Arrow keys, Insert/Delete/Home/End/PgUp/PgDn are "extended" keys.
    bool ext = k == Keys.Up || k == Keys.Down || k == Keys.Left || k == Keys.Right || k == Keys.Insert ||
               k == Keys.Delete || k == Keys.Home || k == Keys.End || k == Keys.PageUp || k == Keys.PageDown;
    uint f = 0x0008u | (ext ? 0x0001u : 0u); // KEYEVENTF_SCANCODE (+ EXTENDEDKEY)
    var down = new INPUT { type = 1, ki = new KEYBDINPUT { vk = (ushort)k, scan = (ushort)sc, flags = f } };
    var up = new INPUT { type = 1, ki = new KEYBDINPUT { vk = (ushort)k, scan = (ushort)sc, flags = f | 0x0002u } };
    SendInput(1, new[] { down }, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(holdMs);
    SendInput(1, new[] { up }, Marshal.SizeOf(typeof(INPUT)));
  }
  public static string Shot(int monitor, string dir, bool fullSize) {
    var s = Screen.AllScreens[monitor - 1].Bounds;
    using (var full = new Bitmap(s.Width, s.Height))
    using (var g = Graphics.FromImage(full)) {
      g.CopyFromScreen(s.X, s.Y, 0, 0, s.Size);
      string p = System.IO.Path.Combine(dir, (fullSize ? "full-m" : "m") + monitor + "-" + DateTime.Now.ToString("HHmmss-fff") + ".png");
      if (fullSize) { full.Save(p, System.Drawing.Imaging.ImageFormat.Png); return p; }
      using (var half = new Bitmap(full, s.Width / 2, s.Height / 2)) {
        half.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        return p;
      }
    }
  }
}
'@
[Drive]::DpiAware()

function KeyOf([string]$name) {
  $map = @{ ENTER = 'Return'; ESC = 'Escape'; SPACE = 'Space'; '1' = 'D1'; '2' = 'D2'; '3' = 'D3'; '4' = 'D4'; '5' = 'D5'; '6' = 'D6'; '7' = 'D7'; '8' = 'D8'; '9' = 'D9'; '0' = 'D0' }
  $n = $name.ToUpper(); if ($map.ContainsKey($n)) { $n = $map[$n] }
  [System.Windows.Forms.Keys][Enum]::Parse([System.Windows.Forms.Keys], $n, $true)
}

switch ($cmd) {
  'shot' {
    $dir = Join-Path $PSScriptRoot '..\runtime\shots'; New-Item -ItemType Directory -Force $dir | Out-Null
    $m = 1; if ($arg) { $m = [int]$arg }
    [Drive]::Shot($m, (Resolve-Path $dir).Path, $false)
  }
  'shotfull' {
    $dir = Join-Path $PSScriptRoot '..\runtime\shots'; New-Item -ItemType Directory -Force $dir | Out-Null
    $m = 1; if ($arg) { $m = [int]$arg }
    [Drive]::Shot($m, (Resolve-Path $dir).Path, $true)
  }
  'key' { [Drive]::Key((KeyOf $arg), $hold) }
  'down' { [Drive]::Down((KeyOf $arg)) }
  'up' { [Drive]::Up((KeyOf $arg)) }
  'keys' { foreach ($k in $arg -split '\s+') { if ($k) { [Drive]::Key((KeyOf $k), $hold); Start-Sleep -Milliseconds 250 } } }
  'focus' {
    # A number is a process id (to pick one of two copies of the same emulator), anything else part of a name.
    if ($arg -match '^\d+$') { $p = Get-Process -Id ([int]$arg) -ErrorAction SilentlyContinue }
    else { $p = Get-Process | Where-Object { $_.ProcessName -like "*$arg*" -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
    if (-not $p -or $p.MainWindowHandle -eq 0) { Write-Error "No window for '$arg'"; exit 1 }
    # SW_RESTORE (9) only for a minimized window: on a maximized one it un-maximizes it.
    if ([Drive]::IsIconic($p.MainWindowHandle)) { [Drive]::ShowWindow($p.MainWindowHandle, 9) | Out-Null }
    [Drive]::Focus($p.MainWindowHandle) | Out-Null
    "focused $($p.ProcessName) ($($p.Id))"
  }
  default { Get-Content $PSCommandPath -TotalCount 6 }
}
