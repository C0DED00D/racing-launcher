namespace AtGamesMouse;

/// <summary>Append-only log (runtime/logs/atgames-mouse.log). Button lines are rate limited.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    private const int ButtonLinesPerMinute = 30;
    private static DateTime _windowStart = DateTime.MinValue;
    private static int _buttonLines, _suppressed;

    public static void Init(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            if (_path == null) return;
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>For button activity: at most 30 lines a minute, then a count of what was left out.</summary>
    public static void Button(string message)
    {
        var now = DateTime.Now;
        if (now - _windowStart > TimeSpan.FromMinutes(1))
        {
            if (_suppressed > 0) Write($"({_suppressed} button lines left out)");
            _windowStart = now;
            _buttonLines = 0;
            _suppressed = 0;
        }
        if (_buttonLines++ < ButtonLinesPerMinute) Write(message);
        else _suppressed++;
    }
}
