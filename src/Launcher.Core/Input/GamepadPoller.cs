using System.Diagnostics;

namespace Launcher.Core.Input;

/// <summary>
/// Polls all XInput slots on a background thread and raises press events.
/// XInput keeps working when another program has focus, so this also catches the Guide button mid-game.
/// Events are raised on the polling thread; UI code must marshal them.
/// </summary>
public sealed class GamepadPoller : IDisposable
{
    const int PollIntervalMs = 8;
    const int DisconnectedRecheckMs = 1000; // polling an empty slot is slow, so check those rarely
    const int RepeatDelayMs = 400;
    const int RepeatIntervalMs = 140;

    // Steering thresholds as a fraction of full lock, with hysteresis so it doesn't chatter.
    const double SteerOn = 0.45, SteerOff = 0.30;
    const byte TriggerOn = 180, TriggerOff = 100;

    public event Action<PadEvent>? Pressed;
    public event Action? ConnectionsChanged;

    readonly Thread _thread;
    volatile bool _running = true;
    readonly IReadOnlyList<Config.JoystickLayout> _layouts;
    readonly Config.TwinStickLayout _twin;

    /// <summary>Slots 0-3: XInput. Slots 4-19: DirectInput controllers (wheels, twin sticks) as they are found.</summary>
    public const int FirstJoystickSlot = XInput.MaxControllers;
    readonly Slot[] _slots = Enumerable.Range(0, XInput.MaxControllers + Joystick.MaxJoysticks).Select(i => new Slot(i)).ToArray();

    /// <summary>Created and used on the polling thread only.</summary>
    Joystick? _joy;
    long _nextJoystickScanMs;
    readonly HashSet<Guid> _ignored = [];

    sealed class Slot(int index)
    {
        public readonly int Index = index;
        public bool Connected;
        public XInputSubType SubType;
        public string Name = "";
        /// <summary>The DirectInput device in this slot (joystick slots), null while the slot is free.</summary>
        public Joystick.Info? Device;
        public string DeviceText = "";
        public long NextCheckMs;
        public PadInput Held;
        public PadInput RepeatInput;
        public long NextRepeatMs;
        public bool IsTwinStick;
        public Config.JoystickLayout Layout = new();
        /// <summary>Environment.TickCount64 when Start went down, or 0 while it is up.</summary>
        public long StartDownSince;
        public bool IsJoystick => Index >= FirstJoystickSlot;
    }

    /// <param name="wheels">Wheel layouts; a joystick uses the first whose vendorId/productId match it (0 = any).</param>
    public GamepadPoller(IReadOnlyList<Config.JoystickLayout>? wheels = null, Config.TwinStickLayout? twinStick = null)
    {
        _layouts = wheels is { Count: > 0 } ? wheels : [new Config.JoystickLayout()];
        _twin = twinStick ?? new Config.TwinStickLayout();
        _thread = new Thread(Run) { IsBackground = true, Name = "GamepadPoller" };
        _thread.Start();
    }

    public PadInfo[] Snapshot()
    {
        lock (_slots) return _slots.Select(s => new PadInfo(s.Index, s.Connected, s.SubType, s.IsJoystick, s.Name, s.IsTwinStick)).ToArray();
    }

    /// <summary>
    /// How long Start has been held on any controller (wheel, pad or twin stick), counting only presses that began
    /// at or after <paramref name="pressedAfter"/> (Environment.TickCount64); 0 if none is held.
    /// </summary>
    public long LongestStartHoldMs(long pressedAfter)
    {
        long now = Environment.TickCount64, best = 0;
        foreach (var s in _slots)
        {
            long since = Volatile.Read(ref s.StartDownSince);
            if (s.Connected && since != 0 && since >= pressedAfter) best = Math.Max(best, now - since);
        }
        return best;
    }

    void Run()
    {
        var clock = Stopwatch.StartNew();
        try { _joy = new Joystick(); }
        catch (Exception ex) { Log.Error("DirectInput unavailable; wheels won't drive the menu", ex); }
        while (_running)
        {
            long now = clock.ElapsedMilliseconds;
            bool changed = false;
            if (_joy is { Available: true } && now >= _nextJoystickScanMs)
            {
                _nextJoystickScanMs = now + DisconnectedRecheckMs;
                try { ScanJoysticks(); }
                catch (Exception ex) { Log.Error("Listing DirectInput controllers", ex); }
            }
            foreach (var slot in _slots)
            {
                if (!slot.Connected && now < slot.NextCheckMs) continue;
                try
                {
                    changed |= PollSlot(slot, now);
                }
                catch (Exception ex)
                {
                    Log.Error($"Polling XInput slot {slot.Index}", ex);
                    slot.NextCheckMs = now + DisconnectedRecheckMs;
                }
            }
            if (changed) ConnectionsChanged?.Invoke();
            Thread.Sleep(PollIntervalMs);
        }
        _joy?.Dispose();
    }

