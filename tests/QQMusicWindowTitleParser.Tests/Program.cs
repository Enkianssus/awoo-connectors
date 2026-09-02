using QQMusicControlPoc;
using UnifiedPlayerControlPoc;

static void AssertTrack(
    string input,
    string expectedTitle,
    string expectedArtist)
{
    var actual = QQMusicWindowTitleParser.Parse(input)
        ?? throw new InvalidOperationException($"Expected a track for: {input}");
    if (actual.Title != expectedTitle || actual.Artist != expectedArtist)
    {
        throw new InvalidOperationException(
            $"Unexpected parse for '{input}': "
            + $"'{actual.Title}' / '{actual.Artist}'");
    }
}

AssertTrack(
    "D3 - 4 4 4 4 / h (Explicit) - VERT3X",
    "D3 - 4 4 4 4 / h (Explicit)",
    "VERT3X");
AssertTrack(
    "Shelter - Porter Robinson、Madeon",
    "Shelter",
    "Porter Robinson、Madeon");
AssertTrack(
    "Part One - Part Two - Artist",
    "Part One - Part Two",
    "Artist");

if (QQMusicWindowTitleParser.Parse("QQ音乐") is not null
    || QQMusicWindowTitleParser.Parse("No separator") is not null
    || QQMusicWindowTitleParser.Parse(null) is not null)
{
    throw new InvalidOperationException("Non-track captions must not parse.");
}

if (!QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Shelter",
        "Porter Robinson、Madeon",
        "Shelter",
        "Porter Robinson / Madeon")
    || QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Home",
        "Artist A",
        "Home",
        "Artist B")
    || QQMusicWindowTitleParser.MetadataRepresentsSameSong(
        "Home",
        "Artist A",
        "Home",
        string.Empty))
{
    throw new InvalidOperationException(
        "Structured metadata confidence checks failed.");
}

var artworkCandidates = new[]
{
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Porter Robinson / Madeon",
        "Shelter: Complete Edition",
        "001GMevG3WoCdt"),
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Porter Robinson / Madeon",
        "Shelter",
        "0046VnUP3it5w8"),
    new QQMusicAlbumArtworkCandidate(
        "Shelter",
        "Different Artist",
        "Shelter",
        "wrong-picture")
};
var pictureFallback = QQMusicAlbumArtwork.SelectPictureId(
    string.Empty,
    "Shelter",
    "Porter Robinson\u3001Madeon",
    artworkCandidates);
if (pictureFallback != "0046VnUP3it5w8"
    || QQMusicAlbumArtwork.SelectPictureId(
        "0046VnUP3it5w8",
        "Shelter",
        "Porter Robinson / Madeon",
        artworkCandidates) != "0046VnUP3it5w8"
    || QQMusicAlbumArtwork.SelectPictureId(
        string.Empty,
        "Unmatched Song",
        "Unmatched Artist",
        artworkCandidates) != string.Empty
    || QQMusicAlbumArtwork.BuildCoverUrl(pictureFallback)
        != "https://y.gtimg.cn/music/photo_new/"
           + "T002R300x300M0000046VnUP3it5w8.jpg"
    || QQMusicAlbumArtwork.BuildCoverUrl(string.Empty) != string.Empty)
{
    throw new InvalidOperationException(
        "Album artwork fallback checks failed.");
}

if (!QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Inst.)",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (Instrumental)",
        "Sparky Deathcap",
        "September",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Live)",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
        "September (纯音乐)",
        "Sparky Deathcap",
        "September (Inst.)",
        "Other Artist")
    || !QQMusicTrackMatchPolicy.TracksRepresentSameSong(
        "395562465",
        "September (Inst.)",
        "Sparky Deathcap",
        "395562465",
        "September (Live)",
        "Other Artist")
    || QQMusicTrackMatchPolicy.TracksRepresentSameSong(
        "111",
        "September (Inst.)",
        "Sparky Deathcap",
        "222",
        "September (纯音乐)",
        "Sparky Deathcap"))
{
    throw new InvalidOperationException(
        "QQ instrumental alias matching checks failed.");
}

