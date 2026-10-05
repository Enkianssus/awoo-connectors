using System.Net.Http.Json;
using System.Text.Json;

namespace QQMusicControlPoc;

internal sealed record QQMusicCatalogSong(
    long SongId,
    string SongMid,
    int SongType,
    string Title,
    string Artist,
    string Album,
    string AlbumMid,
    int DurationSeconds,
    bool IsPlayable)
{
    public string StableIdentity => $"{SongId}:{SongMid}:{SongType}";
}

internal sealed class QQMusicCatalogClient : IDisposable
{
    private static readonly Uri SearchEndpoint =
        new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private const string LegacySearchEndpoint =
        "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _primaryTimeout;
    private readonly TimeSpan _totalTimeout;

    public QQMusicCatalogClient()
        : this(new HttpClientHandler(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(12))
    {
    }

    internal QQMusicCatalogClient(
        HttpMessageHandler handler,
        TimeSpan primaryTimeout,
        TimeSpan totalTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ValidateTimeout(primaryTimeout, nameof(primaryTimeout));
        ValidateTimeout(totalTimeout, nameof(totalTimeout));
        _primaryTimeout = primaryTimeout;
        _totalTimeout = totalTimeout;
        // Keep HttpClientHandler's normal system/network defaults. Per-search
        // cancellation budgets include both HTTP headers and the response body.
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Referrer =
            new Uri("https://y.qq.com/");
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 QQMusicControlPoc/1.0");
    }

    public async Task<IReadOnlyList<QQMusicCatalogSong>> SearchAsync(
        string query,
        int count = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(_totalTimeout);
        SearchAttempt primary;
        using (var primaryBudget = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
        {
            primaryBudget.CancelAfter(_primaryTimeout);
            primary = await SearchEndpointAsync(query, count, legacy: false,
                cancellationToken, primaryBudget.Token).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (primary.Songs is { Count: > 0 } primarySongs)
        {
            var songs = BackfillMissingAlbumArtwork(primarySongs);
            cancellationToken.ThrowIfCancellationRequested();
            if (!total.IsCancellationRequested) return songs;
            primary = new(null, "total timeout");
        }
        if (total.IsCancellationRequested)
        {
            throw SearchFailure(primary.Failure, "total timeout (not attempted)");
        }
        // Exactly one fallback. It receives the remaining total budget, not a
        // fresh full timeout, and a caller cancellation never reaches this send.
        var legacy = await SearchEndpointAsync(query, count, legacy: true,
            cancellationToken, total.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (legacy.Songs is not null)
        {
            var songs = BackfillMissingAlbumArtwork(legacy.Songs);
            cancellationToken.ThrowIfCancellationRequested();
            if (!total.IsCancellationRequested) return songs;
            legacy = new(null, "total timeout");
        }
        throw SearchFailure(primary.Failure, legacy.Failure);
    }

    public async Task<string> SearchRawAsync(
        string query,
        int count = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_totalTimeout);
        return await AwaitRequestAsync(SendPrimaryRawAsync(query, count, budget.Token), budget.Token)
            .ConfigureAwait(false);
    }

    private async Task<string> SendPrimaryRawAsync(string query, int count, CancellationToken cancellationToken)
    {
        var payload = new
        {
            comm = new
            {
                // The public Desktop search service needs a supported client
                // context. ct=24/cv=0 has returned successful but empty responses
                // for known songs, unnecessarily routing them to the legacy API.
                ct = 11,
                cv = 1003006,
                v = 1003006
            },
            search = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService",
                param = new
                {
                    grp = 1,
                    num_per_page = Math.Clamp(count, 1, 30),
                    page_num = 1,
                    query = query.Trim(),
                    search_type = 0
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> SearchLegacyRawAsync(
        string query,
        int count = 12,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_totalTimeout);
        return await AwaitRequestAsync(SendLegacyRawAsync(query, count, budget.Token), budget.Token)
            .ConfigureAwait(false);
    }

    private async Task<string> SendLegacyRawAsync(string query, int count, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["format"] = "json",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf-8",
            ["notice"] = "0",
            ["platform"] = "yqq.json",
            ["needNewCode"] = "0",
            ["p"] = "1",
            ["n"] = Math.Clamp(count, 1, 30).ToString(),
            ["w"] = query.Trim(),
            ["cr"] = "1",
            ["g_tk"] = "5381"
        };
        var queryString = string.Join(
            '&',
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}="
                + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{LegacySearchEndpoint}?{queryString}");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SearchAttempt> SearchEndpointAsync(string query, int count, bool legacy,
        CancellationToken callerCancellation, CancellationToken budget)
    {
        callerCancellation.ThrowIfCancellationRequested();
        try
        {
            budget.ThrowIfCancellationRequested();
            var raw = await AwaitRequestAsync(legacy
                    ? SendLegacyRawAsync(query, count, budget)
                    : SendPrimaryRawAsync(query, count, budget), budget)
                .ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            ValidateApiCode(root);
            if (!legacy && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("search", out var search))
            {
                ValidateApiCode(search);
                if (search.ValueKind == JsonValueKind.Object && search.TryGetProperty("data", out var data))
                    ValidateApiCode(data);
            }
            var path = legacy ? new[] { "data", "song", "list" } : ["search", "data", "body", "song", "list"];
            if (!TryGetProperty(root, out var list, path) || list.ValueKind != JsonValueKind.Array)
                return new(null, "invalid list shape");
            var songs = new List<QQMusicCatalogSong>();
            foreach (var item in list.EnumerateArray())
            {
                budget.ThrowIfCancellationRequested();
                if (TryParseCatalogSong(item, legacy, out var song)) songs.Add(song);
            }
            budget.ThrowIfCancellationRequested();
            if (songs.Count != 0 || (legacy && list.GetArrayLength() == 0)) return new(songs, null);
            return new(null, list.GetArrayLength() == 0 ? "empty-results" : "no valid songs");
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(null, "timeout"); }
        catch (HttpRequestException exception)
        {
            return new(null, exception.StatusCode is { } status ? $"HTTP {(int)status}" : "HTTP transport failure");
        }
        catch (JsonException) { return new(null, "invalid JSON"); }
        catch (CatalogResponseException exception) { return new(null, exception.Message); }
    }

    private static bool TryParseCatalogSong(JsonElement item, bool legacy, out QQMusicCatalogSong song)
    {
        song = null!;
        if (item.ValueKind != JsonValueKind.Object) return false;
        try { return legacy ? TryParseLegacySong(item, out song) : TryParseSong(item, out song); }
        catch (InvalidOperationException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryParseLegacySong(JsonElement item, out QQMusicCatalogSong song)
    {
        song = null!;
        var songId = ReadInt64(item, "songid");
        var songMid = ReadString(item, "songmid");
        var title = ReadString(item, "songname");
        if (songId <= 0 || string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(title)) return false;
        var singers = new List<string>();
        if (item.TryGetProperty("singer", out var singerArray) && singerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var singer in singerArray.EnumerateArray())
            {
                var name = ReadString(singer, "name");
                if (!string.IsNullOrWhiteSpace(name)) singers.Add(name);
            }
        }
        song = new QQMusicCatalogSong(songId, songMid, checked((int)ReadInt64(item, "songtype")), title,
            string.Join(" / ", singers), ReadString(item, "albumname"), ReadString(item, "albummid"),
            checked((int)ReadInt64(item, "interval")), true);
        return true;
    }

    private static void ValidateApiCode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("code", out var code)) return;
        if (code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var number))
            throw new CatalogResponseException("API code invalid");
        if (number != 0) throw new CatalogResponseException($"API code {number}");
    }

    private static async Task<string> AwaitRequestAsync(Task<string> request, CancellationToken budget)
    {
        // A handler that is slow to honor cancellation must not extend the
        // caller's total budget. Its own async method still owns/disposes its
        // request and any eventual response; observe a later failure as well.
        _ = request.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await request.WaitAsync(budget).ConfigureAwait(false);
    }

    private static HttpRequestException SearchFailure(string? primary, string? legacy) =>
        new($"QQ search failed: primary[musicu.fcg]: {primary ?? "no results"}; "
            + $"legacy[client_search_cp]: {legacy ?? "no results"}.");

    private static void ValidateTimeout(TimeSpan timeout, string parameter)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(parameter);
    }

    private sealed record SearchAttempt(IReadOnlyList<QQMusicCatalogSong>? Songs, string? Failure);
    private sealed class CatalogResponseException(string message) : Exception(message);

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static bool TryParseSong(
        JsonElement item,
        out QQMusicCatalogSong song)
    {
        song = null!;
        var songId = ReadInt64(item, "id");
        var songMid = ReadString(item, "mid");
        var title = ReadString(item, "name");
        if (songId <= 0
            || string.IsNullOrWhiteSpace(songMid)
            || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var singers = new List<string>();
        if (item.TryGetProperty("singer", out var singerArray)
            && singerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var singer in singerArray.EnumerateArray())
            {
                var name = ReadString(singer, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    singers.Add(name);
                }
            }
        }

        var album = string.Empty;
        var albumMid = string.Empty;
        if (item.TryGetProperty("album", out var albumElement))
        {
            album = ReadString(albumElement, "name");
            albumMid = ReadString(albumElement, "mid");
        }

        var playable = true;
        if (item.TryGetProperty("action", out var action)
            && action.TryGetProperty("switch", out var switchElement)
            && switchElement.TryGetInt64(out var switchValue))
        {
            playable = (switchValue & 1) != 0;
        }

        song = new QQMusicCatalogSong(
            songId,
            songMid,
            checked((int)ReadInt64(item, "type")),
            title,
            string.Join(" / ", singers),
            album,
            albumMid,
            checked((int)ReadInt64(item, "interval")),
            playable);
        return true;
    }

    private static IReadOnlyList<QQMusicCatalogSong>
        BackfillMissingAlbumArtwork(
            IReadOnlyList<QQMusicCatalogSong> songs)
    {
        var artworkCandidates = songs
            .Select(song => new QQMusicAlbumArtworkCandidate(
                song.Title,
                song.Artist,
                song.Album,
                song.AlbumMid))
            .ToArray();

        return songs
            .Select(song => song with
            {
                AlbumMid = QQMusicAlbumArtwork.SelectPictureId(
                    song.AlbumMid,
                    song.Title,
                    song.Artist,
                    artworkCandidates)
            })
            .ToArray();
    }

    private static bool TryGetProperty(
        JsonElement root,
        out JsonElement result,
        params string[] path)
    {
        result = root;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty(name, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    private static long ReadInt64(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt64(out var value)
                ? value
                : 0;
    }
}
