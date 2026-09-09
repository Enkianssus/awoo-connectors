using System.Text.Json;
using System.Text.Json.Nodes;
using QQMusicControlPoc;

internal static class CatalogMetadataTests
{
    // Synthetic JSON only; no catalog client, network, COM or player is used.
    private const string Legacy = """
        {"type":0,"interval":247,"songmid":"SongMid001","strMediaMid":"MediaMid002",
         "size128":100,"size320":200,"sizeflac":300,"sizeogg":0,"sizeape":0,
         "singer":[{"id":7,"mid":"SingerMid003","name":"Example artist"}],
         "switch":77623,"pay":{"payalbum":1,"paydownload":1,"payplay":1}}
        """;
    private const string Modern = """
        {"type":0,"interval":247,"mid":"SongMid001",
         "file":{"media_mid":"MediaMid002","size_128":100,"size_320":200,
                 "size_flac":300,"size_ogg":0,"size_ape":0},
         "singer":[{"id":7,"mid":"SingerMid003","name":"Example artist","title":"Artist title"}],
         "pay":{"pay_month":1,"price_track":300,"price_album":0,"pay_play":1,
                "pay_down":0,"pay_status":0,"time_free":0,"unmapped":42},
         "action":{"switch":4294967295,"switch2":18446744073709551615,
                   "icon2":9223372036854775808,"unmapped":42}}
        """;