    /// <summary>Puts newly attached DirectInput controllers into free joystick slots (a slot frees up when its device goes).</summary>
    void ScanJoysticks()
    {
        foreach (var info in _joy!.Attached(_ignored))
        {
            if (_slots.Any(s => s.Device?.Instance == info.Instance)) continue;
            var free = _slots.FirstOrDefault(s => s.IsJoystick && s.Device == null);
            if (free == null) return;
            free.Device = info;
            free.NextCheckMs = 0;
        }
    }

    bool PollSlot(Slot slot, long now)
    {
        bool ok;
        XInputState state = default;
        Joystick.State joy = default;
        if (slot.IsJoystick)
        {
            var dev = slot.Device;
            ok = dev != null && _joy!.TryGetState(dev.Instance, out joy);
            if (ok && !slot.Connected)
            {
                // A new controller: a twin stick (by USB id), or a wheel if it matches a configured layout.
                slot.IsTwinStick = dev!.VendorId == _twin.VendorId && dev.ProductId == _twin.ProductId;
                var layout = _layouts.FirstOrDefault(l => (l.VendorId == 0 || l.VendorId == dev.VendorId) &&
                                                          (l.ProductId == 0 || l.ProductId == dev.ProductId));
                ok = slot.IsTwinStick || (layout != null && (string.IsNullOrEmpty(layout.NameContains) ||
                     dev.Name.Contains(layout.NameContains, StringComparison.OrdinalIgnoreCase)));
                if (layout != null) slot.Layout = layout;
                if (!ok)
                {
                    _ignored.Add(dev.Instance); // no layout fits it: leave it alone from now on
                    _joy!.Close(dev.Instance);
                }
            }
            if (!ok) slot.Device = null; // gone (Joystick closed it) or ignored: the slot is free again
        }
        else ok = XInput.TryGetState(slot.Index, out state);

        bool changed = false;
        if (ok != slot.Connected)
        {
            var dev = slot.IsJoystick && ok ? slot.Device : null;
            string name = slot.Name; // for the disconnect line
            lock (_slots)
            {
                slot.Connected = ok;
                slot.SubType = ok && !slot.IsJoystick ? XInput.GetSubType(slot.Index) : XInputSubType.Unknown;
                slot.Name = dev?.Name ?? "";
            }
            if (ok && slot.IsJoystick) slot.DeviceText = $"{dev!.VendorId:X4}:{dev.ProductId:X4}, {dev.Axes} axes, {dev.Buttons} buttons, {dev.Povs} hat(s)";
            string what = slot.IsJoystick
                ? $"Joystick {slot.Index - FirstJoystickSlot} \"{(ok ? slot.Name : name)}\" {slot.DeviceText}{(slot.IsTwinStick ? " (twin stick)" : "")}"
                : $"Controller {slot.Index}";
            Log.Info(ok ? $"{what} connected{(slot.IsJoystick ? "" : $" ({slot.SubType})")}" : $"{what} disconnected");
            slot.Held = PadInput.None;
            slot.RepeatInput = PadInput.None;
            Volatile.Write(ref slot.StartDownSince, 0);
            changed = true;
        }
        if (!ok)
        {
            slot.NextCheckMs = now + DisconnectedRecheckMs;
            return changed;
        }

        var held = !slot.IsJoystick ? ToInputs(state.Gamepad, slot.Held)
                 : slot.IsTwinStick ? TwinStickInputs(joy)
                 : JoyInputs(joy, slot.Device!.Povs > 0, slot.Layout, slot.Held);
        var pressed = held & ~slot.Held;
        slot.Held = held;
        if ((held & PadInput.Start) == 0) Volatile.Write(ref slot.StartDownSince, 0);
        else if (slot.StartDownSince == 0) Volatile.Write(ref slot.StartDownSince, Environment.TickCount64);

        foreach (PadInput bit in Bits(pressed))
        {
            Pressed?.Invoke(new PadEvent(slot.Index, bit, false));
            if ((bit & PadInput.Repeating) != 0)
            {
                slot.RepeatInput = bit;
                slot.NextRepeatMs = now + RepeatDelayMs;
            }
        }

        if (slot.RepeatInput != PadInput.None)
        {
            if ((held & slot.RepeatInput) == 0)
            {
                slot.RepeatInput = PadInput.None;
            }
            else if (now >= slot.NextRepeatMs)
            {
                Pressed?.Invoke(new PadEvent(slot.Index, slot.RepeatInput, true));
                slot.NextRepeatMs = now + RepeatIntervalMs;
            }
        }
        return changed;
    }

