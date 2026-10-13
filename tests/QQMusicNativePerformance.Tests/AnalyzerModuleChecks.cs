using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using QQMusicControlPoc;

internal static class AnalyzerModuleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "qq-analyzer-modules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var clientGetterExecutable in new[] { true, false })
            {
                var clientPath = Path.Combine(directory, "QQMusic.dll");
                var commonPath = Path.Combine(directory, "QQMusicCommon.dll");
                var apiPath = Path.Combine(directory, "QQMusicApi.dll");
                File.WriteAllBytes(clientPath, MakePe(clientGetterExecutable));
                File.WriteAllBytes(commonPath, MakePe(true, exports: true));
                File.WriteAllBytes(apiPath, MakePe(!clientGetterExecutable));
                string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                var profile = new QQMusicNativeNextProfile(
                    "synthetic-test", Hash(clientPath), Hash(commonPath), 0x1000,
                    [0xE8, 0x3B, 0, 0, 0], 0x1010, 0x1020,
                    0x1030, 0x1040, 0x1020, 0x3020, 0x1050, 0x1060, 0x1070,
                    0xA0, "Synthetic file-only module ownership regression", 2, Hash(apiPath),
                    QQMusicNativeProfileContract.CStringAbi, QQMusicNativeProfileContract.Type4Transport, 0x2000);

                // Exercise the production PE reader and full BuildChecks path.
                // Reflection avoids adding a production test hook or evaluating
                // any native function in these inert, synthetic PE files.
                var analyzer = typeof(QQMusicNativeNextAnalyzer);
                var image = analyzer.GetNestedType("PortableExecutableImage", BindingFlags.NonPublic)!;
                var load = image.GetMethod("Load", BindingFlags.Static | BindingFlags.Public)!;
                object Load(string file) => load.Invoke(null, [file])!;
                var build = analyzer.GetMethod("BuildChecks", BindingFlags.Static | BindingFlags.NonPublic)!;
                var checks = (IReadOnlyList<QQMusicNativeNextAnalysisCheck>)build.Invoke(null,
                    [Load(clientPath), Load(commonPath), Load(apiPath), Path.Combine(directory, "QQMusic.exe"),
                        clientPath, commonPath, profile.FileVersion, profile.ClientSha256,
                        profile.CommonSha256, profile.ApiSha256, profile])!;
                var getter = checks.Single(item => item.Name.EndsWith("string-manager-getter", StringComparison.Ordinal));
                check(getter.Required, "string manager ownership stays a mandatory check");
                check(getter.Passed == clientGetterExecutable,
                    clientGetterExecutable ? "client executable getter passes even when API has only data at that RVA"
                        : "client data getter is rejected even when API has executable code at that RVA");
                check(checks.Where(item => item.Required && item != getter).All(item => item.Passed),
                    "synthetic image satisfies every unrelated mandatory analyzer check");
                check(checks.Where(item => item.Required).All(item => item.Passed) == clientGetterExecutable,
                    "full production check result uses the client module for the getter");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] MakePe(bool getterExecutable, bool exports = false)
    {
        var bytes = new byte[0x800];
        void U16(int at, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        void U32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
        U16(0, 0x5A4D); U32(0x3C, 0x80); U32(0x80, 0x4550);
        U16(0x84, 0x14C); U16(0x86, 3); U16(0x94, 0xE0);
        const int optional = 0x98;
        U16(optional, 0x10B); U32(optional + 28, 0x10000000); U32(optional + 60, 0x200);
        U32(optional + 96, exports ? 0x3000u : 0);
        void Section(int index, string name, uint rva, uint raw, uint flags)
        {
            var at = optional + 0xE0 + index * 40;
            Encoding.ASCII.GetBytes(name).CopyTo(bytes, at);
            U32(at + 8, 0x200); U32(at + 12, rva); U32(at + 16, 0x200);
            U32(at + 20, raw); U32(at + 36, flags);
        }
        Section(0, ".text", 0x1000, 0x200, 0x60000020);
        Section(1, ".getter", 0x2000, 0x400, getterExecutable ? 0x60000020u : 0xC0000040u);
        Section(2, ".data", 0x3000, 0x600, 0xC0000040);
        new byte[] { 0xE8, 0x3B, 0, 0, 0 }.CopyTo(bytes, 0x200);
        new byte[] { 0xE8, 0x0B, 0, 0, 0 }.CopyTo(bytes, 0x210);
        Encoding.Unicode.GetBytes("playsong\0").CopyTo(bytes, 0x630);
        if (exports)
        {
            U32(0x614, 2); U32(0x618, 2); U32(0x61C, 0x3040);
            U32(0x620, 0x3050); U32(0x624, 0x3060);
            U32(0x640, 0x1010); U32(0x644, 0x1020);
            U32(0x650, 0x3070); U32(0x654, 0x3080);
            U16(0x660, 0); U16(0x662, 1);
            Encoding.ASCII.GetBytes("GetICatMgr\0").CopyTo(bytes, 0x670);
            Encoding.ASCII.GetBytes("GetQQUinEx\0").CopyTo(bytes, 0x680);
        }
        return bytes;
    }
}