    public static void Run()
    {
        RunSongTypeTests();
        RunOptionalFileTests();
        var legacy = Parse(Legacy, true)!;
        Check.True(legacy is not null, "complete legacy metadata is accepted");
        Check.Equal(247, legacy!.Interval, "actual interval is retained");
        Check.Equal("MediaMid002", legacy.File.MediaMid, "file MID is not inferred from song MID");
        Check.Equal(100, legacy.File.Size128, "legacy size128");
        Check.Equal(200, legacy.File.Size320, "legacy size320");
        Check.Equal(300, legacy.File.SizeFlac, "legacy sizeflac");
        Check.Equal(0, legacy.File.SizeOgg, "explicit zero sizeogg is retained");
        Check.Equal(0, legacy.File.SizeApe, "explicit zero sizeape is retained");
        Check.Equal(new QQMusicWebSinger(7, "SingerMid003", "Example artist", "Example artist"),
            legacy.Singers.Single(), "legacy singer title copies its real name");
        Check.True(legacy.Pay is null, "legacy pay keys are never guessed or mapped to native pay fields");
        Check.Keys(legacy.Action!.Value, "switch");
        Check.Equal(77623u, legacy.Action.Value.GetProperty("switch").GetUInt32(), "legacy switch is preserved");

        var modern = Parse(Modern, false)!;
        Check.True(modern is not null, "complete modern metadata is accepted");
        Check.Equal("Artist title", modern!.Singers.Single().Title, "modern singer title is retained");
        Check.Keys(modern.Pay!.Value, "pay_month", "price_track", "price_album", "pay_play", "pay_down", "pay_status", "time_free");
        Check.Equal(1, modern.Pay.Value.GetProperty("pay_play").GetInt32(), "paid playback flag is not zeroed");
        Check.Equal(300, modern.Pay.Value.GetProperty("price_track").GetInt32(), "server price is preserved");
        Check.Keys(modern.Action!.Value, "switch", "switch2", "icon2");
        Check.Equal(uint.MaxValue, modern.Action.Value.GetProperty("switch").GetUInt32(), "switch supports UInt32");
        Check.Equal(ulong.MaxValue, modern.Action.Value.GetProperty("switch2").GetUInt64(), "switch2 supports UInt64");
        Check.Equal(9223372036854775808ul, modern.Action.Value.GetProperty("icon2").GetUInt64(), "icon2 is not truncated to Int64");
        // Parse disposes the source JsonDocument before returning. These values
        // must remain valid and serializable in a later helper request.
        using var payload = BuildMetadataRequest(modern);
        var serialized = payload.RootElement[0];
        Check.Equal("MediaMid002", serialized.GetProperty("file").GetProperty("media_mid").GetString(),
            "projected file metadata survives document disposal and protocol serialization");
        Check.Equal(ulong.MaxValue, serialized.GetProperty("action").GetProperty("switch2").GetUInt64(),
            "protocol preserves the wide action value from catalog projection");

        var alias = JsonNode.Parse(Modern)!.AsObject();
        var aliasFile = alias["file"]!.AsObject();
        Rename(aliasFile, "size_128", "size_128mp3");
        Rename(aliasFile, "size_320", "size_320mp3");
        Rename(aliasFile, "size_ogg", "size_192ogg");
        alias["singer"]![0]!.AsObject().Remove("title");
        var aliasResult = Parse(alias.ToJsonString(), false)!;
        Check.Equal(modern.File, aliasResult.File, "modern size aliases retain their actual values");
        Check.Equal("Example artist", aliasResult.Singers.Single().Title, "missing modern singer title uses real name");
        var legacyAlias = JsonNode.Parse(Legacy)!.AsObject();
        Rename(legacyAlias, "strMediaMid", "media_mid");
        Check.Equal(legacy.File, Parse(legacyAlias.ToJsonString(), true)!.File, "legacy media_mid alias is accepted");

        foreach (var raw in new[] { "null", "[]", "0", "\"not object\"", "{}" })
            foreach (var isLegacy in new[] { false, true })
                Check.True(Parse(raw, isLegacy) is null, "non-catalog shapes cannot become Web metadata");
        foreach (var isLegacy in new[] { false, true })
        {
            foreach (var value in new JsonNode?[] { null, JsonValue.Create(-1), JsonValue.Create(0),
                JsonValue.Create(86401), JsonValue.Create(2147483648L), JsonValue.Create(1.5), JsonValue.Create("247") })
                RejectMutation(isLegacy, item => item["interval"] = value?.DeepClone(), "invalid interval rejects metadata");
            RejectMutation(isLegacy, item => item.Remove("interval"), "missing interval rejects metadata");
            RejectMutation(isLegacy, item => item.Remove("singer"), "missing singer rejects metadata");
            RejectMutation(isLegacy, item => item["singer"] = new JsonArray(), "empty singer array rejects metadata");
            RejectMutation(isLegacy, item => item["singer"]![0]!["id"] = -1, "negative singer ID rejects metadata");
            RejectMutation(isLegacy, item => item["singer"]![0]!["name"] = "bad\nname", "control character singer rejects metadata");

            var sizes = isLegacy
                ? new[] { "size128", "size320", "sizeflac", "sizeogg", "sizeape" }
                : new[] { "size_128", "size_320", "size_flac", "size_ogg", "size_ape" };
            foreach (var size in sizes)
            {
                RejectMutation(isLegacy, item => File(item, isLegacy).Remove(size), "a missing size is never fabricated as zero");
                foreach (var value in new JsonNode?[] { null, JsonValue.Create(-1), JsonValue.Create(2147483648L),
                    JsonValue.Create(1.5), JsonValue.Create("100") })
                    RejectMutation(isLegacy, item => File(item, isLegacy)[size] = value?.DeepClone(), "invalid size rejects metadata");
            }
            RejectMutation(isLegacy, item => { foreach (var size in sizes) File(item, isLegacy)[size] = 0; },
                "all-zero sizes reject an unusable file");
            var mediaKey = isLegacy ? "strMediaMid" : "media_mid";
            RejectMutation(isLegacy, item => File(item, isLegacy).Remove(mediaKey), "song MID cannot substitute for missing file MID");
            foreach (var value in new[] { "", "0", "bad-mid", "路径", new string('m', 129) })
                RejectMutation(isLegacy, item => File(item, isLegacy)[mediaKey] = value, "invalid file MID rejects metadata");
        }
        RejectMutation(false, item => item["file"] = "bad file", "file must be an object");
        RejectMutation(false, item => item["file"]!["size_128mp3"] = 999, "conflicting size aliases are not silently selected");
        RejectMutation(true, item => item["media_mid"] = "DifferentMid", "conflicting media aliases reject metadata");
        RejectMutation(false, item => item["singer"]![0]!["title"] = null, "present invalid title is not silently replaced");
        foreach (var key in new[] { "pay", "action" })
            RejectMutation(false, item => item[key] = new JsonArray(), "rights must be genuine objects");
        RejectMutation(false, item => item["pay"]!["pay_play"] = -1, "negative pay value cannot imply free playback");
        RejectMutation(false, item => item["pay"]!["price_track"] = 2147483648L, "pay fields cannot exceed nonnegative Int32");
        RejectMutation(false, item => item["action"]!["switch"] = 4294967296ul, "switch cannot exceed UInt32");
        RejectMutation(false, item => item["action"]!["switch2"] = -1, "switch2 cannot be negative");
        RejectMutation(true, item => item["switch"] = -1, "legacy switch is not coerced from signed to unsigned");
        Check.True(Parse(Modern.Replace("\"pay_month\":1", "\"pay_month\":1,\"pay_month\":0"), false) is null,
            "duplicate known permission fields reject metadata rather than overwrite their value");
        var noRights = JsonNode.Parse(Modern)!.AsObject();
        noRights.Remove("pay");
        noRights.Remove("action");
        var withoutRights = Parse(noRights.ToJsonString(), false)!;
        Check.True(withoutRights.Pay is null && withoutRights.Action is null, "absent rights stay absent, not fabricated free flags");
    }

