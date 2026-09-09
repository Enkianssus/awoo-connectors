using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using QQMusicControlPoc;

try
{
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException($"FAILED: {name}");
}
void Throws(Action action, string name)
{
    try { action(); }
    catch (Exception error) when (error is InvalidOperationException or EndOfStreamException)
    { Check(true, name); return; }
    Check(false, name);
}
const string Current = "<response><songinfo songID=\"102797884\" songtype=\"0\" listkey=\"\" songpos=\"3\" playstatus=\"1\" listname=\"not-retained\"/></response>";
const string Setting = "<response Setting=\"1\"/>";
var parsed = QQMusicWebStatusProtocol.Parse(Current, Setting);
Check(parsed.Succeeded && parsed.SongId == 102797884 && parsed.SongType == 0 && parsed.SongPosition == 3 &&
    parsed.PlayStatus == 1 && parsed.QueueSetting == 1 && parsed.Code == "status-read", "exact numeric observation; empty key accepted");
var unknown = QQMusicWebStatusProtocol.Parse(Current.Replace("102797884", "0").Replace("songtype=\"0\"", "songtype=\"-1\""), Setting);
Check(unknown.Succeeded && unknown.SongId is null && unknown.SongType is null, "unknown ID/type not fabricated");
Check(QQMusicWebStatusProtocol.Parse(Current.Replace("songpos=\"3\"", "songpos=\"-1\"").Replace("playstatus=\"1\"", "playstatus=\"-1\""), Setting)
    is { Succeeded: true, SongPosition: null, PlayStatus: null }, "unknown position/status null");
foreach (var invalid in new[]
{
    "", " ", "<", "<response/>", Current.Replace("songID", "songId"),
    Current.Replace("102797884", "4294967296"), Current.Replace("102797884", "+2"),
    Current.Replace("102797884", " 2"), Current.Replace("102797884", "2.0"),
    Current.Replace("songtype=\"0\"", "songtype=\"-2\""),
    Current.Replace("songtype=\"0\"", "songtype=\"2147483648\""),
    Current.Replace("songpos=\"3\"", "songpos=\"+3\""),
    Current.Replace("playstatus=\"1\"", "playstatus=\"1e0\""),
    Current.Replace("listkey=\"\"", "listkey=\"-1\""), Current.Replace("listkey=\"\"", ""),
    Current.Replace("listkey=\"\"", "listkey=\"18446744073709551616\""),
    Current.Replace("</response>", "<songinfo/></response>"),
    Current.Replace("<songinfo", "<songinfo xmlns=\"urn:x\""),
    Current.Replace("songID=", "xmlns:x=\"urn:x\" x:songID="),
    Current.Replace("listname=\"not-retained\"/>", "listname=\"not-retained\"><child/></songinfo>"),
    Current.Replace("listname=\"not-retained\"/>", "listname=\"not-retained\">text</songinfo>"),
    "<!DOCTYPE response [<!ENTITY x '1'>]>" + Current,
    new string('x', 65537), string.Concat(Enumerable.Repeat("<x>", 34)) + Current + string.Concat(Enumerable.Repeat("</x>", 34))
}) Check(!QQMusicWebStatusProtocol.Parse(invalid, Setting).Succeeded, "reject malformed current");
foreach (var invalid in new[]
{
    "", "<r/>", "<r setting=\"1\"/>", "<r Setting=\" 1\"/>", "<r Setting=\"+1\"/>",
    "<r Setting=\"-1\"/>", "<r Setting=\"2147483648\"/>", "<r Setting=\"1\" Setting=\"1\"/>",
    "<r Setting=\"1\"><s Setting=\"1\"/></r>", "<r xmlns:x=\"u\" x:Setting=\"1\"/>",
    "<r xmlns=\"u\" Setting=\"1\"/>", "<!DOCTYPE r SYSTEM 'file:///never-read'><r Setting=\"1\"/>"
}) Check(!QQMusicWebStatusProtocol.Parse(Current, invalid).Succeeded, "reject ambiguous setting");
Check(QQMusicWebStatusProtocol.Parse(Current, "<r><setting Setting=\"0\"/></r>") is { Succeeded: true, QueueSetting: 0 },
    "zero setting is a real value, not missing");
Check(QQMusicWebStatusProtocol.Parse(Current.Replace("listkey=\"\"", "listkey=\"18446744073709551615\""), Setting).Succeeded,
    "uint64 key may be present but not exposed");
