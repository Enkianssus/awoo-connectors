using System.Text.Json;

namespace QQMusicControlPoc;

/// <summary>
/// Pure, fail-closed projection of real search metadata. No lookup, guessed
/// media MID, fabricated file size, or inferred account entitlement.
/// </summary>
internal static class QQMusicWebCatalogMetadata
{
    private static readonly string[] PayFields =
        ["pay_month", "price_track", "price_album", "pay_play", "pay_down", "pay_status", "time_free"];
    private static readonly string[] ActionFields = ["switch", "switch2", "icon2"];

    internal static QQMusicWebMetadata? FromLegacy(JsonElement item) => Read(item, legacy: true);
    internal static QQMusicWebMetadata? FromModern(JsonElement item) => Read(item, legacy: false);

    internal static bool TryReadSongType(JsonElement item, bool legacy, out int songType)
    {
        songType = 0;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        var value = 0;
        var sawType = false;
        var sawSongType = false;
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name == "type")
            {
                if (sawType) return false;
                sawType = true;
            }
            else if (legacy && property.Name == "songtype")
            {
                if (sawSongType) return false;
                sawSongType = true;
            }
            else continue;
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var candidate) || candidate is < 0 or > 255 ||
                (found && value != candidate)) return false;
            value = candidate;
            found = true;
        }
        if (!found) return false;
        songType = value;
        return true;
    }

    internal static int ReadDisplaySongType(JsonElement item, bool legacy)
    {
        if (TryReadSongType(item, legacy, out var songType)) return songType;
        // Preserve the old native/display projection where possible. An invalid
        // server type still cannot produce executable WebMetadata below.
        return item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty(legacy ? "songtype" : "type", out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
                ? value : 0;
    }

    private static QQMusicWebMetadata? Read(JsonElement item, bool legacy)
    {
        if (!TryReadSongType(item, legacy, out _) ||
            !TryNumber(item, out var interval, "interval") || interval is <= 0 or > 86400 ||
            !TrySingers(item, legacy, out var singers))
            return null;

        var file = item;
        if (!legacy && (!item.TryGetProperty("file", out file) || file.ValueKind != JsonValueKind.Object))
            return null;
        if (!TryMid(file, out var mediaMid, legacy ? ["media_mid", "strMediaMid"] : ["media_mid"]) ||
            !TryNumber(file, out var size128, legacy ? ["size128"] : ["size_128", "size_128mp3"]) ||
            !TryNumber(file, out var size320, legacy ? ["size320"] : ["size_320", "size_320mp3"]) ||
            !TryNumber(file, out var sizeFlac, legacy ? ["sizeflac"] : ["size_flac"]) ||
            !TryNumber(file, out var sizeOgg, legacy ? ["sizeogg"] : ["size_ogg", "size_192ogg"]) ||
            !TryNumber(file, out var sizeApe, legacy ? ["sizeape"] : ["size_ape"]) ||
            !(size128 > 0 || size320 > 0 || sizeFlac > 0 || sizeOgg > 0 || sizeApe > 0))
            return null;
        if (!TryOptionalNumber(file, "size_try", out var sizeTry) ||
            !TryOptionalNumber(file, "try_begin", out var tryBegin) ||
            !TryOptionalNumber(file, "try_end", out var tryEnd) ||
            (tryBegin is { } begin && tryEnd is { } end && end < begin))
            return null;

        // All five sizes must be present (an explicit server zero is preserved).
        // A missing format size is not silently converted to a fabricated zero.
        JsonElement? pay = null;
        JsonElement? action = null;
        if (legacy)
        {
            // Legacy payalbum/paydownload/payplay do not have a proven mapping to
            // the native pay_* fields. Missing Pay is not a grant of permission.
            if (!TryRights(item, ["switch"], action: true, out action)) return null;
        }
        else
        {
            if (item.TryGetProperty("pay", out var paySource) &&
                !TryRights(paySource, PayFields, action: false, out pay)) return null;
            if (item.TryGetProperty("action", out var actionSource) &&
                !TryRights(actionSource, ActionFields, action: true, out action)) return null;
        }
        return new(interval, singers, new(mediaMid, size128, size320, sizeFlac, sizeOgg, sizeApe,
            sizeTry, tryBegin, tryEnd), pay, action);
    }

    private static bool TrySingers(JsonElement item, bool legacy, out QQMusicWebSinger[] singers)
    {
        singers = [];
        if (!item.TryGetProperty("singer", out var source) || source.ValueKind != JsonValueKind.Array ||
            source.GetArrayLength() is < 1 or > 32) return false;
        var result = new List<QQMusicWebSinger>();
        foreach (var singer in source.EnumerateArray())
        {
            if (singer.ValueKind != JsonValueKind.Object || !TryNumber(singer, out var id, "id") ||
                !TryText(singer, "name", out var name)) return false;
            var mid = string.Empty;
            if (singer.TryGetProperty("mid", out var midSource))
            {
                if (midSource.ValueKind != JsonValueKind.String) return false;
                mid = midSource.GetString()!;
                if (mid.Length != 0 && !ValidMid(mid)) return false;
            }
            var title = name;
            if (!legacy && singer.TryGetProperty("title", out _) && !TryText(singer, "title", out title))
                return false;
            result.Add(new(id, mid, name, title));
        }
        singers = result.ToArray();
        return true;
    }

    private static bool TryText(JsonElement source, string name, out string value)
    {
        value = string.Empty;
        if (!source.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 && !value.Any(char.IsControl);
    }

    private static bool TryMid(JsonElement source, out string value, params string[] names)
    {
        value = string.Empty;
        var found = false;
        foreach (var name in names)
        {
            if (!source.TryGetProperty(name, out var property)) continue;
            if (property.ValueKind != JsonValueKind.String) return false;
            var candidate = property.GetString()!;
            if (!ValidMid(candidate) || (found && value != candidate)) return false;
            value = candidate;
            found = true;
        }
        return found;
    }

    private static bool ValidMid(string value) => !string.IsNullOrWhiteSpace(value) && value != "0" &&
        value.Length <= 128 && value.All(char.IsAsciiLetterOrDigit);

    private static bool TryNumber(JsonElement source, out int value, params string[] names)
    {
        value = 0;
        var found = false;
        foreach (var name in names)
        {
            if (!source.TryGetProperty(name, out var property)) continue;
            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var candidate) ||
                candidate < 0 || (found && value != candidate)) return false;
            value = candidate;
            found = true;
        }
        return found;
    }

    private static bool TryOptionalNumber(JsonElement source, string name, out int? value)
    {
        value = null;
        foreach (var property in source.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (value.HasValue || property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out var candidate) || candidate < 0)
                return false;
            value = candidate;
        }
        return true;
    }

    private static bool TryRights(JsonElement source, string[] allowed, bool action, out JsonElement? copy)
    {
        copy = null;
        if (source.ValueKind != JsonValueKind.Object) return false;
        var fields = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            // The fixed whitelist is smaller than 32 ASCII keys (all < 64 chars).
            // Unknown fields are not copied or translated to a permission flag.
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) continue;
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetUInt64(out var value) ||
                (!action && value > int.MaxValue) || (action && property.Name == "switch" && value > uint.MaxValue) ||
                !fields.TryAdd(property.Name, value)) return false;
        }
        if (fields.Count > 0) copy = JsonSerializer.SerializeToElement(fields);
        return true;
    }
}
