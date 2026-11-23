using System.Diagnostics;
using System.Xml.Linq;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Arcade PC-based games through TeknoParrot (OutRun 2 SP SDX on its ElfLdr2 Lindbergh loader). Solo only for now.
/// Starts TeknoParrotUi.exe --profile=&lt;tpProfile&gt;.xml: the UI must run alongside the game, since it reads the
/// wheel and feeds the game's emulated I/O board. The per-game settings (game path, XInput bindings, fullscreen
/// resolution, free play) are in emulators/teknoparrot/UserProfiles/&lt;tpProfile&gt;.xml, written by
/// scripts/tp-outrun-profile.ps1.
///
/// Linked: see BuildLinked (SEGA Rally 3, OutRun 2).
/// </summary>
public sealed class TeknoParrotRunner : IRunner
{
    public string Id => "teknoparrot";

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(Id);
        if (exe == null) { problems.Add("TeknoParrot isn't set up in config/emulators.json."); return problems; }
        if (!File.Exists(exe)) { problems.Add($"TeknoParrot not found at {exe}"); return problems; }
        var home = Path.GetDirectoryName(exe)!;

        if (string.IsNullOrWhiteSpace(game.TpProfile)) problems.Add("No tpProfile set in config/games.json.");
        else if (!File.Exists(Path.Combine(home, "UserProfiles", game.TpProfile + ".xml")))
            problems.Add($"TeknoParrot user profile {game.TpProfile}.xml is missing (run scripts/tp-outrun-profile.ps1).");

        if (string.IsNullOrWhiteSpace(game.Rom) || !Directory.Exists(config.Resolve(game.Rom)))
            problems.Add($"Game folder not found at {config.Resolve(game.Rom ?? "")}");

