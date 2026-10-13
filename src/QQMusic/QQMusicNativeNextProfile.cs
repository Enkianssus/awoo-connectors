using System.Globalization;
using System.Text.Json;

namespace QQMusicControlPoc;

internal sealed record QQMusicNativeNextProfile(
    string FileVersion,
    string ClientSha256,
    string CommonSha256,
    int SingleSongPlayDispatchRva,
    byte[] ExpectedPlayDispatchBytes,
    int GetCatManagerRva,
    int GetQqUinExRva,
    int SongItemConstructorRva,
    int SongItemDestructorRva,
    int AddSongsRva,
    int HiddenCategoryIdRva,
    int GetListRootRva,
    int GetListHelperRva,
    int GetCategoryCountRva,
    int SongItemSize,
    string Evidence,
    int SchemaVersion = 1,
    string ApiSha256 = "",
    string AbiContract = "",
    string TransportContract = "",
    int StringManagerGetterRva = 0);

// Profiles contain data for these fixed, reviewed templates only. They cannot
// supply instructions, vtable slots, argument layouts or ownership rules.
internal static class QQMusicNativeProfileContract
{
    internal const string CStringAbi = "qqmusic-addsongs-cstring-v1";
    internal const string Type4Transport = "type4-songid-v1";
    internal static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool IsRva(int value) => value is >= 0x1000 and <= 0x0FFFFFFF;

    internal static void Verify(QQMusicNativeNextProfile p)
    {
        if (p.SchemaVersion != 2 || p.AbiContract != CStringAbi || p.TransportContract != Type4Transport
            || string.IsNullOrWhiteSpace(p.FileVersion) || !IsSha256(p.ClientSha256)
            || !IsSha256(p.CommonSha256) || !IsSha256(p.ApiSha256)
            || p.SongItemSize != 0xA0 || p.ExpectedPlayDispatchBytes is not { Length: 5 }
            || p.ExpectedPlayDispatchBytes[0] != 0xE8
            || !Rvas(p).All(IsRva))
            throw new InvalidOperationException("QQ 兼容画像缺少受支持的 schema 2 原生调用约定或精确文件指纹。");
        var callTarget = (long)p.SingleSongPlayDispatchRva + 5 + BitConverter.ToInt32(p.ExpectedPlayDispatchBytes, 1);
        if (callTarget < 0x1000 || callTarget > 0x0FFFFFFF)
            throw new InvalidOperationException("QQ 兼容画像的分发目标超出允许的模块范围。");
    }

    internal static IEnumerable<int> Rvas(QQMusicNativeNextProfile p) =>
        [p.SingleSongPlayDispatchRva, p.GetCatManagerRva, p.GetQqUinExRva,
            p.SongItemConstructorRva, p.SongItemDestructorRva, p.AddSongsRva,
            p.HiddenCategoryIdRva, p.GetListRootRva, p.GetListHelperRva,
            p.GetCategoryCountRva, p.StringManagerGetterRva];

    internal static bool SameExecutionContract(QQMusicNativeNextProfile a, QQMusicNativeNextProfile b) =>
        a.ExpectedPlayDispatchBytes.SequenceEqual(b.ExpectedPlayDispatchBytes)
        && a with { ExpectedPlayDispatchBytes = b.ExpectedPlayDispatchBytes, Evidence = b.Evidence } == b;
}

