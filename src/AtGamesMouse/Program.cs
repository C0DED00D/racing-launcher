namespace AtGamesMouse;

/// <summary>
/// Tray app: two buttons on the AtGames arcade table (USB VID 0525 / PID 9527) act as left and right mouse buttons.
/// Every other controller is ignored. Replaces the user's JoyToKey "MouseClick" profile.
/// Command line: --dry-run logs button numbers of the matching device and never clicks.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppPaths.FindRoot();
        Log.Init(Path.Combine(root.LogDir, "atgames-mouse.log"));

        using var mutex = new Mutex(true, @"Local\RacingAtGamesMouse", out var first);
        if (!first)
        {
            Log.Write("Another copy is already running; exiting.");
            return 1;
        }

        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        Settings settings;
        try
        {
            settings = Settings.LoadOrCreate(root.SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Write($"Settings error in {root.SettingsFile}: {ex.Message}");
            MessageBox.Show($"Couldn't read {root.SettingsFile}:\n{ex.Message}", "AtGames mouse",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        Log.Write($"Start (pid {Environment.ProcessId}): {settings.Describe()}{(dryRun ? ", DRY RUN (no clicks)" : "")}; " +
                  $"settings {root.SettingsFile}");

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write("Unhandled UI exception: " + e.Exception);
        using var app = new TrayApp(settings, dryRun);
        Application.Run(app);
        Log.Write("Exit");
        return 0;
    }
}

internal sealed record AppPaths(string Root, string SettingsFile, string LogDir)
{
    /// <summary>
    /// Same rule as the launcher (Launcher.Core.Paths): RACING_LAUNCHER_ROOT, else the first folder above the exe
    /// with config/games.json. If neither exists (exe copied elsewhere), settings and log sit next to the exe.
    /// </summary>
    public static AppPaths FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("RACING_LAUNCHER_ROOT");
        string? root = string.IsNullOrWhiteSpace(env) ? null : Path.GetFullPath(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); root == null && dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "games.json"))) root = dir.FullName;
        }

        if (root == null)
        {
            var exeDir = AppContext.BaseDirectory;
            return new AppPaths(exeDir, Path.Combine(exeDir, "atgames-mouse.json"), exeDir);
        }
        return new AppPaths(root, Path.Combine(root, "config", "atgames-mouse.json"),
            Path.Combine(root, "runtime", "logs"));
    }
}