    private static void RunOptionalFileTests()
    {
        foreach (var legacy in new[] { false, true })
        {
            var baseline = Parse(legacy ? Legacy : Modern, legacy)!;
            Check.True(baseline.File.SizeTry is null && baseline.File.TryBegin is null && baseline.File.TryEnd is null,
                "missing optional file values remain missing");
            using var originalRequest = BuildMetadataRequest(baseline);
            var originalItem = originalRequest.RootElement[0];
            Check.Keys(originalItem.GetProperty("file"), "media_mid", "size_128", "size_320", "size_flac", "size_ogg", "size_ape");
            foreach (var values in new[] { (0, 0, 0), (960887, 125976, 177038), (int.MaxValue, int.MaxValue, int.MaxValue) })
            {
                var item = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
                var sourceFile = File(item, legacy);
                sourceFile["size_try"] = values.Item1;
                sourceFile["try_begin"] = values.Item2;
                sourceFile["try_end"] = values.Item3;
                var metadata = Parse(item.ToJsonString(), legacy)!;
                Check.True(metadata is not null, "real nonnegative optional fields are accepted");
                Check.Equal((int?)values.Item1, metadata!.File.SizeTry, "size_try is retained");
                Check.Equal((int?)values.Item2, metadata.File.TryBegin, "try_begin has no unit conversion");
                Check.Equal((int?)values.Item3, metadata.File.TryEnd, "try_end has no unit conversion");
                Check.Equal(baseline.File, metadata.File with { SizeTry = null, TryBegin = null, TryEnd = null },
                    "optional fields do not change MID or existing five format sizes");
                using var request = BuildMetadataRequest(metadata);
                var outputItem = request.RootElement[0];
                var outputFile = outputItem.GetProperty("file");
                Check.Keys(outputFile, "media_mid", "size_128", "size_320", "size_flac", "size_ogg", "size_ape", "size_try", "try_begin", "try_end");
                Check.Equal(values.Item1, outputFile.GetProperty("size_try").GetInt32(), "wire size_try preserves explicit zero/positive/max");
                Check.Equal(values.Item2, outputFile.GetProperty("try_begin").GetInt32(), "wire try_begin preserves raw value");
                Check.Equal(values.Item3, outputFile.GetProperty("try_end").GetInt32(), "wire try_end preserves raw value");
                Check.Equal(originalItem.GetProperty("action").GetRawText(), outputItem.GetProperty("action").GetRawText(),
                    "optional file fields cannot change switch/switch2/icon2");
                if (!legacy) Check.Equal(originalItem.GetProperty("pay").GetRawText(), outputItem.GetProperty("pay").GetRawText(),
                    "optional file fields cannot change pay fields");
            }
            foreach (var field in new[] { "size_try", "try_begin", "try_end" })
            {
                var partial = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
                File(partial, legacy)[field] = 0;
                using var request = BuildMetadataRequest(Parse(partial.ToJsonString(), legacy)!);
                var file = request.RootElement[0].GetProperty("file");
                Check.Keys(file, "media_mid", "size_128", "size_320", "size_flac", "size_ogg", "size_ape", field);
                Check.Equal(0, file.GetProperty(field).GetInt32(), "single explicit optional zero is not omitted");
                Check.True(Parse(partial.ToJsonString().Replace($"\"{field}\":0", $"\"{field}\":0,\"{field}\":0"), legacy) is null,
                    "duplicate optional file fields reject projection even when equal");
                foreach (var invalid in new[] { "null", "\"0\"", "true", "[]", "{}", "-1", "2147483648", "1.25", "1e100" })
                {
                    File(partial, legacy)[field] = JsonNode.Parse(invalid);
                    Check.True(Parse(partial.ToJsonString(), legacy) is null, "invalid present optional field is not dropped or changed to zero");
                }
            }
            RejectMutation(legacy, item => { File(item, legacy)["try_begin"] = 2; File(item, legacy)["try_end"] = 1; },
                "present try_end cannot precede present try_begin");
            foreach (var badFile in new[] { baseline.File with { SizeTry = -1 }, baseline.File with { TryBegin = -1 },
                baseline.File with { TryEnd = -1 }, baseline.File with { TryBegin = 2, TryEnd = 1 } })
                Check.Rejects(() => QQMusicWebProtocol.BuildPlayRequest(new(123, "SongMid001", 0, "Example song", baseline with { File = badFile })),
                    "direct DTO callers cannot bypass optional-field validation");
        }
    }

