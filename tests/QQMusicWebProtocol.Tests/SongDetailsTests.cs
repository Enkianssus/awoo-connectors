using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQMusicControlPoc;

internal static class SongDetailsTests
{
    private static readonly QQMusicWebSong Expected = new(123, "SongMid001", 0, "Expected title",
        new(350, [new(7, "SingerMid001", "Artist", "Artist")], new("OldMediaMid", 10, 20, 30, 0, 0)));
    private const string Valid = """
        {"code":0,"detail":{"code":0,"data":{"track_info":{
          "id":123,"mid":"SongMid001","type":0,"name":"Expected title","title":"Expected title","interval":350,
          "singer":[{"id":7,"mid":"SingerMid001","name":"Artist","title":"Artist"}],
          "file":{"media_mid":"DetailMediaMid","size_128mp3":5612188,"size_320mp3":14030095,
            "size_flac":43098073,"size_192ogg":7544405,"size_ape":0,
            "size_try":960887,"try_begin":125976,"try_end":177038},
          "action":{"switch":16896769,"switch2":0,"icon2":0},
          "pay":{"pay_month":1,"price_track":200,"price_album":0,"pay_play":1,"pay_down":1,"pay_status":0,"time_free":0}
        }}}}
        """;

    public static void Run()
    {
        // All HTTP is intercepted by FakeHandler. No default client is created,
        // no network is contacted and no adapter/helper/COM/player is started.
        ParserBoundaries();
        HttpBoundariesAsync().GetAwaiter().GetResult();
    }

    private static void ParserBoundaries()
    {
        Check.True(!QQMusicWebSongDetails.HasCompleteMetadata(Expected), "legacy-like metadata needs authoritative detail");
        var resolved = Parse(Valid)!;
        Check.True(resolved is not null && QQMusicWebSongDetails.HasCompleteMetadata(resolved), "complete matching detail is accepted");
        Check.Equal(Expected.SongId, resolved!.SongId, "expected song ID is retained");
        Check.Equal(Expected.SongMid, resolved.SongMid, "expected song MID is retained");
        Check.Equal(Expected.SongType, resolved.SongType, "expected song type is retained");
        Check.Equal(Expected.Title, resolved.Title, "detail does not change the requested identity/display title");
        Check.Equal("DetailMediaMid", resolved.Metadata!.File.MediaMid, "real detail file MID replaces older metadata");
        Check.Equal(16896769u, resolved.Metadata.Action!.Value.GetProperty("switch").GetUInt32(), "actual modern switch is retained");
        Check.Equal(0ul, resolved.Metadata.Action.Value.GetProperty("switch2").GetUInt64(), "explicit switch2 zero is retained, not omitted");
        Check.Equal(0ul, resolved.Metadata.Action.Value.GetProperty("icon2").GetUInt64(), "explicit icon2 zero is retained");
        Check.Equal(1, resolved.Metadata.Pay!.Value.GetProperty("pay_play").GetInt32(), "paid status is not converted to free");
        Check.Equal(200, resolved.Metadata.Pay.Value.GetProperty("price_track").GetInt32(), "server price is retained");
        using (var payload = ProtocolTests.ExtractSongs(QQMusicWebProtocol.BuildPlayRequest(resolved)))
            Check.Equal(0ul, payload.RootElement[0].GetProperty("action").GetProperty("switch2").GetUInt64(),
                "metadata survives source document disposal and XML JSON serialization");

        foreach (var raw in new[] { "", "null", "[]", "{}", "{broken", "{\"code\":0}", "{\"code\":0,\"detail\":null}" })
            Check.True(Parse(raw) is null, "missing or malformed response is not an authoritative detail");
        foreach (var code in new JsonNode?[] { null, JsonValue.Create(-1), JsonValue.Create(1),
            JsonValue.Create("0"), JsonValue.Create(true), JsonValue.Create(2147483648L) })
        {
            Reject(root => root["code"] = code?.DeepClone(), "root code must be numeric zero");
            Reject(root => root["detail"]!["code"] = code?.DeepClone(), "detail code must be numeric zero");
        }
        foreach (var key in new[] { "id", "mid", "type", "interval", "singer", "file", "pay", "action" })
            Reject(root => Track(root).Remove(key), "required identity/metadata field cannot be missing");
        Reject(root => root.Remove("code"), "root code must be explicit");
        Reject(root => root["detail"]!.AsObject().Remove("code"), "detail code must be explicit");
        Reject(root => root["detail"]!.AsObject().Remove("data"), "detail data must be present");
        Reject(root => root["detail"]!["data"]!["track_info"] = new JsonArray(), "track_info must be one object");
        Reject(root => Track(root)["id"] = 124, "different numeric identity cannot enrich target");
        Reject(root => Track(root)["id"] = "123", "numeric string ID is not accepted");
        Reject(root => Track(root)["mid"] = "songmid001", "MID equality is exact and case-sensitive");
        Reject(root => Track(root)["type"] = 3, "different type cannot enrich target");
        Reject(root => Track(root)["type"] = "0", "type cannot be a numeric string");
        Reject(root => Track(root)["file"]!["media_mid"] = "", "invalid real file MID rejects detail");
        foreach (var key in new[] { "switch", "switch2" })
            Reject(root => Track(root)["action"]!.AsObject().Remove(key), "modern action switch fields must be explicit");
        foreach (var key in new[] { "pay_month", "price_track", "price_album", "pay_play", "pay_down", "pay_status", "time_free" })
        {
            Reject(root => Track(root)["pay"]!.AsObject().Remove(key), "all seven real native pay fields are required");
            Reject(root => Track(root)["pay"]![key] = -1, "negative pay data cannot become a permission");
        }
        var allZero = JsonNode.Parse(Valid)!.AsObject();
        foreach (var key in Track(allZero)["pay"]!.AsObject().Select(x => x.Key).ToArray()) Track(allZero)["pay"]![key] = 0;
        Track(allZero)["action"]!["switch"] = 0;
        Check.True(Parse(allZero.ToJsonString()) is not null, "explicit valid zero rights values are still complete metadata");
        foreach (var duplicate in new[] { Valid.Replace("\"code\":0", "\"code\":0,\"code\":0"),
            Valid.Replace("\"id\":123", "\"id\":123,\"id\":123"),
            Valid.Replace("\"switch2\":0", "\"switch2\":0,\"switch2\":0"),
            "{\"code\":0,\"detail\":{},\"detail\":" + JsonNode.Parse(Valid)!["detail"]!.ToJsonString() + "}" })
            Check.True(Parse(duplicate) is null, "duplicate keys cannot choose a last-write-wins identity or rights object");
        Check.True(Parse(Valid, Expected with { SongId = 0 }) is null, "invalid expected target rejects before parsing");
        Check.True(Parse(Valid + new string(' ', QQMusicWebSongDetails.MaximumResponseBytes)) is null, "oversized pure parser input rejects");
        var exactLimit = Valid + new string(' ', QQMusicWebSongDetails.MaximumResponseBytes - Encoding.UTF8.GetByteCount(Valid));
        Check.True(Parse(exactLimit) is not null, "the one-MiB limit is inclusive");
    }

