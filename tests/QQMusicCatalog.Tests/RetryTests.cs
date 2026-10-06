using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using QQMusicControlPoc;

internal static partial class CatalogTests
{
    private static async Task RetryCasesAsync()
    {
        foreach (var layer in new[] { "root", "search", "search.data" })
        {
            await CaseAsync($"primary 2001 at {layer} retries once and recovers", async () =>
            {
                using var handler = Sequence(_ => Reply(PrimaryError(layer, 2001)), _ => Reply(Primary(61)));
                using var client = RetryClient(handler);
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 61 && handler.Count == 2, "retry returns primary song");
                Check(handler.Requests.All(request => request.Method == "POST" && request.Host == "u.y.qq.com"),
                    "retry reuses primary endpoint without unnecessary legacy request");
                Check(handler.DisposedResponses == 2, "failed primary and successful retry responses disposed");
            });

            await CaseAsync($"persistent primary 2001 at {layer} retains attempt diagnostics", async () =>
            {
                using var handler = Sequence(_ => Reply(PrimaryError(layer, 2001)),
                    _ => Reply(PrimaryError(layer, 2001)), _ => Reply(SecretBody, 500));
                using var client = RetryClient(handler);
                var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
                CheckCombinedError(error, "2001", "500");
                Check(error.Message.Contains(layer + ".code", StringComparison.Ordinal), "business failure identifies JSON code layer");
                Check(error.Message.Contains("attempt 1", StringComparison.OrdinalIgnoreCase)
                    && error.Message.Contains("attempt 2", StringComparison.OrdinalIgnoreCase), "both primary attempts remain diagnostic");
                CheckRequestOrder(handler, "POST", "POST", "GET");
                Check(handler.DisposedResponses == 3, "all persistent failure responses disposed");
            });
        }

