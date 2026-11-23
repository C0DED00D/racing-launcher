using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Launcher.Core.Display;
using Launcher.Core.Native;

namespace Launcher.App;

/// <summary>A banner shown over a running game ("Press GUIDE again", "Keep holding START"). Never takes focus from the game.</summary>
sealed class ExitPrompt : Window
{
    readonly ScreenRect _area;
    readonly TextBlock _text;

    public ExitPrompt(ScreenRect area, string text = "Press GUIDE again to exit to the menu")
    {
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; // off-screen until positioned in real pixels below
        Top = -10000;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x10, 0x12, 0x18)),
            BorderBrush = (Brush)Application.Current.Resources["Accent"],
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(40, 22, 40, 22),
            Child = _text = new TextBlock
            {
                Text = text,
                TextAlignment = TextAlignment.Center,
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFont"],
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.Resources["Ink"],
            },
        };
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTools.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        WindowTools.ShowTopmostAt(new WindowInteropHelper(this).Handle, _area, fromTop: 0.08);
    }
}