    private static async Task HttpBoundariesAsync()
    {
        using (var handler = new FakeHandler(async (request, token) =>
        {
            Check.Equal(HttpMethod.Post, request.Method, "detail is one fixed POST");
            Check.Equal("https://u.y.qq.com/cgi-bin/musicu.fcg", request.RequestUri!.AbsoluteUri, "detail endpoint stays official");
            Check.Equal("https://y.qq.com/", request.Headers.Referrer!.AbsoluteUri, "catalog referrer is retained");
            Check.True(request.Headers.UserAgent.ToString().Contains("QQMusicControlPoc/1.0"), "catalog user-agent is retained");
            Check.True(request.Headers.Authorization is null && !request.Headers.Contains("Cookie") &&
                !request.Headers.Contains("Proxy-Authorization"), "no account credentials or cookies are supplied");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Check.Keys(body.RootElement, "comm", "detail");
            Check.Keys(body.RootElement.GetProperty("comm"), "ct", "cv");
            Check.Equal(24, body.RootElement.GetProperty("comm").GetProperty("ct").GetInt32(), "fixed ct");
            Check.Equal(0, body.RootElement.GetProperty("comm").GetProperty("cv").GetInt32(), "fixed cv");
            var detail = body.RootElement.GetProperty("detail");
            Check.Equal("music.pf_song_detail_svr", detail.GetProperty("module").GetString(), "observed official module");
            Check.Equal("get_song_detail_yqq", detail.GetProperty("method").GetString(), "observed official method");
            Check.Keys(detail.GetProperty("param"), "song_mid", "song_type");
            Check.Equal(Expected.SongMid, detail.GetProperty("param").GetProperty("song_mid").GetString(), "requested MID remains exact");
            Check.Equal(Expected.SongType, detail.GetProperty("param").GetProperty("song_type").GetInt32(), "requested type remains exact");
            return Response(Valid);
        }))
        using (var client = new HttpClient(handler))
        using (var reader = new QQMusicWebSongDetails(client))
        {
            Check.True(await reader.ResolveAsync(Expected, CancellationToken.None) is not null, "fake HTTP detail resolves");
            Check.Equal(1, handler.Count, "successful resolution makes exactly one request");
        }
        foreach (var failure in new Func<HttpResponseMessage>[]
        {
            () => new(HttpStatusCode.InternalServerError), () => new(HttpStatusCode.Found),
            () => Response("{bad"), () => Response("{\"code\":1}"),
            () => Response(Valid.Replace("\"id\":123", "\"id\":124"))
        })
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(failure()));
            using var client = new HttpClient(handler);
            using var reader = new QQMusicWebSongDetails(client);
            Check.True(await reader.ResolveAsync(Expected, CancellationToken.None) is null, "HTTP/parsing/identity failure returns null");
            Check.Equal(1, handler.Count, "failed read is not retried");
        }
        using (var handler = new FakeHandler((_, _) => throw new HttpRequestException("synthetic transport failure")))
        using (var client = new HttpClient(handler))
        using (var reader = new QQMusicWebSongDetails(client))
        {
            Check.True(await reader.ResolveAsync(Expected, CancellationToken.None) is null, "transport error returns null");
            Check.Equal(1, handler.Count, "transport failure is not retried");
        }
        using (var handler = new FakeHandler((_, _) => Task.FromResult(Response(Valid))))
        using (var client = new HttpClient(handler))
        using (var reader = new QQMusicWebSongDetails(client))
        using (var cancelled = new CancellationTokenSource())
        {
            Check.True(await reader.ResolveAsync(Expected with { SongId = 0 }, CancellationToken.None) is null,
                "invalid expected request never accesses HTTP");
            cancelled.Cancel();
            await ExpectCancellationAsync(reader.ResolveAsync(Expected, cancelled.Token));
            Check.Equal(0, handler.Count, "invalid and already cancelled requests never reach the handler");
        }
        foreach (var advertised in new[] { false, true })
        {
            var stream = new CountingStream(new byte[QQMusicWebSongDetails.MaximumResponseBytes + 1]);
            using var handler = new FakeHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                if (advertised) response.Content.Headers.ContentLength = QQMusicWebSongDetails.MaximumResponseBytes + 1;
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            using var reader = new QQMusicWebSongDetails(client);
            Check.True(await reader.ResolveAsync(Expected, CancellationToken.None) is null, "advertised or chunked oversized response rejects");
            Check.Equal(1, handler.Count, "oversized responses are not retried");
            Check.True(advertised ? stream.ReadBytes == 0 : stream.ReadBytes <= QQMusicWebSongDetails.MaximumResponseBytes + 8192,
                "oversized header rejects before reading; unknown length stays bounded to one extra chunk");
            Check.True(stream.Disposed, "rejected response stream is disposed");
        }

        var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var handler = new FakeHandler((_, _) => delayed.Task))
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var reader = new QQMusicWebSongDetails(client))
        {
            var clock = Stopwatch.StartNew();
            Check.True(await reader.ResolveAsync(Expected, CancellationToken.None) is null,
                "four-second internal deadline bounds even a non-cooperative fake handler");
            Check.True(clock.Elapsed < TimeSpan.FromSeconds(7), "internal timeout does not wait indefinitely");
            Check.Equal(1, handler.Count, "timeout never retries");
            delayed.SetResult(Response(Valid)); // Cleanup only; not a second request.
        }
        using (var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(Valid);
        }))
        using (var client = new HttpClient(handler))
        using (var reader = new QQMusicWebSongDetails(client))
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            await ExpectCancellationAsync(reader.ResolveAsync(Expected, cancelled.Token));
            Check.Equal(1, handler.Count, "in-flight caller cancellation propagates without retry");
        }
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static QQMusicWebSong? Parse(string json, QQMusicWebSong? expected = null) =>
        QQMusicWebSongDetails.Parse(expected ?? Expected, Encoding.UTF8.GetBytes(json));
    private static JsonObject Track(JsonObject root) => root["detail"]!["data"]!["track_info"]!.AsObject();
    private static void Reject(Action<JsonObject> mutation, string label)
    {
        var root = JsonNode.Parse(Valid)!.AsObject();
        mutation(root);
        Check.True(Parse(root.ToJsonString()) is null, label);
    }
    private static async Task ExpectCancellationAsync(Task<QQMusicWebSong?> task)
    {
        try { await task; Check.True(false, "caller cancellation must propagate"); }
        catch (OperationCanceledException) { Check.True(true, "caller cancellation propagated"); }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            return send(request, token);
        }
    }

    private sealed class CountingStream(byte[] bytes) : Stream
    {
        private int position;
        internal int ReadBytes { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var amount = Math.Min(count, bytes.Length - position);
            Array.Copy(bytes, position, buffer, offset, amount);
            position += amount;
            ReadBytes += amount;
            return amount;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var amount = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, amount).CopyTo(buffer);
            position += amount;
            ReadBytes += amount;
            return ValueTask.FromResult(amount);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
