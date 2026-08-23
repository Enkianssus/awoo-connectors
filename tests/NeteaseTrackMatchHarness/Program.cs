using System.Text.Json;
using UnifiedPlayerControlPoc;

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"{label}: expected true");
    }
}

static void False(bool value, string label)
{
    if (value)
    {
        throw new InvalidOperationException($"{label}: expected false");
    }
}

static NeteaseSearchResponseAnalysis AnalyzeSearchResponse(string json)
{
    using var document = JsonDocument.Parse(json);
    return NeteaseSearchResponsePolicy.Analyze(document.RootElement);
}

True(
    NeteaseTrackMatchPolicy.TracksMatch(
        new PlayerTrack(
            "Hachiko|Artist",
            "Hachiko\u0304 - Live",
            "Artist",
            ""),
        new PlayerTrack(
            "123",
            "Hachikō – Live",
            "Artist",
            "")),
    "NFC/NFD title and Unicode dash match");

True(
    NeteaseTrackMatchPolicy.TracksMatch(
        new PlayerTrack(
            "",
            "Café",
            "Zoë",
            ""),
        new PlayerTrack(
            "456",
            "Cafe\u0301",
            "Zoe\u0308",
            "")),
    "canonically equivalent title and artist fallback match");

False(
    NeteaseTrackMatchPolicy.TracksMatch(
        new PlayerTrack(
            "",
            "Hachiko",
            "Artist",
            ""),
        new PlayerTrack(
            "456",
            "Hachikō",
            "Artist",
            "")),
    "missing macron remains a different title");

False(
    NeteaseTrackMatchPolicy.TracksMatch(
        new PlayerTrack(
            "111",
            "Hachiko",
            "Artist",
            ""),
        new PlayerTrack(
            "222",
            "Hachikō",
            "Artist",
            "")),
    "different stable IDs remain authoritative");

False(
    NeteaseTrackMatchPolicy.TracksMatch(
        new PlayerTrack(
            "Hachiko|Other Artist",
            "Hachiko",
            "Other Artist",
            ""),
        new PlayerTrack(
            "333",
            "Hachikō",
            "Artist",
            "")),
    "different artists do not match by title alone");

var rateLimited = AnalyzeSearchResponse(
    "{\"code\":405,\"message\":\"操作频繁，请稍候再试\"}");
True(
    rateLimited.Kind == NeteaseSearchResponseKind.Retryable
        && rateLimited.Message.Contains("405", StringComparison.Ordinal)
        && rateLimited.Message.Contains("操作频繁", StringComparison.Ordinal),
    "rate-limited response is retryable");

var stringResult = AnalyzeSearchResponse(
    "{\"code\":200,\"result\":\"操作频繁\"}");
True(
    stringResult.Kind == NeteaseSearchResponseKind.Retryable
        && stringResult.Message.Contains("操作频繁", StringComparison.Ordinal),
    "string result is retryable");

var emptyResult = AnalyzeSearchResponse(
    "{\"code\":200,\"result\":{\"songs\":[]}}");
True(
    emptyResult.Kind == NeteaseSearchResponseKind.Empty
        && emptyResult.Songs.Count == 0,
    "empty songs is a real empty result");

var oldSongResult = AnalyzeSearchResponse(
    "{\"code\":200,\"result\":{\"songs\":[{\"id\":2715083626,\"name\":\"Hachikō\",\"artists\":[{\"name\":\"藤井風\"}],\"album\":{\"name\":\"Album\"}}]}}");
True(
    oldSongResult.Kind == NeteaseSearchResponseKind.Results,
    "old song shape is a result");
var oldSong = NeteaseSearchResponsePolicy.ParseTrack(oldSongResult.Songs[0]);
True(
    oldSong.Id == "2715083626"
        && oldSong.Title == "Hachikō"
        && oldSong.Artist == "藤井風"
        && oldSong.Album == "Album",
    "old song shape metadata is parsed");

var newSongResult = AnalyzeSearchResponse(
    "{\"code\":200,\"result\":{\"songs\":[{\"id\":2715083626,\"name\":\"Hachikō\",\"ar\":[{\"name\":\"藤井風\"}],\"al\":{\"name\":\"Album\"}}]}}");
True(
    newSongResult.Kind == NeteaseSearchResponseKind.Results,
    "new song shape is a result");
var newSong = NeteaseSearchResponsePolicy.ParseTrack(newSongResult.Songs[0]);
True(
    newSong.Id == "2715083626"
        && newSong.Title == "Hachikō"
        && newSong.Artist == "藤井風"
        && newSong.Album == "Album",
    "new song shape metadata is parsed");

var malformedSongs = AnalyzeSearchResponse(
    "{\"code\":200,\"result\":{\"songs\":\"bad\"}}");
True(
    malformedSongs.Kind == NeteaseSearchResponseKind.Retryable,
    "malformed songs shape is retryable");

Console.WriteLine("NetEase track match harness passed.");
