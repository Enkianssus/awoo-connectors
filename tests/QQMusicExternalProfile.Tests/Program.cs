using System.Text.Json.Nodes;
using QQMusicControlPoc;

const string environmentVariable = "BILINCM_QQMUSIC_PROFILE_DIR";
const string version = "22.61";
const string clientHash =
    "D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29";
const string commonHash =
    "15190F1D87B5B3853EF47F943F333FAD9E8D51277ADFD56AC332EABBDF8FC14D";

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
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

// Keep the fixture outside the loader's bundled profiles/qqmusic directory:
// successful loading must therefore use BILINCM_QQMUSIC_PROFILE_DIR.
var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "22.61.json");
var profileJson = File.ReadAllText(fixturePath);
var previousDirectory = Environment.GetEnvironmentVariable(environmentVariable);
var temporaryDirectory = Directory.CreateTempSubdirectory(
    "Awoo.QQMusicExternalProfile.Tests-");

try
{
    File.WriteAllText(Path.Combine(temporaryDirectory.FullName, "22.61.json"), profileJson);
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
    foreach (var (name, json) in invalidProfiles)
    {
        File.WriteAllText(Path.Combine(temporaryDirectory.FullName, name + ".json"), json);
    }

    // The production loader caches its first read. Configure the environment
    // before touching All or Find, without reflection or test-only loader code.
    Environment.SetEnvironmentVariable(environmentVariable, temporaryDirectory.FullName);
    var profile = QQMusicNativeNextProfiles.Find(version, clientHash, commonHash);
    Assert(profile is not null, "The validated 22.61 profile must load from the external directory.");
    Assert(QQMusicNativeNextProfiles.All.Count == 3,
        "Malformed profiles must not replace or add to the two built-ins and one valid external profile.");
    Assert(ReferenceEquals(profile, QQMusicNativeNextProfiles.Find(
        version, clientHash.ToLowerInvariant(), commonHash.ToLowerInvariant())),
        "SHA-256 matching must be case-insensitive.");

    Assert(QQMusicNativeNextProfiles.Find("22.60", clientHash, commonHash) is null,
        "A different QQ version must not use the 22.61 profile.");
    Assert(QQMusicNativeNextProfiles.Find("22.61.0", clientHash, commonHash) is null,
        "Version matching must remain exact.");
    Assert(QQMusicNativeNextProfiles.Find(version, "0" + clientHash[1..], commonHash) is null,
        "A different QQMusic.dll hash must be rejected.");
    Assert(QQMusicNativeNextProfiles.Find(version, clientHash, "0" + commonHash[1..]) is null,
        "A different QQMusicCommon.dll hash must be rejected.");
    Assert(QQMusicNativeNextProfiles.Find(version, clientHash, string.Empty) is null,
        "The external profile must not accept a missing common DLL hash.");

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

    Console.WriteLine("QQ Music external profile tests passed (22.61, exact version, dual hashes, malformed documents, built-ins).");
}
finally
{
    Environment.SetEnvironmentVariable(environmentVariable, previousDirectory);
    // Only delete the unique directory created by this test, never a caller's
    // configured profile directory or the repository fixture.
    temporaryDirectory.Delete(recursive: true);
}
