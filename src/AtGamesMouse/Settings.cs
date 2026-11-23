using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtGamesMouse;

/// <summary>
/// config/atgames-mouse.json. Defaults come from the user's JoyToKey profile MouseClick.cfg:
/// [Joystick 1] Button13 = left click, Button14 = right click.
/// Button numbers are HID button usages (usage page 0x09): usage N = DirectInput/JoyToKey button N.
/// </summary>
internal sealed class Settings
{
    /// <summary>USB vendor id, hex ("0525").</summary>
    public string VendorId { get; set; } = "0525";

    /// <summary>USB product id, hex ("9527").</summary>
    public string ProductId { get; set; } = "9527";

    /// <summary>USB interface number (the MI_xx in the device path), or null for any interface of the device.
    /// The game controller seen by JoyToKey was MI_01.</summary>
    public int? Interface { get; set; }

    public int LeftButton { get; set; } = 13;
    public int RightButton { get; set; } = 14;

    [JsonIgnore] public ushort Vid => ushort.Parse(VendorId.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);
    [JsonIgnore] public ushort Pid => ushort.Parse(ProductId.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new Settings();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Json));
            Log.Write($"Wrote default settings to {path}");
            return defaults;
        }

        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("empty settings file");
        _ = s.Vid; _ = s.Pid; // validate hex now, not on first device
        if (s.LeftButton < 1 || s.RightButton < 1) throw new InvalidDataException("button numbers start at 1");
        return s;
    }

    public string Describe() =>
        $"device VID {Vid:X4} PID {Pid:X4} interface {(Interface is { } i ? $"MI_{i:X2}" : "any")}, " +
        $"left = button {LeftButton}, right = button {RightButton}";
}
