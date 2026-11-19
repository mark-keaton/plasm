// The host's one clock, registered in its services and given to KanbanData.
// KANBAN_CLOCK_START sets the instant it starts at, for a demonstration or a
// scenario; it runs on from there at the system's pace, and its timers are
// the system's.
namespace Kanban.Host;

public sealed class StartingAt(DateTimeOffset start) : TimeProvider
{
    readonly long started = System.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => start + System.GetElapsedTime(started);
}

public static class Clock
{
    public static TimeProvider FromEnvironment() =>
        Environment.GetEnvironmentVariable("KANBAN_CLOCK_START") is { } start
            ? new StartingAt(DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture))
            : TimeProvider.System;
}
