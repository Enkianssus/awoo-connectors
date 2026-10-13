using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QQMusicControlPoc;

internal sealed record Type4Request(Guid OperationId, int ProcessId, long StartTicks,
    string Executable, long SongId, int SongType);

internal static class Type4Contract
{
    internal static void ValidateRequest(Type4Request request)
    {
        if (request.OperationId == Guid.Empty || request.ProcessId <= 0
            || request.StartTicks <= 0 || request.StartTicks > DateTime.MaxValue.Ticks
            || request.SongId <= 0 || request.SongId > uint.MaxValue || request.SongType != 0
            || !Path.IsPathFullyQualified(request.Executable)
            || !Path.GetFileName(request.Executable).Equals("QQMusic.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(request.Executable).Equals(request.Executable, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A positive UInt32 song ID, song type 0, and exact QQ process identity are required.");
    }

    // Profile and request validation perform no process reads.
    internal static void VerifyProfile(QQMusicNativeNextProfile p) => QQMusicNativeProfileContract.Verify(p);

    internal static void VerifyIdentity(Type4Request request)
    {
        using var process = Process.GetProcessById(request.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != request.StartTicks
            || !string.Equals(process.MainModule?.FileName, request.Executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("QQ process identity changed.");
    }

    internal static long FileTimeToUtcTicks(long fileTime) => DateTime.FromFileTimeUtc(fileTime).Ticks;

    internal static bool OpenedHandleMatches(Type4Request request, long creationFileTime, string executable) =>
        FileTimeToUtcTicks(creationFileTime) == request.StartTicks
        && string.Equals(executable, request.Executable, StringComparison.OrdinalIgnoreCase);

    internal static void VerifyOpenedHandle(Type4Request request, SafeProcessHandle handle)
    {
        if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _))
            throw new InvalidOperationException("Could not verify the opened process creation time.");
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref length)
            || !OpenedHandleMatches(request, created, path.ToString()))
            throw new InvalidOperationException("The opened process handle does not match the authorized instance.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder path, ref int size);

    internal static List<FileStream> LockAndVerifyInstallation(Type4Request request, QQMusicNativeNextProfile profile)
    {
        var held = new List<FileStream>();
        try
        {
            ValidateRequest(request);
            VerifyProfile(profile);
            VerifyIdentity(request);
            using var process = Process.GetProcessById(request.ProcessId);
            var folder = Path.GetDirectoryName(request.Executable)!;
            foreach (var entry in new[] { ("QQMusic.dll", profile.ClientSha256), ("QQMusicCommon.dll", profile.CommonSha256), ("QQMusicApi.dll", profile.ApiSha256) })
            {
                var path = Path.Combine(folder, entry.Item1);
                var modules = process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName.Equals(entry.Item1, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1 || !modules[0].FileName.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Reviewed modules are not loaded from one installation.");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(stream);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Item2, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Installation hash does not match the selected QQ compatibility profile.");
            }
            if (FileVersionInfo.GetVersionInfo(Path.Combine(folder, "QQMusic.dll")).FileVersion != profile.FileVersion)
                throw new InvalidOperationException("QQMusic.dll version does not match the selected compatibility profile.");
            VerifyIdentity(request);
            return held;
        }
        catch
        {
            foreach (var stream in held) stream.Dispose();
            throw;
        }
    }
}
