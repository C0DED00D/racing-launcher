using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Launcher.Core;
using Launcher.Core.Config;
using Launcher.Core.Input;
using Launcher.Core.Native;
using Launcher.Core.Runners;
using Launcher.Core.Session;

namespace Launcher.App;

/// <summary>
/// The tile menu. Driven by wheel/gamepad (XInput), keyboard, or mouse.
/// While a game runs, only the Guide button is listened to (press it twice to return here), and Start: hold it for
/// 3 s on any controller (wheel or twin stick) to return here; a banner explains it after 0.5 s.
/// </summary>
public partial class MainWindow : Window
{
    const double TileStride = 300 + 36; // tile width + horizontal margins, matches the XAML
    static readonly TimeSpan ExitConfirmWindow = TimeSpan.FromSeconds(2.5);
    static readonly TimeSpan InputCooldownAfterGame = TimeSpan.FromMilliseconds(800);
    const int StartHoldHintMs = 500, StartHoldQuitMs = 3000;

    readonly LauncherConfig _config;
    readonly List<GameTile> _tiles;
    readonly GamepadPoller _pads;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _exitPromptTimer;
    readonly DispatcherTimer _startHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly List<ExitPrompt> _holdPrompts = [];
    long _sessionStartedTick;

    int _index;
    PlayMode _mode = PlayMode.Solo;
    GameSession? _session;
    readonly List<ExitPrompt> _exitPrompts = [];
    // Ignore the wheels for a moment at startup: a pedal or button already held would otherwise
    // read as a fresh press and launch a game straight away.
    DateTime _inputBlockedUntil = DateTime.UtcNow.AddSeconds(1.5);
    DateTime _escArmedUntil;

    public MainWindow(LauncherConfig config)
    {
        InitializeComponent();
        _config = config;
        _pads = new GamepadPoller(config.Seats.AllLayouts(), config.Seats.TwinStickLayout);
        _tiles = config.Games.Select(g => new GameTile(g, config)).ToList();
        Carousel.ItemsSource = _tiles;

        _exitPromptTimer = new DispatcherTimer { Interval = ExitConfirmWindow };
        _exitPromptTimer.Tick += (_, _) => HideExitPrompts();
        _statusTimer.Tick += (_, _) => FadeStatus();
        _startHoldTimer.Tick += (_, _) => CheckStartHold();

        _pads.Pressed += e => Dispatcher.BeginInvoke(() => OnPad(e));
        _pads.ConnectionsChanged += () => Dispatcher.BeginInvoke(UpdatePadList);

        Loaded += (_, _) =>
        {
            Select(0, animate: false);
            UpdatePadList();
            if (_tiles.Count == 0) ShowStatus("No games in config/games.json.", isError: true);
        };
        CarouselHost.SizeChanged += (_, _) => UpdateCarouselPosition(animate: false);
        Carousel.SizeChanged += (_, _) => UpdateCarouselPosition(animate: false);
        // Nothing in the menu is focusable, so keep keyboard focus on the window itself
        // or arrow keys may not arrive.
        Activated += (_, _) =>
        {
            Keyboard.Focus(this);
            if (_session == null) RefreshTiles();
        };
        MouseWheel += (_, e) => { if (_session == null) Move(e.Delta < 0 ? 1 : -1); };
    }

    // ---------------------------------------------------------------- selection

    GameTile? Current => _tiles.Count > 0 ? _tiles[_index] : null;

    void Move(int delta)
    {
        if (_tiles.Count == 0) return;
        Select(Math.Clamp(_index + delta, 0, _tiles.Count - 1), animate: true);
    }

