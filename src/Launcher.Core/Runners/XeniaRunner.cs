using System.Diagnostics;
using System.Net.Sockets;
using Launcher.Core.Config;
using Launcher.Core.Native;

namespace Launcher.Core.Runners;

/// <summary>
/// Xbox 360 games in Xenia Canary (netplay build).
///
/// Solo: our build (emulators/xenia-link, for wheel force feedback) on the released build's portable storage
/// (emulators/xenia), on seat 1's monitor; the released exe only if ours is missing.
/// Settings are passed as --name=value, which override xenia-canary-netplay.config.toml:
///   - fullscreen, 3x internal resolution (Xbox 360 games draw at 1280x720, so 3x is 4K),
///   - full license for Xbox Live Arcade games (otherwise they run as trials),
///   - no Discord, Guide not passed to the game (it is the launcher's exit button),
///   - profiles signed in at boot (games.json "profiles": one XUID per seat), so no sign-in dialog.
///
/// Linked: one copy per seat of our patched build (emulators/xenia-link, built from build/xenia-src), talking
/// "Xbox Live" through a local Xenia Web Services server (Node + MongoDB from tools/, started here if needed).
/// Our two patches make that work on one PC:
///   --netplay_local_ip=127.0.0.N   each copy binds and reports its own loopback address,
///   --xinput_slot_offset=S         each copy's player 1 reads only its seat's wheel.
/// Each seat gets runtime/link/xenia/seatN as its storage root (config, cache, its own console MAC) and a copy of
/// the exe (Windows keeps the per-app audio output per exe path); both share emulators/xenia/content (game, DLC,
/// profiles). Linked profiles must be Live-enabled (games.json "linkProfiles").
/// </summary>
public sealed class XeniaRunner : IRunner
{
    public string Id => "xenia";

    const string LinkEmulator = "xenia-link";
    const int ServerPort = 36000;
    const int MongoPort = 27017;

    public IReadOnlyList<string> Check(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(Id);
        if (exe == null) problems.Add("Xenia isn't set up in config/emulators.json.");
        else if (!File.Exists(exe)) problems.Add($"Xenia not found at {exe}");

        if (string.IsNullOrWhiteSpace(game.Rom)) problems.Add("No game file set in config/games.json.");
        else if (!File.Exists(config.Resolve(game.Rom))) problems.Add($"Game file not found at {config.Resolve(game.Rom)}");
        return problems;
    }

    /// <summary>Problems that only stop Linked mode (Solo still works without these).</summary>
    public static IReadOnlyList<string> CheckLinked(GameProfile game, LauncherConfig config)
    {
        var problems = new List<string>();
        var exe = config.EmulatorPath(LinkEmulator);
        if (exe == null || !File.Exists(exe)) problems.Add("The patched Xenia build (emulators/xenia-link) isn't there yet.");
        if (!IsSystemLink(game) && !File.Exists(Path.Combine(config.Root, "build", "xenia-webservices", "dist", "main.js")))
            problems.Add("The local Xenia Web Services server isn't built yet (build/xenia-webservices).");
        if (game.LinkProfiles.Count < 2) problems.Add("games.json needs two Live-enabled \"linkProfiles\".");
        return problems;
    }

