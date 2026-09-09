using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QQMusicControlPoc;

// Only the isolated helper may enter this class. No arbitrary XML, initialization,
// account query, playback method, process launch, or native queue mutation exists here.
internal static class QQMusicWebStatusHost
{
    private const int DeadlineMilliseconds = 4000;

    internal static int RunFromStandardInput()
    {
        var clock = Stopwatch.StartNew();
        Trace(clock, "host-entered");
        using var completed = new ManualResetEventSlim();
        var exitCode = 2;
        var worker = new Thread(() =>
        {
            var operationId = Guid.Empty;
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X86)
                    throw new InvalidOperationException("status-requires-x86-sta");
                SetErrorMode(0x0001 | 0x0002 | 0x8000);
                if (!QQMusicWebHostPolicy.IsExternalApiHost(Environment.ProcessPath ?? string.Empty))
                    throw new InvalidOperationException("status-helper-host-invalid");
                var request = JsonSerializer.Deserialize<QQMusicWebStatusRequest>(ReadRequest(), QQMusicWebStatusProtocol.JsonOptions)
                    ?? throw new InvalidOperationException("status-request-invalid");
                operationId = request.OperationId;
                Trace(clock, "request-read");
                QQMusicWebStatusProtocol.ValidateRequest(request);
                RejectReparsePoints(request.ExecutablePath);
                RejectReparsePoints(request.ApiDllPath);
                VerifyTarget(request);
                using var lockedApi = new FileStream(request.ApiDllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                QQMusicWebStatusProtocol.ValidateApi(lockedApi, request.ApiSha256);
                VerifyTarget(request);
                CheckDeadline(clock);
                PeekMessage(out _, 0, 0, 0, 0);
                Trace(clock, "preflight-completed");
                QQMusicWebStatus status;
                // Dispose on the owning STA and before a success response. If native
                // cleanup blocks, the helper/parent deadline discards the observation.
                using (var client = QQMusicWebApiClient.Open(request.ApiDllPath))
                {
                    Trace(clock, "client-opened");
                    Pump(clock);
                    VerifyTarget(request);
                    CheckDeadline(clock);
                    var current = client.QueryCurrentOnce();
                    Trace(clock, "current-returned");
                    CheckDeadline(clock);
                    VerifyTarget(request);
                    Pump(clock);
                    var setting = client.QueryQueueSettingOnce();
                    Trace(clock, "setting-returned");
                    CheckDeadline(clock);
                    VerifyTarget(request);
                    status = QQMusicWebStatusProtocol.Parse(current, setting);
                    Trace(clock, "parse-completed");
                }
                Trace(clock, "client-disposed");
                CheckDeadline(clock);
                VerifyTarget(request);
                Write(new(operationId, status));
                Trace(clock, "response-written");
                exitCode = status.Succeeded ? 0 : 2;
            }
            catch (Exception error)
            {
                try { Write(new(operationId, QQMusicWebStatus.Failed(error.Message))); }
                catch { /* Broken parent pipe is not permission to retry. */ }
            }
            finally { completed.Set(); }
        }) { IsBackground = true, Name = "QQ fixed status isolated STA" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!completed.Wait(Math.Max(0, DeadlineMilliseconds - (int)clock.ElapsedMilliseconds)))
            ExitSelf(124); // No reporting lock can delay this exact-process watchdog.
        // Native libraries can retain worker threads after their owning RCWs have
        // been released. The worker signals only after every using/finally cleanup
        // has run; terminate this helper explicitly instead of waiting for them.
        Trace(clock, "exit-starting");
        ExitSelf(exitCode);
        return exitCode;
    }

    private static void ExitSelf(int exitCode)
    {
        // Normal DLL process-detach can block for seconds even after RCW Dispose.
        // Use only our own pseudo-handle, never a caller PID or OpenProcess. On
        // success the typed response has already been flushed and cleanup completed;
        // on deadline this is the isolated-helper watchdog, never a QQ termination.
        try { TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        // If the platform call failed or unexpectedly returned, fail conservatively.
        // The parent's exact-child deadline remains an independent outer boundary.
        Environment.Exit(exitCode);
    }

    private static void Trace(Stopwatch clock, string stage)
    {
        if (Environment.GetEnvironmentVariable("AWOO_QQ_WEB_STATUS_TRACE") == "1")
            Console.Error.WriteLine($"qq-status-timing {stage} {clock.ElapsedMilliseconds}");
    }

    private static string ReadRequest()
    {
        var text = new StringBuilder();
        for (;;)
        {
            var value = Console.In.Read();
            if (value is -1 or '\n') break;
            if (text.Length >= QQMusicWebStatusProtocol.MaximumWireCharacters)
                throw new InvalidOperationException("status-request-invalid");
            text.Append((char)value);
        }
        if (text.Length == 0) throw new InvalidOperationException("status-request-invalid");
        return text.ToString();
    }

    internal static void VerifyTarget(QQMusicWebStatusRequest request)
    {
        using var process = Process.GetProcessById(request.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != request.ProcessStartTimeUtcTicks)
            throw new InvalidOperationException("status-target-epoch-changed");
        var actual = process.MainModule?.FileName;
        if (actual is null || !string.Equals(Path.GetFullPath(actual),
            Path.GetFullPath(request.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("status-target-path-changed");
        var window = process.MainWindowHandle;
        if (window == 0 || !IsWindowVisible(window)) throw new InvalidOperationException("status-window-unavailable");
        GetWindowThreadProcessId(window, out var windowPid);
        if (windowPid != (uint)request.ProcessId) throw new InvalidOperationException("status-window-unavailable");
    }

    internal static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("status-target-reparse-point");
    }

    private static void Write(QQMusicWebStatusResponse response)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(response, QQMusicWebStatusProtocol.JsonOptions));
        Console.Out.Flush();
    }

    private static void CheckDeadline(Stopwatch clock)
    {
        if (clock.ElapsedMilliseconds >= DeadlineMilliseconds)
            throw new TimeoutException("status-helper-deadline");
    }

    private static void Pump(Stopwatch clock)
    {
        // WebPerform3 performs its synchronous read; this short native pump lets
        // any client setup/completion messages progress without WinForms.
        var pumpClock = Stopwatch.StartNew();
        do
        {
            CheckDeadline(clock);
            while (PeekMessage(out var message, 0, 0, 0, 1))
            {
                if (message.Message == 0x0012) throw new InvalidOperationException("status-operation-failed");
                TranslateMessage(ref message);
                DispatchMessage(ref message);
                CheckDeadline(clock);
            }
            var remaining = 25 - (int)pumpClock.ElapsedMilliseconds;
            if (remaining > 0 && MsgWaitForMultipleObjectsEx(0, 0, (uint)remaining, 0x04FF, 0x0004) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        } while (pumpClock.ElapsedMilliseconds < 25);
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
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint handles, uint milliseconds, uint wakeMask, uint flags);
}
