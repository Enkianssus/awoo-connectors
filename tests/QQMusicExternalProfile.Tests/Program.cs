using System.Text.Json.Nodes;
using QQMusicControlPoc;

const string environmentVariable = "BILINCM_QQMUSIC_PROFILE_DIR";
const string version = "22.61";
const string clientHash =
    "D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29";
const string commonHash =
    "15190F1D87B5B3853EF47F943F333FAD9E8D51277ADFD56AC332EABBDF8FC14D";
const string newVersion = "22.71";
const string newClientHash =
    "0108E68BEDA8B0AF61A71911519FA4FEB5DF02417D32F4A0993868E5157624BE";
const string newCommonHash =
    "4267E1C27F7251A31460613A97F5787FD7D9D0BE3664E5E615251ABA2780471F";

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RejectContract(QQMusicNativeNextProfile profile, string message)
{
    try { QQMusicNativeProfileContract.Verify(profile); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException(message);
}

static string WithInvalidField(string json, string field, JsonNode? value)
{
    var document = JsonNode.Parse(json)!.AsObject();
    // Give invalid documents a different key so accidental acceptance cannot
    // hide behind replacing the otherwise identical valid profile.
    document["fileVersion"] = "invalid-external-test";
    document[field] = value;
    return document.ToJsonString();
}

static string DifferentHash(string hash) =>
    (hash[0] == '0' ? "1" : "0") + hash[1..];

static QQMusicNativeNextProfile AssertIdentityLock(
    string version,
    string clientHash,
    string commonHash)
{
    var profile = QQMusicNativeNextProfiles.Find(version, clientHash, commonHash);
    Assert(profile is not null, $"The {version} profile must load from the external directory.");
    Assert(ReferenceEquals(profile, QQMusicNativeNextProfiles.Find(
        version, clientHash.ToLowerInvariant(), commonHash.ToLowerInvariant())),
        $"{version} SHA-256 matching must be case-insensitive.");
    Assert(QQMusicNativeNextProfiles.Find("22.60", clientHash, commonHash) is null,
        $"A different QQ version must not use the {version} profile.");
    Assert(QQMusicNativeNextProfiles.Find(version + ".0", clientHash, commonHash) is null,
        $"{version} version matching must remain exact.");
    Assert(QQMusicNativeNextProfiles.Find(version, DifferentHash(clientHash), commonHash) is null,
        $"{version} must reject a different QQMusic.dll hash.");
    Assert(QQMusicNativeNextProfiles.Find(version, clientHash, DifferentHash(commonHash)) is null,
        $"{version} must reject a different QQMusicCommon.dll hash.");
    Assert(QQMusicNativeNextProfiles.Find(version, clientHash, string.Empty) is null,
        $"{version} must not accept a missing common DLL hash.");
    return profile!;
}

// Keep the fixtures outside the loader's bundled profiles/qqmusic directory:
// successful loading must therefore use BILINCM_QQMUSIC_PROFILE_DIR.
var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "22.61.json");
var profileJson = File.ReadAllText(fixturePath);
var newFixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "22.71.json");
var newProfileJson = File.ReadAllText(newFixturePath);
var previousDirectory = Environment.GetEnvironmentVariable(environmentVariable);
var temporaryDirectory = Directory.CreateTempSubdirectory(
    "Awoo.QQMusicExternalProfile.Tests-");

