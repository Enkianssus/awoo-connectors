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
    internal const string ClientHash = "0108E68BEDA8B0AF61A71911519FA4FEB5DF02417D32F4A0993868E5157624BE";
    internal const string CommonHash = "4267E1C27F7251A31460613A97F5787FD7D9D0BE3664E5E615251ABA2780471F";
    internal const string ApiHash = "10ED93D19DDD4934111A7D789790DFA6953450F6F3DBD51E2CCF80BEB6B8F027";
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
    internal static void VerifyProfile(QQMusicNativeNextProfile p)
    {
        if (p.FileVersion != "22.71" || p.ClientSha256 != ClientHash || p.CommonSha256 != CommonHash
            || p.SingleSongPlayDispatchRva != 0x004B0CE4
            || !p.ExpectedPlayDispatchBytes.SequenceEqual(new byte[] { 0xE8, 0xC7, 0x91, 0x16, 0x00 })
            || p.GetCatManagerRva != 0x0000F18A || p.GetQqUinExRva != 0x0002E283
            || p.SongItemConstructorRva != 0x0004BB70 || p.SongItemDestructorRva != 0x0004B6B0
            || p.AddSongsRva != 0x00462600 || p.HiddenCategoryIdRva != 0x00C6B1C8
            || p.GetListRootRva != 0x0063CAF0 || p.GetListHelperRva != 0x0063CC50
            || p.GetCategoryCountRva != 0x005134E0 || p.SongItemSize != 0xA0)
            throw new InvalidOperationException("Only the validated 22.71 profile supports Type4 insertion.");
    }

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

    internal static List<FileStream> LockAndVerifyInstallation(Type4Request request)
    {
        var held = new List<FileStream>();
        try
        {
            VerifyIdentity(request);
            using var process = Process.GetProcessById(request.ProcessId);
            var folder = Path.GetDirectoryName(request.Executable)!;
            foreach (var entry in new[] { ("QQMusic.dll", ClientHash), ("QQMusicCommon.dll", CommonHash), ("QQMusicApi.dll", ApiHash) })
            {
                var path = Path.Combine(folder, entry.Item1);
                var modules = process.Modules.Cast<ProcessModule>().Where(m => m.ModuleName.Equals(entry.Item1, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1 || !modules[0].FileName.Equals(path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Reviewed modules are not loaded from one installation.");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(stream);
                if (Convert.ToHexString(SHA256.HashData(stream)) != entry.Item2)
                    throw new InvalidOperationException("Installation hash does not match reviewed 22.71.");
            }
            if (FileVersionInfo.GetVersionInfo(Path.Combine(folder, "QQMusic.dll")).FileVersion != "22.71")
                throw new InvalidOperationException("Exact QQMusic.dll version 22.71 required.");
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