    private static JsonDocument BuildMetadataRequest(QQMusicWebMetadata metadata)
    {
        var song = new QQMusicWebSong(123, "SongMid001", 0, "Example song", metadata);
        using var next = ProtocolTests.ExtractSongs(QQMusicWebProtocol.BuildInsertNextRequest(song));
        var play = ProtocolTests.ExtractSongs(QQMusicWebProtocol.BuildPlayRequest(song));
        Check.Equal(play.RootElement.GetRawText(), next.RootElement.GetRawText(),
            "legacy validation alias retains the same insertion metadata, rights and optional file field presence");
        return play;
    }

    private static void RunSongTypeTests()
    {
        foreach (var legacy in new[] { false, true })
        {
            foreach (var value in new[] { 0, 1, 112, 255 })
            {
                var item = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
                item["type"] = value;
                AssertType(item.ToJsonString(), legacy, value);
                if (legacy)
                {
                    item["songtype"] = value;
                    AssertType(item.ToJsonString(), true, value);
                    item.Remove("type");
                    AssertType(item.ToJsonString(), true, value);
                }
            }
            var missing = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
            missing.Remove("type");
            AssertInvalidType(missing.ToJsonString(), legacy, "missing server type");
            if (!legacy)
            {
                missing["songtype"] = 0;
                AssertInvalidType(missing.ToJsonString(), false, "modern requires explicit type, not a legacy-only alias");
            }
            foreach (var invalid in new[] { "null", "\"0\"", "true", "[]", "{}", "-1", "256", "2147483648", "4294967296", "1.25", "1e100" })
            {
                var item = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
                item["type"] = JsonNode.Parse(invalid);
                AssertInvalidType(item.ToJsonString(), legacy, "invalid explicit server type");
                if (legacy)
                {
                    item["songtype"] = 0;
                    AssertInvalidType(item.ToJsonString(), true, "valid alias cannot rescue invalid type");
                    item.Remove("type");
                    item["songtype"] = JsonNode.Parse(invalid);
                    AssertInvalidType(item.ToJsonString(), true, "invalid legacy-only alias");
                    item["type"] = 0;
                    AssertInvalidType(item.ToJsonString(), true, "valid type cannot rescue invalid alias");
                }
            }
            AssertInvalidType((legacy ? Legacy : Modern).Replace("\"type\":0", "\"type\":0,\"type\":0"), legacy,
                "duplicate server type field is ambiguous");
        }
        var conflict = JsonNode.Parse(Legacy)!.AsObject();
        conflict["songtype"] = 112;
        AssertInvalidType(conflict.ToJsonString(), true, "conflicting type aliases reject executable projection");
    }

