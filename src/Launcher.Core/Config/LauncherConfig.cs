using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core.Config;

/// <summary>Everything loaded from the config folder, plus path helpers.</summary>
public sealed class LauncherConfig
{
    public required string Root { get; init; }
    public required IReadOnlyList<GameProfile> Games { get; init; }
    public required IReadOnlyDictionary<string, EmulatorEntry> Emulators { get; init; }
    public required SeatsFile Seats { get; init; }

    public string ConfigDir => Path.Combine(Root, "config");
    public string RuntimeDir => Path.Combine(Root, "runtime");

    /// <summary>Turns a repo-relative path into an absolute one. Absolute paths pass through.</summary>
    public string Resolve(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

    public string? EmulatorPath(string id) =>
        Emulators.TryGetValue(id, out var e) && !string.IsNullOrWhiteSpace(e.Path) ? Resolve(e.Path) : null;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LauncherConfig Load(string root)
    {
        var configDir = Path.Combine(root, "config");
        var games = Read<GamesFile>(Path.Combine(configDir, "games.json")) ?? new GamesFile();
        var emus = Read<EmulatorsFile>(Path.Combine(configDir, "emulators.json")) ?? new EmulatorsFile();
        var seats = Read<SeatsFile>(Path.Combine(configDir, "seats.json")) ?? new SeatsFile();
        if (seats.Seats.Count == 0)
            seats.Seats.Add(new SeatConfig { Id = 1, XInputSlot = 0, Monitor = "primary" });

        return new LauncherConfig
        {
            Root = root,
            Games = games.Games,
            Emulators = new Dictionary<string, EmulatorEntry>(emus.Emulators, StringComparer.OrdinalIgnoreCase),
            Seats = seats,
        };
    }

    static T? Read<T>(string file)
    {
        if (!File.Exists(file)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)}: {ex.Message}", ex);
        }
    }
}