    static PadInput ToInputs(XInputGamepad g, PadInput previous)
    {
        var result = (PadInput)g.Buttons;

        double x = g.ThumbLX / 32767.0, y = g.ThumbLY / 32767.0;
        result |= Axis(-x, previous, PadInput.SteerLeft);
        result |= Axis(x, previous, PadInput.SteerRight);
        result |= Axis(y, previous, PadInput.StickUp);
        result |= Axis(-y, previous, PadInput.StickDown);

        result |= Trigger(g.RightTrigger, previous, PadInput.Gas);
        result |= Trigger(g.LeftTrigger, previous, PadInput.Brake);
        return result;
    }

    /// <summary>A DirectInput wheel in the same terms: steering on X, pedals on the configured axes, buttons by number.</summary>
    static PadInput JoyInputs(Joystick.State j, bool hasHat, Config.JoystickLayout l, PadInput previous)
    {
        var r = hasHat ? Joystick.Hat(j.Pov0) : PadInput.None;
        void Btn(int n, PadInput bit) { if (j.Button(n)) r |= bit; }
        Btn(l.A, PadInput.A); Btn(l.B, PadInput.B); Btn(l.X, PadInput.X); Btn(l.Y, PadInput.Y);
        Btn(l.LeftShoulder, PadInput.LeftShoulder); Btn(l.RightShoulder, PadInput.RightShoulder);
        Btn(l.Back, PadInput.Back); Btn(l.Guide, PadInput.Guide);
        foreach (var s in l.StartButtons) Btn(s, PadInput.Start);

        double x = Joystick.Centered(j.X);
        r |= Axis(-x, previous, PadInput.SteerLeft);
        r |= Axis(x, previous, PadInput.SteerRight);
        double Pedal(string axis)
        {
            string n;
            try { n = Config.JoystickLayout.NormAxis(axis); } catch (InvalidDataException) { return 0; }
            return Joystick.Pedal(j.Axis(n), l.PedalsReversed);
        }
        r |= Trigger((byte)(Pedal(l.GasAxis) * 255), previous, PadInput.Gas);
        r |= Trigger((byte)(Pedal(l.BrakeAxis) * 255), previous, PadInput.Brake);
        return r;
    }

    /// <summary>
    /// A Virtual-On twin stick in menu terms: either lever moves (left lever = the hat), a trigger = A (start the
    /// game), a turbo button = B, Start = Start. Its axes are left alone (they rest centred and mean nothing).
    /// </summary>
    PadInput TwinStickInputs(Joystick.State j)
    {
        var r = Joystick.Hat(j.Pov0);
        void Btn(int n, PadInput bit) { if (j.Button(n)) r |= bit; }
        Btn(_twin.RightUp, PadInput.DPadUp); Btn(_twin.RightDown, PadInput.DPadDown);
        Btn(_twin.RightLeft, PadInput.DPadLeft); Btn(_twin.RightRight, PadInput.DPadRight);
        Btn(_twin.LeftTrigger, PadInput.A); Btn(_twin.RightTrigger, PadInput.A);
        Btn(_twin.LeftTurbo, PadInput.B); Btn(_twin.RightTurbo, PadInput.B);
        Btn(_twin.Start, PadInput.Start);
        return r;
    }

    static PadInput Axis(double value, PadInput previous, PadInput bit) =>
        value >= ((previous & bit) != 0 ? SteerOff : SteerOn) ? bit : PadInput.None;

    static PadInput Trigger(byte value, PadInput previous, PadInput bit) =>
        value >= ((previous & bit) != 0 ? TriggerOff : TriggerOn) ? bit : PadInput.None;

    static IEnumerable<PadInput> Bits(PadInput mask)
    {
        for (int i = 0; i < 32; i++)
        {
            var bit = (PadInput)(1u << i);
            if ((mask & bit) != 0) yield return bit;
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(500);
    }
}
