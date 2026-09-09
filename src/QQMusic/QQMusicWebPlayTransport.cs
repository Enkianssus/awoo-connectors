using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

/// <summary>
/// Supervises one bounded child and one typed request. Never injects into QQ,
/// retries a possibly submitted command, or falls back to fixed-RVA transport.
/// </summary>
internal sealed class QQMusicWebPlayTransport
{
    internal const string HelperArgument = "--qqmusic-web-bridge";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 16
    };

    internal async Task<QQMusicWebSubmission> PlayAsync(
        QQMusicWebSong song, int processId, CancellationToken cancellationToken,
        long? expectedProcessStartTimeUtcTicks = null,
        QQMusicWebIntent intent = QQMusicWebIntent.InsertNext)
    {
        QQMusicWebPlayRequest request;
        ProcessStartInfo startInfo;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = QQMusicWebProtocol.BuildRequest(song, intent);
            using var target = Process.GetProcessById(processId);
            var path = target.MainModule?.FileName;
            if (path is null || target.HasExited ||
                !string.Equals(Path.GetFileName(path), "QQMusic.exe", StringComparison.OrdinalIgnoreCase))
                return Rejected("qq-process-unavailable");
            var epoch = target.StartTime.ToUniversalTime().Ticks;
            if (expectedProcessStartTimeUtcTicks.HasValue && epoch != expectedProcessStartTimeUtcTicks.Value)
                return Rejected("qq-process-epoch-changed");
            var api = Path.Combine(Path.GetDirectoryName(path)!, "QQMusicApi.dll");
            using var source = File.OpenRead(api);
            var hash = Convert.ToHexString(SHA256.HashData(source));
            request = new(Guid.NewGuid(), processId, epoch, path, api, hash, song, intent);
            startInfo = CreateStartInfo();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Rejected(error is OperationCanceledException ? "cancelled-before-dispatch" : "web-preflight-failed");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(17));
        using var child = new Process { StartInfo = startInfo };
        var receipt = new QQMusicWebReceipt(request.OperationId);
        var mayHaveDeliveredInput = false;
        Task? readEvents = null;
        Task? drainErrors = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!child.Start()) return Rejected("web-helper-not-started");
            readEvents = ReadEventsAsync(child.StandardOutput, receipt, deadline.Token);
            drainErrors = DrainErrorsAsync(child.StandardError, deadline.Token);
            // Mark before the write: a partial/cancelled pipe write can still
            // deliver the full request. Child stdout is the only later witness.
            mayHaveDeliveredInput = true;
            await child.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request, JsonOptions).AsMemory(), deadline.Token);
            await child.StandardInput.FlushAsync(deadline.Token);
            child.StandardInput.Close();
            await Task.WhenAll(readEvents, drainErrors, child.WaitForExitAsync(deadline.Token));
            return receipt.Complete(child.ExitCode, interrupted: false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            deadline.Cancel();
            StopExactChild(child);
            if (readEvents is not null)
            {
                try { await readEvents; } catch (Exception readError) when (readError is not OutOfMemoryException) { }
            }
            if (drainErrors is not null)
            {
                try { await drainErrors; } catch (Exception readError) when (readError is not OutOfMemoryException) { }
            }
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await child.WaitForExitAsync(cleanupDeadline.Token); }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
            if (!mayHaveDeliveredInput) return Rejected("web-helper-start-failed");
            int? exitCode = null;
            try { if (child.HasExited) exitCode = child.ExitCode; } catch (InvalidOperationException) { }
            var unknown = receipt.Complete(exitCode, interrupted: true);
            var reason = error switch
            {
                OperationCanceledException => "cancelled-or-deadline",
                JsonException => "invalid-helper-json",
                InvalidDataException => "invalid-helper-output",
                IOException => "helper-pipe-failure",
                _ => "supervisor-failed"
            };
            return unknown with { Diagnostics = $"{unknown.Diagnostics}; supervisor={reason}" };
        }
        finally
        {
            StopExactChild(child);
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("host-path-unavailable");
        var executable = QQMusicWebHostPolicy.SelectExecutable(currentExecutable, AppContext.BaseDirectory);
        if (!File.Exists(executable)) throw new InvalidOperationException("web-helper-file-missing");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (QQMusicWebHostPolicy.IsDotnetHost(executable))
        {
            // Only dotnet-hosted (non-single-file) execution reaches this branch.
#pragma warning disable IL3000
            var assembly = typeof(QQMusicWebPlayTransport).Assembly.Location;
#pragma warning restore IL3000
            if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("assembly-path-unavailable");
            info.ArgumentList.Add(assembly);
        }
        info.ArgumentList.Add(HelperArgument);
        return info;
    }

    private static async Task ReadEventsAsync(StreamReader reader, QQMusicWebReceipt receipt, CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[1024];
        var total = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            total += count;
            if (total > 32_768) throw new InvalidDataException("bridge-output-limit");
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n')
                {
                    var item = JsonSerializer.Deserialize<QQMusicWebBridgeEvent>(line.ToString(), JsonOptions)
                        ?? throw new InvalidDataException("bridge-event-invalid");
                    receipt.Observe(item);
                    line.Clear();
                }
                else if (buffer[index] != '\r')
                {
                    line.Append(buffer[index]);
                    if (line.Length > 4096) throw new InvalidDataException("bridge-event-limit");
                }
            }
        }
        if (line.Length != 0) throw new InvalidDataException("bridge-event-truncated");
    }

    private static QQMusicWebSubmission Rejected(string code) => new(QQMusicWebSubmissionState.RejectedBeforeDispatch, code);

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var total = 0;
        int count;
        // Never forward native diagnostic text: it may contain private state.
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            total += count;
            if (total > 65_536) throw new InvalidDataException("bridge-error-output-limit");
        }
    }

    private static void StopExactChild(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
