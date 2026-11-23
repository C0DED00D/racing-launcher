using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Launcher.Core.Audio;

/// <summary>
/// Sends one process's sound to a chosen output, like Settings > System > Sound > Volume mixer > "Output device"
/// per app. Uses Windows' internal Windows.Media.Internal.AudioPolicyConfig factory (the one the mixer and
/// EarTrumpet use). Windows stores the choice per exe path, so two seats need two different exe paths.
/// </summary>
public static class AudioRouting
{
    const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string FriendlyName = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    const string RenderInterface = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    // Windows 10 21H2 and later, then the older interface id.
    static readonly Guid[] FactoryIids = [new("ab3d4648-e242-459f-b02f-541c70306324"), new("2a59116d-6c4f-45e0-a74f-707e3fef9258")];

    // IUnknown (3) + IInspectable (3) + 19 methods we don't use, then SetPersistedDefaultAudioEndpoint.
    const int SetPersistedSlot = 25;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int SetPersistedDefaultAudioEndpointFn(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>Active audio outputs: MMDevice id (e.g. "{0.0.0.00000000}.{guid}") and name.</summary>
    public static IReadOnlyList<(string Id, string Name)> Outputs()
    {
        var list = new List<(string, string)>();
        using var root = Registry.LocalMachine.OpenSubKey(RenderKey);
        if (root == null) return list;
        foreach (var guid in root.GetSubKeyNames())
        {
            using var dev = root.OpenSubKey(guid);
            if (dev?.GetValue("DeviceState") is not int state || state != 1) continue; // 1 = active
            using var props = dev.OpenSubKey("Properties");
            if (props?.GetValue(FriendlyName) is string name) list.Add(("{0.0.0.00000000}." + guid, name));
        }
        return list;
    }

    /// <summary>
    /// Routes <paramref name="processId"/> to the first active output whose name contains <paramref name="nameMatch"/>.
    /// Returns a line for the log; never throws.
    /// </summary>
    public static string Route(int processId, string nameMatch)
    {
        try
        {
            var match = Outputs().FirstOrDefault(o => o.Name.Contains(nameMatch, StringComparison.OrdinalIgnoreCase));
            if (match.Id == null) return $"no audio output named like \"{nameMatch}\" (have: {string.Join(", ", Outputs().Select(o => o.Name))})";

            var factory = Factory();
            if (factory == IntPtr.Zero) return "per-app audio routing isn't available on this Windows version";
            try
            {
                var vtable = Marshal.ReadIntPtr(factory);
                var set = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointFn>(
                    Marshal.ReadIntPtr(vtable, SetPersistedSlot * IntPtr.Size));
                var id = @"\\?\SWD#MMDEVAPI#" + match.Id + RenderInterface;
                WindowsCreateString(id, id.Length, out var hs);
                try
                {
                    // eRender; roles eConsole and eMultimedia (what apps open as "the default device").
                    int hr0 = set(factory, (uint)processId, 0, 0, hs);
                    int hr1 = set(factory, (uint)processId, 0, 1, hs);
                    return hr0 == 0 && hr1 == 0 ? $"audio -> {match.Name}" : $"audio routing failed (0x{hr0:X8}/0x{hr1:X8})";
                }
                finally { WindowsDeleteString(hs); }
            }
            finally { Marshal.Release(factory); }
        }
        catch (Exception ex) { return $"audio routing failed: {ex.Message}"; }
    }

    static IntPtr Factory()
    {
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(cls, cls.Length, out var hs);
        try
        {
            foreach (var iid in FactoryIids)
            {
                var g = iid;
                if (RoGetActivationFactory(hs, ref g, out var f) == 0 && f != IntPtr.Zero) return f;
            }
            return IntPtr.Zero;
        }
        finally { WindowsDeleteString(hs); }
    }
}
