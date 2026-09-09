using System.Numerics;
using QQMusicControlPoc;

internal static class EndPolicyTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    public static void Run()
    {
        Near(true, 0, 60 * Second, 60 * Second, "exact end");
        Near(true, 0, 60 * Second, 57 * Second, "inclusive three-second tail");
        Near(false, 0, 60 * Second, 57 * Second - 1, "one tick before tail");
        Near(false, 0, 60 * Second, 60 * Second + 1, "one tick beyond end");
        Near(true, 5 * Second, 7 * Second, 5 * Second, "short positive range may be near end");
        Near(true, 0, 1, 0, "positive one-tick duration");
        Near(false, 0, 0, 0, "zero duration");
        Near(false, 10, 9, 9, "reversed range");
        Near(false, 10, 20, 9, "position before nonzero start");
        Near(false, -1, 20, 20, "negative start");
        Near(false, 0, 20, -1, "negative position");
        Near(false, long.MinValue, long.MaxValue, long.MaxValue, "cross-sign duration cannot overflow");
        Near(true, 0, long.MaxValue, long.MaxValue, "maximum nonnegative range");
        Near(true, long.MaxValue - 1, long.MaxValue, long.MaxValue, "range next to maximum");

        Roll(true, 0, 249 * Second, 248 * Second, 0, 249 * Second, Second, "single-track loop candidate");
        Roll(true, 50 * Second, 110 * Second, 107 * Second,
            50 * Second, 110 * Second, 53 * Second, "nonzero start and inclusive windows");
        Roll(false, 0, 60 * Second, 57 * Second - 1, 0, 60 * Second, 0, "previous outside tail");
        Roll(false, 0, 60 * Second, 60 * Second, 0, 60 * Second, 3 * Second + 1, "current outside head");
        Roll(false, 0, 60 * Second, 60 * Second, 0, 60 * Second, -1, "current below start");
        Roll(false, 0, 60 * Second, 30 * Second, 0, 60 * Second, Second, "mid-track seek is not rollover");
        Roll(false, 0, 60 * Second, 58 * Second, 0, 60 * Second, 59 * Second, "forward progress");
        Roll(false, 0, 60 * Second, 59 * Second, 0, 60 * Second, 57 * Second, "two-second backward seek");
        Roll(false, 0, 10 * Second, 10 * Second, 0, 10 * Second, 0, "ten seconds is excluded");
        Roll(true, 0, 10 * Second + 1, 7 * Second + 1,
            0, 10 * Second + 1, 3 * Second, "one tick above minimum duration");
        Roll(false, 0, 60 * Second, 60 * Second, 1, 60 * Second + 1, 1, "same duration but changed start");
        Roll(false, 0, 60 * Second, 60 * Second, 0, 60 * Second + 1, 0, "changed end");
        Roll(false, -Second, 60 * Second, 60 * Second,
            -Second, 60 * Second, 0, "negative timeline cannot roll over");
        Roll(false, long.MinValue, long.MaxValue, long.MaxValue,
            long.MinValue, long.MaxValue, long.MinValue, "cross-sign extremes fail closed");
        Roll(true, 0, long.MaxValue, long.MaxValue,
            0, long.MaxValue, 0, "maximum representable backward distance");
        Roll(true, long.MaxValue - 20 * Second, long.MaxValue, long.MaxValue,
            long.MaxValue - 20 * Second, long.MaxValue, long.MaxValue - 20 * Second,
            "head window near maximum never adds past maximum");

        // BigInteger is an independent reference: even invalid geometry may
        // span more than Int64.MaxValue, without overflowing the test oracle.
        long[] edges = [long.MinValue, -1, 0, 1, 2 * Second, 3 * Second,
            10 * Second, 10 * Second + 1, 60 * Second, long.MaxValue - 1, long.MaxValue];
        foreach (var start in edges)
        foreach (var end in edges)
        foreach (var position in edges)
            Near(ExpectedNear(start, end, position), start, end, position, "extreme-grid near-end reference");

        var random = new Random(8675309);
        for (var index = 0; index < 300; index++)
        {
            var start = edges[random.Next(edges.Length)];
            var end = edges[random.Next(edges.Length)];
            var before = edges[random.Next(edges.Length)];
            var after = edges[random.Next(edges.Length)];
            Roll(ExpectedRoll(start, end, before, start, end, after), start, end, before,
                start, end, after, "extreme sampled rollover reference");
        }
    }

    private static bool ExpectedNear(long start, long end, long position) =>
        start >= 0 && end > start && position >= start && position <= end &&
        (BigInteger)end - position <= 3 * Second;

    private static bool ExpectedRoll(long start, long end, long before,
        long currentStart, long currentEnd, long after) =>
        start == currentStart && end == currentEnd && ExpectedNear(start, end, before) &&
        (BigInteger)end - start > 10 * Second && after >= start && after <= end &&
        (BigInteger)after - start <= 3 * Second && (BigInteger)before - after > 2 * Second;

    private static void Near(bool expected, long start, long end, long position, string label) =>
        Check.Equal(expected, QQMusicWebEndPolicy.IsNearEnd(
            TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), TimeSpan.FromTicks(position)), label);

    private static void Roll(bool expected, long start, long end, long before,
        long currentStart, long currentEnd, long after, string label) =>
        Check.Equal(expected, QQMusicWebEndPolicy.IsSameTrackRollover(
            TimeSpan.FromTicks(start), TimeSpan.FromTicks(end), TimeSpan.FromTicks(before),
            TimeSpan.FromTicks(currentStart), TimeSpan.FromTicks(currentEnd), TimeSpan.FromTicks(after)), label);
}
