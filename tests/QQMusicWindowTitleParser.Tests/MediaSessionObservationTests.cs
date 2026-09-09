using System.Reflection;
using UnifiedPlayerControlPoc;
using Windows.Media.Control;

internal static class MediaSessionObservationTests
{
    public static async Task RunAsync()
    {
        var selected = Session("qqmusic-selected", "Before");
        var other = Session("qqmusic-other", "Other");
        var manager = new GlobalSystemMediaTransportControlsSessionManager();
        manager.Sessions.Add(selected);
        manager.Sessions.Add(other);
        await using var monitor = new QQMusicEventMonitor();
        Set(monitor, "_manager", manager);
        SelectSession(monitor);

        manager.Sessions.Reverse();
        Check(monitor.ReadTimelineSnapshot()?.SourceAppUserModelId == "qqmusic-selected",
            "Monitor timeline must retain its subscribed session when enumeration order changes.");
        Check(new QQMusicTimelineProbe(manager).ReadSnapshot()?.SourceAppUserModelId == "qqmusic-other",
            "Standalone timeline discovery must retain its original first-match behavior.");

        selected.Playback.PlaybackType = Windows.Media.MediaPlaybackType.Video;
        other.Playback.PlaybackType = Windows.Media.MediaPlaybackType.Music;
        Check(monitor.ReadTimelineSnapshot()?.MediaType == "Video" &&
            new QQMusicTimelineProbe(manager).ReadSnapshot()?.MediaType == "Music",
            "Diagnostic media type must come from the same selected session, not independent discovery.");
        selected.Playback.PlaybackType = null;
        Check(monitor.ReadTimelineSnapshot()?.MediaType == string.Empty,
            "Missing PlaybackType must remain unknown, never default to Music.");
        selected.Playback.PlaybackType = Windows.Media.MediaPlaybackType.Unknown;
        Check(monitor.ReadTimelineSnapshot()?.MediaType == "Unknown",
            "Explicit SDK Unknown must remain distinct from missing PlaybackType.");
        selected.Playback.PlaybackType = (Windows.Media.MediaPlaybackType)999;
        Check(monitor.ReadTimelineSnapshot()?.MediaType == "999",
            "Future enum values must be preserved diagnostically, not interpreted as music.");
        selected.Playback.PlaybackType = null;
        var legacy = new QQMusicTimelineSnapshot("legacy", "Paused", default, default,
            default, default, default, default, default, "reported-paused");
        Check(legacy.MediaType == string.Empty,
            "Existing snapshot constructors must retain a compatible optional MediaType default.");

        Set(monitor, "_session", null);
        Check(monitor.ReadTimelineSnapshot() is null && !await monitor.RefreshMediaAsync(),
            "No selected session must not fall back to independent discovery.");
        Set(monitor, "_session", selected);

        selected.BeforeReadTimeline = () => Set(monitor, "_session", other);
        Check(monitor.ReadTimelineSnapshot() is null,
            "A session change during timeline reading must invalidate that read.");
        selected.BeforeReadTimeline = null;
        Set(monitor, "_session", selected);

        selected.ReadMedia = () => Task.FromResult(Properties("Refreshed"));
        Check(await monitor.RefreshMediaAsync() && monitor.ReadMediaTrack()?.Title == "Refreshed",
            "Explicit refresh must reread metadata even without a properties event.");
        selected.ReadMedia = () => Task.FromResult(Properties(string.Empty));
        Check(await monitor.RefreshMediaAsync() && monitor.ReadMediaTrack() is null,
            "A successful empty metadata read is not a valid track or playback acknowledgement.");

        var stale = PendingProperties();
        selected.ReadMedia = () => stale.Task;
        var staleRefresh = monitor.RefreshMediaAsync();
        selected.ReadMedia = () => Task.FromResult(Properties("Newer"));
        Check(await monitor.RefreshMediaAsync(), "Newer refresh should commit.");
        stale.SetResult(Properties("Stale"));
        Check(!await staleRefresh && monitor.ReadMediaTrack()?.Title == "Newer",
            "An older concurrent refresh must not overwrite the newer generation.");

        var canceled = PendingProperties();
        selected.ReadMedia = () => canceled.Task;
        using var cancellation = new CancellationTokenSource();
        var canceledRefresh = monitor.RefreshMediaAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await canceledRefresh;
            throw new InvalidOperationException("Caller cancellation must propagate.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        canceled.SetResult(Properties("Late canceled"));
        await Task.Yield();
        Check(monitor.ReadMediaTrack()?.Title == "Newer",
            "Canceled reads must never publish a late result.");

        var timedOut = PendingProperties();
        selected.ReadMedia = () => timedOut.Task;
        Check(!await monitor.RefreshMediaAsync().WaitAsync(TimeSpan.FromSeconds(5)),
            "An unresponsive metadata read must return false within the fixed timeout.");
        timedOut.SetResult(Properties("Late timed out"));
        await Task.Yield();
        Check(monitor.ReadMediaTrack()?.Title == "Newer",
            "Timed-out reads must never publish a late result.");

        var changed = PendingProperties();
        selected.ReadMedia = () => changed.Task;
        var changedRefresh = monitor.RefreshMediaAsync();
        Set(monitor, "_session", other);
        changed.SetResult(Properties("Old session"));
        Check(!await changedRefresh && monitor.ReadMediaTrack()?.Title == "Newer",
            "A refresh from a replaced session must not commit.");
        Console.WriteLine("QQ Music media observation tests passed (in-memory fakes only).");
    }

    private static GlobalSystemMediaTransportControlsSession Session(string id, string title) => new()
    {
        SourceAppUserModelId = id,
        ReadMedia = () => Task.FromResult(Properties(title))
    };

    private static GlobalSystemMediaTransportControlsSessionMediaProperties Properties(string title) =>
        new() { Title = title, Artist = "Artist" };

    private static TaskCompletionSource<GlobalSystemMediaTransportControlsSessionMediaProperties> PendingProperties() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Set(QQMusicEventMonitor monitor, string field, object? value) =>
        typeof(QQMusicEventMonitor).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, value);

    private static void SelectSession(QQMusicEventMonitor monitor) =>
        typeof(QQMusicEventMonitor).GetMethod("RefreshSession", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(monitor, null);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
