using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace KugouControlPoc;

/// <summary>
/// Facts about a running KuGou process that can be read across bitness.
/// The connector is x86, and an x86 process cannot read the module list of
/// 64-bit KuGou (Process.MainModule and Process.Modules throw), so the image
/// path comes from QueryFullProcessImageName and the architecture from
/// IsWow64Process2.  <see cref="Is64Bit"/> is true only when Windows
/// confirmed a 64-bit image.
/// </summary>
internal sealed record KugouProcessImage(
    string ExecutablePath,
    string Version,
    bool Is64Bit)
{
    /// <summary>
    /// Finds kugou.dll from the executable instead of the process module
    /// list: next to KuGou.exe, or, for the KuGou.exe at the install root
    /// that owns the player windows, in the folder named after its file
    /// version (for example KGMusic\20.1.41.27870\kugou.dll).
    /// </summary>
    internal string LocateKugouDll()
    {
        var directory = Path.GetDirectoryName(ExecutablePath) ?? string.Empty;
        var besideExecutable = Path.Combine(directory, "kugou.dll");
        if (File.Exists(besideExecutable) || string.IsNullOrWhiteSpace(Version))
        {
            return besideExecutable;
        }

        var versioned = Path.Combine(directory, Version.Trim(), "kugou.dll");
        return File.Exists(versioned) ? versioned : besideExecutable;
    }
}

internal static class KugouProcessInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumImagePathLength = 32768;
    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineIa64 = 0x0200;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;
    private static readonly object HashSync = new();
    private static string _hashedFileKey = string.Empty;
    private static string _hashedFileSha256 = string.Empty;

    /// <summary>
    /// Returns null when the process is gone or its image path cannot be
    /// queried; callers then keep their unknown-version behavior.
    /// </summary>
    internal static KugouProcessImage? TryInspect(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                processId);
            if (process.IsInvalid)
            {
                return null;
            }

            var path = QueryImagePath(process);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return new KugouProcessImage(
                path,
                ReadFileVersion(path),
                QueryIs64Bit(process));
        }
        catch
        {
            return null;
        }
    }

    internal static bool Is64BitProcess(
        ushort processMachine,
        ushort nativeMachine)
    {
        // IsWow64Process2 reports IMAGE_FILE_MACHINE_UNKNOWN for a process
        // that does not run under WOW64.  Such a process has the native
        // architecture; x64 processes emulated on ARM64 are reported the
        // same way.
        return processMachine == ImageFileMachineUnknown
            && nativeMachine is ImageFileMachineAmd64
                or ImageFileMachineArm64
                or ImageFileMachineIa64;
    }

    internal static bool Is64BitProcess(
        bool isWow64,
        bool is64BitOperatingSystem)
    {
        return !isWow64 && is64BitOperatingSystem;
    }

    /// <summary>
    /// SHA-256 of a file such as kugou.dll, or empty when it cannot be read.
    /// The last result is reused while the path, size and write time are
    /// unchanged, because the insertion path asks for it on every request.
    /// </summary>
    internal static string TryHashFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return string.Empty;
            }

            var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (HashSync)
            {
                if (key == _hashedFileKey)
                {
                    return _hashedFileSha256;
                }
            }

            string sha256;
            using (var stream = File.OpenRead(file.FullName))
            {
                sha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            lock (HashSync)
            {
                _hashedFileKey = key;
                _hashedFileSha256 = sha256;
            }

            return sha256;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string QueryImagePath(SafeProcessHandle process)
    {
        var buffer = new char[1024];
        while (true)
        {
            var size = buffer.Length;
            if (QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return new string(buffer, 0, size);
            }

            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer
                || buffer.Length >= MaximumImagePathLength)
            {
                return string.Empty;
            }

            buffer = new char[MaximumImagePathLength];
        }
    }

    private static string ReadFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion
                ?? string.Empty;
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private static bool QueryIs64Bit(SafeProcessHandle process)
    {
        try
        {
            if (IsWow64Process2(
                    process,
                    out var processMachine,
                    out var nativeMachine))
            {
                return Is64BitProcess(processMachine, nativeMachine);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows builds without IsWow64Process2 use the older query.
        }

        return IsWow64Process(process, out var isWow64)
            && Is64BitProcess(isWow64, Environment.Is64BitOperatingSystem);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out] char[] exeName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        SafeProcessHandle process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(
        SafeProcessHandle process,
        [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
}
