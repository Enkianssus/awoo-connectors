using System.Text.Json;
using System.Xml.Linq;
using QQMusicControlPoc;

ProtocolTests.Run();
CatalogMetadataTests.Run();
EndPolicyTests.Run();
GuardPolicyTests.Run();
SubmissionPolicyTests.Run();
SongDetailsTests.Run();
ReceiptTests.Run();
DrainPolicyTests.Run();
var hostSourceChecks = HostPolicyTests.Run();
Console.WriteLine($"PASS: {Check.Count} QQ Web protocol/receipt/host-policy assertions "
    + $"({hostSourceChecks} host/packaging source checks; no native execution).");

internal static class ProtocolTests
{
    private static readonly QQMusicWebSong Valid =
        new(123, "AbC0123456789", 0, "A title", new(249,
            [new(42, "SingerMid", "Artist", "Artist")],
            new("DistinctFileMid", 3990150, 9974908, 29663797, 5799774, 0)));

    public static void Run()
    {
        CheckRequest(Valid);
        CheckRequest(Valid with { SongId = uint.MaxValue });
        CheckRequest(Valid with { SongId = (uint)int.MaxValue + 1 });
        CheckRequest(Valid with { SongId = 1 });
        CheckRequest(Valid with
        {
            Title = "中文 🎵 / \"quoted\" \\ path <tag>& 'apostrophe' \u2028 separator"
        });
        CheckRequest(Valid with { Title = new string('曲', 1024) });
        CheckRequest(Valid with { SongMid = new string('Z', 128) });
        CheckRequest(Valid with { Title = "]]></songinfo></music></cmd><cmd value=\"1008\"><isshow>1</isshow></cmd><!-- 中文" });
        Check.Equal(1, Enum.GetValues<QQMusicWebIntent>().Length, "only one production dispatch intent exists");
        Check.Equal(1, (int)QQMusicWebIntent.InsertNext, "insert wire value remains 1, not retired value 0");
        Check.Equal(QQMusicWebProtocol.BuildRequest(Valid, QQMusicWebIntent.InsertNext),
            QQMusicWebProtocol.BuildPlayRequest(Valid), "legacy validation name can only build insertion XML");
        Check.Equal(QQMusicWebProtocol.BuildRequest(Valid, QQMusicWebIntent.InsertNext),
            QQMusicWebProtocol.BuildInsertNextRequest(Valid), "next convenience method uses explicit next intent");
        Check.True(QQMusicWebProtocol.IsDispatchIntentAllowed(QQMusicWebIntent.InsertNext), "insert is explicitly allowed");
        foreach (var invalidIntent in new[] { 0, -1, 2, int.MinValue, int.MaxValue })
        {
            Check.True(!QQMusicWebProtocol.IsDispatchIntentAllowed((QQMusicWebIntent)invalidIntent),
                "host dispatch policy rejects retired zero and every unknown intent");
            Check.Rejects(() => QQMusicWebProtocol.BuildRequest(Valid, (QQMusicWebIntent)invalidIntent),
                "retired playback or undefined intent is rejected before command construction");
        }
        var defaultRequest = new QQMusicWebPlayRequest(Guid.NewGuid(), 123, 456, "exe", "api", "hash", Valid);
        Check.Equal(QQMusicWebIntent.InsertNext, defaultRequest.Intent, "wire DTO defaults only to insertion");
        var retiredRequest = JsonSerializer.Deserialize<QQMusicWebPlayRequest>(
            JsonSerializer.Serialize(defaultRequest with { Intent = (QQMusicWebIntent)0 }))!;
        Check.True(!QQMusicWebProtocol.IsDispatchIntentAllowed(retiredRequest.Intent),
            "numeric zero in an old serialized request cannot acquire dispatch permission");
        Check.Rejects(() => QQMusicWebProtocol.BuildRequest(retiredRequest.Song, retiredRequest.Intent),
            "old numeric-zero helper request cannot construct replacement XML");
        foreach (var intent in Enum.GetValues<QQMusicWebIntent>())
        {
            var roundtrip = JsonSerializer.Deserialize<QQMusicWebPlayRequest>(
                JsonSerializer.Serialize(defaultRequest with { Intent = intent }))!;
            Check.Equal(intent, roundtrip.Intent, "typed intent survives helper wire serialization");
            Check.Equal(QQMusicWebProtocol.BuildRequest(Valid, intent),
                QQMusicWebProtocol.BuildRequest(roundtrip.Song, roundtrip.Intent), "helper reconstructs identical XML intent");
        }
        var legacyWire = JsonSerializer.Serialize(defaultRequest);
        using (var legacyDocument = JsonDocument.Parse(legacyWire))
        {
            var fields = legacyDocument.RootElement.EnumerateObject().Where(p => p.Name != "Intent")
                .ToDictionary(p => p.Name, p => p.Value);
            var missingIntent = JsonSerializer.Deserialize<QQMusicWebPlayRequest>(JsonSerializer.Serialize(fields))!;
            Check.Equal(QQMusicWebIntent.InsertNext, missingIntent.Intent,
                "old request without intent defaults only to safe insertion, never replacement");
            Check.Equal(QQMusicWebProtocol.BuildInsertNextRequest(Valid),
                QQMusicWebProtocol.BuildRequest(missingIntent.Song, missingIntent.Intent),
                "missing-intent wire request emits exactly the fixed insertion shape");
        }

        foreach (var type in new[] { 0, 1, 3, 111, 112, 113, 255 })
        {
            CheckRequest(Valid with { SongType = type });
        }

        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(null!), "null song");
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with { Metadata = null }), "missing metadata rejected before dispatch");
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with
        { Metadata = Valid.Metadata! with { File = Valid.Metadata!.File with { Size128 = -1 } } }), "negative size rejected");
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with
        { Metadata = Valid.Metadata! with { File = new("FileMid", 0, 0, 0, 0, 0) } }), "no real media format rejected");
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with
        { Metadata = Valid.Metadata! with { Singers = [] } }), "missing singer rejected");
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with
        { Metadata = Valid.Metadata! with { Interval = 0 } }), "unknown duration rejected");
        using (var rights = JsonDocument.Parse("{\"switch\":4294967295,\"switch2\":18446744073709551615}"))
        {
            using var result = ExtractSongs(QQMusicWebProtocol.BuildPlayRequest(Valid with
            { Metadata = Valid.Metadata! with { Action = rights.RootElement } }));
            Check.Equal(ulong.MaxValue, result.RootElement[0]
                .GetProperty("action").GetProperty("switch2").GetUInt64(), "server UInt64 action preserved");
        }
        foreach (var invalid in new[] { "{\"cmd\":1}", "{\"switch\":-1}", "{\"switch\":4294967296}", "{\"switch\":\"1\"}", "{\"switch\":1,\"switch\":2}" })
        {
            using var rights = JsonDocument.Parse(invalid);
            Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(Valid with
            { Metadata = Valid.Metadata! with { Action = rights.RootElement } }), "invalid action whitelist");
        }
        Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(
            Valid with { SongId = 0 }), "zero id");
        foreach (var mid in new[]
        {
            null, "", " ", "0", "bad-mid", "bad_mid", "bad.mid", "bad/mid",
            "has space", " leading", "trailing ", "é", "Ａ", "中", "a\nb",
            new string('a', 129)
        })
        {
            Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(
                Valid with { SongMid = mid! }), $"invalid mid: {JsonSerializer.Serialize(mid)}");
        }

        foreach (var type in new[] { int.MinValue, -1, 256, int.MaxValue })
        {
            Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(
                Valid with { SongType = type }), $"invalid song type {type}");
        }

        foreach (var title in new[]
        {
            null, "", "   ", new string('a', 1025), "zero\0byte", "control\u0001",
            "tab\there", "line\nfeed", "carriage\rreturn", "delete\u007f", "next\u0085line"
        })
        {
            Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(
                Valid with { Title = title! }), $"invalid title: {JsonSerializer.Serialize(title)}");
        }
    }

    private static void CheckRequest(QQMusicWebSong song)
    {
        // Verify both maintained builder entry points, including the historical
        // name used by validation callers. Neither may recreate playback XML.
        CheckRequest(song, QQMusicWebProtocol.BuildInsertNextRequest(song));
        CheckRequest(song, QQMusicWebProtocol.BuildPlayRequest(song));
    }

    internal static JsonDocument ExtractSongs(string request) => JsonDocument.Parse(
        XElement.Parse(request).Element("cmd")!.Element("music")!.Element("songinfo")!.Value);

    private static void CheckRequest(QQMusicWebSong song, string xml)
    {
        var root = XElement.Parse(xml);
        Check.Equal(XName.Get("command-lable-xwl78-qq-music"), root.Name, "official XML envelope spelling");
        Check.Equal(0, root.Attributes().Count(), "no generated envelope attributes");
        Check.Equal(1, root.Elements().Count(), "exactly one media command; no 1008 window command");
        var command = root.Elements().Single();
        Check.Equal(XName.Get("cmd"), command.Name, "exact command element");
        Check.Equal("value=1002|verson=3", string.Join("|", command.Attributes().Select(a => $"{a.Name}={a.Value}")),
            "official command attributes and version spelling");
        Check.Equal("qq|playindex|clearlast|listname|listkey|bsingle|webview|targeturl|music|adddepottag|addplaylisttag|cat|insertbefore",
            string.Join("|", command.Elements().Select(e => e.Name.ToString())), "exact official field hierarchy and order");
        Check.Equal("0", command.Element("playindex")!.Value, "single-song play index");
        Check.Equal("0", command.Element("clearlast")!.Value, "no production builder requests clearLast=1");
        Check.Equal("", command.Element("listname")!.Value, "no source playlist name");
        Check.Equal("", command.Element("listkey")!.Value, "no source playlist key");
        Check.Equal("", command.Element("targeturl")!.Value, "no navigation target");
        Check.Equal("0", command.Element("bsingle")!.Value, "insertion never sets immediate-play bsingle");
        Check.Equal("3", command.Element("webview")!.Value, "official modern songinfo version");
        Check.Equal("-1", command.Element("cat")!.Value, "official current category selector");
        Check.Equal("-1", command.Element("insertbefore")!.Value, "only native-next insertion position sentinel");
        Check.Equal("uin=0", string.Join("|", command.Element("qq")!.Attributes().Select(a => $"{a.Name}={a.Value}")),
            "official fixed qq placeholder, not user credentials");
        Check.Equal("depot=0", string.Join("|", command.Element("adddepottag")!.Attributes().Select(a => $"{a.Name}={a.Value}")),
            "official depot tag attribute");
        Check.Equal("playlist=1",
            string.Join("|", command.Element("addplaylisttag")!.Attributes().Select(a => $"{a.Name}={a.Value}")),
            "playlist is always insert-only, never the playback discriminator");
        Check.Equal("songinfo", string.Join("|", command.Element("music")!.Elements().Select(e => e.Name.ToString())),
            "music contains only songinfo");
        Check.Equal(0, command.Element("music")!.Element("songinfo")!.Elements().Count(),
            "JSON is escaped XML text, never nested XML commands");
        using var parsed = ExtractSongs(xml);
        var list = parsed.RootElement;
        Check.Equal(JsonValueKind.Array, list.ValueKind, "songList array");
        Check.Equal(1, list.GetArrayLength(), "only one selected song");
        var item = list[0];
        Check.Keys(item, "id", "type", "mid", "name", "title", "interval", "singer", "file");
        Check.Equal(JsonValueKind.Number, item.GetProperty("id").ValueKind, "id is numeric");
        Check.Equal(song.SongId, item.GetProperty("id").GetUInt32(), "unsigned id roundtrip");
        Check.Equal(song.SongType, item.GetProperty("type").GetInt32(), "type must not normalize");
        Check.Equal(song.SongMid, item.GetProperty("mid").GetString(), "mid exact roundtrip");
        Check.Equal(song.Title, item.GetProperty("name").GetString(), "title exact roundtrip");
        Check.Equal(song.Title, item.GetProperty("title").GetString(), "native title is independently populated");
        Check.Equal(song.Metadata!.Interval, item.GetProperty("interval").GetInt32(), "real duration");
        Check.Equal(song.Metadata.Singers[0].Title, item.GetProperty("singer")[0].GetProperty("title").GetString(), "native singer title");
        var file = item.GetProperty("file");
        Check.Keys(file, "media_mid", "size_128", "size_320", "size_flac", "size_ogg", "size_ape");
        Check.Equal(song.Metadata.File.MediaMid, file.GetProperty("media_mid").GetString(), "file mid is not substituted with song mid");
        Check.Equal(song.Metadata.File.Size128, file.GetProperty("size_128").GetInt32(), "real signed Int32 size");
        Check.Equal(song.Metadata.File.SizeApe, file.GetProperty("size_ape").GetInt32(), "absent format stays zero");
    }
}

internal static class Check
{
    public static int Count { get; private set; }

    public static void Equal<T>(T expected, T actual, string label)
    {
        Count++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
        }
    }

    public static void True(bool condition, string label)
    {
        Count++;
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
    }

    public static void Keys(JsonElement value, params string[] expected)
    {
        Equal(JsonValueKind.Object, value.ValueKind, "object shape");
        var actual = value.EnumerateObject().Select(x => x.Name).Order().ToArray();
        Equal(string.Join("|", expected.Order()), string.Join("|", actual),
            "exact property set, including absence of queue/clear/retry fields");
    }

    public static void Rejects(Action operation, string label)
    {
        Count++;
        try
        {
            operation();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected argument rejection: {label}");
    }
}
