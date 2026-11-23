using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Launcher.Core.Native;

/// <summary>
/// Shut down or hibernate the PC from the launcher. Needs no elevation: standard users hold SeShutdownPrivilege on
/// desktop Windows (whoami /priv lists it as "Disabled", meaning held but switched off); a program only has to switch
/// it on in its own token first, as Start's power button does.
/// Hibernate also needs hibernation enabled in Windows (powercfg /a lists "Hibernate"; it is on this PC).
/// </summary>
public static class PowerActions
{
    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES: LUID is two DWORDs, so 4-byte packing (Luid at offset 4).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvents);
    [DllImport("powrprof.dll")] static extern bool IsPwrHibernateAllowed();

    /// <summary>Switches SeShutdownPrivilege on for this process; throws if the account doesn't hold it.</summary>
    public static void EnableShutdownPrivilege()
    {
        const int TokenAdjust = 0x20, TokenQuery = 0x8, Enabled = 2, NotAllAssigned = 1300;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjust | TokenQuery, out var token)) throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) throw new Win32Exception();
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = Enabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            if (Marshal.GetLastWin32Error() == NotAllAssigned)
                throw new Win32Exception(NotAllAssigned, "this Windows account may not shut down the PC");
        }
        finally { CloseHandle(token); }
    }

    public static bool HibernateAvailable
    {
        get { try { return IsPwrHibernateAllowed(); } catch { return false; } }
    }

    /// <summary>
    /// A full shutdown with power off (not Fast Startup's hybrid one, so drivers and USB devices such as the wheel
    /// receiver start fresh). Programs are asked to close as usual. Returns once Windows has accepted it.
    /// </summary>
    public static void ShutDown()
    {
        const uint Shutdown = 0x1, PowerOff = 0x8;
        const uint ReasonOtherPlanned = 0x80000000; // SHTDN_REASON_FLAG_PLANNED | MAJOR_OTHER | MINOR_OTHER
        EnableShutdownPrivilege();
        if (!ExitWindowsEx(Shutdown | PowerOff, ReasonOtherPlanned)) throw new Win32Exception();
    }

    /// <summary>Hibernates; blocks until the PC has resumed (call it off the UI thread).</summary>
    public static void Hibernate()
    {
        EnableShutdownPrivilege();
        if (!SetSuspendState(true, false, false)) throw new Win32Exception();
    }
}
