using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using KugouControlPoc;

// Run once per bitness. The connector itself is x86, so the x86 run covers
// the production direction (x86 connector, 64-bit KuGou):
//   dotnet run --project tests/KugouProcessImageHarness -c Release
//   & "${env:ProgramFiles(x86)}\dotnet\dotnet.exe" `
//     tests/KugouProcessImageHarness/bin/Release/net8.0/KugouProcessImageHarness.dll

static void Equal<T>(T expected, T actual, string label)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{label}: expected {expected}, actual {actual}");
    }
}

static void True(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException($"{label}: expected true");
    }
}

static void False(bool value, string label)
{
    if (value)
    {
        throw new InvalidOperationException($"{label}: expected false");
    }
}

static void InspectChild(string executable, bool expected64Bit)
{
    using var child = Process.Start(new ProcessStartInfo(executable, "/d /q")
    {
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException($"{executable}: did not start");
    var label = expected64Bit ? "64-bit cmd.exe" : "32-bit cmd.exe";
    try
    {
        var image = KugouProcessInspector.TryInspect(child.Id)
            ?? throw new InvalidOperationException($"{label}: inspection failed");
        Equal(expected64Bit, image.Is64Bit, $"{label} bitness");
        True(
            image.ExecutablePath.EndsWith(
                @"\cmd.exe",
                StringComparison.OrdinalIgnoreCase),
            $"{label} image path ({image.ExecutablePath})");
        True(image.Version.Length > 0, $"{label} file version");
        Equal(
            Path.Combine(
                Path.GetDirectoryName(image.ExecutablePath)!,
                "kugou.dll"),
            image.LocateKugouDll(),
            $"{label} kugou.dll fallback path");

        if (expected64Bit && !Environment.Is64BitProcess)
        {
            // The reported failure: an x86 process cannot read the module
            // list of a 64-bit process, which the inspector does not need.
            var mainModuleFailed = false;
            try
            {
                _ = child.MainModule;
            }
            catch (Win32Exception)
            {
                mainModuleFailed = true;
            }

            True(mainModuleFailed, "x86 MainModule of 64-bit process throws");
        }
    }
    finally
    {
        child.Kill();
        child.WaitForExit();
    }
}

True(
    KugouProcessInspector.Is64BitProcess(0, 0x8664),
    "native x64 process is 64-bit");
True(
    KugouProcessInspector.Is64BitProcess(0, 0xAA64),
    "native ARM64 or emulated x64 process is 64-bit");
False(
    KugouProcessInspector.Is64BitProcess(0x014C, 0x8664),
    "WOW64 x86 process on x64 Windows is 32-bit");
False(
    KugouProcessInspector.Is64BitProcess(0x014C, 0xAA64),
    "WOW64 x86 process on ARM64 Windows is 32-bit");
False(
    KugouProcessInspector.Is64BitProcess(0, 0x014C),
    "process on 32-bit Windows is 32-bit");
True(
    KugouProcessInspector.Is64BitProcess(
        isWow64: false,
        is64BitOperatingSystem: true),
    "IsWow64Process fallback: native process on 64-bit Windows");
False(
    KugouProcessInspector.Is64BitProcess(
        isWow64: true,
        is64BitOperatingSystem: true),
    "IsWow64Process fallback: WOW64 process");
False(
    KugouProcessInspector.Is64BitProcess(
        isWow64: false,
        is64BitOperatingSystem: false),
    "IsWow64Process fallback: 32-bit Windows");

Equal(
    "检测到 64 位酷狗（版本 20.1.41.27870），无切歌插入暂不支持，已使用兼容插入",
    KugouAnchorResetProfilePolicy.BuildUnsupported64BitPrompt("20.1.41.27870"),
    "64-bit KuGou status");
Equal(
    "检测到 64 位酷狗（版本 未知），无切歌插入暂不支持，已使用兼容插入",
    KugouAnchorResetProfilePolicy.BuildUnsupported64BitPrompt(string.Empty),
    "64-bit KuGou status without a version");
True(
    KugouAnchorResetProfilePolicy.Find(
        "20.0.81.27563",
        "193CEB92AC2281FCDC8A109BC533F3BC54FCCAFDA0CB1C0E61C0D140657F6132")
        is not null,
    "verified 32-bit anchor-reset profile is unchanged");

var hashDirectory = Directory.CreateTempSubdirectory("kugou-process-image-");
try
{
    // KuGou 20.1.41.27870 x64: KGMusic\KuGou.exe owns the player windows
    // and IPC endpoint, while kugou.dll lives in KGMusic\20.1.41.27870.
    var install = Directory.CreateDirectory(
        Path.Combine(hashDirectory.FullName, "KGMusic"));
    var versionedDll = Path.Combine(
        install.CreateSubdirectory("20.1.41.27870").FullName,
        "kugou.dll");
    File.WriteAllBytes(versionedDll, [9]);
    var launcher = new KugouProcessImage(
        Path.Combine(install.FullName, "KuGou.exe"),
        "20.1.41.27870",
        true);
    Equal(
        versionedDll,
        launcher.LocateKugouDll(),
        "kugou.dll in the folder named after the launcher version");
    var besideDll = Path.Combine(install.FullName, "kugou.dll");
    Equal(
        besideDll,
        (launcher with { Version = string.Empty }).LocateKugouDll(),
        "unknown version does not guess a versioned folder");
    File.WriteAllBytes(besideDll, [8]);
    Equal(
        besideDll,
        launcher.LocateKugouDll(),
        "kugou.dll next to KuGou.exe is preferred");

    var dll = Path.Combine(hashDirectory.FullName, "kugou.dll");
    File.WriteAllBytes(dll, [1, 2, 3]);
    Equal(
        Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })),
        KugouProcessInspector.TryHashFile(dll),
        "kugou.dll SHA-256");
    File.WriteAllBytes(dll, [4, 5, 6, 7]);
    Equal(
        Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6, 7 })),
        KugouProcessInspector.TryHashFile(dll),
        "changed kugou.dll is hashed again");
    Equal(
        string.Empty,
        KugouProcessInspector.TryHashFile(
            Path.Combine(hashDirectory.FullName, "missing.dll")),
        "missing kugou.dll has no hash");
}
finally
{
    hashDirectory.Delete(recursive: true);
}

True(KugouProcessInspector.TryInspect(0) is null, "PID 0 is rejected");
var self = KugouProcessInspector.TryInspect(Environment.ProcessId)
    ?? throw new InvalidOperationException("current process inspection failed");
True(
    string.Equals(
        self.ExecutablePath,
        Environment.ProcessPath,
        StringComparison.OrdinalIgnoreCase),
    $"current image path ({self.ExecutablePath})");
Equal(Environment.Is64BitProcess, self.Is64Bit, "current process bitness");
Equal(
    FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion
        ?? string.Empty,
    self.Version,
    "current file version");

if (Environment.Is64BitOperatingSystem)
{
    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    // WOW64 redirects System32 for an x86 process; Sysnative reaches the
    // real 64-bit directory.
    InspectChild(
        Path.Combine(
            windows,
            Environment.Is64BitProcess ? "System32" : "Sysnative",
            "cmd.exe"),
        expected64Bit: true);
    InspectChild(
        Path.Combine(windows, "SysWOW64", "cmd.exe"),
        expected64Bit: false);
}

Console.WriteLine(
    $"KuGou process image harness passed ({(Environment.Is64BitProcess ? "x64" : "x86")} host).");
