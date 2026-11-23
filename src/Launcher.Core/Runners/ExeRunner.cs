using System.Diagnostics;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Starts a plain Windows program from the game's "exe" field and fills the seat's monitor with it.
/// For native PC games and for testing the launcher without an emulator.
/// </summary>
public sealed class ExeRunner : IRunner
{
    public string Id => "exe";

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(game.Exe)) return ["No \"exe\" set in config/games.json."];
        var path = ResolveExe(game.Exe, config);
        return path == null ? [$"Program not found: {game.Exe}"] : [];
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        var exe = ResolveExe(game.Exe!, config)!;
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        };
        // No SDL game-controller mapping here (2026-11-07): Project R gives force feedback only to devices it sees as
        // a plain joystick ("wheel-and-pedals"); as a mapped gamepad the wheel got rumble only. Its joystick bindings
        // live in %LOCALAPPDATA%\ProjectR\project-r.toml [input.joysticks].
        psi.Environment["SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS"] = "1";
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        var seat = seats[0];
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: true, seat.Audio)]);
    }

    /// <summary>Repo-relative, absolute, or a bare name found on PATH / in System32.</summary>
    static string? ResolveExe(string exe, LauncherConfig config)
    {
        var local = config.Resolve(exe);
        if (File.Exists(local)) return local;
        if (Path.IsPathRooted(exe) || exe.Contains('/') || exe.Contains('\\')) return null;

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(Environment.SystemDirectory);
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }
}