if (!QQMusicTrackMatchPolicy.ObservationMetadataRepresentsSameSong(
        "Sad Machine",
        "Porter Robinson",
        "Sad Machine",
        "Porter Robinson/Madeon")
    || !QQMusicTrackMatchPolicy.ObservationMetadataRepresentsSameSong(
        "Sad Machine",
        "Porter Robinson、Madeon",
        "Sad Machine",
        "Porter Robinson / Madeon")
    || QQMusicTrackMatchPolicy.ObservationMetadataRepresentsSameSong(
        "Sad Machine",
        "Porter Robinson",
        "Sad Machine",
        "Daft Punk")
    || QQMusicTrackMatchPolicy.ObservationMetadataRepresentsSameSong(
        "Sad Machine",
        "Porter Robinson",
        "Musician",
        "Porter Robinson")
    || QQMusicTrackMatchPolicy.ObservationMetadataRepresentsSameSong(
        "Shelter",
        "Porter Robinson/Madeon",
        "Shelter (乐器版)",
        "Porter Robinson/Madeon")
    || !QQMusicTrackMatchPolicy.TracksRepresentSameObservation(
        "395562465",
        "Unhydrated title",
        "Unknown artist",
        "395562465",
        "September",
        "Sparky Deathcap")
    || QQMusicTrackMatchPolicy.TracksRepresentSameObservation(
        "111",
        "Same title",
        "Same artist",
        "222",
        "Same title",
        "Same artist")
    || !QQMusicTrackMatchPolicy.TracksRepresentSameObservation(
        "111|Artist",
        "Sad Machine",
        "Porter Robinson",
        "",
        "Sad Machine",
        "Porter Robinson/Madeon")
    || QQMusicTrackMatchPolicy.TracksRepresentSameSong(
        string.Empty,
        "Sad Machine",
        "Porter Robinson",
        string.Empty,
        "Sad Machine",
        "Porter Robinson/Madeon"))
{
    throw new InvalidOperationException(
        "QQ same-observation metadata matching must remain narrow and "
        + "must not relax strict target matching.");
}

static QQMusicPlaybackState PlaybackState(
    bool isRunning,
    string? title,
    string? windowTitle = null) =>
    new(
        isRunning,
        title,
        title is null ? null : "Artist",
        isRunning ? 101L : null,
        windowTitle ?? (title is null ? "QQ音乐" : $"{title} - Artist"),
        DateTimeOffset.UtcNow);

static QQMusicTimelineEvidence Timeline(
    string status) =>
    new(
        status,
        TimeSpan.Zero,
        TimeSpan.FromMinutes(4),
        TimeSpan.FromSeconds(30));

var freshLaunch = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, null),
    Timeline("Paused"),
    hasActiveAudioSession: false,
    sessionObservedPlaying: false);
if (freshLaunch.IsReliable
    || freshLaunch.FailureCode
        != QQMusicPlaybackAnchorPolicy.MissingFailureCode)
{
    throw new InvalidOperationException(
        "A fresh QQ launch must not treat a stale paused timeline as an anchor.");
}

var captionOnly = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    null,
    hasActiveAudioSession: true,
    sessionObservedPlaying: false);
if (captionOnly.IsReliable
    || captionOnly.FailureCode
        != QQMusicPlaybackAnchorPolicy.MissingFailureCode)
{
    throw new InvalidOperationException(
        "A parsed QQ caption alone must not establish the playback anchor.");
}

var ghostPlaying = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    Timeline("Playing"),
    hasActiveAudioSession: false,
    sessionObservedPlaying: false);
if (ghostPlaying.IsReliable
    || ghostPlaying.FailureCode
        != QQMusicPlaybackAnchorPolicy.MissingFailureCode)
{
    throw new InvalidOperationException(
        "A stale GSMTC Playing timeline without active QQ audio must be rejected.");
}

var playing = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    Timeline("Playing"),
    hasActiveAudioSession: true,
    sessionObservedPlaying: false);
if (!playing.IsReliable || !playing.ObservedPlaying)
{
    throw new InvalidOperationException(
        "A credible Playing timeline must establish the playback anchor.");
}

