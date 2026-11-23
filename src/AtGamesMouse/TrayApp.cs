using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using static AtGamesMouse.Native;

namespace AtGamesMouse;

/// <summary>Tray icon + menu, and the mouse output: button down = mouse down, button up = mouse up, no repeat.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly bool _dryRun;
    private readonly DeviceWatcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _enabled;
    private readonly Icon _iconOn, _iconOff;
    private bool _leftHeld, _rightHeld;

    public TrayApp(Settings settings, bool dryRun)
    {
        _settings = settings;
        _dryRun = dryRun;
        _iconOn = MakeIcon(Color.FromArgb(0x2E, 0xB8, 0x4B));
        _iconOff = MakeIcon(Color.FromArgb(0x8A, 0x8A, 0x8A));

        _status = new ToolStripMenuItem { Enabled = false };
        _enabled = new ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        _enabled.CheckedChanged += (_, _) =>
        {
            if (!_enabled.Checked) ReleaseAll();
            Log.Write(_enabled.Checked ? "Enabled" : "Disabled");
            UpdateStatus();
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripMenuItem($"Left = button {settings.LeftButton}, right = button {settings.RightButton}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabled);
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };

        _watcher = new DeviceWatcher(settings);
        _watcher.ButtonChanged += OnButton;
        _watcher.ConnectionChanged += UpdateStatus;
        UpdateStatus();
    }

    private void OnButton(int button, bool down)
    {
        if (!_enabled.Checked && down) return; // releases always go through
        if (button == _settings.LeftButton) Mouse(ref _leftHeld, down, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, "left");
        else if (button == _settings.RightButton) Mouse(ref _rightHeld, down, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, "right");
    }

    private void Mouse(ref bool held, bool down, uint downFlag, uint upFlag, string which)
    {
        if (held == down) return; // only send transitions: never a second down or a stray up
        held = down;
        if (_dryRun)
        {
            Log.Button($"(dry run) mouse {which} {(down ? "down" : "up")}");
            return;
        }
        var input = new[] { new INPUT { Type = INPUT_MOUSE, Mouse = new MOUSEINPUT { Flags = down ? downFlag : upFlag } } };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            Log.Write($"SendInput (mouse {which} {(down ? "down" : "up")}) failed: error {Marshal.GetLastWin32Error()}");
    }

    private void ReleaseAll()
    {
        Mouse(ref _leftHeld, false, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, "left");
        Mouse(ref _rightHeld, false, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, "right");
    }

    private void UpdateStatus()
    {
        bool connected = _watcher != null && _watcher.MatchingCount > 0;
        var state = connected ? "AtGames table connected" : "AtGames table not found";
        _status.Text = state;
        var text = "AtGames mouse: " + (connected ? "connected" : "not found") +
                   (_enabled.Checked ? "" : " (disabled)") + (_dryRun ? " (dry run)" : "");
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _tray.Icon = connected && _enabled.Checked ? _iconOn : _iconOff;
    }

    /// <summary>A plain filled circle with a white ring; 64 px so it stays sharp at 300 % scaling.</summary>
    private static Icon MakeIcon(Color fill)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(Color.White, 6);
            g.FillEllipse(brush, 4, 4, 56, 56);
            g.DrawEllipse(pen, 7, 7, 50, 50);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 22, 22, 20, 20);
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void ExitThreadCore()
    {
        ReleaseAll();
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watcher.Dispose();
            _tray.Dispose();
            _iconOn.Dispose();
            _iconOff.Dispose();
        }
        base.Dispose(disposing);
    }
}
