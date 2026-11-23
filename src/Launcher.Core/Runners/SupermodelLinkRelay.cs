using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Launcher.Core.Runners;

/// <summary>
/// Holds back the link between two Supermodel copies until the receiving copy can take it (Model 2 build,
/// 2026-11-16). Supermodel opens its TCP port in main(), but registers the net board's message handlers only later,
/// in the emulator's Init (CNetBoardLink in Src/Model3/NetBoardLink.cpp). A peer that connects in between has its
/// join message ('NBJ1') rejected as unknown, and neither side ever retries (CModel2Comm::ConnectProc connects once),
/// so the ring stays broken. Seen in Virtual-On Linked: seat 2 logged "sent message 'NBJ1', which is not known".
///
/// So each copy's "next machine" is a relay port here instead of the other copy's own port. The relay only starts
/// listening once the copy behind it has logged "initialized." in its Supermodel.log; until then the sender's connect
/// fails and it simply tries again. After that the relay pipes bytes both ways.
/// </summary>
static class SupermodelLinkRelay
{
    public const int RelayBasePort = 1990;

    /// <summary>
    /// Companion for the copy that listens on <paramref name="targetPort"/> in <paramref name="workingDir"/>: opens
    /// relay port <paramref name="relayPort"/> once that copy has initialized, and forwards to it.
    /// </summary>
    public static Func<Process, CancellationToken, Task> For(string label, string workingDir, int relayPort, int targetPort) =>
        async (process, ct) =>
        {
            var started = DateTime.Now;
            var log = Path.Combine(workingDir, "Supermodel.log");
            while (!ct.IsCancellationRequested && !process.HasExited && !Initialized(log, started))
                await Task.Delay(250, ct);
            if (process.HasExited) return;
            await Task.Delay(1000, ct); // a margin after the line, for the rest of Init
            var listener = new TcpListener(IPAddress.Loopback, relayPort);
            listener.Start();
            Log.Info($"  {label}: link relay open, {relayPort} -> {targetPort}");
            using var reg = ct.Register(listener.Stop);
            try
            {
                while (!ct.IsCancellationRequested && !process.HasExited)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => PipeAsync(label, client, targetPort, ct));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
            finally { listener.Stop(); }
        };

    static bool Initialized(string log, DateTime started)
    {
        try
        {
            if (!File.Exists(log) || File.GetLastWriteTime(log) < started.AddSeconds(-2)) return false;
            using var s = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(s);
            return r.ReadToEnd().Contains(" initialized.", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    static async Task PipeAsync(string label, TcpClient client, int targetPort, CancellationToken ct)
    {
        using (client)
        using (var target = new TcpClient())
        {
            try
            {
                client.NoDelay = true;
                target.NoDelay = true;
                await target.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var a = client.GetStream().CopyToAsync(target.GetStream(), both.Token);
                var b = target.GetStream().CopyToAsync(client.GetStream(), both.Token);
                await Task.WhenAny(a, b);
                both.Cancel();
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                if (!ct.IsCancellationRequested) Log.Info($"  {label}: link relay connection ended ({ex.Message})");
            }
        }
    }
}
