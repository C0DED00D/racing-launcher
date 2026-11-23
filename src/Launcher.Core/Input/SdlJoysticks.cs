using System.Runtime.InteropServices;

namespace Launcher.Core.Input;

/// <summary>One joystick as SDL2 sees it: its instance id (what Flycast's maple_sdl_joystick_N means), name and path.</summary>
public sealed record SdlJoystick(int InstanceId, string Name, string Path);

/// <summary>
/// Lists joysticks through SDL2 (tools/sdl2/SDL2.dll, 2.32 x64, the copy from TeknoParrot's ElfLdr2 release), with
/// the same hints a game's SDL gets, so the instance ids match what that program will assign at startup. Checked
/// 2026-11-12: with SDL_JOYSTICK_RAWINPUT=0 they follow DirectInput's order (vJoy COL01 = 0, COL02 = 1, Saturn
/// adapter port 1 = 2, port 2 = 3), while the device indexes run the other way.
/// </summary>
public static unsafe class SdlJoysticks
{
    const uint InitJoystick = 0x200;
    static readonly object Gate = new();
    static IntPtr _lib;

    public static List<SdlJoystick> List(string sdlDll, IReadOnlyDictionary<string, string> hints)
    {
        lock (Gate)
        {
            if (_lib == IntPtr.Zero) _lib = NativeLibrary.Load(sdlDll);
            IntPtr F(string name) => NativeLibrary.GetExport(_lib, name);
            var setHint = (delegate* unmanaged[Cdecl]<byte*, byte*, int>)F("SDL_SetHint");
            var initSub = (delegate* unmanaged[Cdecl]<uint, int>)F("SDL_InitSubSystem");
            var quitSub = (delegate* unmanaged[Cdecl]<uint, void>)F("SDL_QuitSubSystem");
            var count = (delegate* unmanaged[Cdecl]<int>)F("SDL_NumJoysticks");
            var instance = (delegate* unmanaged[Cdecl]<int, int>)F("SDL_JoystickGetDeviceInstanceID");
            var name = (delegate* unmanaged[Cdecl]<int, IntPtr>)F("SDL_JoystickNameForIndex");
            var path = (delegate* unmanaged[Cdecl]<int, IntPtr>)F("SDL_JoystickPathForIndex");

            foreach (var (k, v) in hints)
            {
                var kb = System.Text.Encoding.UTF8.GetBytes(k + "\0");
                var vb = System.Text.Encoding.UTF8.GetBytes(v + "\0");
                fixed (byte* kp = kb, vp = vb) setHint(kp, vp);
            }
            if (initSub(InitJoystick) != 0) return [];
            try
            {
                var result = new List<SdlJoystick>();
                for (int i = 0, n = count(); i < n; i++)
                    result.Add(new SdlJoystick(instance(i), Marshal.PtrToStringUTF8(name(i)) ?? "", Marshal.PtrToStringUTF8(path(i)) ?? ""));
                // SDL2's instance counter never resets inside a process, so a second listing starts above 0; a
                // freshly started program numbers from 0.
                int first = result.Count > 0 ? result.Min(j => j.InstanceId) : 0;
                return result.Select(j => j with { InstanceId = j.InstanceId - first }).OrderBy(j => j.InstanceId).ToList();
            }
            finally { quitSub(InitJoystick); }
        }
    }
}
