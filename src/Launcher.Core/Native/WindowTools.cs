using Launcher.Core.Display;

namespace Launcher.Core.Native;

public static class WindowTools
{
    /// <summary>The largest visible top-level window owned by a process, or zero if it has none yet.</summary>
    public static IntPtr FindMainWindow(int processId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId || !Win32.IsWindowVisible(hwnd)) return true;
            if (Win32.GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (!Win32.GetWindowRect(hwnd, out var r)) return true;
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { best = hwnd; bestArea = area; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Strips the window frame and fills the given rectangle exactly.</summary>
    public static void MakeBorderless(IntPtr hwnd, ScreenRect target, bool activate = true)
    {
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        style &= ~(Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_SYSMENU);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_STYLE, new IntPtr(style));

        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex &= ~(Win32.WS_EX_DLGMODALFRAME | Win32.WS_EX_CLIENTEDGE | Win32.WS_EX_STATICEDGE | Win32.WS_EX_WINDOWEDGE);
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

        Win32.SetWindowPos(hwnd, Win32.HWND_TOP, target.X, target.Y, target.Width, target.Height,
            Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW | (activate ? 0 : Win32.SWP_NOACTIVATE));
    }

    /// <summary>Removes the window's menu bar (the program keeps its keyboard shortcuts).</summary>
    public static void HideMenu(IntPtr hwnd) => Win32.SetMenu(hwnd, IntPtr.Zero);

    /// <summary>True if the window has a title bar or sizing border.</summary>
    public static bool HasFrame(IntPtr hwnd) =>
        (Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64() & (Win32.WS_CAPTION | Win32.WS_THICKFRAME)) != 0;

    public static bool IsAt(IntPtr hwnd, ScreenRect target) =>
        Win32.GetWindowRect(hwnd, out var r) &&
        r.Left == target.X && r.Top == target.Y && r.Width == target.Width && r.Height == target.Height;

    public static void RequestClose(IntPtr hwnd) => Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Posts a menu command (WM_COMMAND) to a window, as if its menu item had been clicked.</summary>
    public static void PostCommand(IntPtr hwnd, int id) => Win32.PostMessage(hwnd, 0x0111 /* WM_COMMAND */, id, IntPtr.Zero);

    /// <summary>
    /// Posts one key press (down, then up) straight to a window's message queue, so it works even when the
    /// window isn't in front. Used to send Xenia its fullscreen key (F11) after its window has been placed.
    /// </summary>
    public static void PostKey(IntPtr hwnd, int virtualKey)
    {
        const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        Win32.PostMessage(hwnd, WM_KEYDOWN, (IntPtr)virtualKey, (IntPtr)1);
        Thread.Sleep(60);
        Win32.PostMessage(hwnd, WM_KEYUP, (IntPtr)virtualKey, unchecked((IntPtr)(long)0xC0000001L));
    }

    /// <summary>
    /// Brings a window to the front even when this process isn't in the foreground.
    /// Windows normally blocks that unless this process sent the last input, so we attach to the
    /// foreground thread's input and tap a key. The key is F24, which nothing uses: tapping Alt
    /// (the usual trick) puts WPF windows into menu mode, which then swallows the next key presses.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        IntPtr fg = Win32.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        uint me = Win32.GetCurrentThreadId();

        const byte VK_F24 = 0x87;
        const uint KEYEVENTF_KEYUP = 0x2;
        Win32.keybd_event(VK_F24, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        bool attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(me, fgThread, false);
        }
    }

    /// <summary>The window in front, as "process (window class)", for the log.</summary>
    public static string DescribeForeground() => Describe(Win32.GetForegroundWindow());

    /// <summary>"process (window class)" for the log.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { name = $"pid {pid}"; }
        var cls = new System.Text.StringBuilder(256);
        Win32.GetClassName(hwnd, cls, cls.Capacity);
        return $"{name} ({cls})";
    }

    public static void AllowAnyForeground() => Win32.AllowSetForegroundWindow(-1 /* ASFW_ANY */);

    /// <summary>
    /// Windows 11 opens Task View on a Guide press, and Settings has no switch for it. If the shell
    /// (explorer.exe) has taken the foreground, give it back to <paramref name="restoreTo"/>; Task View
    /// closes when it loses focus. No keys are sent: Esc would reach the game and many games quit on Esc.
    /// Returns the shell window's class name if it acted, otherwise null.
    /// </summary>
    public static string? DismissShellOverlay(IntPtr restoreTo)
    {
        if (restoreTo == IntPtr.Zero) return null;
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == restoreTo) return null;
        Win32.GetWindowThreadProcessId(fg, out uint pid);
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return null; }
        if (!name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return null;

        var cls = new System.Text.StringBuilder(256);
        Win32.GetClassName(fg, cls, cls.Capacity);
        ForceForeground(restoreTo);
        return cls.ToString();
    }

    /// <summary>
    /// Makes an overlay window that never takes focus or clicks, so showing it over a game
    /// doesn't pause the game or steal its input.
    /// </summary>
    public static void MakeOverlay(IntPtr hwnd)
    {
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Moves a window (keeping its size) so it's centred horizontally in an area, near the top, above everything.</summary>
    public static void ShowTopmostAt(IntPtr hwnd, ScreenRect area, double fromTop)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return;
        int x = area.X + (area.Width - r.Width) / 2;
        int y = area.Y + (int)(area.Height * fromTop);
        const uint SWP_NOSIZE = 0x0001;
        Win32.SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0,
            SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }
}
