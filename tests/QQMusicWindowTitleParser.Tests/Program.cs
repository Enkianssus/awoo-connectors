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
    sessionObservedPlaying: false);
if (captionOnly.IsReliable
    || captionOnly.FailureCode
        != QQMusicPlaybackAnchorPolicy.MissingFailureCode)
{
    throw new InvalidOperationException(
        "A parsed QQ caption alone must not establish the playback anchor.");
}

var playing = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    Timeline("Playing"),
    sessionObservedPlaying: false);
if (!playing.IsReliable || !playing.ObservedPlaying)
{
    throw new InvalidOperationException(
        "A credible Playing timeline must establish the playback anchor.");
}

var pausedAfterPlaying = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(true, "Song"),
    Timeline("Paused"),
    sessionObservedPlaying: true);
if (!pausedAfterPlaying.IsReliable)
{
    throw new InvalidOperationException(
        "A paused QQ track must remain insertable after this session observed Playing.");
}

var disconnected = QQMusicPlaybackAnchorPolicy.Evaluate(
    PlaybackState(false, null),
    null,
    sessionObservedPlaying: false);
if (disconnected.IsReliable || disconnected.FailureCode is not null)
{
    throw new InvalidOperationException(
        "A disconnected QQ process must not be reported as a missing playback anchor.");
}

Console.WriteLine("QQ Music metadata policy tests passed.");
