using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

internal sealed class QQMusicWebStatusTransport
{
    internal const string HelperArgument = "--qq-web-status";

    internal async Task<QQMusicWebStatus> ReadAsync(int processId, long expectedStartTimeUtcTicks, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        Process? child = null;
        Task<string>? output = null;
        Task? errors = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (processId <= 0 || expectedStartTimeUtcTicks <= 0)
                return QQMusicWebStatus.Failed("status-request-invalid");
            using var target = Process.GetProcessById(processId);
            if (target.HasExited || target.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
                return QQMusicWebStatus.Failed("status-target-epoch-changed");
            var executable = target.MainModule?.FileName;
            if (executable is null) return QQMusicWebStatus.Failed("status-target-unavailable");
            var api = Path.Combine(Path.GetDirectoryName(executable)!, "QQMusicApi.dll");
            QQMusicWebStatusHost.RejectReparsePoints(executable);
            QQMusicWebStatusHost.RejectReparsePoints(api);
            using var lockedApi = new FileStream(api, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(lockedApi, deadline.Token));
            var request = new QQMusicWebStatusRequest(Guid.NewGuid(), processId, expectedStartTimeUtcTicks, executable, api, hash);
            QQMusicWebStatusProtocol.ValidateRequest(request);
            QQMusicWebStatusProtocol.ValidateApi(lockedApi, hash);
            QQMusicWebStatusHost.VerifyTarget(request);
            deadline.Token.ThrowIfCancellationRequested();
            child = new Process { StartInfo = CreateStartInfo() };
            if (!child.Start()) return QQMusicWebStatus.Failed("status-operation-failed");
            output = ReadOutputAsync(child.StandardOutput, deadline.Token);
            errors = DrainErrorsAsync(child.StandardError, deadline.Token);
            var wire = JsonSerializer.Serialize(request, QQMusicWebStatusProtocol.JsonOptions);
            if (wire.Length > QQMusicWebStatusProtocol.MaximumWireCharacters)
                throw new InvalidDataException("status-request-invalid");
            await child.StandardInput.WriteLineAsync(wire.AsMemory(), deadline.Token);
            await child.StandardInput.FlushAsync(deadline.Token);
            child.StandardInput.Close();
            await Task.WhenAll(output, errors, child.WaitForExitAsync(deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
            QQMusicWebStatusHost.VerifyTarget(request);
            var status = QQMusicWebStatusProtocol.ParseResponse(await output, request.OperationId);
            return child.ExitCode == 0 ? status : QQMusicWebStatus.Failed(
                status.Succeeded ? "status-helper-exit-failed" : status.Code);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return QQMusicWebStatus.Failed(error is OperationCanceledException
                ? token.IsCancellationRequested ? "status-cancelled" : "status-helper-deadline"
                : error.Message);
        }
        finally
        {
            deadline.Cancel();
            if (child is not null)
            {
                StopExactChild(child);
                child.Dispose();
            }
            // Observe task exceptions without extending the five-second call budget
            // if a platform pipe cancellation/cleanup misbehaves.
            ObserveFailure(output);
            ObserveFailure(errors);
        }
    }

    private static void ObserveFailure(Task? task)
    {
        if (task is not null) _ = task.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("status-helper-host-invalid");
        var executable = QQMusicWebHostPolicy.SelectExecutable(current, AppContext.BaseDirectory);
        if (!File.Exists(executable)) throw new InvalidOperationException("status-helper-host-invalid");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (QQMusicWebHostPolicy.IsDotnetHost(executable))
        {
#pragma warning disable IL3000
            var assembly = typeof(QQMusicWebStatusTransport).Assembly.Location;
#pragma warning restore IL3000
            if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("status-helper-host-invalid");
            info.ArgumentList.Add(assembly);
        }
        info.ArgumentList.Add(HelperArgument);
        return info;
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > QQMusicWebStatusProtocol.MaximumWireCharacters)
                throw new InvalidDataException("status-helper-output-invalid");
            text.Append(buffer, 0, count);
        }
        // Exactly one complete JSON response; extra native stdout or partial lines fail closed.
        var wire = text.ToString();
        if (!wire.EndsWith('\n') || wire[..^1].Contains('\n'))
            throw new InvalidDataException("status-helper-output-invalid");
        return wire.TrimEnd('\r', '\n');
    }

    private static async Task DrainErrorsAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1024];
        var total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            total += count;
            if (total > 65_536) throw new InvalidDataException("status-helper-output-invalid");
        }
    }

    private static void StopExactChild(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
