namespace Launcher.Core;

public static class Paths
{
    /// <summary>
    /// Finds the repository root: the RACING_LAUNCHER_ROOT environment variable if set,
    /// otherwise the first folder above the executable that contains config/games.json.
    /// </summary>
    public static string FindRoot()
    {
        var env = Environment.GetEnvironmentVariable("RACING_LAUNCHER_ROOT");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "games.json")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException(
            "Couldn't find config/games.json above " + AppContext.BaseDirectory +
            ". Set RACING_LAUNCHER_ROOT to the launcher folder.");
    }
}
