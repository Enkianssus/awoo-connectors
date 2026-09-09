using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

/// <summary>
/// One bounded, credential-free official song-detail read. This does not send a
/// player command or establish account entitlement; only matching raw metadata
/// may replace the expected song's metadata before a separate submission.
/// </summary>
internal sealed class QQMusicWebSongDetails : IDisposable
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(4);
    private static readonly Uri Endpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private static readonly string[] RequiredPayFields =
        ["pay_month", "price_track", "price_album", "pay_play", "pay_down", "pay_status", "time_free"];
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal QQMusicWebSongDetails() : this(new HttpClient(new HttpClientHandler
    {
        UseCookies = false,
        UseDefaultCredentials = false,
        AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan }, ownsClient: true) { }

    // Test injection does not transfer ownership of the caller's HttpClient.
    internal QQMusicWebSongDetails(HttpClient client) : this(client, ownsClient: false) { }

    private QQMusicWebSongDetails(HttpClient client, bool ownsClient)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    internal async Task<QQMusicWebSong?> ResolveAsync(
        QQMusicWebSong expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidSong(expected)) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RequestBudget);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 QQMusicControlPoc/1.0");
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                comm = new { ct = 24, cv = 0 },
                detail = new
                {
                    module = "music.pf_song_detail_svr",
                    method = "get_song_detail_yqq",
                    param = new { song_mid = expected.SongMid, song_type = expected.SongType }
                }
            }), Encoding.UTF8, "application/json");

            var send = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            using var response = await AwaitResponseAsync(send, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token)
                .WaitAsync(budget.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            // No pooled buffer: a cancelled non-cooperative stream must not write
            // into memory returned to another operation after WaitAsync expires.
            var chunk = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(), budget.Token).AsTask()
                    .WaitAsync(budget.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            budget.Token.ThrowIfCancellationRequested();
            var resolved = Parse(expected, buffer.ToArray());
            budget.Token.ThrowIfCancellationRequested();
            return resolved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static async Task<HttpResponseMessage> AwaitResponseAsync(
        Task<HttpResponseMessage> send, CancellationToken token)
    {
        try { return await send.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // A custom/non-cooperative handler may complete after the deadline.
            // It never authorizes another request; release its eventual response.
            _ = send.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    internal static bool HasCompleteMetadata(QQMusicWebSong song)
    {
        if (!IsValidSong(song)) return false;
        var metadata = song.Metadata!;
        if (metadata.Action is not { ValueKind: JsonValueKind.Object } action ||
            !action.TryGetProperty("switch", out var switchValue) || !switchValue.TryGetUInt32(out _) ||
            !action.TryGetProperty("switch2", out var switch2) || !switch2.TryGetUInt64(out _) ||
            metadata.Pay is not { ValueKind: JsonValueKind.Object } pay)
            return false;
        return RequiredPayFields.All(name => pay.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0);
    }

    internal static QQMusicWebSong? Parse(QQMusicWebSong expected, ReadOnlyMemory<byte> payload)
    {
        if (!IsValidSong(expected) || payload.Length is 0 or > MaximumResponseBytes) return null;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!UniqueProperties(root) || !CodeIsZero(root) ||
                !ObjectProperty(root, "detail", out var detail) || !CodeIsZero(detail) ||
                !ObjectProperty(detail, "data", out var data) ||
                !ObjectProperty(data, "track_info", out var track) ||
                !track.TryGetProperty("id", out var id) || !id.TryGetUInt32(out var idValue) || idValue != expected.SongId ||
                !track.TryGetProperty("mid", out var mid) || mid.ValueKind != JsonValueKind.String || mid.GetString() != expected.SongMid ||
                !track.TryGetProperty("type", out var type) || !type.TryGetInt32(out var typeValue) || typeValue != expected.SongType)
                return null;
            var metadata = QQMusicWebCatalogMetadata.FromModern(track);
            if (metadata is null) return null;
            var resolved = expected with { Metadata = metadata };
            return HasCompleteMetadata(resolved) ? resolved : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static bool IsValidSong(QQMusicWebSong song)
    {
        try { _ = QQMusicWebProtocol.BuildPlayRequest(song); return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static bool CodeIsZero(JsonElement item) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value == 0;

    private static bool ObjectProperty(JsonElement source, string name, out JsonElement value)
    {
        value = default;
        return source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object;
    }

    private static bool UniqueProperties(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (item.ValueKind == JsonValueKind.Array)
            foreach (var child in item.EnumerateArray())
                if (!UniqueProperties(child)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