internal static class QQMusicNativeNextProfiles
{
    private static readonly QQMusicNativeNextProfile[] BuiltInProfiles =
    [
        new(
            "22.22",
            "FF0AB7911EB2ACF433F2DAF0FC4BA48FFFC64169CD822CE4D5B00E88FA180A50",
            "9F7FC7DF5BC4BBE9B4C3377449CBCB3C47A218A934FAAE4DFF8578C3EDAF652F",
            0x0047A4F4,
            [0xE8, 0xD7, 0x53, 0x16, 0x00],
            0x0000F0ED,
            0x0002E089,
            0x0004A2A0,
            0x00049DE0,
            0x0042C010,
            0x00C141A0,
            0x00602430,
            0x00602590,
            0x004DBBC0,
            0xA0,
            "2026-07-30 现场捕捉右键下一首播放并重复验证"),
        new(
            "22.41",
            "A5F3E917A5233D925268C34656E49096B6223B74631C5002DB606AD4B2C7A3F3",
            "36775378403DB33D049EE87BCAD654BA3A041B7D41259CD7EDFE65457D7E2A06",
            0x0048C124,
            [0xE8, 0x67, 0x55, 0x16, 0x00],
            0x0000F0ED,
            0x0002E089,
            0x0004B800,
            0x0004B340,
            0x0043DA80,
            0x00C301A0,
            0x006142F0,
            0x00614450,
            0x004ED5D0,
            0xA0,
            "2026-08-01 校准 cmd_count=1 单曲分支并现场动态验证")
    ];

    private static readonly object ProfileLock = new();
    private static IReadOnlyList<QQMusicNativeNextProfile>? loadedProfiles;
    private static long revision;

    public static IReadOnlyList<QQMusicNativeNextProfile> All =>
        Volatile.Read(ref loadedProfiles) ?? Reload();
    public static long Revision { get { _ = All; return Interlocked.Read(ref revision); } }

    // Reconnect/reprobe after the core installs a signature-verified pack can
    // refresh the snapshot without restarting this binary. In-flight insertions
    // keep their selected immutable profile and reject a changed contract.
    public static IReadOnlyList<QQMusicNativeNextProfile> Reload()
    {
        lock (ProfileLock)
        {
            var snapshot = LoadProfiles();
            var previous = loadedProfiles;
            if (previous is null || previous.Count != snapshot.Count
                || previous.Any(old => !snapshot.Any(next => QQMusicNativeProfileContract.SameExecutionContract(old, next))))
                Interlocked.Increment(ref revision);
            Volatile.Write(ref loadedProfiles, snapshot);
            return snapshot;
        }
    }

    public static QQMusicNativeNextProfile? Find(
        string fileVersion,
        string clientSha256,
        string commonSha256)
    {
        return All.FirstOrDefault(profile =>
            string.Equals(
                profile.FileVersion,
                fileVersion,
                StringComparison.Ordinal)
            && string.Equals(
                profile.ClientSha256,
                clientSha256,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                    profile.CommonSha256,
                    commonSha256,
                    StringComparison.OrdinalIgnoreCase));
    }

