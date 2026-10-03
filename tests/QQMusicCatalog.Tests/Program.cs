using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using QQMusicControlPoc;

return await CatalogTests.RunAsync();

internal static class CatalogTests
{
    private const string SecretQuery = "private-query-fixture-947281";
    private const string SecretBody = "private-response-body-fixture-718245";
    private static int checks;
    private static int cases;
    private static readonly List<string> Failures = [];

    internal static async Task<int> RunAsync()
    {
        foreach (var status in new[] { 500, 502, 403, 429 })
        {
            await CaseAsync("primary HTTP " + status + " falls back", async () =>
            {
                using var handler = Sequence(
                    _ => Reply(SecretBody, status), _ => Reply(Legacy(21)));
                using var client = Client(handler);
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Count == 1 && songs[0].SongId == 21, "fallback returned legacy song");
                Check(handler.Count == 2, "exactly one fallback");
                Check(handler.DisposedResponses == 2, "HTTP failure and legacy response disposed");
            });
        }

        await CaseAsync("transport exception falls back without exposing private text", async () =>
        {
            using var handler = Sequence(_ => throw new HttpRequestException(SecretQuery + SecretBody), _ => Reply(Legacy(22)));
            using var client = Client(handler);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 22 && handler.Count == 2, "transport fallback");
        });

        await CaseAsync("independent primary timeout leaves time for fallback", async () =>
        {
            using var handler = Sequence(WaitForCancellation, _ => Reply(Legacy(23)));
            using var client = Client(handler, 40, 300);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 23 && handler.Count == 2, "primary deadline is not caller cancellation");
        });

        await CaseAsync("successful primary remains authoritative", async () =>
        {
            using var handler = Sequence(_ => Reply(Primary(11)), _ => throw new InvalidOperationException("unexpected fallback"));
            using var client = Client(handler);
            var songs = await client.SearchAsync(SecretQuery, 99);
            Check(songs.Count == 1 && songs[0].SongId == 11 && songs[0].AlbumMid == "album11", "primary metadata preserved");
            Check(handler.Count == 1, "no fallback after usable primary");
            Check(handler.DisposedResponses == 1, "primary success response disposed before SearchAsync returns");
            Check(handler.Requests[0].Method == "POST" && handler.Requests[0].Host == "u.y.qq.com", "primary endpoint and method");
        });

        var primaryFailures = new Dictionary<string, string>
        {
            ["valid empty"] = Primary(),
            ["missing list"] = "{\"code\":0,\"search\":{\"code\":0,\"data\":{}}}",
            ["wrong list type"] = "{\"code\":0,\"search\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":{}}}}}}",
            ["invalid JSON"] = SecretBody,
            ["root business error"] = Primary(11).Replace("\"code\":0,\"search\"", "\"code\":5001,\"search\""),
            ["search business error"] = Primary(11).Replace("\"search\":{\"code\":0", "\"search\":{\"code\":1001"),
            ["root array"] = "[]",
            ["root code wrong type"] = Primary(11).Replace("\"code\":0,\"search\"", "\"code\":\"bad\",\"search\""),
            ["search code wrong type"] = Primary(11).Replace("\"search\":{\"code\":0", "\"search\":{\"code\":null"),
            ["unusable items"] = "{\"code\":0,\"search\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[{}]}}}}}"
        };
        foreach (var scenario in primaryFailures)
        {
            await CaseAsync("primary " + scenario.Key + " falls back", async () =>
            {
                using var handler = Sequence(_ => Reply(scenario.Value), _ => Reply(Legacy(31)));
                using var client = Client(handler);
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 31 && handler.Count == 2, "semantic failure fallback");
                Check(handler.DisposedResponses == 2, "both semantic-path responses disposed");
            });
        }

        await CaseAsync("legacy valid empty means no matches", async () =>
        {
            using var handler = Sequence(_ => Reply("", 500), _ => Reply(Legacy()));
            using var client = Client(handler);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Count == 0 && handler.Count == 2, "valid fallback empty is not an exception");
        });

        await CaseAsync("empty primary plus failed fallback remains a diagnostic error", async () =>
        {
            using var handler = Sequence(_ => Reply(Primary()), _ => Reply(SecretBody, 500));
            using var client = Client(handler);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "", "500");
            Check(handler.Count == 2, "empty primary must not hide fallback failure");
        });

        await CaseAsync("partial malformed primary songs preserve usable result", async () =>
        {
            var body = Primary(11).Replace("\"list\":[", "\"list\":[{},null,");
            using var handler = Sequence(_ => Reply(body));
            using var client = Client(handler);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 11 && handler.Count == 1, "partial malformed primary keeps valid song");
        });

        var legacyFailures = new Dictionary<string, (string Body, int Status, string Detail)>
        {
            ["HTTP failure"] = (SecretBody, 500, "500"),
            ["business failure"] = ("{\"code\":10005,\"data\":{\"song\":{\"list\":[]}}}", 200, "10005"),
            ["missing shape"] = ("{\"code\":0,\"data\":{}}", 200, ""),
            ["wrong list type"] = ("{\"code\":0,\"data\":{\"song\":{\"list\":null}}}", 200, ""),
            ["bad JSON"] = (SecretBody, 200, ""),
            ["only malformed songs"] = ("{\"code\":0,\"data\":{\"song\":{\"list\":[{}]}}}", 200, "")
        };
        foreach (var scenario in legacyFailures)
        {
            await CaseAsync("combined errors: " + scenario.Key, async () =>
            {
                using var handler = Sequence(_ => Reply(SecretBody, 502), _ => Reply(scenario.Value.Body, scenario.Value.Status));
                using var client = Client(handler);
                var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
                CheckCombinedError(error, "502", scenario.Value.Detail);
                Check(handler.Count == 2, "failed fallback does not issue a third request");
                Check(handler.DisposedResponses == 2, "failed responses disposed");
            });
        }

        await CaseAsync("transport errors redact both inner messages", async () =>
        {
            using var handler = Sequence(_ => throw new HttpRequestException(SecretQuery), _ => throw new HttpRequestException(SecretBody));
            using var client = Client(handler);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "", "");
            Check(handler.Count == 2, "transport fallback only once");
        });

        await CaseAsync("pre-cancelled caller sends nothing", async () =>
        {
            using var caller = new CancellationTokenSource();
            caller.Cancel();
            using var handler = Sequence(_ => Reply(Primary(11)));
            using var client = Client(handler);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery, cancellationToken: caller.Token));
            Check(error is OperationCanceledException, "caller cancellation is preserved");
            Check(handler.Count == 0, "no request for pre-cancelled call");
        });

        await CaseAsync("caller cancels primary, no fallback", async () =>
        {
            var entered = Signal();
            using var caller = new CancellationTokenSource();
            using var handler = Sequence(async token => { entered.SetResult(); return await WaitForCancellation(token); }, _ => Reply(Legacy(32)));
            using var client = Client(handler, 1000, 2000);
            var search = client.SearchAsync(SecretQuery, cancellationToken: caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            var error = await FailureAsync(() => search);
            Check(error is OperationCanceledException && handler.Count == 1, "primary caller cancellation does not fall back");
        });

        await CaseAsync("caller cancels fallback, cancellation remains visible", async () =>
        {
            var entered = Signal();
            using var caller = new CancellationTokenSource();
            using var handler = Sequence(_ => Reply("", 500), async token => { entered.SetResult(); return await WaitForCancellation(token); });
            using var client = Client(handler, 1000, 2000);
            var search = client.SearchAsync(SecretQuery, cancellationToken: caller.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            var error = await FailureAsync(() => search);
            Check(error is OperationCanceledException && handler.Count == 2, "fallback cancellation is not replaced by combined failure");
        });

        await CaseAsync("total budget covers both requests", async () =>
        {
            using var handler = Sequence(WaitForCancellation, WaitForCancellation);
            using var client = Client(handler, 60, 120);
            var clock = Stopwatch.StartNew();
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            clock.Stop();
            Check(clock.Elapsed < TimeSpan.FromSeconds(1.5), "total request budget bounded");
            Check(handler.Count == 2, "one fallback within total budget");
            CheckCombinedError(error, "", "");
            Check(error is not OperationCanceledException, "internal deadline is a search failure, not caller cancellation");
        });

        await CaseAsync("exhausted total budget never starts fallback", async () =>
        {
            using var handler = Sequence(WaitForCancellation, _ => Reply(Legacy(45)));
            using var client = Client(handler, 500, 60);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            Check(handler.Count == 1, "fallback cannot start after total deadline");
            CheckCombinedError(error, "", "");
        });

        await CaseAsync("slow handler cancellation cannot extend primary budget", async () =>
        {
            var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = Sequence(_ => late.Task, _ => Reply(Legacy(46)));
            using var client = Client(handler, 40, 500);
            var clock = Stopwatch.StartNew();
            try
            {
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 46 && clock.Elapsed < TimeSpan.FromSeconds(1.5), "late primary does not block fallback");
            }
            finally
            {
                var content = new TrackedContent(Primary(47));
                late.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                await content.DisposedTask.WaitAsync(TimeSpan.FromSeconds(2));
                Check(content.Disposed, "late response disposed after abandoned wait");
            }
            Check(handler.Count == 2, "late primary never starts another request");
        });

        await CaseAsync("body cancellation budget includes content read and eventual disposal", async () =>
        {
            var content = new GatedContent(Primary(51));
            using var handler = Sequence(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
                _ => Reply(Legacy(52)));
            using var client = Client(handler, 40, 500);
            try
            {
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 52 && content.ReadStarted, "body stall is bounded and falls back");
            }
            finally
            {
                content.Release();
                await content.DisposedTask.WaitAsync(TimeSpan.FromSeconds(2));
                Check(content.Disposed, "stalled response body eventually disposed");
            }
        });

        await CaseAsync("concurrent searches keep independent fallback state", async () =>
        {
            var primaryEntered = Signal();
            var fallbackEntered = Signal();
            using var handler = new FakeHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadAsStringAsync(token);
                    if (body.Contains("fast-query", StringComparison.Ordinal))
                    {
                        primaryEntered.SetResult();
                        await fallbackEntered.Task.WaitAsync(token);
                        return await Reply(Primary(41));
                    }
                    await primaryEntered.Task.WaitAsync(token);
                    return await Reply("", 500);
                }
                Check(request.RequestUri!.Query.Contains("slow-query", StringComparison.Ordinal), "fallback carries correct concurrent query");
                fallbackEntered.SetResult();
                return await Reply(Legacy(42));
            });
            using var client = Client(handler, 1000, 2000);
            var fast = client.SearchAsync("fast-query");
            var slow = client.SearchAsync("slow-query");
            await Task.WhenAll(fast, slow);
            Check(fast.Result.Single().SongId == 41 && slow.Result.Single().SongId == 42, "results stay bound to their requests");
            Check(handler.Count == 3 && handler.DisposedResponses == 3, "one primary success and one independent fallback");
        });

        Console.WriteLine(JsonSerializer.Serialize(new { passed = Failures.Count == 0, cases, checks,
            networkRequests = 0, failures = Failures }));
        return Failures.Count == 0 ? 0 : 1;
    }

    private static QQMusicCatalogClient Client(HttpMessageHandler handler, int primaryMs = 500, int totalMs = 1000) =>
        new(handler, TimeSpan.FromMilliseconds(primaryMs), TimeSpan.FromMilliseconds(totalMs));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FakeHandler Sequence(params Func<CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        var index = -1;
        return new FakeHandler((_, token) =>
        {
            var current = Interlocked.Increment(ref index);
            if (current >= steps.Length) throw new InvalidOperationException("unexpected extra request");
            return steps[current](token);
        });
    }
    private static Task<HttpResponseMessage> Reply(string body, int status = 200) => Task.FromResult(
        new HttpResponseMessage((HttpStatusCode)status) { Content = new TrackedContent(body) });
    private static async Task<HttpResponseMessage> WaitForCancellation(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("uncancellable wait completed");
    }
    private static async Task<Exception> FailureAsync(Func<Task<IReadOnlyList<QQMusicCatalogSong>>> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("expected search failure");
    }
    private static async Task CaseAsync(string name, Func<Task> action)
    {
        cases++;
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception error) { Failures.Add(name + ": " + error.GetType().Name + ": " + error.Message); }
    }
    private static void Check(bool value, string name)
    {
        Interlocked.Increment(ref checks);
        if (!value) throw new InvalidOperationException(name);
    }
    private static void CheckCombinedError(Exception error, string primaryDetail, string legacyDetail)
    {
        var message = error.Message;
        Check(message.Contains("musicu.fcg", StringComparison.OrdinalIgnoreCase)
            && message.Contains("client_search_cp", StringComparison.OrdinalIgnoreCase), "combined failure names both endpoints");
        if (primaryDetail.Length > 0) Check(message.Contains(primaryDetail, StringComparison.Ordinal), "primary failure detail");
        if (legacyDetail.Length > 0) Check(message.Contains(legacyDetail, StringComparison.Ordinal), "legacy failure detail");
        Check(!error.ToString().Contains(SecretQuery, StringComparison.Ordinal)
            && !error.ToString().Contains(SecretBody, StringComparison.Ordinal), "failure does not leak query/body through message or inner exception");
    }
    private static string Primary(params int[] ids) => JsonSerializer.Serialize(new
    {
        code = 0, search = new { code = 0, data = new { body = new { song = new { list = ids.Select(id => new
        { id, mid = "mid" + id, type = 0, name = "Song" + id, singer = new[] { new { name = "Artist" } },
            album = new { name = "Album", mid = "album" + id }, interval = 180 }).ToArray() } } } }
    });
    private static string Legacy(params int[] ids) => JsonSerializer.Serialize(new
    {
        code = 0, data = new { song = new { list = ids.Select(id => new
        { songid = id, songmid = "mid" + id, songtype = 0, songname = "Song" + id,
            singer = new[] { new { name = "Artist" } }, albumname = "Album", albummid = "album" + id, interval = 180 }).ToArray() } }
    });
}

internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int count;
    private readonly ConcurrentBag<TrackedContent> responses = [];
    internal int Count => Volatile.Read(ref count);
    internal int DisposedResponses => responses.Count(content => content.Disposed);
    internal List<(string Method, string Host)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref count);
        lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.Host));
        var response = await send(request, cancellationToken);
        if (response.Content is TrackedContent tracked) responses.Add(tracked);
        return response;
    }
}

internal sealed class TrackedContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
{
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Disposed { get; private set; }
    internal Task DisposedTask => disposed.Task;
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); disposed.TrySetResult(); }
}

internal sealed class GatedContent(string body) : HttpContent
{
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool ReadStarted { get; private set; }
    internal bool Disposed { get; private set; }
    internal Task DisposedTask => disposed.Task;
    internal void Release() => release.TrySetResult();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => WriteAsync(stream);
    private async Task WriteAsync(Stream stream)
    {
        ReadStarted = true;
        await release.Task;
        await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); disposed.TrySetResult(); }
}