Check(!JsonSerializer.Serialize(parsed).Contains("not-retained", StringComparison.Ordinal), "names excluded");
Check(!typeof(QQMusicWebStatus).GetProperties().Any(p => p.Name.Contains("List", StringComparison.Ordinal)), "no queue identity");

var readBudget = new QQMusicWebApiBudget();
readBudget.ReserveCurrent();
Throws(readBudget.ReserveCurrent, "current cannot repeat");
Throws(readBudget.ReserveWrite, "read then write forbidden");
readBudget.ReserveQueueSetting();
Throws(readBudget.ReserveQueueSetting, "setting cannot repeat");
Throws(readBudget.ReserveWrite, "two reads then write forbidden");
var reverseReadBudget = new QQMusicWebApiBudget();
reverseReadBudget.ReserveQueueSetting(); reverseReadBudget.ReserveCurrent();
Throws(reverseReadBudget.ReserveCurrent, "reverse read order still bounded");
var writeBudget = new QQMusicWebApiBudget();
writeBudget.ReserveWrite();
Throws(writeBudget.ReserveWrite, "write once");
Throws(writeBudget.ReserveCurrent, "write then current forbidden");
Throws(writeBudget.ReserveQueueSetting, "write then setting forbidden");
for (var round = 0; round < 25; round++)
{
    var budget = new QQMusicWebApiBudget();
    var writes = 0; var reads = 0;
    Parallel.For(0, 100, index =>
    {
        try
        {
            if (index % 3 == 0) { budget.ReserveWrite(); Interlocked.Increment(ref writes); }
            else { if (index % 3 == 1) budget.ReserveCurrent(); else budget.ReserveQueueSetting(); Interlocked.Increment(ref reads); }
        }
        catch (InvalidOperationException) { }
    });
    Check((writes == 1 && reads == 0) || (writes == 0 && reads == 2), "concurrent budgets cannot mix modes");
}

var request = new QQMusicWebStatusRequest(Guid.NewGuid(), 1, 1, @"C:\QQ\QQMusic.exe", @"C:\QQ\QQMusicApi.dll", new string('A', 64));
QQMusicWebStatusProtocol.ValidateRequest(request);
Check(true, "valid local preflight");
foreach (var invalid in new[]
{
    request with { OperationId = Guid.Empty }, request with { ProcessId = 0 }, request with { ProcessStartTimeUtcTicks = 0 },
    request with { ExecutablePath = "QQMusic.exe" }, request with { ApiDllPath = "QQMusicApi.dll" },
    request with { ExecutablePath = @"\\server\share\QQMusic.exe" },
    request with { ExecutablePath = @"C:\QQ\Other.exe" }, request with { ApiDllPath = @"C:\Other\QQMusicApi.dll" },
    request with { ApiDllPath = @"C:\QQ\Other.dll" }, request with { ApiSha256 = "00" }, request with { ApiSha256 = new string('Z', 64) }
}) Throws(() => QQMusicWebStatusProtocol.ValidateRequest(invalid), "reject invalid preflight");
var pe = new byte[256];
pe[0] = (byte)'M'; pe[1] = (byte)'Z';
BinaryPrimitives.WriteInt32LittleEndian(pe.AsSpan(0x3c), 64);
pe[64] = (byte)'P'; pe[65] = (byte)'E';
BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(68), 0x14c);
BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(70), 1);
BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(84), 96);
BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(86), 0x2000);
BinaryPrimitives.WriteUInt16LittleEndian(pe.AsSpan(88), 0x10b);
void Validate(byte[] bytes, string? hash = null) => QQMusicWebStatusProtocol.ValidateApi(new MemoryStream(bytes, false),
    hash ?? Convert.ToHexString(SHA256.HashData(bytes)));
Validate(pe); Check(true, "valid hash and x86 DLL PE");
Throws(() => Validate(pe, new string('0', 64)), "hash gate fails");
foreach (var offset in new[] { 0, 64, 68, 70, 84, 87, 88 })
{
    var changed = (byte[])pe.Clone(); changed[offset] = 0;
    Throws(() => Validate(changed), "reject wrong PE header");
}
var badOffset = (byte[])pe.Clone(); BinaryPrimitives.WriteInt32LittleEndian(badOffset.AsSpan(0x3c), int.MaxValue);
Throws(() => Validate(badOffset), "reject out of bounds PE offset");
Throws(() => Validate(new byte[8]), "reject truncated PE");