        if (!PoliciesAccepted(home))
            problems.Add("Open emulators/teknoparrot/TeknoParrotUi.exe once and accept its privacy notice.");
        return problems;
    }

    /// <summary>TeknoParrot shows its privacy notice and policies on first start; that choice is the user's.</summary>
    static bool PoliciesAccepted(string home)
    {
        var file = Path.Combine(home, "ParrotData.xml");
        if (!File.Exists(file)) return false;
        try
        {
            // Newer UIs record the acceptance as HasReadPoliciesNew.
            var root = XDocument.Load(file).Root;
            return new[] { "HasReadPolicies", "HasReadPoliciesNew" }.Any(n =>
                string.Equals(root?.Element(n)?.Value, "true", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Linked) return BuildLinked(game, seats, config);
        if (mode != PlayMode.Solo)
            throw new NotSupportedException($"{game.Title}: only Solo is set up in TeknoParrot so far.");

        var exe = config.EmulatorPath(Id)!;
        var seat = seats[0];
        TpWheelProfile.Apply(Path.Combine(Path.GetDirectoryName(exe)!, "UserProfiles", game.TpProfile + ".xml"), seat, seat.Wheel);
        var gameDir = config.Resolve(game.Rom!);
        foreach (var rel in FfbPluginInis) SetFfbDevice(Path.Combine(gameDir, rel), seat);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        psi.ArgumentList.Add($"--profile={game.TpProfile}.xml");
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);

        // The game opens its own fullscreen window on the primary monitor.
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: false, seat.Audio)]);
    }

    /// <summary>runtime/link/teknoparrot/seatN: the seat's own TeknoParrot copy (tp/) and game copy.</summary>
    static string SeatDir(LauncherConfig config, int seat) => Path.Combine(config.RuntimeDir, "link", "teknoparrot", $"seat{seat}");

    /// <summary>
    /// Linked (SEGA Rally 3, OutRun 2): one TeknoParrot copy per seat, each with its own game copy and profile
    /// (runtime/link/teknoparrot/seatN; setup in scripts/tp-sr3-link.ps1 and tp-outrun-link.ps1). Seat 2's copy
    /// uses its own names for TeknoParrot's pipes and input block, and tools/seat-names inside each game gives it
    /// its own network address. The game runs windowed at the seat's resolution and the launcher makes its window
    /// (games.json windowProcess, a child of the TeknoParrot UI) borderless on the seat's monitor; fullscreen would
    /// minimize whenever the other seat's window is in front.
    /// </summary>
    LaunchPlan BuildLinked(GameProfile game, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (seats.Count < 2) throw new NotSupportedException($"{game.Title}: 2 PLAYER LINK needs two seats.");
        if (string.IsNullOrWhiteSpace(game.WindowProcess))
            throw new NotSupportedException($"{game.Title}: 2 PLAYER LINK isn't set up in TeknoParrot yet.");

        var instances = new List<InstancePlan>();
        foreach (var seat in seats)
        {
            var tp = Path.Combine(SeatDir(config, seat.SeatId), "tp");
            var exe = Path.Combine(tp, "TeknoParrotUi.exe");
            if (!File.Exists(exe) || !File.Exists(Path.Combine(tp, "UserProfiles", game.TpProfile + ".xml")))
                throw new NotSupportedException($"{game.Title}: seat {seat.SeatId}'s TeknoParrot copy isn't set up ({tp}).");
            TpWheelProfile.Apply(Path.Combine(tp, "UserProfiles", game.TpProfile + ".xml"), seat, seat.Wheel);
            if (game.WindowProcess == "BudgieLoader") InstallOutRunFfb(config, game, seat, tp);
            var psi = new ProcessStartInfo(exe) { WorkingDirectory = tp, UseShellExecute = false };
            psi.ArgumentList.Add($"--profile={game.TpProfile}.xml");
            // OutRun 2's loader keeps the backup RAM (settings, Link ID) under %APPDATA%\TeknoParrot: one per seat.
            var appData = Path.Combine(SeatDir(config, seat.SeatId), "appdata");
            if (Directory.Exists(appData)) psi.Environment["APPDATA"] = appData;
            // The UI refuses a second copy unless it's started in TeknoParrot Online mode.
            if (instances.Count > 0) psi.ArgumentList.Add("--tponline");
            // The first UI must be up (it takes a single-instance mutex) before the --tponline one starts.
            instances.Add(new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: true, seat.Audio,
                WindowProcess: game.WindowProcess, StartDelay: instances.Count > 0 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero));
        }
        return new LaunchPlan(game, PlayMode.Linked, instances);
    }

    /// <summary>
    /// Where the FFB Arcade Plugin reads its FFBPlugin.ini, relative to the game folder: OutRun 2 from the game's
    /// working directory (or2g\disk0\Jennifer; the plugin itself is opengl32.dll beside BudgieLoader in ElfLdr2),
    /// SEGA Rally 3 beside Rally.exe (the plugin is dinput8.dll there).
    /// </summary>
    static readonly string[] FfbPluginInis = [@"or2g\disk0\Jennifer\FFBPlugin.ini", @"Rally\FFBPlugin.ini"];

    /// <summary>
    /// Points the plugin at the seat's wheel: it opens only the device whose SDL 2.0.9 GUID is DeviceGUID (2.0.9 has
    /// no name CRC in its GUIDs). The G923 and the hub's vJoy wheel differ; two vJoy devices don't (SDL 2.0.9 then
    /// takes vJoy device 2 first).
    /// </summary>
    static void SetFfbDevice(string ini, SeatAssignment seat)
    {
        if (!File.Exists(ini)) return;
        var guid = seat.Wheel.Sdl2GuidNoCrc;
        var lines = File.ReadAllLines(ini).ToList();
        int i = lines.FindIndex(l => l.StartsWith("DeviceGUID=", StringComparison.OrdinalIgnoreCase));
        var line = $"DeviceGUID={guid}";
        if (i >= 0 && lines[i] == line) return;
        if (i >= 0) lines[i] = line; else lines.Add(line);
        File.WriteAllLines(ini, lines);
        Log.Info($"  Seat {seat.SeatId}: FFB plugin device {guid} ({ini})");
    }

    /// <summary>
    /// OutRun 2 Linked: the FFB Arcade Plugin in the seat's ElfLdr2 (opengl32.dll + its SDL2.dll 2.0.9, loaded through
    /// gl-tap like Solo's) and its ini in the seat's game copy, pointed at the seat's wheel.
    /// </summary>
    static void InstallOutRunFfb(LauncherConfig config, GameProfile game, SeatAssignment seat, string tp)
    {
        var solo = Path.Combine(Path.GetDirectoryName(config.EmulatorPath("teknoparrot")!)!, "ElfLdr2");
        var elf = Path.Combine(tp, "ElfLdr2");
        foreach (var dll in new[] { "opengl32.dll", "SDL2.dll" })
            if (File.Exists(Path.Combine(solo, dll))) SupermodelRunner.CopyIfNewer(Path.Combine(solo, dll), Path.Combine(elf, dll));
        var jennifer = Path.Combine(SeatDir(config, seat.SeatId), "game", "or2g", "disk0", "Jennifer");
        var ini = Path.Combine(jennifer, "FFBPlugin.ini");
        var template = Path.Combine(config.Resolve(game.Rom!), "or2g", "disk0", "Jennifer", "FFBPlugin.ini");
        if (!File.Exists(ini) && Directory.Exists(jennifer) && File.Exists(template)) File.Copy(template, ini);
        SetFfbDevice(ini, seat);
    }
}
