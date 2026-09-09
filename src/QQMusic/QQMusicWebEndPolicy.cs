namespace QQMusicControlPoc;

/// <summary>
/// Pure timeline geometry only. A match does not prove natural completion;
/// callers must separately enforce fresh events, playback, identity and epoch
/// checks, and their one-consumption rule before taking any action.
/// </summary>
internal static class QQMusicWebEndPolicy
{
    private const long NearEndTicks = 3 * TimeSpan.TicksPerSecond;
    private const long MinimumDurationTicks = 10 * TimeSpan.TicksPerSecond;
    private const long MinimumBackwardTicks = 2 * TimeSpan.TicksPerSecond;

    internal static bool IsNearEnd(TimeSpan start, TimeSpan end, TimeSpan position)
    {
        // Validate ordering and nonnegative bounds before subtracting. This
        // also keeps every difference representable for extreme TimeSpans.
        return start.Ticks >= 0 && end.Ticks > start.Ticks &&
            position.Ticks >= start.Ticks && position.Ticks <= end.Ticks &&
            end.Ticks - position.Ticks <= NearEndTicks;
    }

    internal static bool IsSameTrackRollover(
        TimeSpan previousStart, TimeSpan previousEnd, TimeSpan previousPosition,
        TimeSpan currentStart, TimeSpan currentEnd, TimeSpan currentPosition)
    {
        if (previousStart != currentStart || previousEnd != currentEnd ||
            !IsNearEnd(previousStart, previousEnd, previousPosition) ||
            currentPosition.Ticks < currentStart.Ticks ||
            currentPosition.Ticks > currentEnd.Ticks)
            return false;

        return previousEnd.Ticks - previousStart.Ticks > MinimumDurationTicks &&
            currentPosition.Ticks - currentStart.Ticks <= NearEndTicks &&
            previousPosition.Ticks - currentPosition.Ticks > MinimumBackwardTicks;
    }
}
