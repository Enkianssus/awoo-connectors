using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

/// <summary>
/// Child-process entry point, never run in the connector's normal process.
/// The parent supervises this exact child; neither side cancels or retries COM.
/// </summary>
internal static class QQMusicWebBridgeHost
{
    private const int DeadlineMilliseconds = 15_000;
    private const int DrainMilliseconds = 6_000;
    private const int MaximumRequestCharacters = 65_536;
    private static readonly object OutputSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16
    };

    internal static int RunFromStandardInput()
    {
        var clock = Stopwatch.StartNew();
        using var completed = new ManualResetEventSlim();
        var operation = new OperationIdentity();
        var exitCode = 2;
        var worker = new Thread(() =>
        {
            var stage = "request-read";
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X86)
                    throw new InvalidOperationException("requires-x86-sta");
                SetErrorMode(0x0001 | 0x0002 | 0x8000);
                var request = JsonSerializer.Deserialize<QQMusicWebPlayRequest>(ReadOneRequest(), JsonOptions)
                    ?? throw new InvalidOperationException("request-invalid");
                operation.Set(request.OperationId);
                stage = "target-validation";
                if (!QQMusicWebHostPolicy.IsExternalApiHost(Environment.ProcessPath ?? string.Empty))
                    throw new InvalidOperationException("helper-host-name-selects-internal-route");
                ValidateRequest(request);
                using var lockedApi = LockAndValidateApi(request);
                VerifyTargetProcess(request);
                stage = "command-validation";
                var command = QQMusicWebProtocol.BuildRequest(request.Song, request.Intent);
                CheckDeadline(clock);

                // Create the native thread's message queue before COM creates its
                // completion windows. No visible window or WinForms runtime is used.
                PeekMessage(out _, 0, 0, 0, 0);
                stage = "client-open";
                using (var client = QQMusicWebApiClient.Open(request.ApiDllPath))
                {
                    var drainIdentity = client.CaptureDrainIdentity(request.ApiSha256);
                    stage = "target-recheck";
                    VerifyTargetProcess(request);
                    CheckDeadline(clock);
                    stage = "dispatch-starting";
                    Write(new(request.OperationId, stage, "single-command-dispatch-reserved"));
                    // The flushed event makes all later outcomes ambiguous to the parent,
                    // including a crash or timeout between this line and the native call.
                    client.SendOnce(command);
                    stage = "submission-returned";
                    Write(new(request.OperationId, stage, "transport-returned-unconfirmed"));

                    // The audited sender-completion tuple may finish this drain
                    // early. All unknown/missing evidence keeps the full six seconds;
                    // neither path is delivery, queue or playback confirmation.
                    stage = "sender-drain";
                    var matchedCompletion = PumpForDrain(clock, drainIdentity);
                    TraceDrain(matchedCompletion);
                    VerifyTargetProcess(request);
                    stage = "client-dispose";
                }
                CheckDeadline(clock);
                stage = "drain-completed";
                Write(new(request.OperationId, stage, "observation-required"));
                exitCode = 0;
            }
            catch (Exception error)
            {
                exitCode = 2;
                // The parent may already have closed a timed-out child's pipe.
                // Never turn failure reporting into an unhandled worker exception.
                TryWrite(new(operation.Get(), stage, SafeErrorCode(error), error.HResult));
            }
            finally { completed.Set(); }
        }) { IsBackground = true, Name = "QQ Web bridge isolated STA" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        var remaining = Math.Max(0, DeadlineMilliseconds - (int)clock.ElapsedMilliseconds);
        if (!completed.Wait(remaining))
        {
            // Reporting is best-effort: a full stdout pipe or a blocked writer
            // must not prevent this watchdog from terminating the helper.
            try
            {
                Task.Run(() => TryWrite(new(operation.Get(), "helper-deadline", "helper-deadline")))
                    .Wait(50);
            }
            catch { }
            // A blocked synchronous COM call cannot safely be aborted. Exit only this
            // isolated process. The parent's exact-child watchdog is a second boundary.
            ExitSelf(124);
        }
        // completed is signalled only after the STA's using/finally cleanup and
        // flushed receipts. Avoid native DLL process-detach waits after that.
        ExitSelf(exitCode);
        return exitCode;
    }

    private static void ExitSelf(int exitCode)
    {
        // Only our own pseudo-handle is accepted here. Never terminate the target
        // QQ PID. The normal path has finished Dispose, drain and output flush;
        // the timeout path is the isolated helper's existing watchdog.
        try { TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        // Preserve the independent parent deadline if the platform call fails.
        Environment.Exit(exitCode);
    }

    private static string ReadOneRequest()
    {
        var text = new StringBuilder();
        for (;;)
        {
            var value = Console.In.Read();
            if (value is -1 or '\n') break;
            if (text.Length >= MaximumRequestCharacters)
                throw new InvalidOperationException("request-too-large");
            text.Append((char)value);
        }
        if (text.Length == 0) throw new InvalidOperationException("request-empty");
        return text.ToString();
    }

    private static void ValidateRequest(QQMusicWebPlayRequest request)
    {
        if (request.OperationId == Guid.Empty || request.ProcessId <= 0 ||
            request.ProcessStartTimeUtcTicks <= 0 || request.Song is null ||
            !QQMusicWebProtocol.IsDispatchIntentAllowed(request.Intent))
            throw new InvalidOperationException("request-invalid");
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) || string.IsNullOrWhiteSpace(request.ApiDllPath) ||
            !Path.IsPathFullyQualified(request.ExecutablePath) || !Path.IsPathFullyQualified(request.ApiDllPath) ||
            request.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            request.ApiDllPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("target-path-invalid");
        if (!string.Equals(Path.GetFileName(request.ExecutablePath), "QQMusic.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(request.ApiDllPath), "QQMusicApi.dll", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(request.ExecutablePath)),
                Path.GetDirectoryName(Path.GetFullPath(request.ApiDllPath)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("target-path-mismatch");
        if (request.ApiSha256 is null || request.ApiSha256.Length != 64 || !request.ApiSha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("api-hash-invalid");
        RejectReparsePoints(request.ExecutablePath);
        RejectReparsePoints(request.ApiDllPath);
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("target-reparse-point-rejected");
    }

    private static FileStream LockAndValidateApi(QQMusicWebPlayRequest request)
    {
        VerifyTargetProcess(request);
        var stream = new FileStream(request.ApiDllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actualHash = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(request.ApiSha256)))
                throw new InvalidOperationException("api-hash-mismatch");
            stream.Position = 0;
            Span<byte> dos = stackalloc byte[64];
            stream.ReadExactly(dos);
            if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidOperationException("api-not-pe");
            var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3c..]);
            if (offset < 64 || offset > stream.Length - 26)
                throw new InvalidOperationException("api-pe-offset-invalid");
            stream.Position = offset;
            Span<byte> pe = stackalloc byte[26];
            stream.ReadExactly(pe);
            if (!pe[..4].SequenceEqual(new byte[] { (byte)'P', (byte)'E', 0, 0 }) ||
                BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) != 0x014c ||
                BinaryPrimitives.ReadUInt16LittleEndian(pe[24..]) != 0x010b)
                throw new InvalidOperationException("api-not-x86-pe");
            var optionalHeaderBytes = BinaryPrimitives.ReadUInt16LittleEndian(pe[20..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(pe[6..]) == 0 || optionalHeaderBytes < 96 ||
                (BinaryPrimitives.ReadUInt16LittleEndian(pe[22..]) & 0x2000) == 0 ||
                (long)offset + 24 + optionalHeaderBytes > stream.Length)
                throw new InvalidOperationException("api-invalid-pe-header");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void VerifyTargetProcess(QQMusicWebPlayRequest request)
    {
        // Bind only the supplied PID. Other QQ helper processes are not targets.
        using var process = Process.GetProcessById(request.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != request.ProcessStartTimeUtcTicks)
            throw new InvalidOperationException("qq-process-epoch-mismatch");
        var actualPath = process.MainModule?.FileName;
        if (actualPath is null || !string.Equals(Path.GetFullPath(actualPath),
                Path.GetFullPath(request.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(actualPath), "QQMusic.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("qq-executable-path-mismatch");
        var window = process.MainWindowHandle;
        if (window == 0 || !IsWindowVisible(window))
            throw new InvalidOperationException("qq-visible-window-missing");
        GetWindowThreadProcessId(window, out var windowProcessId);
        if (windowProcessId != (uint)request.ProcessId)
            throw new InvalidOperationException("qq-window-process-mismatch");
    }

    private static bool PumpForDrain(Stopwatch overallClock, QQMusicWebDrainIdentity identity)
    {
        var drainClock = Stopwatch.StartNew();
        while (drainClock.ElapsedMilliseconds < DrainMilliseconds)
        {
            CheckDeadline(overallClock);
            while (PeekMessage(out var message, 0, 0, 0, 1))
            {
                if (message.Message == 0x0012)
                    throw new InvalidOperationException("message-loop-terminated");
                var candidate = QQMusicWebDrainPolicy.IsCompletionCandidate(identity,
                    message.Window, message.Message, message.WParam, message.LParam);
                var before = candidate ? ReadDrainWindow(message.Window) : null;
                TranslateMessage(ref message);
                // The native handler owns the paired Release. Never dereference
                // or manually Release a pointer received in a window message.
                var dispatchResult = DispatchMessage(ref message);
                CheckDeadline(overallClock);
                if (candidate && QQMusicWebDrainPolicy.CanFinishDrain(identity,
                    message.Window, message.Message, message.WParam, message.LParam,
                    before, ReadDrainWindow(message.Window), dispatchResult))
                    return true;
            }
            var remaining = Math.Min(100, DrainMilliseconds - (int)drainClock.ElapsedMilliseconds);
            if (remaining > 0 && MsgWaitForMultipleObjectsEx(0, 0, (uint)remaining, 0x04FF, 0x0004) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return false;
    }

    private static void TraceDrain(bool matchedCompletion)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AWOO_QQMUSIC_WEB_TRACE"), "1", StringComparison.Ordinal))
            return;
        try
        {
            // Diagnostic-only stderr, never a new receipt event or native text.
            Console.Error.WriteLine(matchedCompletion
                ? "qq-web-drain:matched-completion" : "qq-web-drain:fallback-timeout");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static QQMusicWebDrainWindow? ReadDrainWindow(nint window)
    {
        // Inspect only this message's HWND, never enumerate other threads' windows
        // or inspect WndProc code (ATL uses a generated thunk). Repeated after Dispatch.
        try
        {
            if (window == 0 || !IsWindow(window)) return null;
            var threadId = GetWindowThreadProcessId(window, out var processId);
            var className = new StringBuilder(256);
            if (threadId == 0 || processId == 0 || GetClassName(window, className, className.Capacity) == 0)
                return null;
            // This host is x86; GCL_HMODULE is GetClassLongW, not the x64 alias.
            var classModule = (nint)(nuint)GetClassLong(window, -16);
            if (classModule == 0 || !IsWindow(window)) return null;
            return new(window, processId, threadId, classModule, className.ToString(), IsWindowVisible(window));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static void CheckDeadline(Stopwatch clock)
    {
        if (clock.ElapsedMilliseconds >= DeadlineMilliseconds)
            throw new TimeoutException("helper-deadline");
    }

    private static string SafeErrorCode(Exception error) => error.Message switch
    {
        "requires-x86-sta" or "requires-owning-sta" or "api-path-not-absolute" or
        "helper-host-name-selects-internal-route" or
        "factory-not-returned" or "client-not-returned" or "dispatch-budget-exhausted" or
        "request-invalid" or "request-empty" or "request-too-large" or "target-path-invalid" or
        "target-path-mismatch" or "api-hash-invalid" or "target-reparse-point-rejected" or
        "api-hash-mismatch" or "api-not-pe" or "api-pe-offset-invalid" or "api-not-x86-pe" or "api-invalid-pe-header" or
        "qq-process-epoch-mismatch" or "qq-executable-path-mismatch" or "qq-visible-window-missing" or
        "qq-window-process-mismatch" or "message-loop-terminated" or "helper-deadline" => error.Message,
        _ => "operation-failed"
    };

    private static void Write(QQMusicWebBridgeEvent item)
    {
        lock (OutputSync)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
            Console.Out.Flush();
        }
    }

    private static void TryWrite(QQMusicWebBridgeEvent item)
    {
        try { Write(item); }
        catch { /* The exact-child supervisor also observes the exit code. */ }
    }

    private sealed class OperationIdentity
    {
        private readonly object sync = new();
        private Guid value;
        internal Guid Get() { lock (sync) return value; }
        internal void Set(Guid operationId) { lock (sync) value = operationId; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximum);
    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong(nint window, int index);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);
}