        await CaseAsync("persistent primary 2001 falls back to legacy only once", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("root", 2001)),
                _ => Reply(PrimaryError("search.data", 2001)), _ => Reply(Legacy(62)));
            using var client = RetryClient(handler);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 62, "legacy rescues persistent primary business failure");
            CheckRequestOrder(handler, "POST", "POST", "GET");
        });

        await CaseAsync("compatible three-argument constructor applies default short retry delay", async () =>
        {
            var gap = new Stopwatch();
            using var handler = Sequence(_ =>
            {
                gap.Start();
                return Reply(PrimaryError("root", 2001));
            }, _ =>
            {
                gap.Stop();
                return Reply(Primary(73));
            });
            using var client = Client(handler, 2000, 3000);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 73 && gap.Elapsed >= TimeSpan.FromMilliseconds(250),
                "default constructor waits briefly before retrying instead of resending immediately");
            CheckRequestOrder(handler, "POST", "POST");
        });

        var secondFailures = new Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>>
        {
            ["HTTP failure"] = _ => Reply(SecretBody, 500),
            ["transport failure"] = _ => throw new HttpRequestException(SecretQuery + SecretBody),
            ["other business failure"] = _ => Reply(PrimaryError("search", 1001)),
            ["empty results"] = _ => Reply(Primary()),
            ["invalid JSON"] = _ => Reply(SecretBody)
        };
        foreach (var scenario in secondFailures)
        {
            await CaseAsync("primary retry " + scenario.Key + " goes directly to legacy", async () =>
            {
                using var handler = Sequence(_ => Reply(PrimaryError("search", 2001)), scenario.Value, _ => Reply(Legacy(63)));
                using var client = RetryClient(handler);
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 63, "second failure permits existing legacy fallback");
                CheckRequestOrder(handler, "POST", "POST", "GET");
            });
        }

        await CaseAsync("legacy 2001 does not trigger a legacy or primary retry", async () =>
        {
            var legacyError = Legacy(64).Replace("\"code\":0", "\"code\":2001", StringComparison.Ordinal);
            using var handler = Sequence(_ => Reply(SecretBody, 500), _ => Reply(legacyError));
            using var client = RetryClient(handler);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "500", "2001");
            CheckRequestOrder(handler, "POST", "GET");
        });

        var nonRetryable2001 = new Dictionary<string, (string Body, int Status)>
        {
            ["HTTP error body"] = (PrimaryError("root", 2001), 500),
            ["unrelated message text"] = (PrimaryError("root", 1001).Replace(SecretBody, "2001", StringComparison.Ordinal), 200),
            ["invalid string code"] = (PrimaryError("root", 2001).Replace("\"code\":2001", "\"code\":\"2001\"", StringComparison.Ordinal), 200)
        };
        foreach (var scenario in nonRetryable2001)
        {
            await CaseAsync("2001 appearing in " + scenario.Key + " does not qualify for retry", async () =>
            {
                using var handler = Sequence(_ => Reply(scenario.Value.Body, scenario.Value.Status), _ => Reply(Legacy(74)));
                using var client = RetryClient(handler);
                var songs = await client.SearchAsync(SecretQuery);
                Check(songs.Single().SongId == 74, "only a parsed successful-HTTP numeric API code 2001 receives retry");
                CheckRequestOrder(handler, "POST", "GET");
            });
        }

        await CaseAsync("retry redacts different layer failures and legacy exception text", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("root", 2001)),
                _ => Reply(PrimaryError("search.data", 10005)),
                _ => throw new HttpRequestException(SecretQuery + SecretBody));
            using var client = RetryClient(handler);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "2001", "10005");
            Check(error.Message.Contains("root.code", StringComparison.Ordinal)
                && error.Message.Contains("search.data.code", StringComparison.Ordinal), "attempt-specific JSON layers preserved");
            CheckRequestOrder(handler, "POST", "POST", "GET");
        });

        await CaseAsync("caller cancellation interrupts retry delay without another request", async () =>
        {
            using var caller = new CancellationTokenSource();
            var content = new TrackedContent(PrimaryError("root", 2001));
            using var handler = Sequence(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }),
                _ => Reply(Primary(65)));
            using var client = RetryClient(handler, primaryMs: 3000, totalMs: 4000, retryMs: 2000);
            var search = client.SearchAsync(SecretQuery, cancellationToken: caller.Token);
            await content.DisposedTask.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            Check(handler.Count == 1 && !search.IsCompleted, "retry delay occurs before any second request");
            caller.Cancel();
            var error = await FailureAsync(() => search);
            Check(error is OperationCanceledException && handler.Count == 1, "delay cancellation propagates without retry or legacy");
        });

        await CaseAsync("caller cancellation interrupts second primary without fallback", async () =>
        {
            var retryEntered = Signal();
            using var caller = new CancellationTokenSource();
            using var handler = Sequence(_ => Reply(PrimaryError("search", 2001)), async token =>
            {
                retryEntered.SetResult();
                return await WaitForCancellation(token);
            }, _ => Reply(Legacy(66)));
            using var client = RetryClient(handler, primaryMs: 2000, totalMs: 3000);
            var search = client.SearchAsync(SecretQuery, cancellationToken: caller.Token);
            await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            caller.Cancel();
            var error = await FailureAsync(() => search);
            Check(error is OperationCanceledException, "retry caller cancellation remains cancellation");
            CheckRequestOrder(handler, "POST", "POST");
        });

        await CaseAsync("retry delay consumes primary budget but leaves legacy budget", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("search", 2001)), _ => Reply(Legacy(67)));
            using var client = RetryClient(handler, primaryMs: 120, totalMs: 2000, retryMs: 1000);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 67, "legacy retains remaining total budget after retry delay expires");
            CheckRequestOrder(handler, "POST", "GET");
        });

        await CaseAsync("expired retry delay is not reported as a sent second primary attempt", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("search", 2001)), _ => Reply(SecretBody, 500));
            using var client = RetryClient(handler, primaryMs: 120, totalMs: 2000, retryMs: 1000);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "2001", "500");
            Check(error.Message.Contains("retry not attempted", StringComparison.OrdinalIgnoreCase)
                && !error.Message.Contains("attempt 2", StringComparison.OrdinalIgnoreCase),
                "diagnostic distinguishes skipped retry from sent second request");
            CheckRequestOrder(handler, "POST", "GET");
        });

        await CaseAsync("primary retry shares original deadline instead of receiving a fresh timeout", async () =>
        {
            var retryClock = new Stopwatch();
            using var handler = Sequence(async token =>
            {
                await Task.Delay(700, token);
                return await Reply(PrimaryError("root", 2001));
            }, async token =>
            {
                retryClock.Start();
                try { return await WaitForCancellation(token); }
                finally { retryClock.Stop(); }
            }, _ => Reply(Legacy(68)));
            using var client = RetryClient(handler, primaryMs: 1000, totalMs: 3000);
            var songs = await client.SearchAsync(SecretQuery);
            Check(songs.Single().SongId == 68, "shared deadline still allows legacy fallback");
            CheckRequestOrder(handler, "POST", "POST", "GET");
            Check(retryClock.Elapsed < TimeSpan.FromMilliseconds(650), "second request has only original primary budget remainder");
        });

        await CaseAsync("total deadline during retry delay prevents both retry and fallback", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("root", 2001)), _ => Reply(Primary(69)));
            using var client = RetryClient(handler, primaryMs: 2000, totalMs: 120, retryMs: 1000);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "2001", "total timeout");
            Check(error is not OperationCanceledException, "internal total timeout is not caller cancellation");
            CheckRequestOrder(handler, "POST");
        });

        await CaseAsync("total deadline during second primary prevents legacy", async () =>
        {
            using var handler = Sequence(_ => Reply(PrimaryError("search.data", 2001)), WaitForCancellation,
                _ => Reply(Legacy(70)));
            using var client = RetryClient(handler, primaryMs: 2000, totalMs: 120);
            var error = await FailureAsync(() => client.SearchAsync(SecretQuery));
            CheckCombinedError(error, "2001", "total timeout");
            CheckRequestOrder(handler, "POST", "POST");
        });

        await CaseAsync("concurrent 2001 searches keep retry counters and results independent", async () =>
        {
            var bothEntered = Signal();
            var firstCount = 0;
            var attempts = new ConcurrentDictionary<string, int>();
            using var handler = new FakeHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Check(request.RequestUri!.Query.Contains("fallback-retry", StringComparison.Ordinal), "legacy keeps its own query");
                    return await Reply(Legacy(72));
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var query = body.RootElement.GetProperty("search").GetProperty("param").GetProperty("query").GetString()!;
                var attempt = attempts.AddOrUpdate(query, 1, (_, previous) => previous + 1);
                if (attempt == 1)
                {
                    if (Interlocked.Increment(ref firstCount) == 2) bothEntered.SetResult();
                    await bothEntered.Task.WaitAsync(token);
                    return await Reply(PrimaryError("root", 2001));
                }
                Check(attempt == 2, "concurrent request has at most one primary retry");
                return await Reply(query == "successful-retry" ? Primary(71) : PrimaryError("search", 2001));
            });
            using var client = RetryClient(handler, primaryMs: 1500, totalMs: 2500);
            var successful = client.SearchAsync("successful-retry");
            var fallback = client.SearchAsync("fallback-retry");
            await Task.WhenAll(successful, fallback);
            Check(successful.Result.Single().SongId == 71 && fallback.Result.Single().SongId == 72,
                "one search retry success does not alter the other search fallback");
            Check(handler.Count == 5 && handler.DisposedResponses == 5 && attempts.Values.All(value => value == 2),
                "each search independently retries once and disposes all responses");
        });

        await CaseAsync("raw primary helper returns 2001 without policy retries", async () =>
        {
            var body = PrimaryError("root", 2001);
            using var handler = Sequence(_ => Reply(body));
            using var client = RetryClient(handler);
            Check(await client.SearchRawAsync(SecretQuery) == body, "raw helper preserves the service body");
            CheckRequestOrder(handler, "POST");
        });
    }

    private static QQMusicCatalogClient RetryClient(HttpMessageHandler handler,
        int primaryMs = 1000, int totalMs = 2000, int retryMs = 0) =>
        new(handler, TimeSpan.FromMilliseconds(primaryMs), TimeSpan.FromMilliseconds(totalMs), TimeSpan.FromMilliseconds(retryMs));

    private static void CheckRequestOrder(FakeHandler handler, params string[] methods) =>
        Check(handler.Count == methods.Length && handler.Requests.Select(request => request.Method).SequenceEqual(methods),
            "request order remains " + string.Join(" -> ", methods));

    private static string PrimaryError(string layer, int code)
    {
        var body = Primary(60);
        var error = $"\"code\":{code},\"message\":\"{SecretQuery} {SecretBody}\"";
        return layer switch
        {
            "root" => body.Replace("\"code\":0,\"search\"", error + ",\"search\"", StringComparison.Ordinal),
            "search" => body.Replace("\"search\":{\"code\":0", "\"search\":{" + error, StringComparison.Ordinal),
            "search.data" => body.Replace("\"data\":{", "\"data\":{" + error + ",", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
    }
}