var pausedAfterPlaying = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    Timeline("Paused"),
    hasActiveAudioSession: false,
    sessionObservedPlaying: true);
if (!pausedAfterPlaying.IsReliable)
{
    throw new InvalidOperationException(
        "A paused QQ track must remain insertable after this session observed Playing.");
}

var disconnected = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(false, null),
    null,
    hasActiveAudioSession: false,
    sessionObservedPlaying: false);
if (disconnected.IsReliable || disconnected.FailureCode is not null)
{
    throw new InvalidOperationException(
        "A disconnected QQ process must not be reported as a missing playback anchor.");
}

if (!QQMusicAudioMuteScope.IsActiveAudioSessionState(1)
    || QQMusicAudioMuteScope.IsActiveAudioSessionState(0)
    || QQMusicAudioMuteScope.IsActiveAudioSessionState(2))
{
    throw new InvalidOperationException(
        "Windows active audio-session state mapping is incorrect.");
}

var snapshot = new PlayerSnapshot(
    true,
    "QQ 音乐",
    101,
    "22.52",
    "test",
    null,
    DateTimeOffset.UtcNow,
    PlaybackAnchorReady: true);
if (!snapshot.PlaybackAnchorReady)
{
    throw new InvalidOperationException(
        "PlayerSnapshot must expose an explicit QQ playback-anchor state.");
}

var legacySnapshot = new PlayerSnapshot(
    true,
    "legacy",
    null,
    string.Empty,
    string.Empty,
    null,
    DateTimeOffset.UtcNow);
if (legacySnapshot.PlaybackAnchorReady)
{
    throw new InvalidOperationException(
        "PlayerSnapshot anchor state must remain false for legacy adapters.");
}

var defaultEndpointVolume = new FakeAudioVolume();
var routedEndpointVolume = new FakeAudioVolume();
var secondRoutedEndpointVolume = new FakeAudioVolume(wasMuted: true);
var inactiveRoutedEndpointVolume = new FakeAudioVolume(wasMuted: true);
var endpoints = new IQQMusicAudioEndpoint[]
{
    new FakeAudioEndpoint(
        "系统默认设备",
        new IQQMusicAudioSession[]
        {
            new FakeAudioSession(
                processId: 9001,
                isActive: true,
                volume: defaultEndpointVolume)
        }),
    new FakeAudioEndpoint(
        "QQ 指定设备",
        new IQQMusicAudioSession[]
        {
            new FakeAudioSession(
                processId: 4242,
                isActive: true,
                volume: routedEndpointVolume),
            new FakeAudioSession(
                processId: 4242,
                isActive: true,
                volume: secondRoutedEndpointVolume),
            new FakeAudioSession(
                processId: 4242,
                isActive: false,
                volume: inactiveRoutedEndpointVolume),
            new FakeAudioSession(
                processId: 7777,
                isActive: true,
                volume: new FakeAudioVolume())
        }),
    new FakeAudioEndpoint(
        "暂时不可用的设备",
        Array.Empty<IQQMusicAudioSession>(),
        succeeds: false,
        error: "设备暂时不可用")
};

var routedCapture = QQMusicAudioSessionCapturePolicy.Capture(
    endpoints,
    expectedProcessId: 4242,
    isQqMusicProcess: processId => processId == 4242);
if (routedCapture.Sessions.Count != 3
    || routedCapture.ActiveSessionCount != 2
    || !routedCapture.CaptureError.Contains(
        "暂时不可用的设备",
        StringComparison.Ordinal)
    || !routedCapture.CaptureError.Contains(
        "设备暂时不可用",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "QQ audio capture must keep matching sessions from a non-default "
        + "endpoint when another endpoint fails.");
}

var processFilteredCapture = QQMusicAudioSessionCapturePolicy.Capture(
    endpoints,
    expectedProcessId: null,
    isQqMusicProcess: processId => processId == 4242);
if (processFilteredCapture.Sessions.Count != 3
    || processFilteredCapture.ActiveSessionCount != 2)
{
    throw new InvalidOperationException(
        "QQ audio capture must filter non-QQ process sessions across devices.");
}

