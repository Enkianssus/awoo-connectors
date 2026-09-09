using System.Reflection;
using UnifiedPlayerControlPoc;
using Windows.Media.Control;
using Windows.Storage.Streams;

internal static class MediaArtworkTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j/AkAAAAASUVORK5CYII=");
    private static int _checks;

    internal static async Task RunAsync()
    {
        var encoded = await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream(Png), default);
        Check(encoded == "data:image/png;base64," + Convert.ToBase64String(Png), "PNG bytes must roundtrip with fixed MIME.");
        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0xff, 0xd9 };
        Check((await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream(jpeg), default)).StartsWith("data:image/jpeg;base64,"), "JPEG signature is accepted.");
        Check(await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream("<svg onload='evil'>"u8.ToArray()), default) == "", "SVG/text must never become artwork data.");
        Check(await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream([0xff, 0xd8, 0xff, 0x00]), default) == "", "Truncated JPEG must fail closed.");
        var oversized = new byte[QQMusicMediaArtwork.MaximumBytes + 1];
        Png.CopyTo(oversized, 0);
        Check(await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream(oversized), default) == "", "Actual bytes enforce the size limit.");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream(Png), canceled.Token); throw new Exception("Cancellation not observed."); }
            catch (OperationCanceledException) { _checks++; }
        }

        await using var monitor = new QQMusicEventMonitor();
        await using var subscription = monitor.Subscribe();
        var selected = new GlobalSystemMediaTransportControlsSession { SourceAppUserModelId = "qqmusic-selected" };
        var other = new GlobalSystemMediaTransportControlsSession { SourceAppUserModelId = "qqmusic-other" };
        Set(monitor, "_session", selected); // Never call EnsureStarted/RequestAsync.

        var delayed = PendingStream();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        selected.ReadMedia = () => Task.FromResult(Properties("First", new(() => { opened.TrySetResult(); return delayed.Task; })));
        Check(await monitor.RefreshMediaAsync(), "Text commits without waiting for a thumbnail.");
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Check(monitor.ReadMediaTrack() is { Title: "First", CoverUrl: "" }, "Text remains available while image is delayed.");
        var firstEvents = Drain(subscription);
        Check(firstEvents.SequenceEqual([QQMusicEventKind.MediaPropertiesChanged]), "Text publication is the only initial metadata event.");

        selected.ReadMedia = () => Task.FromResult(Properties("Newer", Image(Png)));
        Check(await monitor.RefreshMediaAsync(), "New metadata supersedes a slow artwork read.");
        await WaitUntil(() => monitor.ReadMediaTrack()?.CoverUrl == encoded);
        delayed.SetResult(new(new MemoryStream(jpeg)));
        await WaitUntil(() => Get(monitor, "_artworkWorker") is null);
        Check(monitor.ReadMediaTrack() is { Title: "Newer" } track && track.CoverUrl == encoded, "Old generation cannot overwrite newer image/track.");
        var events = Drain(subscription);
        Check(events.Count(x => x == QQMusicEventKind.MediaPropertiesChanged) == 1 &&
            events.Count(x => x == QQMusicEventKind.ArtworkChanged) == 1 && events.Count == 2,
            "Image completion publishes only ArtworkChanged, never playback/timeline/metadata evidence.");

        selected.ReadMedia = () => Task.FromResult(Properties("Newer", null));
        await monitor.RefreshMediaAsync();
        Check(monitor.ReadMediaTrack()?.CoverUrl == encoded, "Exact same-track metadata refresh retains known cover without flicker.");
        selected.ReadMedia = () => Task.FromResult(Properties("Different", null));
        await monitor.RefreshMediaAsync();
        Check(monitor.ReadMediaTrack()?.CoverUrl == "", "Different track never inherits old artwork.");

        var staleSession = PendingStream();
        selected.ReadMedia = () => Task.FromResult(Properties("Old session", new(() => staleSession.Task)));
        await monitor.RefreshMediaAsync();
        Set(monitor, "_session", other);
        staleSession.SetResult(new(new MemoryStream(Png)));
        await WaitUntil(() => Get(monitor, "_artworkWorker") is null);
        Check(monitor.ReadMediaTrack()?.CoverUrl == "", "Same-looking metadata from an old session cannot commit its cover.");

        var never = PendingStream();
        other.ReadMedia = () => Task.FromResult(Properties("Timeout", new(() => never.Task)));
        await monitor.RefreshMediaAsync();
        await WaitUntil(() => Get(monitor, "_artworkWorker") is null, 3500);
        Check(monitor.ReadMediaTrack() is { Title: "Timeout", CoverUrl: "" }, "Two-second artwork timeout does not erase text.");
        never.SetResult(new(new MemoryStream(Png)));
        await Task.Yield();
        Check(monitor.ReadMediaTrack()?.CoverUrl == "", "Late timeout result cannot commit.");

        var falseSize = new FakeRandomAccessStream(new MemoryStream(oversized), 1);
        Check(await QQMusicMediaArtwork.ReadAsync(new RandomAccessStreamReference(() => Task.FromResult(falseSize)), default) == "" && falseSize.Disposed,
            "False advertised size cannot bypass actual cap, and stream is disposed.");
        Check(!QQMusicEventMonitor.SameMediaTrack(new("", "T", "A", "one"), new("", "T", "A", "two")),
            "Album participates in strict decorative identity.");
        var exactLimit = new byte[QQMusicMediaArtwork.MaximumBytes];
        Png.CopyTo(exactLimit, 0);
        Check((await QQMusicMediaArtwork.ReadBoundedAsync(new MemoryStream(exactLimit), default)).Length < 1024 * 1024,
            "Maximum accepted artwork leaves the JSON envelope below one MiB.");
        other.ReadMedia = () => Task.FromResult(Properties("Reader failure", new(() => throw new IOException("synthetic"))));
        Check(await monitor.RefreshMediaAsync(), "A failed thumbnail open cannot fail text refresh.");
        await WaitUntil(() => Get(monitor, "_artworkWorker") is null);
        Check(monitor.ReadMediaTrack() is { Title: "Reader failure", CoverUrl: "" }, "Thumbnail failure preserves metadata.");

        var disposing = PendingStream();
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        other.ReadMedia = () => Task.FromResult(Properties("Disposing", new(() => { disposeStarted.TrySetResult(); return disposing.Task; })));
        await monitor.RefreshMediaAsync();
        await disposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Drain(subscription);
        await monitor.DisposeAsync();
        disposing.SetResult(new(new MemoryStream(Png)));
        await WaitUntil(() => Get(monitor, "_artworkWorker") is null);
        Check(monitor.ReadMediaTrack() is null && !Drain(subscription).Contains(QQMusicEventKind.ArtworkChanged),
            "Disposal cancels artwork and forbids late publication.");
        Console.WriteLine($"QQ Music artwork tests passed ({_checks} pure checks; no Windows/QQ/API).");
    }

    private static RandomAccessStreamReference Image(byte[] bytes) => new(() => Task.FromResult(new FakeRandomAccessStream(new MemoryStream(bytes))));
    private static GlobalSystemMediaTransportControlsSessionMediaProperties Properties(string title, RandomAccessStreamReference? thumbnail) =>
        new() { Title = title, Artist = "Artist", AlbumTitle = "Album", Thumbnail = thumbnail };
    private static TaskCompletionSource<FakeRandomAccessStream> PendingStream() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? Get(QQMusicEventMonitor monitor, string field) => typeof(QQMusicEventMonitor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(monitor);
    private static void Set(QQMusicEventMonitor monitor, string field, object? value) => typeof(QQMusicEventMonitor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, value);
    private static List<QQMusicEventKind> Drain(QQMusicEventMonitor.QQMusicEventSubscription subscription)
    {
        List<QQMusicEventKind> kinds = [];
        while (subscription.Reader.TryRead(out var item)) kinds.Add(item.Kind);
        return kinds;
    }
    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 1500)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }
}