var operationId = Guid.NewGuid();
var wire = JsonSerializer.Serialize(new QQMusicWebStatusResponse(operationId, parsed), QQMusicWebStatusProtocol.JsonOptions);
Check(QQMusicWebStatusProtocol.ParseResponse(wire, operationId) == parsed, "typed response roundtrip");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire, Guid.NewGuid()).Succeeded, "operation binding");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"songId\":102797884", "\"songId\":102797884,\"songId\":1"), operationId).Succeeded, "duplicate response key");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"code\":\"status-read\"", "\"code\":\"private-account-data\""), operationId).Succeeded, "unknown response code");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"songId\":102797884", "\"songId\":0"), operationId).Succeeded, "response ID zero not accepted");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"songType\":0", "\"songType\":-1"), operationId).Succeeded, "response unknown must already be null");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"queueSetting\":1", "\"queueSetting\":null"), operationId).Succeeded, "missing setting not success");
Check(!QQMusicWebStatusProtocol.ParseResponse(wire.Replace("\"code\":", "\"rawXml\":\"private\",\"code\":"), operationId).Succeeded, "unknown wire fields rejected");
Check(QQMusicWebStatus.Failed("private account text").Code == "status-operation-failed", "safe error allowlist");

// Source contracts do not execute helpers, COM, target processes or the network.
var host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StatusHost.source.cs"));
var transport = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StatusTransport.source.cs"));
var apiClient = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ApiClient.source.cs"));
Check(host.Contains("DeadlineMilliseconds = 4000", StringComparison.Ordinal) && transport.Contains("TimeSpan.FromSeconds(5)", StringComparison.Ordinal), "bounded watchdogs");
Check(host.Contains("Environment.Exit(exitCode)", StringComparison.Ordinal) &&
    host.IndexOf("ExitSelf(exitCode)", StringComparison.Ordinal) > host.IndexOf("completed.Wait", StringComparison.Ordinal) &&
    host.Contains("finally { completed.Set(); }", StringComparison.Ordinal), "completed cleanup explicitly exits only helper");
Check(host.Contains("TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode))", StringComparison.Ordinal) &&
    !host.Contains("OpenProcess(", StringComparison.Ordinal) && !host.Contains("TerminateProcess(request", StringComparison.Ordinal) &&
    host.Contains("ExitSelf(124)", StringComparison.Ordinal), "both exit paths terminate only current helper, never target PID");
Check(!host.Contains("System.Windows.Forms", StringComparison.Ordinal) && host.Contains("ApartmentState.STA", StringComparison.Ordinal), "native STA pump only");
Check(host.Contains("FileShare.Read", StringComparison.Ordinal) && host.Contains("ValidateApi(lockedApi", StringComparison.Ordinal), "locked API verification");
Check(!host.Contains("SendOnce", StringComparison.Ordinal) && !transport.Contains("SendOnce", StringComparison.Ordinal), "no status mutation route");
Check(transport.Contains("child.Kill(entireProcessTree: false)", StringComparison.Ordinal), "only exact child termination");
Check(apiClient.Contains("budget.ReserveWrite()", StringComparison.Ordinal) && apiClient.Contains("budget.ReserveCurrent()", StringComparison.Ordinal) &&
    apiClient.Contains("budget.ReserveQueueSetting()", StringComparison.Ordinal), "one shared budget wired");
Check(QQMusicWebStatusProtocol.QueryCurrentXml.Contains("QueryPlayStatus", StringComparison.Ordinal) &&
    QQMusicWebStatusProtocol.QueryQueueSettingXml.Contains("QueryPlayQueueSetting", StringComparison.Ordinal), "fixed query names");
Check(!QQMusicWebHostPolicy.IsExternalApiHost(@"C:\QQMusic.exe\Awoo.QqWebBridge.exe"), "host path cannot select internal route");
Console.WriteLine($"PASS {checks} pure status checks; no QQ, COM, UI, or network calls.");
return 0;
}
catch (Exception error)
{
    // Assertion failures must be ordinary CLI failures, not unhandled CLR crashes
    // that can leave a Windows error-report dialog and the test apphost behind.
    Console.Error.WriteLine(error.Message);
    return 1;
}
