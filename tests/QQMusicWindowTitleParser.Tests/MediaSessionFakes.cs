// Test-only in-memory substitutes, not a Windows/QQ integration probe.
// RequestAsync is forbidden so an accidental real-start test fails immediately.
#pragma warning disable CS0067
namespace Windows.Media.Control;

internal sealed class SessionsChangedEventArgs : EventArgs;
internal sealed class CurrentSessionChangedEventArgs : EventArgs;
internal sealed class MediaPropertiesChangedEventArgs : EventArgs;
internal sealed class PlaybackInfoChangedEventArgs : EventArgs;
internal sealed class TimelinePropertiesChangedEventArgs : EventArgs;

internal enum GlobalSystemMediaTransportControlsSessionPlaybackStatus
{
    Opened, Playing, Paused, Stopped
}

internal sealed class GlobalSystemMediaTransportControlsSessionManager
{
    public List<GlobalSystemMediaTransportControlsSession> Sessions { get; } = [];
    public event Action<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? SessionsChanged;
    public event Action<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? CurrentSessionChanged;
    public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions() => Sessions;
    public static Task<GlobalSystemMediaTransportControlsSessionManager> RequestAsync() =>
        throw new InvalidOperationException("Pure tests must not start media-session discovery.");
}

internal sealed class GlobalSystemMediaTransportControlsSession
{
    public required string SourceAppUserModelId { get; init; }
    public event Action<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? MediaPropertiesChanged;
    public event Action<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? PlaybackInfoChanged;
    public event Action<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? TimelinePropertiesChanged;
    public GlobalSystemMediaTransportControlsSessionPlaybackInfo Playback { get; } = new();
    public GlobalSystemMediaTransportControlsSessionTimelineProperties Timeline { get; } = new();
    public Action? BeforeReadTimeline { get; set; }
    public Func<Task<GlobalSystemMediaTransportControlsSessionMediaProperties>> ReadMedia { get; set; } =
        () => Task.FromResult(new GlobalSystemMediaTransportControlsSessionMediaProperties());
    public GlobalSystemMediaTransportControlsSessionPlaybackInfo GetPlaybackInfo() => Playback;
    public GlobalSystemMediaTransportControlsSessionTimelineProperties GetTimelineProperties()
    {
        BeforeReadTimeline?.Invoke();
        return Timeline;
    }
    public Task<GlobalSystemMediaTransportControlsSessionMediaProperties> TryGetMediaPropertiesAsync() => ReadMedia();
}

internal sealed class GlobalSystemMediaTransportControlsSessionPlaybackInfo
{
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; set; }
    public Windows.Media.MediaPlaybackType? PlaybackType { get; set; }
}

internal sealed class GlobalSystemMediaTransportControlsSessionTimelineProperties
{
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public TimeSpan Position { get; set; }
    public DateTimeOffset LastUpdatedTime { get; set; }
}

internal sealed class GlobalSystemMediaTransportControlsSessionMediaProperties
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public Windows.Storage.Streams.IRandomAccessStreamReference? Thumbnail { get; set; }
}
