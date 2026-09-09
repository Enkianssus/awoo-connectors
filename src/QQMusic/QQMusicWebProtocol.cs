using System.Text.Json;
using System.Xml.Linq;

namespace QQMusicControlPoc;

internal sealed record QQMusicWebSong(uint SongId, string SongMid, int SongType, string Title,
    QQMusicWebMetadata? Metadata = null);
internal sealed record QQMusicWebMetadata(int Interval, QQMusicWebSinger[] Singers,
    QQMusicWebFile File, JsonElement? Pay = null, JsonElement? Action = null);
internal sealed record QQMusicWebSinger(int Id, string Mid, string Name, string Title);
internal sealed record QQMusicWebFile(string MediaMid, int Size128, int Size320,
    int SizeFlac, int SizeOgg, int SizeApe,
    int? SizeTry = null, int? TryBegin = null, int? TryEnd = null);

internal enum QQMusicWebIntent
{
    // Keep value 1. The old value 0 requested append-and-play and must fail closed.
    InsertNext = 1
}

internal sealed record QQMusicWebPlayRequest(
    Guid OperationId, int ProcessId, long ProcessStartTimeUtcTicks,
    string ExecutablePath, string ApiDllPath, string ApiSha256, QQMusicWebSong Song,
    QQMusicWebIntent Intent = QQMusicWebIntent.InsertNext);

internal sealed record QQMusicWebBridgeEvent(
    Guid OperationId, string Stage, string Code, int? HResult = null);

/// <summary>
/// Insert-only single-song request from the official wkframe desktop XML template.
/// Immediate playback is a separately guarded InsertNext then native Next workflow;
/// no append-and-play or replacement XML intent is accepted by this protocol.
/// No native addresses, queue positions, or guessed search-type conversion.
/// </summary>
internal static class QQMusicWebProtocol
{
    // Compatibility name for existing song-validation callers. It now builds only
    // the same insertion request; it cannot produce the retired playback branch.
    internal static string BuildPlayRequest(QQMusicWebSong song) => BuildInsertNextRequest(song);

    internal static string BuildInsertNextRequest(QQMusicWebSong song) =>
        BuildRequest(song, QQMusicWebIntent.InsertNext);

    internal static bool IsDispatchIntentAllowed(QQMusicWebIntent intent) => intent == QQMusicWebIntent.InsertNext;

