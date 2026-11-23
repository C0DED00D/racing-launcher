using Launcher.Core.Config;
using Launcher.Core.Display;

namespace Launcher.Core.Runners;

public static class SeatResolver
{
    /// <summary>
    /// Works out which controller, screen area and audio output each seat gets for a launch.
    /// Solo and Split use seat 1's controller on the full primary monitor.
    /// Linked gives each seat its own monitor, or in dev mode, half of the primary monitor each.
    /// </summary>
    public static IReadOnlyList<SeatAssignment> Resolve(LauncherConfig config, PlayMode mode, GameProfile? game = null)
    {
        var all = ResolveSeats(config, mode).Select(a => a with { Layout = config.Seats.LayoutFor(a.SeatId) }).ToList();
        if (game?.UsesTwinStick != true) return all;
        // Twin-stick games: the seat's controller is its twin stick, not its wheel.
        var byId = config.Seats.Seats.ToDictionary(s => s.Id);
        return all.Select(a => a with { JoystickMatch = byId[a.SeatId].TwinStick }).ToList();
    }

    static IReadOnlyList<SeatAssignment> ResolveSeats(LauncherConfig config, PlayMode mode)
    {
        var seats = config.Seats.Seats.OrderBy(s => s.Id).ToList();
        var primary = Monitors.Primary();

        if (mode != PlayMode.Linked)
        {
            var s1 = seats[0];
            return [new SeatAssignment(s1.Id, s1.XInputSlot, primary, ScreenRect.Of(primary), s1.Audio, s1.Joystick)];
        }

        if (seats.Count < 2) throw new InvalidOperationException("Linked mode needs two seats in config/seats.json.");

        var result = new List<SeatAssignment>();
        for (int i = 0; i < 2; i++)
        {
            var seat = seats[i];
            var monitor = i == 0 ? primary : Monitors.Find(seat.Monitor);
            // Seat 1 always owns the primary monitor; it is only halved below if seat 2 has to share it.
            if (monitor != null && (i == 0 || monitor != primary))
            {
                result.Add(new SeatAssignment(seat.Id, seat.XInputSlot, monitor, ScreenRect.Of(monitor), seat.Audio, seat.Joystick));
            }
            else if (config.Seats.DevMode)
            {
                // Tile both seats side by side on the primary monitor.
                int half = primary.Width / 2;
                var area = new ScreenRect(primary.X + i * half, primary.Y, half, primary.Height);
                result.Add(new SeatAssignment(seat.Id, seat.XInputSlot, primary, area, seat.Audio, seat.Joystick));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Seat {seat.Id}'s monitor ({seat.Monitor}) isn't connected. Turn on devMode in config/seats.json to share one screen.");
            }
        }

        // In dev mode seat 1 also has to shrink to the left half if seat 2 is sharing its monitor.
        if (result[1].Monitor == primary)
            result[0] = result[0] with { Area = new ScreenRect(primary.X, primary.Y, primary.Width / 2, primary.Height) };
        return result;
    }
}
