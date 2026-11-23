namespace Launcher.Core;

/// <summary>Minimal append-only log at runtime/logs/launcher.log.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _file;

    public static void Init(string runtimeDir)
    {
        var dir = Path.Combine(runtimeDir, "logs");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "launcher.log");
        Info("---- launcher started ----");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(_file, line + Environment.NewLine); }
            catch (IOException) { /* logging must never crash the launcher */ }
        }
    }
}