    private static void AssertType(string json, bool legacy, int expected)
    {
        using var document = JsonDocument.Parse(json);
        Check.True(QQMusicWebCatalogMetadata.TryReadSongType(document.RootElement, legacy, out var actual), "explicit type is valid");
        Check.Equal(expected, actual, "server type is preserved without enum mapping");
        var displayed = QQMusicWebCatalogMetadata.ReadDisplaySongType(document.RootElement, legacy);
        Check.Equal(expected, displayed, "CatalogClient display/native identity projection uses the same valid type");
        var metadata = legacy ? QQMusicWebCatalogMetadata.FromLegacy(document.RootElement) : QQMusicWebCatalogMetadata.FromModern(document.RootElement);
        Check.True(metadata is not null, "valid type retains otherwise valid executable metadata");
        using var request = ProtocolTests.ExtractSongs(QQMusicWebProtocol.BuildPlayRequest(new(123, "SongMid001", displayed, "Example song", metadata)));
        Check.Equal(expected, request.RootElement[0].GetProperty("type").GetInt32(),
            "catalog-to-wire identity retains explicit nonzero type exactly");
        using var nextRequest = ProtocolTests.ExtractSongs(QQMusicWebProtocol.BuildInsertNextRequest(new(123, "SongMid001", displayed, "Example song", metadata)));
        Check.Equal(expected, nextRequest.RootElement[0].GetProperty("type").GetInt32(),
            "insert-next identity retains explicit nonzero type exactly");
    }

    private static void AssertInvalidType(string json, bool legacy, string label)
    {
        using var document = JsonDocument.Parse(json);
        Check.True(!QQMusicWebCatalogMetadata.TryReadSongType(document.RootElement, legacy, out _), label);
        Check.True((legacy ? QQMusicWebCatalogMetadata.FromLegacy(document.RootElement) : QQMusicWebCatalogMetadata.FromModern(document.RootElement)) is null,
            label + " cannot generate executable Web metadata");
        _ = QQMusicWebCatalogMetadata.ReadDisplaySongType(document.RootElement, legacy); // Display stays nonthrowing.
    }

    private static QQMusicWebMetadata? Parse(string json, bool legacy)
    {
        using var document = JsonDocument.Parse(json);
        return legacy ? QQMusicWebCatalogMetadata.FromLegacy(document.RootElement)
            : QQMusicWebCatalogMetadata.FromModern(document.RootElement);
    }

    private static void RejectMutation(bool legacy, Action<JsonObject> mutation, string label)
    {
        var item = JsonNode.Parse(legacy ? Legacy : Modern)!.AsObject();
        mutation(item);
        Check.True(Parse(item.ToJsonString(), legacy) is null, label);
    }

    private static JsonObject File(JsonObject item, bool legacy) => legacy ? item : item["file"]!.AsObject();
    private static void Rename(JsonObject item, string original, string replacement)
    {
        var value = item[original]!.DeepClone();
        item.Remove(original);
        item[replacement] = value;
    }
}