    public LaunchPlan BuildPlan(GameProfile game, PlayMode mode, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        if (mode == PlayMode.Split)
            throw new NotSupportedException($"{game.Title}: split screen isn't offered; use Linked.");
        SyncShaderCaches(config);
        if (mode == PlayMode.Linked) return BuildLinked(game, seats, config);

        var stock = config.EmulatorPath(Id)!;
        var seat = seats[0];
        // Our build (emulators/xenia-link) plays the wheel force feedback; it runs on the stock install's storage
        // (profiles, saves, config, content), so Solo is the same Xenia setup as before. Stock exe if it's missing.
        var link = config.EmulatorPath(LinkEmulator);
        bool ours = link != null && File.Exists(link);
        var exe = ours ? link! : stock;
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        foreach (var a in CommonArgs()) psi.ArgumentList.Add(a);
        ApplySdlMapping(psi, game, config, seat);
        if (ours)
        {
            var home = Path.GetDirectoryName(stock)!;
            psi.ArgumentList.Add($"--storage_root={home}");
            psi.ArgumentList.Add($"--content_root={Path.Combine(home, "content")}");
            foreach (var a in ControllerArgs(game, seat)) psi.ArgumentList.Add(a);
        }
        psi.ArgumentList.Add("--fullscreen=true");
        psi.ArgumentList.Add("--network_mode=0");
        if (game.Profiles.Count > 0) psi.ArgumentList.Add($"--logged_profile_slot_0_xuid={game.Profiles[0]}");
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(config.Resolve(game.Rom!));

        // Xenia goes fullscreen on the monitor its window opens on (the primary one); moving it would fight that.
        return new LaunchPlan(game, mode, [new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: false, seat.Audio)]);
    }

    /// <param name="scale">
    /// Internal resolution scale (Xbox 360 games draw at 1280x720). Solo uses 3 (4K). Linked uses 2 (1440p) and
    /// FSR upscaling to 4K: two copies at 3x saturated the RTX 5070 Ti and the copy in the background (seat 2) got
    /// only ~17% of the GPU, so it ran badly.
    /// </param>
    static string[] CommonArgs(int scale = 3) =>
    [
        $"--draw_resolution_scale_x={scale}",
        $"--draw_resolution_scale_y={scale}",
        "--license_mask=1",
        "--discord=false",
        "--guide_button=false",
        "--keyboard_mode=0",
        // The wheels are DirectInput wheels (vJoy now): SDL reads them, as game controllers through the mapping
        // WheelSdl passes in SDL_GAMECONTROLLERCONFIG. (XInput until 2026-11-07.)
        "--hid=sdl",
    ];

    /// <summary>
    /// Our build only: this copy takes just the seat's wheel (sdl_joystick_match, our cvar: both vJoy devices have
    /// the same name and GUID, only the device path differs) and plays the Xbox 360 wheel force-feedback API on it
    /// (ffb_wheel; ffb_device picks it by the same path). Strength 150 %: the user's pick (on the SideWinder, then
    /// again on the 360 wheel, 2026-11-07); Hurricane sets the wheel gain to half and 100 % felt light.
    /// </summary>
    static IEnumerable<string> ControllerArgs(GameProfile game, SeatAssignment seat)
    {
        if (!game.UsesTwinStick) return WheelArgs(seat);
        // Twin stick: just pick the device; no wheel force feedback (ffb_wheel also reports a wheel to the game).
        return Input.DirectInputDevices.UsableMatch(seat.JoystickMatch, $"Seat {seat.SeatId}") is { } m
            ? [$"--sdl_joystick_match={m}", "--ffb_wheel=false"] : ["--ffb_wheel=false"];
    }

    static void ApplySdlMapping(ProcessStartInfo psi, GameProfile game, LauncherConfig config, SeatAssignment seat)
    {
        if (game.UsesTwinStick) Wheels.WheelSdl.ApplyTwinStick(psi, config.Seats.TwinStickLayout);
        else Wheels.WheelSdl.Apply(psi, seat.Wheel);
    }

    static IEnumerable<string> WheelArgs(SeatAssignment seat)
    {
        if (Input.DirectInputDevices.UsableMatch(seat.JoystickMatch, $"Seat {seat.SeatId}") is { } m)
        {
            yield return $"--sdl_joystick_match={m}";
            yield return $"--ffb_device={m}";
        }
        yield return "--ffb_wheel=true";
        yield return "--ffb_strength=150";
    }

    LaunchPlan BuildLinked(GameProfile game, IReadOnlyList<SeatAssignment> seats, LauncherConfig config)
    {
        var problems = CheckLinked(game, config);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(" ", problems));

        bool systemLink = IsSystemLink(game);
        if (!systemLink)
        {
            EnsureServer(config);
            ClearSeatSessions(seats.Count);
        }

        var linkExe = config.EmulatorPath(LinkEmulator)!;
        var content = Path.Combine(Path.GetDirectoryName(config.EmulatorPath(Id)!)!, "content");
        var instances = new List<InstancePlan>();
        for (int i = 0; i < seats.Count; i++)
        {
            var seat = seats[i];
            var dir = Path.Combine(config.Root, "runtime", "link", "xenia", $"seat{seat.SeatId}");
            Directory.CreateDirectory(dir);
            var exe = SupermodelRunner.CopyIfNewer(linkExe, Path.Combine(dir, Path.GetFileName(linkExe)));

            var psi = new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false };
            foreach (var a in CommonArgs(scale: 2)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--postprocess_scaling_and_sharpening=fsr");
            // Our build: high GPU scheduling priority, so the copy whose window isn't in front isn't starved.
            psi.ArgumentList.Add("--gpu_scheduling_priority=4");
            psi.ArgumentList.Add($"--storage_root={dir}");
            psi.ArgumentList.Add($"--content_root={content}");
            psi.ArgumentList.Add(systemLink ? "--network_mode=1" : "--network_mode=2");
            if (!systemLink)
            {
                psi.ArgumentList.Add($"--api_address=127.0.0.1:{ServerPort}/");
                psi.ArgumentList.Add($"--api_list=http://127.0.0.1:{ServerPort}/,");
            }
            psi.ArgumentList.Add("--upnp=false");
            psi.ArgumentList.Add($"--netplay_local_ip=127.0.0.{2 + i}");
            foreach (var a in ControllerArgs(game, seat)) psi.ArgumentList.Add(a);
            ApplySdlMapping(psi, game, config, seat);
            psi.ArgumentList.Add($"--logged_profile_slot_0_xuid={game.LinkProfiles[i]}");
            // Opens windowed (Xenia can't pick a monitor); the launcher moves it onto the seat's monitor and then
            // sends F11, which makes Xenia fullscreen on that monitor (and hides its menu bar).
            psi.ArgumentList.Add("--fullscreen=false");
            foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(config.Resolve(game.Rom!));
            instances.Add(new InstancePlan($"Seat {seat.SeatId}", psi, seat.Area, PlaceWindow: true, seat.Audio,
                FullscreenKey: 0x7A /* VK_F11 */));
        }
        return new LaunchPlan(game, PlayMode.Linked, instances);
    }

    /// <summary>
    /// games.json "xeniaLinkMode": "systemlink" uses Xenia's System Link (network_mode 1, no server); anything else
    /// is the Live-style path through the local Xenia Web Services that Hurricane uses (network_mode 2).
    /// </summary>
    static bool IsSystemLink(GameProfile game) =>
        string.Equals(game.XeniaLinkMode, "systemlink", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pools the shader caches of the 1-player install and both seats (see XeniaShaderCache).</summary>
    static void SyncShaderCaches(LauncherConfig config)
    {
        if (Process.GetProcessesByName("xenia_canary_netplay").Length > 0)
        {
            Log.Info("  shader caches: not merged (a Xenia copy is still running)");
            return;
        }
        var roots = new List<string> { Path.GetDirectoryName(config.EmulatorPath("xenia")!)! };
        var link = Path.Combine(config.Root, "runtime", "link", "xenia");
        if (Directory.Exists(link)) roots.AddRange(Directory.GetDirectories(link, "seat*"));
        try { Log.Info("  " + XeniaShaderCache.Sync(roots)); }
        catch (Exception ex) { Log.Error("  shader caches", ex); }
    }

    /// <summary>
    /// Stops the MongoDB and Node server started from tools/: only the copies from tools/ that listen on the
    /// server's ports, so other programs on the same node.exe (such as diffx) keep running.
    /// Called when the launcher quits. A hard stop is fine: MongoDB journals, and it only holds the session list.
    /// </summary>
    public static void StopServer(LauncherConfig config)
    {
        var tools = Path.GetFullPath(Path.Combine(config.Root, "tools")) + Path.DirectorySeparatorChar;
        foreach (var (name, port) in new[] { ("node", ServerPort), ("mongod", MongoPort) })
        {
            var owners = TcpListeners.Owners(port);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (!owners.Contains(p.Id)) continue;
                    if (p.MainModule?.FileName is not { } file || !file.StartsWith(tools, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(3000);
                    Log.Info($"Stopped {name} (pid {p.Id}), the local Xenia server");
                }
                catch (Exception ex) { Log.Warn($"Stopping {name} (pid {p.Id}): {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>
    /// Starts MongoDB and the Xenia Web Services server from tools/ unless they're already listening.
    /// They keep running between sessions, so the next launch is quicker, and stop when the launcher quits.
    /// </summary>
    static void EnsureServer(LauncherConfig config)
    {
        var tools = Path.Combine(config.Root, "tools");
        var data = Path.Combine(config.Root, "runtime", "link", "xenia", "server");
        Directory.CreateDirectory(Path.Combine(data, "db"));

        if (!Listening(MongoPort))
        {
            Log.Info("  Starting MongoDB for the local Xenia server");
            var mongo = new ProcessStartInfo(Path.Combine(tools, "mongodb", "bin", "mongod.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = data,
            };
            foreach (var a in new[] { "--dbpath", Path.Combine(data, "db"), "--bind_ip", "127.0.0.1",
                                      "--port", MongoPort.ToString(), "--logpath", Path.Combine(data, "mongod.log") })
                mongo.ArgumentList.Add(a);
            Process.Start(mongo);
            WaitListening(MongoPort, TimeSpan.FromSeconds(20));
        }

        if (!Listening(ServerPort))
        {
            Log.Info("  Starting the local Xenia Web Services server");
            var server = Path.Combine(config.Root, "build", "xenia-webservices");
            var node = new ProcessStartInfo(Path.Combine(tools, "node", "node.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = server,
            };
            node.ArgumentList.Add("dist/main");
            node.Environment["API_PORT"] = ServerPort.ToString();
            node.Environment["MONGO_URI"] = $"mongodb://127.0.0.1:{MongoPort}/";
            node.Environment["SWAGGER_API"] = "false";
            node.Environment["SCARF_ANALYTICS"] = "false";
            Process.Start(node);
            WaitListening(ServerPort, TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// Deletes the sessions each seat's address (127.0.0.2, .3, ...) still has on the server. Xenia deletes its
    /// own sessions when it closes cleanly, but a copy that was killed or crashed leaves them in the server
    /// browser, and they stay there across launches (MongoDB keeps them). No copy is running at this point, so
    /// none of them can be live.
    /// </summary>
    static void ClearSeatSessions(int seatCount)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 0; i < seatCount; i++)
        {
            var address = $"127.0.0.{2 + i}";
            try
            {
                var url = $"http://127.0.0.1:{ServerPort}/DeleteSessions?hostAddress={address}";
                var response = http.DeleteAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                    Log.Warn($"  Clearing old sessions of {address}: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) { Log.Warn($"  Clearing old sessions of {address}: {ex.Message}"); }
        }
    }

    static bool Listening(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    static void WaitListening(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Listening(port)) return;
            Thread.Sleep(250);
        }
        Log.Warn($"  Nothing listening on 127.0.0.1:{port} after {timeout.TotalSeconds}s");
    }
}