var unavailableVolumeCapture = QQMusicAudioSessionCapturePolicy.Capture(
    new IQQMusicAudioEndpoint[]
    {
        new FakeAudioEndpoint(
            "QQ 无音量接口设备",
            new IQQMusicAudioSession[]
            {
                new FakeAudioSession(
                    processId: 4242,
                    isActive: true,
                    volume: null)
            })
    },
    expectedProcessId: 4242,
    isQqMusicProcess: processId => processId == 4242);
using (var unavailableVolumeScope =
       QQMusicAudioMuteScope.FromCaptureResult(unavailableVolumeCapture))
{
    if (unavailableVolumeCapture.ActiveSessionCount != 1
        || unavailableVolumeCapture.Sessions.Count != 0
        || !unavailableVolumeScope.HasActiveAudioSession
        || unavailableVolumeScope.CapturedSessionCount != 0
        || unavailableVolumeScope.Mute())
    {
        throw new InvalidOperationException(
            "An active QQ session without a volume interface must still count "
            + "as playback evidence but must not be muted.");
    }
}

using (var audioScope =
       QQMusicAudioMuteScope.FromCaptureResult(routedCapture))
{
    if (!audioScope.HasActiveAudioSession
        || audioScope.CapturedSessionCount != 3
        || !audioScope.Mute())
    {
        throw new InvalidOperationException(
            "QQ audio capture must expose active sessions to the mute scope.");
    }

    if (!defaultEndpointVolume.MuteCalls.SequenceEqual(
            Array.Empty<bool>())
        || !routedEndpointVolume.MuteCalls.SequenceEqual([true])
        || !secondRoutedEndpointVolume.MuteCalls.SequenceEqual([true])
        || !inactiveRoutedEndpointVolume.MuteCalls.SequenceEqual([true]))
    {
        throw new InvalidOperationException(
            "QQ mute scope selected an unexpected device or session.");
    }

    audioScope.Restore();
    if (!routedEndpointVolume.MuteCalls.SequenceEqual([true, false])
        || !secondRoutedEndpointVolume.MuteCalls.SequenceEqual([true, true])
        || !inactiveRoutedEndpointVolume.MuteCalls.SequenceEqual([true, true]))
    {
        throw new InvalidOperationException(
            "QQ mute scope must restore each session's original mute state.");
    }
}

Console.WriteLine("QQ Music metadata policy tests passed.");

sealed class FakeAudioVolume : IQQMusicAudioVolume
{
    internal FakeAudioVolume(bool wasMuted = false)
    {
        WasMuted = wasMuted;
    }

    internal bool WasMuted { get; }

    internal List<bool> MuteCalls { get; } = [];

    public int SetMute(bool mute, ref Guid eventContext)
    {
        MuteCalls.Add(mute);
        return 0;
    }
}

sealed class FakeAudioSession : IQQMusicAudioSession
{
    private readonly FakeAudioVolume? _volume;

    internal FakeAudioSession(
        uint processId,
        bool isActive,
        FakeAudioVolume? volume)
    {
        ProcessId = processId;
        IsActive = isActive;
        _volume = volume;
    }

    public uint ProcessId { get; }

    public bool IsActive { get; }

    public bool TryGetVolume(
        out IQQMusicAudioVolume volume,
        out bool wasMuted)
    {
        if (_volume is null)
        {
            volume = null!;
            wasMuted = false;
            return false;
        }

        volume = _volume;
        wasMuted = _volume.WasMuted;
        return true;
    }
}

sealed class FakeAudioEndpoint : IQQMusicAudioEndpoint
{
    private readonly IReadOnlyList<IQQMusicAudioSession> _sessions;
    private readonly bool _succeeds;
    private readonly string _error;

    internal FakeAudioEndpoint(
        string description,
        IReadOnlyList<IQQMusicAudioSession> sessions,
        bool succeeds = true,
        string error = "")
    {
        Description = description;
        _sessions = sessions;
        _succeeds = succeeds;
        _error = error;
    }

    public string Description { get; }

    public bool TryGetSessions(
        out IReadOnlyList<IQQMusicAudioSession> sessions,
        out string error)
    {
        sessions = _sessions;
        error = _error;
        return _succeeds;
    }
}
