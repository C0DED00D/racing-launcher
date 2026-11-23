using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher.Core.Native;

/// <summary>Finds the processes a program started (children, grandchildren, ...).</summary>
public static class ProcessTree
{
    const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>Process ids started (directly or not) by <paramref name="root"/> and still running.</summary>
    public static List<int> Descendants(Process root)
    {
        var parentOf = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return [];
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                parentOf[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }

        DateTime rootStart;
        try { rootStart = root.StartTime; } catch { return []; }

        var found = new List<int>();
        var frontier = new Queue<int>([root.Id]);
        while (frontier.Count > 0)
        {
            int parent = frontier.Dequeue();
            foreach (var (pid, ppid) in parentOf)
            {
                if (ppid != parent || pid == parent || found.Contains(pid)) continue;
                // Parent ids get reused: only count processes that started after the root.
                try { if (Process.GetProcessById(pid).StartTime < rootStart) continue; }
                catch { continue; }
                found.Add(pid);
                frontier.Enqueue(pid);
            }
        }
        return found;
    }
}