    public static QQMusicNativeNextProfile? Find(string fileVersion, string clientSha256,
        string commonSha256, string apiSha256) => All.FirstOrDefault(profile =>
            profile.SchemaVersion == 2 && profile.FileVersion == fileVersion
            && string.Equals(profile.ClientSha256, clientSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.CommonSha256, commonSha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.ApiSha256, apiSha256, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<QQMusicNativeNextProfile> LoadProfiles()
    {
        var profiles = new Dictionary<string, QQMusicNativeNextProfile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BuiltInProfiles)
        {
            profiles[BuildKey(profile)] = profile;
        }

        foreach (var directory in GetProfileDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.json",
                         SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    using var parsed = JsonDocument.Parse(json);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object) continue;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (parsed.RootElement.EnumerateObject().Any(property => !names.Add(property.Name))) continue;
                    var document = JsonSerializer.Deserialize<QQMusicNativeNextProfileDocument>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
                    var profile = document?.ToProfile();
                    if (profile is not null)
                    {
                        var sameIdentity = profiles.Values.Where(existing =>
                            BuildBaseKey(existing).Equals(BuildBaseKey(profile), StringComparison.OrdinalIgnoreCase)).ToArray();
                        // A frozen schema 1 pack must never downgrade a bundled
                        // or previously selected schema 2 execution contract.
                        if (profile.SchemaVersion == 1 && sameIdentity.Any(existing => existing.SchemaVersion == 2)) continue;
                        if (profile.SchemaVersion == 2)
                            foreach (var old in sameIdentity.Where(existing => existing.SchemaVersion == 1))
                                profiles.Remove(BuildKey(old));
                        profiles[BuildKey(profile)] = profile;
                    }
                }
                catch
                {
                    // A malformed downloaded profile must never disable the
                    // built-in, hash-locked compatibility set.
                }
            }
        }

        return profiles.Values.ToArray();
    }

    private static IEnumerable<string> GetProfileDirectories()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "profiles",
            "qqmusic");
        var configured = Environment.GetEnvironmentVariable(
            "BILINCM_QQMUSIC_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Path.GetFullPath(configured);
        }
    }

    private static string BuildBaseKey(QQMusicNativeNextProfile profile) =>
        $"{profile.FileVersion}|{profile.ClientSha256}|{profile.CommonSha256}";
    private static string BuildKey(QQMusicNativeNextProfile profile) =>
        $"{BuildBaseKey(profile)}|{profile.ApiSha256}";

    private sealed record QQMusicNativeNextProfileDocument(
        int SchemaVersion,
        string FileVersion,
        string ClientSha256,
        string CommonSha256,
        string SingleSongPlayDispatchRva,
        string ExpectedPlayDispatchBytes,
        string GetCatManagerRva,
        string GetQqUinExRva,
        string SongItemConstructorRva,
        string SongItemDestructorRva,
        string AddSongsRva,
        string HiddenCategoryIdRva,
        string GetListRootRva,
        string GetListHelperRva,
        string GetCategoryCountRva,
        string SongItemSize,
        string Evidence,
        string ApiSha256 = "",
        string AbiContract = "",
        string TransportContract = "",
        string StringManagerGetterRva = "0")
    {
        public QQMusicNativeNextProfile? ToProfile()
        {
            if (SchemaVersion is not (1 or 2)
                || string.IsNullOrWhiteSpace(FileVersion)
                || !QQMusicNativeProfileContract.IsSha256(ClientSha256)
                || !QQMusicNativeProfileContract.IsSha256(CommonSha256))
            {
                return null;
            }

            var expected = ExpectedPlayDispatchBytes
                .Split([' ', '-', ':'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => byte.Parse(
                    value,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture))
                .ToArray();
            if (expected.Length != 5 || expected[0] != 0xE8)
            {
                return null;
            }

            var profile = new QQMusicNativeNextProfile(
                FileVersion.Trim(),
                ClientSha256.Trim().ToUpperInvariant(),
                CommonSha256.Trim().ToUpperInvariant(),
                ParseNumber(SingleSongPlayDispatchRva),
                expected,
                ParseNumber(GetCatManagerRva),
                ParseNumber(GetQqUinExRva),
                ParseNumber(SongItemConstructorRva),
                ParseNumber(SongItemDestructorRva),
                ParseNumber(AddSongsRva),
                ParseNumber(HiddenCategoryIdRva),
                ParseNumber(GetListRootRva),
                ParseNumber(GetListHelperRva),
                ParseNumber(GetCategoryCountRva),
                ParseNumber(SongItemSize),
                Evidence ?? string.Empty,
                SchemaVersion,
                ApiSha256?.Trim().ToUpperInvariant() ?? string.Empty,
                AbiContract ?? string.Empty,
                TransportContract ?? string.Empty,
                ParseNumber(StringManagerGetterRva));
            if (profile.SongItemSize != 0xA0
                || !QQMusicNativeProfileContract.Rvas(profile).Take(10).All(QQMusicNativeProfileContract.IsRva)) return null;
            if (SchemaVersion == 2) QQMusicNativeProfileContract.Verify(profile);
            else if (!string.IsNullOrEmpty(profile.ApiSha256) || !string.IsNullOrEmpty(profile.AbiContract)
                || !string.IsNullOrEmpty(profile.TransportContract) || profile.StringManagerGetterRva != 0) return null;
            return profile;
        }

        private static int ParseNumber(string value)
        {
            var trimmed = value.Trim();
            return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.Parse(
                    trimmed[2..],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture)
                : int.Parse(trimmed, CultureInfo.InvariantCulture);
        }

    }
}
