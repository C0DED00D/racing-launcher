using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher.Core.Native;

/// <summary>
/// Makes Windows treat a game process the same whether or not its window is in front. With two linked copies
/// only one window can be in front, and Windows 11 favours it: the background copy (seat 2) ran at a much lower
/// frame rate. Per process only; no system setting is changed.
///   - power throttling (EcoQoS) off, so the background copy keeps full CPU speed,
///   - above-normal CPU priority.
/// </summary>
public static class ProcessBoost
{
    const int ProcessPowerThrottling = 4;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    // GPU priority can't be set from here: D3DKMTSetProcessSchedulingPriorityClass only works on the calling
    // process (any other process gives STATUS_INVALID_PARAMETER, tested 2026-11-04), and a child doesn't
    // inherit it. Our Xenia build sets it on itself (--gpu_scheduling_priority, see XeniaRunner).

    /// <summary>Applies the CPU-side settings; returns a short report for the log.</summary>
    public static string Apply(Process p)
    {
        var parts = new List<string>();
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0, // controlled and off = never throttled
            };
            parts.Add(SetProcessInformation(p.Handle, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())
                ? "power throttling off" : $"power throttling: error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { parts.Add($"power throttling: {ex.Message}"); }

        try { p.PriorityClass = ProcessPriorityClass.AboveNormal; parts.Add("CPU above normal"); }
        catch (Exception ex) { parts.Add($"CPU priority: {ex.Message}"); }

        return string.Join(", ", parts);
    }
}
