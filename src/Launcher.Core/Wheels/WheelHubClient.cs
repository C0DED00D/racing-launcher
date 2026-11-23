using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Launcher.Core.Config;

namespace Launcher.Core.Wheels;

/// <summary>
/// Talks to WheelHub.exe (github.com/C0DED00D/x360-wheel-ffb), which owns the physical wheels and shows games a virtual Xbox 360 pad and a
/// vJoy force-feedback wheel per seat. The launcher starts it, and tells it which game is running so it can apply the
/// game's "ffb" settings from games.json. The hub resets every force when a session's pipe connection closes.
/// </summary>
public static class WheelHubClient
{
    const string PipeName = "RacingWheelHub";
    const string MutexName = @"Local\RacingWheelHub";

    /// <summary>The hub exe: tools\wheelhub\WheelHub.exe, published there from x360-wheel-ffb by scripts\build-wheelhub.ps1.</summary>
    public static string? FindExe(LauncherConfig config)
    {
        var published = config.Resolve(@"tools\wheelhub\WheelHub.exe");
        return File.Exists(published) ? published : null;
    }

    public static bool IsRunning()
    {
        try
        {
            using var m = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>Starts the hub unless it's running already. It stops by itself when this launcher process exits.</summary>
    public static void EnsureRunning(LauncherConfig config)
    {
        if (!File.Exists(Path.Combine(config.ConfigDir, "wheels.json"))) return; // no hub on this machine
        if (IsRunning()) { Log.Info("Wheel hub already running"); return; }
        var exe = FindExe(config);
        if (exe == null) { Log.Warn("Wheel hub: tools\\wheelhub\\WheelHub.exe not found (publish x360-wheel-ffb there: scripts\\build-wheelhub.ps1)"); return; }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
            psi.ArgumentList.Add("--parent");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.Environment["RACING_LAUNCHER_ROOT"] = config.Root;
            Process.Start(psi);
            Log.Info($"Wheel hub started: {exe}");
        }
        catch (Exception ex) { Log.Error("Wheel hub didn't start", ex); }
    }

    /// <summary>
    /// Tells the hub a game is running on these seats. Dispose when the game ends (the hub also notices if the
    /// launcher dies, because the connection drops). Null if the hub isn't running.
    /// </summary>
    public static IDisposable? BeginSession(GameProfile game, IEnumerable<int> seats)
    {
        if (!IsRunning()) return null;
        try
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(1000);
            var conn = new Connection(pipe);
            var ffb = game.Ffb ?? new GameFfbSettings();
            var reply = conn.Send(JsonSerializer.Serialize(new
            {
                cmd = "session",
                game = game.Id,
                seats = seats.ToArray(),
                ffb = new { rumble = ffb.Rumble ?? "motors", strength = ffb.Strength, invert = ffb.Invert },
            }));
            Log.Info($"  Wheel hub: {game.Id} on seat(s) {string.Join(",", seats)}: {reply}");
            return conn;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            Log.Warn($"  Wheel hub not answering: {ex.Message}");
            return null;
        }
    }

    sealed class Connection(NamedPipeClientStream pipe) : IDisposable
    {
        readonly StreamWriter _w = new(pipe, leaveOpen: true) { AutoFlush = true };
        readonly StreamReader _r = new(pipe, leaveOpen: true);

        public string? Send(string json)
        {
            _w.WriteLine(json);
            return _r.ReadLine();
        }

        public void Dispose()
        {
            try { Send("{\"cmd\":\"end\"}"); } catch { }
            _w.Dispose();
            _r.Dispose();
            pipe.Dispose();
        }
    }
}
