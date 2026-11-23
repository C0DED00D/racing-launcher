using System.Diagnostics;
using Launcher.Core.Config;
using Launcher.Core.Display;

namespace Launcher.Core.Runners;

/// <summary>A seat resolved for one launch: which controller slot and which part of which monitor.</summary>
/// <summary>JoystickMatch: part of the seat's wheel's device path or name (seats.json "joystick"), or null.</summary>
/// <summary>Layout: the seat's wheel's buttons and axes (seats.json seat "wheel", else the shared "joystick" block).</summary>
public sealed record SeatAssignment(int SeatId, int XInputSlot, MonitorInfo Monitor, ScreenRect Area, string? Audio = null,
    string? JoystickMatch = null, JoystickLayout? Layout = null)
{
    public JoystickLayout Wheel => Layout ?? new JoystickLayout();
}

/// <summary>
/// One process to start, where its window goes, and which audio output it plays on (null: default).
/// FullscreenKey: a virtual-key code posted to the window once it has been placed (e.g. F11 for Xenia, whose
/// fullscreen covers whichever monitor the window is on); 0 for none.
/// WindowProcess: when the game window belongs to a process the started program launches (TeknoParrot starts
/// Rally.exe), that process's name; the window placed is then that process's, not the started program's.
/// StartDelay: wait this long after the previous instance before starting this one (TeknoParrot: the first UI
/// must be up before a second one starts, or the first asks whether to close the second).
/// Companion: runner code run in the background for the life of the instance, from the moment it starts
/// (Virtual-On's Supermodel link relay). It ends when the session's token is cancelled or the process exits;
/// exceptions are logged.
/// AdoptProcess: the started program only asks another one to start the game (Steam: steam.exe -applaunch hands the
/// request to the running Steam and exits). The session then neither tracks nor kills the started program; it waits
/// up to AdoptWait for a process of this name (no ".exe") and treats that as the instance.
/// </summary>
public sealed record InstancePlan(string Label, ProcessStartInfo StartInfo, ScreenRect Area, bool PlaceWindow, string? Audio = null,
    int FullscreenKey = 0, string? WindowProcess = null, TimeSpan StartDelay = default,
    Func<Process, CancellationToken, Task>? Companion = null, string? AdoptProcess = null, TimeSpan AdoptWait = default);

public sealed record LaunchPlan(GameProfile Game, PlayMode Mode, IReadOnlyList<InstancePlan> Instances);

/// <summary>
/// Knows how to launch games on one emulator or port: what files it needs, what to pass it,
/// and where its windows go.
/// </summary>
public interface IRunner
{
    string Id { get; }

    /// <summary>Problems that would stop this game launching, in plain words. Empty means ready.</summary>
    IReadOnlyList<string> Check(GameProfile game, LauncherConfig config);

    LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config);
}