    internal static string BuildRequest(QQMusicWebSong song, QQMusicWebIntent intent)
    {
        if (!IsDispatchIntentAllowed(intent))
            throw new ArgumentException("web-intent-invalid", nameof(intent));
        ArgumentNullException.ThrowIfNull(song);
        if (song.SongId == 0 || string.IsNullOrWhiteSpace(song.SongMid) ||
            song.SongMid == "0" || song.SongMid.Length > 128 ||
            !song.SongMid.All(char.IsAsciiLetterOrDigit) ||
            song.SongType is < 0 or > 255 || string.IsNullOrWhiteSpace(song.Title) ||
            song.Title.Length > 1024 || song.Title.Any(char.IsControl))
            throw new ArgumentException("song-invalid", nameof(song));

        // The parser treats name (public metadata) and title (native title) as
        // distinct fields. ID/MID alone also leave its media-format fields empty.
        // Only real catalog metadata may supply these values; never invent sizes
        // or infer a file MID from a song MID.
        var metadata = song.Metadata ?? throw new ArgumentException("song-metadata-missing", nameof(song));
        var file = metadata.File;
        if (metadata.Interval is <= 0 or > 86400 || metadata.Singers is not { Length: > 0 and <= 32 } ||
            file is null || !ValidMid(file.MediaMid) ||
            new[] { file.Size128, file.Size320, file.SizeFlac, file.SizeOgg, file.SizeApe }.Any(x => x < 0) ||
            file.SizeTry is < 0 || file.TryBegin is < 0 || file.TryEnd is < 0 ||
            (file.TryBegin is { } begin && file.TryEnd is { } end && end < begin) ||
            !(file.Size128 > 0 || file.Size320 > 0 || file.SizeFlac > 0 || file.SizeOgg > 0 || file.SizeApe > 0) ||
            metadata.Singers.Any(x => x is null || x.Id < 0 ||
                (x.Mid != string.Empty && !ValidMid(x.Mid)) || !ValidText(x.Name) || !ValidText(x.Title)))
            throw new ArgumentException("song-metadata-invalid", nameof(song));

        var fileFields = new Dictionary<string, object>
        {
            ["media_mid"] = file.MediaMid, ["size_128"] = file.Size128,
            ["size_320"] = file.Size320, ["size_flac"] = file.SizeFlac,
            ["size_ogg"] = file.SizeOgg, ["size_ape"] = file.SizeApe
        };
        // Optional server fields retain their exact integers and presence. Their
        // units and account-entitlement meaning are not inferred here.
        if (file.SizeTry is { } sizeTry) fileFields["size_try"] = sizeTry;
        if (file.TryBegin is { } tryBegin) fileFields["try_begin"] = tryBegin;
        if (file.TryEnd is { } tryEnd) fileFields["try_end"] = tryEnd;
        var item = new Dictionary<string, object?>
        {
            ["id"] = song.SongId, ["type"] = song.SongType, ["mid"] = song.SongMid,
            ["name"] = song.Title, ["title"] = song.Title, ["interval"] = metadata.Interval,
            ["singer"] = metadata.Singers.Select(x => new { id = x.Id, mid = x.Mid, name = x.Name, title = x.Title }).ToArray(),
            ["file"] = fileFields
        };
        // Copy only known, numeric server fields. Missing rights remain missing;
        // neither a successful search nor these flags prove account entitlement.
        if (metadata.Pay is { } pay)
            item["pay"] = NumericFields(pay, false, "pay_month", "price_track", "price_album",
                "pay_play", "pay_down", "pay_status", "time_free");
        if (metadata.Action is { } action)
            item["action"] = NumericFields(action, true, "switch", "switch2", "icon2");

        // Official music_56b304d.js S.new_play / player.play callers:
        // Next: isOnlyAdd=1, bsingle=0, playsong=1,
        // insertbefore=-1. Preserve spelling and XML hierarchy, including verson.
        // qq uin=0 is the official fixed placeholder, never a user account ID.
        // The separate 1008 window-display command is intentionally omitted.
        return new XElement("command-lable-xwl78-qq-music",
            new XElement("cmd", new XAttribute("value", "1002"), new XAttribute("verson", "3"),
                new XElement("qq", new XAttribute("uin", "0")),
                new XElement("playindex", "0"),
                new XElement("clearlast", "0"),
                new XElement("listname", string.Empty),
                new XElement("listkey", string.Empty),
                new XElement("bsingle", "0"),
                new XElement("webview", "3"),
                new XElement("targeturl", string.Empty),
                new XElement("music", new XElement("songinfo", JsonSerializer.Serialize(new[] { item }))),
                new XElement("adddepottag", new XAttribute("depot", "0")),
                new XElement("addplaylisttag", new XAttribute("playlist", "1")),
                new XElement("cat", "-1"),
                new XElement("insertbefore", "-1")))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static bool ValidMid(string? value) => !string.IsNullOrWhiteSpace(value) && value != "0" &&
        value.Length <= 128 && value.All(char.IsAsciiLetterOrDigit);
    private static bool ValidText(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 1024 && !value.Any(char.IsControl);

    private static Dictionary<string, ulong> NumericFields(JsonElement source, bool action, params string[] allowed)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("song-rights-invalid");
        var result = new Dictionary<string, ulong>();
        foreach (var property in source.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) ||
                property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetUInt64(out var value) ||
                (!action && value > int.MaxValue) || (action && property.Name == "switch" && value > uint.MaxValue) ||
                !result.TryAdd(property.Name, value))
                throw new ArgumentException("song-rights-invalid");
        }
        return result;
    }
}