try
{
    File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "22.61.json"), profileJson);
    File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "22.71.json"), newProfileJson);
    var invalidProfiles = new Dictionary<string, string>
    {
        ["invalid-json"] = "{ invalid JSON",
        ["null-document"] = "null",
        ["missing-fields"] = "{\"schemaVersion\":1,\"fileVersion\":\"broken\"}",
        ["unsupported-schema"] = WithInvalidField(profileJson, "schemaVersion", JsonValue.Create(2)),
        ["invalid-client-hash"] = WithInvalidField(profileJson, "clientSha256", JsonValue.Create("not-a-hash")),
        ["invalid-common-hash"] = WithInvalidField(profileJson, "commonSha256", JsonValue.Create("not-a-hash")),
        ["invalid-dispatch-size"] = WithInvalidField(profileJson, "expectedPlayDispatchBytes", JsonValue.Create("E8 57")),
        ["invalid-dispatch-hex"] = WithInvalidField(profileJson, "expectedPlayDispatchBytes", JsonValue.Create("E8 ZZ 8D 16 00")),
        ["invalid-rva"] = WithInvalidField(profileJson, "addSongsRva", JsonValue.Create("invalid"))
    };
    foreach (var field in new[] { "apiSha256", "abiContract", "transportContract", "stringManagerGetterRva" })
        invalidProfiles["missing-v2-" + field] = WithInvalidField(newProfileJson, field, null);
    foreach (var field in new[] { "singleSongPlayDispatchRva", "getCatManagerRva", "getQqUinExRva",
        "songItemConstructorRva", "songItemDestructorRva", "addSongsRva", "hiddenCategoryIdRva",
        "getListRootRva", "getListHelperRva", "getCategoryCountRva", "stringManagerGetterRva" })
    foreach (var value in new[] { "0", "-1", "0x10000000", "0xFFFFFFFF", "2147483648" })
        invalidProfiles["invalid-v2-" + field + value] = WithInvalidField(newProfileJson, field, JsonValue.Create(value));
    invalidProfiles["unknown-v2-field"] = WithInvalidField(newProfileJson, "vtableOffset", JsonValue.Create("0x10"));
    invalidProfiles["unknown-v2-abi"] = WithInvalidField(newProfileJson, "abiContract", JsonValue.Create("future-unknown"));
    invalidProfiles["unknown-v2-transport"] = WithInvalidField(newProfileJson, "transportContract", JsonValue.Create("future-unknown"));
    invalidProfiles["invalid-v2-api-hash"] = WithInvalidField(newProfileJson, "apiSha256", JsonValue.Create("ZZ" + new string('0', 62)));
    invalidProfiles["invalid-v2-dispatch-opcode"] = WithInvalidField(newProfileJson, "expectedPlayDispatchBytes", JsonValue.Create("E9 00 00 00 00"));
    invalidProfiles["invalid-v2-dispatch-target"] = WithInvalidField(newProfileJson, "expectedPlayDispatchBytes", JsonValue.Create("E8 FF FF FF 7F"));
    invalidProfiles["invalid-v2-size"] = WithInvalidField(newProfileJson, "songItemSize", JsonValue.Create("0xA4"));
    invalidProfiles["duplicate-field"] = newProfileJson.Replace("{", "{\"fileVersion\":\"shadow-version\",", StringComparison.Ordinal);
    invalidProfiles["duplicate-case-field"] = newProfileJson.Replace("{", "{\"FILEVERSION\":\"shadow-version\",", StringComparison.Ordinal);
    invalidProfiles["schema1-cannot-opt-into-abi"] = WithInvalidField(newProfileJson, "schemaVersion", JsonValue.Create(1));
    foreach (var (name, json) in invalidProfiles)
    {
        File.WriteAllText(Path.Combine(temporaryDirectory.FullName, name + ".json"), json);
    }

    // Exercise the production snapshot loader and reload entry point.
    Environment.SetEnvironmentVariable(environmentVariable, temporaryDirectory.FullName);
    var profile = AssertIdentityLock(version, clientHash, commonHash);
    var newProfile = AssertIdentityLock(newVersion, newClientHash, newCommonHash);
    QQMusicNativeProfileContract.Verify(newProfile);
    RejectContract(profile, "A historical schema 1 document must never authorize the CString ABI.");
    Assert(newProfile.SchemaVersion == 2 && newProfile.StringManagerGetterRva == 0x2B420,
        "The schema 2 profile must retain its reviewed string manager getter.");
    Assert(ReferenceEquals(newProfile, QQMusicNativeNextProfiles.Find(newVersion, newClientHash, newCommonHash,
        newProfile.ApiSha256.ToLowerInvariant())), "Execution selection must match the API fingerprint case-insensitively.");
    Assert(QQMusicNativeNextProfiles.Find(newVersion, newClientHash, newCommonHash, DifferentHash(newProfile.ApiSha256)) is null,
        "A mixed API DLL must not select a profile.");
    Assert(QQMusicNativeNextProfiles.Find(version, clientHash, commonHash, newProfile.ApiSha256) is null,
        "Three matching legacy fields cannot opt into a new wire contract.");
    Assert(QQMusicNativeNextProfiles.All.Count == 4,
        "Malformed profiles must not replace or add to the two built-ins and two valid external profiles.");
    Assert(QQMusicNativeNextProfiles.Find(version, newClientHash, newCommonHash) is null
        && QQMusicNativeNextProfiles.Find(newVersion, clientHash, commonHash) is null,
        "The 22.61 and 22.71 profiles must not match each other's DLL pair.");
    Assert(QQMusicNativeNextProfiles.Find(newVersion, newClientHash, commonHash) is null
        && QQMusicNativeNextProfiles.Find(version, clientHash, newCommonHash) is null,
        "DLLs from different player builds must not form a supported profile.");

    Assert(profile!.SingleSongPlayDispatchRva == 0x004A7934
        && profile.ExpectedPlayDispatchBytes.SequenceEqual(new byte[] { 0xE8, 0x57, 0x8D, 0x16, 0x00 })
        && profile.GetCatManagerRva == 0x0000F0ED
        && profile.GetQqUinExRva == 0x0002E1E5
        && profile.SongItemConstructorRva == 0x0004B8D0
        && profile.SongItemDestructorRva == 0x0004B410
        && profile.AddSongsRva == 0x00459280
        && profile.HiddenCategoryIdRva == 0x00C5D1C8
        && profile.GetListRootRva == 0x006332F0
        && profile.GetListHelperRva == 0x00633450
        && profile.GetCategoryCountRva == 0x00509FB0
        && profile.SongItemSize == 0xA0
        && !string.IsNullOrWhiteSpace(profile.Evidence),
        "The external document must preserve every validated 22.61 native field.");

    Assert(newProfile.SingleSongPlayDispatchRva == 0x004B0CE4
        && newProfile.ExpectedPlayDispatchBytes.SequenceEqual(new byte[] { 0xE8, 0xC7, 0x91, 0x16, 0x00 })
        && newProfile.GetCatManagerRva == 0x0000F18A
        && newProfile.GetQqUinExRva == 0x0002E283
        && newProfile.SongItemConstructorRva == 0x0004BB70
        && newProfile.SongItemDestructorRva == 0x0004B6B0
        && newProfile.AddSongsRva == 0x00462600
        && newProfile.HiddenCategoryIdRva == 0x00C6B1C8
        && newProfile.GetListRootRva == 0x0063CAF0
        && newProfile.GetListHelperRva == 0x0063CC50
        && newProfile.GetCategoryCountRva == 0x005134E0
        && newProfile.SongItemSize == 0xA0
        && !string.IsNullOrWhiteSpace(newProfile.Evidence),
        "The external document must preserve every mapped 22.71 native field.");

    Assert(QQMusicNativeNextProfiles.Find(
        "22.22",
        "FF0AB7911EB2ACF433F2DAF0FC4BA48FFFC64169CD822CE4D5B00E88FA180A50",
        "9F7FC7DF5BC4BBE9B4C3377449CBCB3C47A218A934FAAE4DFF8578C3EDAF652F")
        is { AddSongsRva: 0x0042C010 },
        "The 22.22 built-in profile must survive malformed external documents.");
    Assert(QQMusicNativeNextProfiles.Find(
        "22.41",
        "A5F3E917A5233D925268C34656E49096B6223B74631C5002DB606AD4B2C7A3F3",
        "36775378403DB33D049EE87BCAD654BA3A041B7D41259CD7EDFE65457D7E2A06")
        is { AddSongsRva: 0x0043DA80 },
        "The 22.41 built-in profile must survive malformed external documents.");

    var stableRevision = QQMusicNativeNextProfiles.Revision;
    QQMusicNativeNextProfiles.Reload();
    Assert(QQMusicNativeNextProfiles.Revision == stableRevision, "Unchanged reloads must preserve revision.");
    var legacy = JsonNode.Parse(newProfileJson)!.AsObject();
    legacy["schemaVersion"] = 1;
    foreach (var field in new[] { "apiSha256", "abiContract", "transportContract", "stringManagerGetterRva" }) legacy.Remove(field);
    legacy["addSongsRva"] = "0x00462601";
    File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "zz-legacy-downgrade.json"), legacy.ToJsonString());
    QQMusicNativeNextProfiles.Reload();
    Assert(QQMusicNativeNextProfiles.Find(newVersion, newClientHash, newCommonHash)?.SchemaVersion == 2
        && QQMusicNativeNextProfiles.Find(newVersion, newClientHash, newCommonHash)?.AddSongsRva == newProfile.AddSongsRva,
        "Later schema 1 packs must not replace a schema 2 profile of the same identity.");
    Assert(QQMusicNativeNextProfiles.Revision == stableRevision, "Rejected legacy downgrade must not change revision.");

    var bundledFixtureDirectory = Path.Combine(AppContext.BaseDirectory, "profiles", "qqmusic");
    Directory.CreateDirectory(bundledFixtureDirectory);
    var bundledFixture = Path.Combine(bundledFixtureDirectory, "test-owned-" + Guid.NewGuid().ToString("N") + ".json");
    try
    {
        File.WriteAllText(bundledFixture, newProfileJson);
        File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "22.71.json"), legacy.ToJsonString());
        QQMusicNativeNextProfiles.Reload();
        Assert(QQMusicNativeNextProfiles.Find(newVersion, newClientHash, newCommonHash, newProfile.ApiSha256)
            is { SchemaVersion: 2, AddSongsRva: 0x00462600 },
            "A configured frozen pack must not shadow the bundled schema 2 profile.");
    }
    finally
    {
        File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "22.71.json"), newProfileJson);
        File.Delete(bundledFixture); // Exact unique fixture owned by this test.
        QQMusicNativeNextProfiles.Reload();
    }

    var future = JsonNode.Parse(newProfileJson)!.AsObject();
    future["fileVersion"] = "99.42";
    future["clientSha256"] = new string('A', 64);
    future["commonSha256"] = new string('B', 64);
    future["apiSha256"] = new string('C', 64);
    future["stringManagerGetterRva"] = "0x0002C420";
    var futurePath = Path.Combine(temporaryDirectory.FullName, "future-profile.json");
    File.WriteAllText(futurePath, future.ToJsonString());
    QQMusicNativeNextProfiles.Reload();
    var selectedFuture = QQMusicNativeNextProfiles.Find("99.42", new string('A', 64), new string('B', 64), new string('C', 64));
    Assert(selectedFuture is not null && selectedFuture.StringManagerGetterRva == 0x2C420,
        "The same running binary must discover a future external schema 2 profile after reload.");
    QQMusicNativeProfileContract.Verify(selectedFuture!);
    Assert(QQMusicNativeNextProfiles.Revision > stableRevision, "New profile must invalidate capability cache.");
    stableRevision = QQMusicNativeNextProfiles.Revision;
    future["evidence"] = "Only diagnostic text changed.";
    File.WriteAllText(futurePath, future.ToJsonString());
    QQMusicNativeNextProfiles.Reload();
    Assert(QQMusicNativeNextProfiles.Revision == stableRevision, "Evidence-only edits do not change execution identity.");

    var alternate = JsonNode.Parse(future.ToJsonString())!.AsObject();
    alternate["apiSha256"] = new string('D', 64);
    File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "future-api-variant.json"), alternate.ToJsonString());
    QQMusicNativeNextProfiles.Reload();
    Assert(QQMusicNativeNextProfiles.Find("99.42", new string('A', 64), new string('B', 64), new string('C', 64))?.ApiSha256 == new string('C', 64)
        && QQMusicNativeNextProfiles.Find("99.42", new string('A', 64), new string('B', 64), new string('D', 64))?.ApiSha256 == new string('D', 64),
        "The same client/common pair can retain distinct exact API variants without selection ambiguity.");
    Assert(QQMusicNativeNextProfiles.Find("99.42", new string('A', 64), new string('B', 64), new string('E', 64)) is null,
        "An unknown API variant is never selected via a legacy three-part match.");
    File.Delete(futurePath);
    QQMusicNativeNextProfiles.Reload();
    Assert(QQMusicNativeNextProfiles.Find("99.42", new string('A', 64), new string('B', 64), new string('C', 64)) is null,
        "Removing a profile must invalidate its former executable support.");

    Console.WriteLine($"QQ Music external profile tests passed (schema 1/2, {invalidProfiles.Count} malformed documents, triple hashes, future profiles, reload, downgrade protection, API variants).");
}
finally
{
    Environment.SetEnvironmentVariable(environmentVariable, previousDirectory);
    // Only delete the unique directory created by this test, never a caller's
    // configured profile directory or the repository fixture.
    temporaryDirectory.Delete(recursive: true);
}
