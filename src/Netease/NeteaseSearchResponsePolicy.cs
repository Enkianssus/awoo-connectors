using System.Text.Json;

namespace UnifiedPlayerControlPoc;

internal enum NeteaseSearchResponseKind
{
    Results,
    Empty,
    Retryable
}

internal sealed record NeteaseSearchResponseAnalysis(
    NeteaseSearchResponseKind Kind,
    IReadOnlyList<JsonElement> Songs,
    string Message);

internal sealed record NeteaseSearchTrackMetadata(
    string Id,
    string Title,
    string Artist,
    string Album);

/// <summary>
/// Keeps server-response classification separate from transport and player
/// control. A valid empty result is not retryable; malformed or rate-limited
/// responses are deliberately distinguishable from "no song found".
/// </summary>
internal static class NeteaseSearchResponsePolicy
{
    public static NeteaseSearchResponseAnalysis Analyze(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Retryable("响应不是 JSON 对象");
        }

        if (root.TryGetProperty("code", out var code))
        {
            if (!TryReadInt(code, out var codeValue))
            {
                return Retryable("响应 code 格式异常");
            }

            if (codeValue != 200)
            {
                var message = ReadText(root, "message");
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = ReadText(root, "msg");
                }

                return Retryable(
                    string.IsNullOrWhiteSpace(message)
                        ? $"网易云返回 code={codeValue}"
                        : $"网易云返回 code={codeValue}：{message}");
            }
        }

        if (!root.TryGetProperty("result", out var result))
        {
            return Retryable("响应缺少 result");
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            var resultMessage = result.ValueKind == JsonValueKind.String
                ? result.GetString()
                : null;
            return Retryable(
                string.IsNullOrWhiteSpace(resultMessage)
                    ? "响应 result 结构异常"
                    : $"网易云返回：{resultMessage}");
        }

        if (!result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return Retryable("响应缺少可解析的 songs");
        }

        var entries = songs
            .EnumerateArray()
            .Select(song => song.Clone())
            .ToArray();
        return entries.Length == 0
            ? new NeteaseSearchResponseAnalysis(
                NeteaseSearchResponseKind.Empty,
                entries,
                string.Empty)
            : new NeteaseSearchResponseAnalysis(
                NeteaseSearchResponseKind.Results,
                entries,
                string.Empty);
    }

    public static NeteaseSearchTrackMetadata ParseTrack(JsonElement song)
    {
        if (song.ValueKind != JsonValueKind.Object)
        {
            return new NeteaseSearchTrackMetadata(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        return new NeteaseSearchTrackMetadata(
            ReadText(song, "id"),
            ReadText(song, "name"),
            ReadArtists(song),
            ReadAlbum(song));
    }

    private static NeteaseSearchResponseAnalysis Retryable(string message)
    {
        return new NeteaseSearchResponseAnalysis(
            NeteaseSearchResponseKind.Retryable,
            Array.Empty<JsonElement>(),
            message);
    }

    private static string ReadArtists(JsonElement song)
    {
        JsonElement artists;
        if ((!song.TryGetProperty("artists", out artists)
             || artists.ValueKind != JsonValueKind.Array)
            && (!song.TryGetProperty("ar", out artists)
                || artists.ValueKind != JsonValueKind.Array))
        {
            return string.Empty;
        }

        return string.Join(
            "/",
            artists.EnumerateArray()
                .Select(artist => ReadText(artist, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadAlbum(JsonElement song)
    {
        JsonElement album;
        return ((song.TryGetProperty("album", out album)
                 && album.ValueKind == JsonValueKind.Object)
                || (song.TryGetProperty("al", out album)
                    && album.ValueKind == JsonValueKind.Object))
            ? ReadText(album, "name")
            : string.Empty;
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out result))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(
                value.GetString(),
                out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static string ReadText(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }
}
