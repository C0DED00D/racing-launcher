using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Launcher.Core.Native;

namespace Launcher.Core.Session;

/// <summary>
/// Groups a session's processes so they can be killed together, including any child processes
/// they start. If the launcher itself dies, Windows kills everything in the job.
/// </summary>
sealed class JobObject : IDisposable
{
    IntPtr _handle;

    public JobObject()
    {
        _handle = Win32.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!Win32.SetInformationJobObject(_handle, Win32.JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Add(Process p)
    {
        if (!Win32.AssignProcessToJobObject(_handle, p.Handle))
            Log.Warn($"Couldn't add process {p.Id} to job: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public void Terminate()
    {
        if (_handle != IntPtr.Zero) Win32.TerminateJobObject(_handle, 1);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Win32.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
