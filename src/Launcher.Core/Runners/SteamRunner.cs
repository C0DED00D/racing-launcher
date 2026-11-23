using System.Diagnostics;
using System.Text.RegularExpressions;
using Launcher.Core.Config;
using Microsoft.Win32;

namespace Launcher.Core.Runners;

/// <summary>
/// Starts an installed Steam game: steam.exe -applaunch &lt;steamAppId&gt; hands the request to the running Steam (or
/// starts Steam first), and the session then adopts the game's own process (games.json "exe" = its exe file name,
/// e.g. "forzahorizon6.exe") so Guide-twice / START-hold can close it and its exit returns to the menu.
/// The game keeps its own display settings (set fullscreen on the seat-1 monitor in the game); the launcher doesn't
/// move its window, only brings it to the front. Solo only.
/// </summary>
public sealed class SteamRunner : IRunner
{
    public string Id => "steam";

    // F1 25 goes through the EA app first, and a cold Steam may update before starting anything.
    static readonly TimeSpan AdoptWait = TimeSpan.FromMinutes(4);

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        if (game.SteamAppId is not int appId) return ["No \"steamAppId\" set in config/games.json."];
        if (string.IsNullOrWhiteSpace(game.Exe)) return ["No \"exe\" (the game's exe name) set in config/games.json."];
        var steam = SteamFolder();
        if (steam == null) return ["Steam isn't installed."];
        var dir = InstallDir(steam, appId);
        if (dir == null) return [$"{game.Title} isn't installed in Steam (app {appId})."];
        if (!File.Exists(Path.Combine(dir, game.Exe))) return [$"{game.Exe} not found in {dir}."];
        return [];
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode != PlayMode.Solo) throw new NotSupportedException($"{game.Title}: only Solo is set up.");
        var steamExe = Path.Combine(SteamFolder()!, "steam.exe");
        var psi = new ProcessStartInfo(steamExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(steamExe)! };
        psi.ArgumentList.Add("-applaunch");
        psi.ArgumentList.Add(game.SteamAppId!.Value.ToString());
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        var seat = seats[0];
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: false, seat.Audio,
            AdoptProcess: Path.GetFileNameWithoutExtension(game.Exe!), AdoptWait: AdoptWait)]);
    }

    static string? SteamFolder()
    {
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                   ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (path == null) return null;
        path = Path.GetFullPath(path.Replace('/', '\\'));
        return File.Exists(Path.Combine(path, "steam.exe")) ? path : null;
    }

    /// <summary>The game's folder from its appmanifest in any Steam library, if Steam lists it as fully installed.</summary>
    static string? InstallDir(string steam, int appId)
    {
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var text = File.ReadAllText(manifest);
            var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
            if (dir.Length == 0) continue;
            var full = Path.Combine(lib, "steamapps", "common", dir);
            if (Directory.Exists(full)) return full;
        }
        return null;
    }
}
