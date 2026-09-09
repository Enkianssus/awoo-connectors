using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QQMusicControlPoc;

internal sealed record QQMusicWindowInfo(
    long Handle,
    int ProcessId,
    string ProcessName,
    string ClassName,
    string Title,
    bool IsVisible);

internal sealed record QQMusicPlaybackState(
    bool IsRunning,
    string? Title,
    string? Artist,
    long? WindowHandle,
    string? WindowTitle,
    DateTimeOffset ObservedAt,
    int? ProcessId = null);

internal static class QQMusicNativeController
{
    public static IReadOnlyList<QQMusicWindowInfo> InspectWindows()
    {
        var windows = new List<QQMusicWindowInfo>();
        Process[] processes;
        try
        {
            // A fresh candidate snapshot avoids a process-name lookup for every
            // desktop window. Neither process IDs nor window text are cached.
            processes = Process.GetProcessesByName("QQMusic");
        }
        catch (Exception error) when (IsProcessInspectionFailure(error))
        {
            return windows;
        }

        try
        {
            var candidates = new Dictionary<int, (Process Process, string Name)>();
            foreach (var process in processes)
            {
                try
                {
                    var name = process.ProcessName;
                    if (name.Equals("QQMusic", StringComparison.OrdinalIgnoreCase)
                        && !process.HasExited)
                    {
                        candidates.TryAdd(process.Id, (process, name));
                    }
                }
                catch (Exception error) when (IsProcessInspectionFailure(error))
                {
                    // An exited or inaccessible candidate is not a QQ window.
                }
            }

            if (candidates.Count == 0)
            {
                return windows;
            }

            if (!EnumWindows(
                (handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var processId);
                    if (processId == 0 || processId > int.MaxValue
                        || !candidates.TryGetValue((int)processId, out var candidate))
                    {
                        return true;
                    }

                    try
                    {
                        if (candidate.Process.HasExited)
                        {
                            return true;
                        }

                        var window = new QQMusicWindowInfo(
                            handle,
                            (int)processId,
                            candidate.Name,
                            ReadClassName(handle),
                            ReadWindowText(handle),
                            IsWindowVisible(handle));
                        GetWindowThreadProcessId(handle, out var currentProcessId);
                        if (currentProcessId == processId && !candidate.Process.HasExited)
                        {
                            windows.Add(window);
                        }
                    }
                    catch (Exception error) when (IsProcessInspectionFailure(error))
                    {
                        // The candidate exited or became inaccessible during enumeration.
                    }

                    return true;
                },
                0))
            {
                windows.Clear();
            }

            return windows;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsProcessInspectionFailure(Exception error) =>
        error is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException;

    public static QQMusicPlaybackState ReadPlaybackState()
    {
        var window = FindMainWindow();
        if (window is null)
        {
            return new QQMusicPlaybackState(
                false,
                null,
                null,
                null,
                null,
                DateTimeOffset.Now);
        }

        var parsed = QQMusicWindowTitleParser.Parse(window.Title);
        return new QQMusicPlaybackState(
            true,
            parsed?.Title,
            parsed?.Artist,
            window.Handle,
            window.Title,
            DateTimeOffset.Now,
            window.ProcessId);
    }

    private static QQMusicWindowInfo? FindMainWindow()
    {
        return InspectWindows()
            .Where(window => window.IsVisible)
            .OrderByDescending(window =>
                QQMusicWindowTitleParser.Parse(window.Title) is not null)
            .ThenByDescending(window =>
                window.Title.Equals(
                    "QQ音乐",
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(window => window.Title.Length)
            .FirstOrDefault();
    }

    private static string ReadWindowText(nint handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ReadClassName(nint handle)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsCallback(nint handle, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint handle,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint handle);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetWindowText(
        nint handle,
        StringBuilder text,
        int maximum);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern int GetClassName(
        nint handle,
        StringBuilder className,
        int maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
}
