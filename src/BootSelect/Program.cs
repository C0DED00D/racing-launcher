using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BootSelect;

/// <summary>
/// Runs at logon and decides which setup this PC is plugged into, by the primary display's resolution:
/// 3840x2160 = racing cabinet (start the racing launcher), 1920x1080 = AtGames pinball table (Pinball FX on Steam
/// plus AtGamesMouse). It waits until Windows reports a matching resolution (displays may come up late, or not
/// at all while a TV is off and drops its HDMI connection), needs the same answer a few checks in a row, starts
/// that profile's programs and exits. Profiles: config/boot-select.json (written with defaults on first run).
/// Command line: --dry-run logs the decision without starting anything; --list logs the displays and exits.
/// Log: runtime/logs/boot-select.log.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Native.SetProcessDpiAwarenessContext(-4); // physical pixels at 300 % scaling
        var root = FindRoot();
        var log = new Log(Path.Combine(root, "runtime", "logs", "boot-select.log"));
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(true, @"Local\RacingBootSelect", out var first);
        if (!first) { log.Write("Already running; exiting."); return 1; }

        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var d in Displays.Active()) log.Write($"Display: {d}");
            return 0;
        }

        Settings settings;
        var settingsFile = Path.Combine(root, "config", "boot-select.json");
        try { settings = Settings.LoadOrCreate(settingsFile); }
        catch (Exception ex) { log.Write($"Settings error in {settingsFile}: {ex.Message}"); return 2; }
        log.Write($"Start (pid {Environment.ProcessId}){(dryRun ? ", DRY RUN" : "")}; profiles: " +
                  string.Join(", ", settings.Profiles.Select(p => $"{p.Name} {p.Width}x{p.Height}")));

        var deadline = settings.TimeoutMinutes > 0 ? DateTime.Now.AddMinutes(settings.TimeoutMinutes) : DateTime.MaxValue;
        Profile? candidate = null;
        var streak = 0;
        var lastSeen = "";
        while (DateTime.Now < deadline)
        {
            var displays = Displays.Active();
            var seen = displays.Count == 0 ? "no active display" : string.Join("; ", displays);
            if (seen != lastSeen) { log.Write($"Displays: {seen}"); lastSeen = seen; }

            var primary = displays.FirstOrDefault(d => d.Primary);
            var match = primary == null ? null
                : settings.Profiles.FirstOrDefault(p => p.Width == primary.Width && p.Height == primary.Height);
            streak = match != null && match == candidate ? streak + 1 : match != null ? 1 : 0;
            candidate = match;
            if (candidate != null && streak >= settings.StableChecks)
            {
                log.Write($"Primary display {primary!.Width}x{primary.Height}: profile '{candidate.Name}'");
                foreach (var item in candidate.Run) Start(item, root, dryRun, log);
                return 0;
            }
            Thread.Sleep(TimeSpan.FromSeconds(settings.PollSeconds));
        }
        log.Write($"Gave up after {settings.TimeoutMinutes} min: no profile matches ({lastSeen}).");
        return 3;
    }

    /// <summary>Starts a program (path relative to the root, or absolute) or opens a URL such as steam://.
    /// Retries a failed start: Smart App Control sometimes blocks our unsigned exes in the first minutes after
    /// boot and lets the same file run a little later (seen 2026-11-12 and 2026-11-13).</summary>
    private static void Start(string item, string root, bool dryRun, Log log)
    {
        const int attempts = 12;
        var retryDelay = TimeSpan.FromSeconds(15);
        var isUrl = item.Contains("://", StringComparison.Ordinal);
        var target = isUrl || Path.IsPathRooted(item) ? item : Path.GetFullPath(Path.Combine(root, item));
        if (dryRun) { log.Write($"  would start {target}"); return; }
        if (!isUrl && !File.Exists(target)) { log.Write($"  MISSING {target}"); return; }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!isUrl) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
                Process.Start(psi);
                log.Write($"  started {target}{(attempt > 1 ? $" (attempt {attempt})" : "")}");
                return;
            }
            catch (Exception ex)
            {
                log.Write($"  FAILED {target} (attempt {attempt}/{attempts}): {ex.Message}");
                if (attempt >= attempts) return;
                Thread.Sleep(retryDelay);
            }
        }
    }

    /// <summary>Same rule as Launcher.Core.Paths: RACING_LAUNCHER_ROOT, else the first folder above the exe with
    /// config/games.json, else the exe's folder.</summary>
    private static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("RACING_LAUNCHER_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "config", "games.json"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }
}

internal sealed class Profile
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Programs (relative to the root or absolute) or URLs, started in order.</summary>
    public List<string> Run { get; set; } = [];
}

internal sealed class Settings
{
    public double PollSeconds { get; set; } = 2;
    /// <summary>Checks in a row that must agree before starting (displays can switch modes during startup).</summary>
    public int StableChecks { get; set; } = 3;
    /// <summary>0 = wait forever (e.g. until the TVs are switched on).</summary>
    public double TimeoutMinutes { get; set; } = 0;
    public List<Profile> Profiles { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true,
    };

    public static Settings LoadOrCreate(string file)
    {
        if (File.Exists(file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json) ?? new();
        var s = new Settings
        {
            Profiles =
            [
                new() { Name = "racing", Width = 3840, Height = 2160,
                        Run = [@"src\Launcher.App\bin\Debug\net10.0-windows\RacingLauncher.exe"] },
                new() { Name = "pinball", Width = 1920, Height = 1080,
                        Run = ["steam://rungameid/2328760", @"src\AtGamesMouse\bin\Debug\net10.0-windows\AtGamesMouse.exe"] },
            ],
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(s, Json));
        return s;
    }
}

internal sealed record Display(string Name, int Width, int Height, int Hz, bool Primary)
{
    public override string ToString() => $"{Name} {Width}x{Height}@{Hz}{(Primary ? " primary" : "")}";
}

internal static class Displays
{
    public static List<Display> Active()
    {
        var list = new List<Display>();
        for (var i = 0; ; i++)
        {
            var dev = new Native.DisplayDevice { cb = Marshal.SizeOf<Native.DisplayDevice>() };
            if (!Native.EnumDisplayDevices(null, i, ref dev, 0)) break;
            if ((dev.StateFlags & Native.AttachedToDesktop) == 0) continue;
            var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(dev.DeviceName, Native.EnumCurrentSettings, ref mode)) continue;
            list.Add(new Display(dev.DeviceName, mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency,
                (dev.StateFlags & Native.PrimaryDevice) != 0));
        }
        return list;
    }
}

internal sealed class Log(string file)
{
    public void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException) { }
    }
}

internal static class Native
{
    public const int AttachedToDesktop = 0x1, PrimaryDevice = 0x4, EnumCurrentSettings = -1;

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint value);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string? device, int index, ref DisplayDevice info, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string device, int mode, ref DevMode info);
}
