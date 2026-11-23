using System.Diagnostics;
using Launcher.Core.Native;
using Launcher.Core.Runners;

namespace Launcher.Core.Session;

/// <summary>
/// One running launch: starts every instance in the plan, moves their windows into place,
/// and shuts all of them down together. If any instance exits, the whole session ends.
/// </summary>
public sealed class GameSession : IDisposable
{
    // TeknoParrot takes 20-25 s before the game's window appears.
    static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(60);
    static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(3);

    readonly LaunchPlan _plan;
    readonly JobObject _job = new();
    readonly List<(InstancePlan Plan, Process Process)> _instances = [];
    readonly CancellationTokenSource _cts = new();
    int _ended;

    /// <summary>Raised once, on a background thread, when the session is over for any reason.</summary>
    public event Action<GameSession>? Ended;

    public LaunchPlan Plan => _plan;
    public bool HasEnded => _ended != 0;

    public GameSession(LaunchPlan plan) => _plan = plan;

    /// <summary>The main window of the first game instance still running, or zero.</summary>
    public IntPtr MainGameWindow()
    {
        foreach (var (inst, p) in Instances())
        {
            if (Exited(p)) continue;
            var hwnd = GameWindow(inst, p);
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>Starts every instance. Subscribe to <see cref="Ended"/> first. On failure, anything started is killed.</summary>
    public void Start()
    {
        try
        {
            StartAll();
        }
        catch
        {
            Interlocked.Exchange(ref _ended, 1);
            KillAll();
            EndWheelSession();
            throw;
        }
    }

    IDisposable? _wheelSession;

    void StartAll()
    {
        Log.Info($"Starting {_plan.Game.Title} ({_plan.Mode}), {_plan.Instances.Count} instance(s)");
        // The hub applies this game's force settings to the seats in use, and resets forces when it ends.
        _wheelSession = Wheels.WheelHubClient.BeginSession(_plan.Game, _plan.Mode == Config.PlayMode.Solo ? [1] : [1, 2]);
        WindowTools.AllowAnyForeground();
        foreach (var inst in _plan.Instances)
        {
            var psi = inst.StartInfo;
            // Short and rare (TeknoParrot's second seat), so a plain wait on the caller's thread is fine.
            if (inst.StartDelay > TimeSpan.Zero) Thread.Sleep(inst.StartDelay);
            Log.Info($"  {inst.Label}: \"{psi.FileName}\" {string.Join(' ', psi.ArgumentList.Select(Quote))}");
            var p = Process.Start(psi) ?? throw new InvalidOperationException($"{psi.FileName} didn't start.");
            if (inst.AdoptProcess != null)
            {
                // The started program (steam.exe) only hands the request on. Never put it in the job: if Steam wasn't
                // running, this process IS Steam, and ending the session would kill it.
                p.Dispose();
                _ = Task.Run(() => AdoptAsync(inst));
                continue;
            }
            _job.Add(p);
            if (!string.IsNullOrWhiteSpace(inst.Audio))
            {
                var (label, audio) = (inst.Label, inst.Audio);
                _ = Task.Run(() => RouteAudioAsync(label, p, audio, _cts.Token));
            }
            if (inst.Companion is { } companion)
            {
                var label = inst.Label;
                _ = Task.Run(async () =>
                {
                    try { await companion(p, _cts.Token); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Log.Warn($"  {label}: companion failed: {ex.Message}"); }
                });
            }
            // Two or more copies: only one window can be in front, and Windows favours it.
            if (_plan.Instances.Count > 1) Log.Info($"  {inst.Label}: {ProcessBoost.Apply(p)}");
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => OnInstanceExited(inst, p);
            lock (_instances) _instances.Add((inst, p));
            if (p.HasExited) OnInstanceExited(inst, p);
        }

        foreach (var (inst, p) in Instances().Where(i => i.Plan.PlaceWindow && i.Plan.AdoptProcess == null))
            _ = Task.Run(() => PlaceWindowAsync(inst, p, _cts.Token));
    }

    List<(InstancePlan Plan, Process Process)> Instances()
    {
        lock (_instances) return [.. _instances];
    }

    /// <summary>
    /// Waits for the game another program starts for us (Steam) and tracks it like a started instance. Games under
    /// anti-cheat may refuse full access to their process, so exit is watched by polling, not by an exit event.
    /// If the session ended before the game showed up (Guide pressed while Steam was still starting it), the game is
    /// closed when it appears, so it doesn't run on behind the menu.
    /// </summary>
    async Task AdoptAsync(InstancePlan inst)
    {
        var name = inst.AdoptProcess!;
        var wait = inst.AdoptWait > TimeSpan.Zero ? inst.AdoptWait : TimeSpan.FromMinutes(3);
        var deadline = DateTime.UtcNow + wait;
        Log.Info($"  {inst.Label}: waiting up to {wait.TotalSeconds:0}s for {name}.exe");
        Process? p = null;
        while (DateTime.UtcNow < deadline)
        {
            p = Process.GetProcessesByName(name).OrderBy(StartTimeOrMax).FirstOrDefault();
            if (p != null) break;
            await Task.Delay(500);
        }
        if (p == null)
        {
            if (!HasEnded) { Log.Warn($"  {inst.Label}: {name}.exe didn't start within {wait.TotalSeconds:0}s"); await StopAsync(); }
            return;
        }
        if (HasEnded)
        {
            Log.Info($"  {inst.Label}: {name}.exe (pid {p.Id}) started after the session ended; closing it");
            await CloseAsync([p]);
            return;
        }
        Log.Info($"  {inst.Label}: adopted {name}.exe (pid {p.Id})");
        try { _job.Add(p); } catch (Exception ex) { Log.Warn($"  {inst.Label}: not added to the job: {ex.Message}"); }
        lock (_instances) _instances.Add((inst, p));
        if (!string.IsNullOrWhiteSpace(inst.Audio))
            _ = Task.Run(() => RouteAudioAsync(inst.Label, p, inst.Audio, _cts.Token));
        if (inst.PlaceWindow) _ = Task.Run(() => PlaceWindowAsync(inst, p, _cts.Token));
        else _ = Task.Run(() => BringToFrontAsync(inst, p, _cts.Token));
        try
        {
            while (!Exited(p)) await Task.Delay(1000, _cts.Token);
            OnInstanceExited(inst, p);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A game started by Steam may open behind the launcher's busy cover: bring its window to the front once.</summary>
    static async Task BringToFrontAsync(InstancePlan inst, Process p, CancellationToken ct)
    {
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTime.UtcNow < deadline && !Exited(p))
            {
                var hwnd = WindowTools.FindMainWindow(p.Id);
                if (hwnd != IntPtr.Zero)
                {
                    await Task.Delay(1000, ct);
                    WindowTools.ForceForeground(hwnd);
                    Log.Info($"  {inst.Label}: game window brought to the front");
                    return;
                }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    static DateTime StartTimeOrMax(Process p)
    {
        try { return p.StartTime; } catch { return DateTime.MaxValue; }
    }

    /// <summary>HasExited, also for processes that deny the access it needs (then: does the pid still exist).</summary>
    static bool Exited(Process p)
    {
        try { return p.HasExited; }
        catch (System.ComponentModel.Win32Exception)
        {
            try { using var again = Process.GetProcessById(p.Id); return false; }
            catch (ArgumentException) { return true; }
        }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Asks each process's window to close, then kills whatever is left after the grace period.</summary>
    static async Task CloseAsync(IReadOnlyList<Process> processes)
    {
        foreach (var p in processes)
        {
            if (Exited(p)) continue;
            var hwnd = WindowTools.FindMainWindow(p.Id);
            if (hwnd != IntPtr.Zero) WindowTools.RequestClose(hwnd);
        }
        var deadline = DateTime.UtcNow + CloseGrace;
        while (DateTime.UtcNow < deadline && processes.Any(p => !Exited(p)))
            await Task.Delay(100);
        foreach (var p in processes.Where(p => !Exited(p)))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warn($"  Couldn't kill {p.ProcessName} ({p.Id}): {ex.Message}"); }
        }
    }

    /// <summary>
    /// Sends the instance's sound to its seat's output. Windows rejects the call (E_INVALIDARG) until a process
    /// is fully set up (and, it seems, for processes that never open audio), so keep retrying for a while. Loaders
    /// (TeknoParrot starts Rally.exe / BudgieLoader.exe) play the sound from a child process, so the processes the
    /// instance starts are routed too. Windows remembers the choice per exe, so later launches start routed.
    /// </summary>
    static async Task RouteAudioAsync(string label, Process root, string audio, CancellationToken ct)
    {
        var done = new HashSet<int>();
        string rootResult = "";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        bool rootLogged = false;
        try
        {
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline && !root.HasExited)
            {
                if (!done.Contains(root.Id))
                {
                    rootResult = Audio.AudioRouting.Route(root.Id, audio);
                    if (!rootResult.Contains("0x80070057")) { done.Add(root.Id); Log.Info($"  {label}: {rootResult}"); rootLogged = true; }
                }
                foreach (var pid in ProcessTree.Descendants(root))
                {
                    if (done.Contains(pid)) continue;
                    var result = Audio.AudioRouting.Route(pid, audio);
                    if (result.Contains("0x80070057")) continue;
                    done.Add(pid);
                    string name;
                    try { name = Process.GetProcessById(pid).ProcessName; } catch { name = $"pid {pid}"; }
                    Log.Info($"  {label}: {name}: {result}");
                }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // root exited
        if (!rootLogged) Log.Info($"  {label}: {rootResult}");
    }

    /// <summary>The instance's game window: the started process's own, or that of its WindowProcess child.</summary>
    static IntPtr GameWindow(InstancePlan inst, Process p)
    {
        if (inst.WindowProcess == null) return WindowTools.FindMainWindow(p.Id);
        foreach (var pid in ProcessTree.Descendants(p))
        {
            try
            {
                if (!Process.GetProcessById(pid).ProcessName.Equals(inst.WindowProcess, StringComparison.OrdinalIgnoreCase)) continue;
            }
            catch (ArgumentException) { continue; }
            var hwnd = WindowTools.FindMainWindow(pid);
            if (hwnd != IntPtr.Zero) return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Some games put their window frame back later (SEGA Rally 3 does, when focus moves between the two seats'
    /// windows), so for the rest of the session strip it again whenever it reappears or the window moves.
    /// </summary>
    static async Task KeepPlacedAsync(InstancePlan inst, Process p, CancellationToken ct)
    {
        int fixes = 0;
        while (!ct.IsCancellationRequested && !p.HasExited)
        {
            await Task.Delay(1000, ct);
            var hwnd = GameWindow(inst, p);
            if (hwnd == IntPtr.Zero || (!WindowTools.HasFrame(hwnd) && WindowTools.IsAt(hwnd, inst.Area))) continue;
            WindowTools.MakeBorderless(hwnd, inst.Area, activate: false);
            if (++fixes <= 5) Log.Info($"  {inst.Label}: window frame came back, placed again");
        }
    }

    static async Task PlaceWindowAsync(InstancePlan inst, Process p, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + WindowWait;
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && !p.HasExited)
            {
                var hwnd = GameWindow(inst, p);
                if (hwnd != IntPtr.Zero)
                {
                    // Give the program a moment to finish creating/sizing its window, then place it.
                    await Task.Delay(500, ct);
                    hwnd = GameWindow(inst, p);
                    if (hwnd == IntPtr.Zero) continue;
                    if (!WindowTools.IsAt(hwnd, inst.Area)) WindowTools.MakeBorderless(hwnd, inst.Area);
                    if (inst.FullscreenKey != 0)
                    {
                        // Let the program notice its new monitor before it goes fullscreen there.
                        await Task.Delay(500, ct);
                        WindowTools.PostKey(hwnd, inst.FullscreenKey);
                    }
                    WindowTools.ForceForeground(hwnd);
                    Log.Info($"  {inst.Label}: window placed at {inst.Area}");
                    if (inst.WindowProcess != null) await KeepPlacedAsync(inst, p, ct);
                    return;
                }
                await Task.Delay(200, ct);
            }
            if (!p.HasExited && !ct.IsCancellationRequested)
                Log.Warn($"  {inst.Label}: no window appeared within {WindowWait.TotalSeconds}s");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"  {inst.Label}: placing window", ex); }
    }

    void OnInstanceExited(InstancePlan inst, Process p)
    {
        int code = -1;
        try { code = p.ExitCode; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Log.Info($"  {inst.Label} exited (code {code})");
        _ = Task.Run(StopAsync);
    }

    /// <summary>Asks every instance to close, force-kills whatever is left after a grace period, then raises Ended.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _cts.Cancel();

        var processes = Instances().Select(i => i.Process).ToList();
        foreach (var p in processes)
        {
            if (Exited(p)) continue;
            var hwnd = WindowTools.FindMainWindow(p.Id);
            if (hwnd != IntPtr.Zero) WindowTools.RequestClose(hwnd);
        }

        var deadline = DateTime.UtcNow + CloseGrace;
        while (DateTime.UtcNow < deadline && processes.Any(p => !Exited(p)))
            await Task.Delay(100);

        if (processes.Any(p => !Exited(p)))
        {
            Log.Info("  Force-closing remaining processes");
            // Adopted processes (Steam games) may have stayed out of the job, so kill them by hand too.
            foreach (var p in processes.Where(p => !Exited(p)))
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) { Log.Warn($"  Couldn't kill pid {p.Id}: {ex.Message}"); }
        }
        KillAll();
        EndWheelSession();
        Log.Info($"Session ended: {_plan.Game.Title}");
        Ended?.Invoke(this);
    }

    void KillAll() => _job.Terminate();

    void EndWheelSession()
    {
        try { Interlocked.Exchange(ref _wheelSession, null)?.Dispose(); }
        catch (Exception ex) { Log.Warn($"  Wheel hub: ending session: {ex.Message}"); }
    }

    static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public void Dispose()
    {
        EndWheelSession();
        _cts.Cancel();
        foreach (var (_, p) in Instances()) p.Dispose();
        _job.Dispose();
        _cts.Dispose();
    }
}