    void Select(int index, bool animate)
    {
        if (_tiles.Count == 0) return;
        if (_quitFocused) SetQuitFocus(false);
        _index = index;
        for (int i = 0; i < _tiles.Count; i++) _tiles[i].IsSelected = i == index;

        var tile = _tiles[index];
        if (!tile.Game.Modes.Contains(_mode)) _mode = tile.Game.Modes.FirstOrDefault(PlayMode.Solo);
        UpdateDetails();
        UpdateCarouselPosition(animate);

        var tint = tile.AccentColor;
        tint.A = 0x50;
        BgTint.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(tint, TimeSpan.FromMilliseconds(animate ? 350 : 0)));
    }

    void ChangeMode(int delta)
    {
        var modes = Current?.Game.Modes;
        if (modes == null || modes.Count < 2) return;
        int i = Math.Clamp(modes.IndexOf(_mode) + delta, 0, modes.Count - 1);
        _mode = modes[i];
        UpdateDetails();
    }

    void UpdateDetails()
    {
        var tile = Current;
        if (tile == null) return;
        DetailTitle.Text = tile.Title;
        DetailStatus.Text = tile.StatusText;
        DetailStatus.Foreground = tile.State == TileState.Ready
            ? (Brush)FindResource("InkDim")
            : (Brush)FindResource("Accent");
        ModeList.ItemsSource = tile.Game.Modes.Select(m => new ModeChip(m, m == _mode)).ToList();
        SetHint(ModeHint, tile.Game.HintFor(_mode));
        Hints.Visibility = ModeHint.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
    }

    static void SetHint(TextBlock block, string? hint)
    {
        block.Text = hint ?? "";
        block.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateCarouselPosition(bool animate)
    {
        // Centre the row vertically (a Canvas doesn't do it for us).
        Canvas.SetTop(Carousel, Math.Max(0, (CarouselHost.ActualHeight - Carousel.ActualHeight) / 2));

        double target = CarouselHost.ActualWidth / 2 - (_index * TileStride + TileStride / 2);
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(animate ? 260 : 0))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        CarouselShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    void RefreshTiles()
    {
        foreach (var t in _tiles) t.Refresh(_config);
        UpdateDetails();
    }

    void UpdatePadList()
    {
        var snap = _pads.Snapshot().Where(p => p.Connected).ToList();
        int wheel = 0, stick = 0;
        var pads = snap.Select(p => p.IsTwinStick ? $"S{++stick} · STICK"
                : p.IsJoystick ? $"W{++wheel} · WHEEL"
                : $"P{p.Slot + 1} · {(p.SubType == XInputSubType.Wheel ? "WHEEL" : "PAD")}")
            .ToList();
        PadList.ItemsSource = pads.Count > 0 ? pads : ["NO CONTROLLERS"];
    }

    // ---------------------------------------------------------------- input

    // Only while the launcher is the window in front: XInput is read in the background too, and a wheel
    // press while another program has focus must not start a game behind it.
    bool MenuInputAllowed => _session == null && IsActive && DateTime.UtcNow >= _inputBlockedUntil;

    void OnPad(PadEvent e)
    {
        if (e.Input == PadInput.Guide)
        {
            OnGuide();
            DismissTaskViewAfterGuide();
            return;
        }
        if (!MenuInputAllowed) return;

        if (_quitConfirming)
        {
            switch (e.Input)
            {
                case PadInput.SteerLeft or PadInput.DPadLeft or PadInput.LeftShoulder: MoveQuitChoice(-1); break;
                case PadInput.SteerRight or PadInput.DPadRight or PadInput.RightShoulder: MoveQuitChoice(1); break;
                case PadInput.A or PadInput.Start when !e.IsRepeat: ConfirmQuitChoice("controller"); break;
                case PadInput.B or PadInput.Back when !e.IsRepeat: CloseQuitConfirm(); break;
            }
            return;
        }
        if (_quitFocused)
        {
            switch (e.Input)
            {
                case PadInput.A or PadInput.Start when !e.IsRepeat: OpenQuitConfirm(); break;
                // Gas is ignored here so a pedal press can't head towards quitting.
                case PadInput.DPadUp or PadInput.StickUp or PadInput.Gas or PadInput.Brake: break;
                default: SetQuitFocus(false); break; // down, steer, B: back to the games
            }
            return;
        }

        switch (e.Input)
        {
            case PadInput.DPadUp or PadInput.StickUp when !e.IsRepeat && AtTopMode:
                SetQuitFocus(true);
                break;
            case PadInput.SteerLeft or PadInput.DPadLeft or PadInput.LeftShoulder:
                Move(-1);
                break;
            case PadInput.SteerRight or PadInput.DPadRight or PadInput.RightShoulder:
                Move(1);
                break;
            case PadInput.DPadUp or PadInput.StickUp:
                ChangeMode(-1);
                break;
            case PadInput.DPadDown or PadInput.StickDown:
                ChangeMode(1);
                break;
            case PadInput.A or PadInput.Start or PadInput.Gas when !e.IsRepeat:
                Launch();
                break;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!MenuInputAllowed) return;
        if (_quitConfirming)
        {
            switch (e.Key)
            {
                case Key.Left: MoveQuitChoice(-1); break;
                case Key.Right: MoveQuitChoice(1); break;
                case Key.Enter or Key.Space: ConfirmQuitChoice("keyboard"); break;
                case Key.Escape: CloseQuitConfirm(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (_quitFocused)
        {
            switch (e.Key)
            {
                case Key.Enter or Key.Space: OpenQuitConfirm(); break;
                case Key.Up: break;
                case Key.Down or Key.Left or Key.Right or Key.Escape: SetQuitFocus(false); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Up when AtTopMode: SetQuitFocus(true); break;
            case Key.Left: Move(-1); break;
            case Key.Right: Move(1); break;
            case Key.Up: ChangeMode(-1); break;
            case Key.Down: ChangeMode(1); break;
            case Key.Enter or Key.Space: Launch(); break;
            case Key.F5: RefreshTiles(); ShowStatus("Rechecked game files."); break;
            case Key.Escape:
                if (DateTime.UtcNow < _escArmedUntil)
                {
                    QuitFromMenu("Esc pressed twice");
                }
                else
                {
                    _escArmedUntil = DateTime.UtcNow.AddSeconds(2.5);
                    ShowStatus("Press Esc again to quit the launcher.");
                }
                break;
            default: return;
        }
        e.Handled = true;
    }

    void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!MenuInputAllowed || sender is not FrameworkElement { Tag: GameTile tile }) return;
        int i = _tiles.IndexOf(tile);
        if (i == _index) Launch();
        else Select(i, animate: true);
    }

    void Mode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!MenuInputAllowed || sender is not FrameworkElement { Tag: PlayMode mode }) return;
        _mode = mode;
        UpdateDetails();
    }

    // ---------------------------------------------------------------- launching

    void Launch()
    {
        var tile = Current;
        if (tile == null || _session != null) return;

        tile.Refresh(_config);
        UpdateDetails();

        var runner = RunnerRegistry.Get(tile.Game.Runner);
        if (runner == null)
        {
            ShowStatus($"{tile.Title} isn't built yet. It arrives in a later phase.");
            return;
        }
        var problems = runner.Check(tile.Game, _config);
        if (problems.Count > 0)
        {
            ShowStatus(string.Join("   •   ", problems), isError: true);
            return;
        }

        GameSession session;
        try
        {
            var seats = SeatResolver.Resolve(_config, _mode, tile.Game);
            var plan = runner.BuildPlan(tile.Game, _mode, seats, _config);
            session = new GameSession(plan);
            session.Ended += s => Dispatcher.BeginInvoke(() => OnSessionEnded(s));
            _session = session;
            session.Start();
            Launcher.Core.Wheels.WheelRange.ApplyInBackground(seats, tile.Game);
        }
        catch (NotSupportedException ex)
        {
            _session = null;
            ShowStatus(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _session = null;
            Log.Error($"Launching {tile.Title}", ex);
            ShowStatus($"Couldn't start {tile.Title}: {ex.Message}", isError: true);
            return;
        }

        BusyTitle.Text = tile.Title;
        SetHint(BusyHint, tile.Game.HintFor(_mode));
        BusyCover.Visibility = Visibility.Visible;
        // Start may still be down from launching the game: only presses from now on count towards quitting.
        _sessionStartedTick = Environment.TickCount64 + 200;
        _startHoldTimer.Start();
        if (!XInput.GuideSupported && !_pads.Snapshot().Any(p => p.Connected && p.IsJoystick))
            ShowStatus("The Guide button can't be read on this PC. Quit the game normally to come back.", isError: true);
    }

    void OnGuide()
    {
        var session = _session;
        if (session == null || session.HasEnded) return;

        if (_exitPrompts.Count > 0)
        {
            // Second press while the prompt is up: leave the game.
            HideExitPrompts();
            BusyTitle.Text = "Returning to menu…";
            _ = session.StopAsync();
            return;
        }

        foreach (var inst in session.Plan.Instances)
        {
            var prompt = new ExitPrompt(inst.Area);
            prompt.Show();
            _exitPrompts.Add(prompt);
        }
        _exitPromptTimer.Stop();
        _exitPromptTimer.Start();
    }

    /// <summary>
    /// Windows opens Task View on every Guide press. Watch for it for a moment and close it,
    /// handing focus back to the game (or to the menu when no game is running).
    /// </summary>
    async void DismissTaskViewAfterGuide()
    {
        foreach (int delay in new[] { 120, 250, 400, 700 })
        {
            await Task.Delay(delay);
            var session = _session;
            IntPtr target = session != null && !session.HasEnded
                ? session.MainGameWindow()
                : new WindowInteropHelper(this).Handle;
            if (WindowTools.DismissShellOverlay(target) is { } cls)
                Log.Info($"Guide button: took focus back from the Windows shell ({cls})");
        }
    }

    /// <summary>
    /// Hold Start for 3 s on any controller to leave the game. After 0.5 s a banner on every seat's screen says so and
    /// counts down; letting go earlier hides it.
    /// </summary>
    void CheckStartHold()
    {
        var session = _session;
        if (session == null || session.HasEnded) { HideHoldPrompts(); _startHoldTimer.Stop(); return; }
        long held = _pads.LongestStartHoldMs(_sessionStartedTick);
        if (held < StartHoldHintMs) { HideHoldPrompts(); return; }
        if (held >= StartHoldQuitMs)
        {
            HideHoldPrompts();
            _startHoldTimer.Stop();
            HideExitPrompts();
            Log.Info("START held for 3 s: returning to the menu");
            BusyTitle.Text = "Returning to menu…";
            _ = session.StopAsync();
            return;
        }
        var text = $"Keep holding START to quit to the menu\nquitting in {Math.Ceiling((StartHoldQuitMs - held) / 1000.0):0}…";
        if (_holdPrompts.Count == 0)
        {
            foreach (var inst in session.Plan.Instances)
            {
                var prompt = new ExitPrompt(inst.Area, text);
                prompt.Show();
                _holdPrompts.Add(prompt);
            }
        }
        else foreach (var p in _holdPrompts) p.Text = text;
        BusyHoldHint.Visibility = Visibility.Visible;
    }

    void HideHoldPrompts()
    {
        foreach (var p in _holdPrompts) p.Close();
        _holdPrompts.Clear();
        BusyHoldHint.Visibility = Visibility.Collapsed;
    }

    void HideExitPrompts()
    {
        _exitPromptTimer.Stop();
        foreach (var p in _exitPrompts) p.Close();
        _exitPrompts.Clear();
    }

    void OnSessionEnded(GameSession session)
    {
        if (!ReferenceEquals(_session, session)) return;
        _session = null;
        session.Dispose();
        HideExitPrompts();
        _startHoldTimer.Stop();
        HideHoldPrompts();
        BusyCover.Visibility = Visibility.Collapsed;
        _inputBlockedUntil = DateTime.UtcNow + InputCooldownAfterGame;

        WindowTools.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        RefreshTiles();
    }

    // ---------------------------------------------------------------- status

    public void ShowStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) : (Brush)FindResource("Ink");
        StatusBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    void FadeStatus()
    {
        _statusTimer.Stop();
        StatusBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
    }

    // ---------------------------------------------------------------- closing

    // The launcher once closed by itself a few seconds after both wheels woke up (2026-11-03 20:02), not
    // through Esc-twice; a Guide press opens Task View, which can close windows. Blocking outside closes made
    // Alt+F4 and the taskbar useless too, so every close is allowed again and only logged with its source and
    // the window in front, to catch the culprit if it happens again.
    string _lastCloseSource = "unknown";

    void QuitFromMenu(string why)
    {
        _lastCloseSource = why;
        Close();
    }

    // The QUIT button opens the quit / power menu (it used to quit at once; shutting down needs a choice).
    void Quit_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_session == null && !_quitConfirming) OpenQuitConfirm();
    }

    // QUIT from the wheel: D-pad up from the first mode highlights the button, A/Start opens the menu
    // (CANCEL, QUIT LAUNCHER, HIBERNATE PC, SHUT DOWN PC; CANCEL picked first so a stray press does nothing).
    bool _quitFocused, _quitConfirming;
    int _quitChoice;
    bool _powerBusy; // a hibernate is in progress: ignore the menu until the PC has resumed

    Border[] QuitOptions => PowerActions.HibernateAvailable
        ? [QuitOptCancel, QuitOptQuit, QuitOptHibernate, QuitOptShutDown]
        : [QuitOptCancel, QuitOptQuit, QuitOptShutDown];

    bool AtTopMode
    {
        get
        {
            var modes = Current?.Game.Modes;
            return modes == null || modes.Count == 0 || modes.IndexOf(_mode) <= 0;
        }
    }

    void SetQuitFocus(bool on)
    {
        _quitFocused = on;
        QuitButton.BorderBrush = on ? (Brush)FindResource("Accent") : Brushes.Transparent;
        QuitButton.Background = new SolidColorBrush(on ? Color.FromRgb(0xC6, 0x3A, 0x32) : Color.FromArgb(0x33, 0xFF, 0x5A, 0x4F));
        if (on) ShowStatus("Press A or START to quit, hibernate or shut down.   ▼ back to the games.");
    }

    void OpenQuitConfirm()
    {
        if (_powerBusy) return;
        _quitConfirming = true;
        QuitOptHibernate.Visibility = PowerActions.HibernateAvailable ? Visibility.Visible : Visibility.Collapsed;
        SetQuitChoice(0); // CANCEL by default
        QuitConfirm.Visibility = Visibility.Visible;
    }

    void MoveQuitChoice(int step) => SetQuitChoice(Math.Clamp(_quitChoice + step, 0, QuitOptions.Length - 1));

    void SetQuitChoice(int index)
    {
        _quitChoice = index;
        var accent = (Brush)FindResource("Accent");
        var ink = (Brush)FindResource("Ink");
        var dark = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
        var plain = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var options = QuitOptions;
        for (int i = 0; i < options.Length; i++)
        {
            options[i].Background = i == index ? accent : plain;
            if (options[i].Child is TextBlock t) t.Foreground = i == index ? dark : ink;
        }
    }

    void ConfirmQuitChoice(string how) => RunQuitOption((string)QuitOptions[_quitChoice].Tag, how);

    void QuitOption_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_session == null && sender is Border { Tag: string choice }) RunQuitOption(choice, "mouse");
    }

    void RunQuitOption(string choice, string how)
    {
        if (_powerBusy) return;
        switch (choice)
        {
            case "Quit":
                QuitFromMenu($"QUIT confirmed ({how})");
                break;
            case "ShutDown":
                Log.Info($"Shut down PC requested ({how})");
                try
                {
                    PowerActions.ShutDown();
                    QuitFromMenu($"shutting down the PC ({how})"); // closes games' servers and logs cleanly first
                }
                catch (Exception ex)
                {
                    Log.Error("Shut down failed", ex);
                    CloseQuitConfirm();
                    ShowStatus($"Couldn't shut down the PC: {ex.Message}", isError: true);
                }
                break;
            case "Hibernate":
                Log.Info($"Hibernate PC requested ({how})");
                CloseQuitConfirm();
                ShowStatus("Hibernating…");
                _powerBusy = true;
                // SetSuspendState returns only after the PC has woken up again, so keep it off the UI thread.
                Task.Run(() =>
                {
                    Exception? error = null;
                    try { PowerActions.Hibernate(); } catch (Exception ex) { error = ex; }
                    Dispatcher.BeginInvoke(() =>
                    {
                        _powerBusy = false;
                        if (error != null)
                        {
                            Log.Error("Hibernate failed", error);
                            ShowStatus($"Couldn't hibernate the PC: {error.Message}", isError: true);
                        }
                        else
                        {
                            Log.Info("Resumed from hibernation");
                            FadeStatus();
                        }
                    });
                });
                break;
            default:
                CloseQuitConfirm();
                break;
        }
    }

    void CloseQuitConfirm()
    {
        _quitConfirming = false;
        QuitConfirm.Visibility = Visibility.Collapsed;
        SetQuitFocus(false);
        FadeStatus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_CLOSE = 0x0010, WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;
        if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_CLOSE)
            _lastCloseSource = "system close command (Alt+F4, taskbar or title bar)";
        else if (msg == WM_CLOSE && _lastCloseSource == "unknown")
            _lastCloseSource = "WM_CLOSE from another program";
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        Log.Info($"Quit: {_lastCloseSource}; foreground window: {WindowTools.DescribeForeground()}");
    }

    protected override void OnClosed(EventArgs e)
    {
        Log.Info("Launcher window closed");
        HideExitPrompts();
        HideHoldPrompts();
        _pads.Dispose();
        _session?.Dispose(); // closing the job kills any game still running
        XeniaRunner.StopServer(_config); // MongoDB + Node for Hurricane's linked play
        base.OnClosed(e);
    }
}
