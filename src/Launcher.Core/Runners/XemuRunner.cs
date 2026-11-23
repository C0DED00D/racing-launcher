using System.Diagnostics;
using Launcher.Core.Config;

namespace Launcher.Core.Runners;

/// <summary>
/// Original Xbox games in xemu (Crazy Taxi 3). Solo only for now.
/// xemu runs in portable mode (xemu.toml next to the exe): fullscreen, no menu bar, 3x surface scale, background
/// input on, boot animation skipped. It needs three files dumped from a real Xbox, in emulators/xemu/bios/:
/// mcpx_1.0.bin (boot ROM), bios.bin (flash BIOS) and xbox_hdd.qcow2 (hard disk). Paths are in xemu.toml.
/// Games are XISO images (the redump format has to be converted with extract-xiso first).
///
/// Linked (later): xemu's UDP network backend links two copies on one PC for System Link
/// (net.backend = udp, bind_addr / remote_addr), each with its own config (-config_path) and EEPROM (own MAC).
/// </summary>
public sealed class XemuRunner : IRunner
{
    public string Id => "xemu";

    static readonly (string File, string What)[] SystemFiles =
    [
        ("mcpx_1.0.bin", "Xbox boot ROM (MCPX 1.0)"),
        ("bios.bin", "Xbox flash BIOS"),
        ("xbox_hdd.qcow2", "Xbox hard disk image"),
    ];

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(Id);
        if (exe == null) { problems.Add("xemu isn't set up in config/emulators.json."); return problems; }
        if (!File.Exists(exe)) { problems.Add($"xemu not found at {exe}"); return problems; }

        var bios = Path.Combine(Path.GetDirectoryName(exe)!, "bios");
        var missing = SystemFiles.Where(f => !File.Exists(Path.Combine(bios, f.File))).ToList();
        if (missing.Count > 0)
            problems.Add("Needs files from your Xbox in emulators/xemu/bios: " +
                         string.Join(", ", missing.Select(m => $"{m.File} ({m.What})")) + ".");

        if (string.IsNullOrWhiteSpace(game.Rom)) problems.Add("No game file set in config/games.json.");
        else if (!File.Exists(config.Resolve(game.Rom))) problems.Add($"Game file not found at {config.Resolve(game.Rom)}");
        return problems;
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode != PlayMode.Solo)
            throw new NotSupportedException($"{game.Title}: only Solo is set up in xemu so far.");

        var exe = config.EmulatorPath(Id)!;
        var seat = seats[0];

        // Leftover cache from an earlier boot makes Crazy Taxi 3 hang loading Glitter Oasis (see XboxHddCache).
        var hdd = Path.Combine(Path.GetDirectoryName(exe)!, "bios", "xbox_hdd.qcow2");
        try { Log.Info($"xemu: {XboxHddCache.Clear(hdd)}"); }
        catch (IOException ex) { Log.Info($"xemu: HDD cache not cleared ({ex.Message})"); }

        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        // Controller port 1 = the DirectInput wheel, as an SDL game controller (xemu only takes those).
        BindPort1(Path.Combine(Path.GetDirectoryName(exe)!, "xemu.toml"), seat.Wheel.Sdl2Guid);
        Wheels.WheelSdl.Apply(psi, seat.Wheel);
        psi.ArgumentList.Add("-dvd_path");
        psi.ArgumentList.Add(config.Resolve(game.Rom!));
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);

        // xemu goes fullscreen by itself (fullscreen_on_startup) on the monitor its window opens on.
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: false, seat.Audio)]);
    }

    /// <summary>Sets [input.bindings] port1 (an SDL2 controller GUID) in xemu.toml; other lines stay as they are.</summary>
    static void BindPort1(string toml, string guid)
    {
        if (!File.Exists(toml)) return;
        var lines = File.ReadAllLines(toml).ToList();
        int i = lines.FindIndex(l => l.TrimStart().StartsWith("port1 =", StringComparison.Ordinal));
        var line = $"port1 = '{guid}'";
        if (i < 0 || lines[i] == line) return;
        lines[i] = line;
        File.WriteAllLines(toml, lines);
        Log.Info($"  xemu.toml: port1 = {guid} (the wheel)");
    }
}
