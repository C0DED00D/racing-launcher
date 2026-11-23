using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core.Config;
using Launcher.Core.Runners;

namespace Launcher.App;

public enum TileState { Ready, NeedsSetup, ComingSoon }

/// <summary>What one tile in the menu shows.</summary>
public sealed class GameTile : INotifyPropertyChanged
{
    public GameTile(GameProfile game, LauncherConfig config)
    {
        Game = game;
        var color = TryParseColor(game.Color) ?? Color.FromRgb(0x55, 0x5B, 0x66);
        AccentColor = color;
        TileBrush = new LinearGradientBrush(color, Darken(color, 0.45), 90);
        TileBrush.Freeze();

        var art = Path.Combine(config.Root, "art", game.Id + ".png");
        if (File.Exists(art))
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(art);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            Art = img;
        }
        Refresh(config);
    }

    public GameProfile Game { get; }
    public string Title => Game.Title;
    public string Subtitle => Game.Subtitle ?? "";
    public Color AccentColor { get; }
    public Brush TileBrush { get; }
    public ImageSource? Art { get; }
    public bool HasArt => Art != null;

    TileState _state;
    public TileState State { get => _state; private set => Set(ref _state, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public string Badge => State switch
    {
        TileState.ComingSoon => "COMING SOON",
        TileState.NeedsSetup => "SETUP NEEDED",
        _ => "",
    };

    bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Re-checks whether the game can launch (files may have been added since).</summary>
    public void Refresh(LauncherConfig config)
    {
        var runner = RunnerRegistry.Get(Game.Runner);
        if (runner == null)
        {
            State = TileState.ComingSoon;
            StatusText = "Not built yet — arrives in a later phase.";
        }
        else
        {
            var problems = runner.Check(Game, config);
            State = problems.Count == 0 ? TileState.Ready : TileState.NeedsSetup;
            StatusText = problems.Count == 0 ? "Ready" : problems[0];
        }
        OnPropertyChanged(nameof(Badge));
    }

    static Color? TryParseColor(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return (Color)ColorConverter.ConvertFromString(s); }
        catch (FormatException) { return null; }
    }

    static Color Darken(Color c, double f) =>
        Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One play-mode chip under the selected game.</summary>
public sealed class ModeChip(PlayMode mode, bool selected)
{
    public PlayMode Mode { get; } = mode;
    public bool IsSelected { get; } = selected;
    public string Label => Mode switch
    {
        PlayMode.Solo => "1 PLAYER",
        PlayMode.Linked => "2 PLAYER LINK",
        PlayMode.Split => "2 PLAYER SPLIT",
        _ => Mode.ToString().ToUpperInvariant(),
    };
}
