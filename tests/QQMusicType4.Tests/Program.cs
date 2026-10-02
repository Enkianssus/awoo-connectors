using System.Text;
using System.Text.Json;
using QQMusicControlPoc;

var checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new InvalidOperationException(name); }
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or IOException or InvalidDataException)
    { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
var request = new Type4Request(Guid.NewGuid(), 123, 639000000000000000,
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixture", "QQMusic.exe")), 213086592, 0);
QQMusicNativeNextResult Success(Type4Request r)
{
    var state = new QQMusicPlaybackState(true, "fixture", "fixture", 42, "fixture", DateTimeOffset.UtcNow);
    return new(new(r.SongId, r.SongType), true, true, true, true, true, true, state, state,
        r.ProcessId, "", "22.71", "", "", 5, 0, 0, 1, checked((uint)r.SongId), 1, 1,
        false, Type4OutcomePolicy.AcceptedVerification, 1, Type4OutcomePolicy.TransportName, null, null,
        true, true, false);
}
Type4Receipt Receipt(Type4Request r) => new(r.OperationId, true, true, 5, true, false, true,
    checked((uint)r.SongId), 1, true, true, true, false, "native-add-accepted-memory-retained", null);
var evidence = new NativeEvidence(0, 0, true, true);
var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

foreach (var id in new long[] { 1, 108031940, 213086592, uint.MaxValue })
{
    var r = request with { SongId = id };
    Type4Contract.ValidateRequest(r);
    var packet = Type4Transport.BuildPacket(id, 0);
    Check(BitConverter.ToUInt32(packet, 0) == packet.Length && BitConverter.ToUInt32(packet, 4) == 0x514D4153
        && BitConverter.ToUInt16(packet, 8) == 100 && BitConverter.ToUInt32(packet, 10) == 4
        && BitConverter.ToUInt32(packet, 14) == packet.Length - 18, "type4 envelope " + id);
    Check(Encoding.Unicode.GetString(packet, 18, packet.Length - 18)
        == $"/playbysongid cmd_count==1&&id_0=={id}&&songtype_0==0", "exact arbitrary song payload " + id);
    Check(Type4OutcomePolicy.Accepted(r, Success(r), true), "arbitrary song native success " + id);
    Check(QQMusicNativeInsertOutcomePolicy.IsAccepted(Success(r), id), "adapter success " + id);
}
foreach (var id in new long[] { -1, 0, (long)uint.MaxValue + 1, long.MaxValue })
{
    Reject(() => Type4Contract.ValidateRequest(request with { SongId = id }), "invalid request song");
    Reject(() => Type4Transport.BuildPacket(id, 0), "invalid wire song");
}
foreach (var type in new[] { -1, 1, 2, int.MaxValue })
{
    Reject(() => Type4Contract.ValidateRequest(request with { SongType = type }), "nonzero type request");
    Reject(() => Type4Transport.BuildPacket(1, type), "nonzero type wire");
}
Reject(() => Type4Contract.ValidateRequest(request with { OperationId = Guid.Empty }), "empty operation");
Reject(() => Type4Contract.ValidateRequest(request with { StartTicks = 0 }), "missing epoch");
Reject(() => Type4Contract.ValidateRequest(request with { ProcessId = 0 }), "missing pid");
Reject(() => Type4Contract.ValidateRequest(request with { Executable = "QQMusic.exe" }), "relative path");
var fileTime = new DateTime(request.StartTicks, DateTimeKind.Utc).ToFileTimeUtc();
Check(Type4Contract.OpenedHandleMatches(request, fileTime, request.Executable), "opened handle epoch");
Check(!Type4Contract.OpenedHandleMatches(request, fileTime + 1, request.Executable), "PID reuse");
Check(!Type4Contract.OpenedHandleMatches(request, fileTime, request.Executable + ".other"), "opened handle path");
Check(!Type4OutcomePolicy.MustObserveStage(new(false, false, 0)), "no send may reject early");
Check(Type4OutcomePolicy.MustObserveStage(new(true, false, 1460)), "send timeout observes native completion");
Check(Type4OutcomePolicy.MustObserveStage(new(true, false, 5)), "send BOOL failure observes native completion");
Check(Type4OutcomePolicy.MustObserveStage(new(true, true, 0)), "send success still observes completion");
Check(!Type4OutcomePolicy.CanReleaseRemoteMemory(true), "never free exposed trampoline even at stage5");
Check(Type4OutcomePolicy.CanReleaseRemoteMemory(false), "unexposed allocation may free");
var good = Success(request);
var badResults = new[]
{
    good with { CommandSent = false }, good with { PatchApplied = false },
    good with { PatchWriteAttempted = false }, good with { GetCatManagerHresult = -1 },
    good with { GetSongInfoHresult = -1 }, good with { AddSongsHresult = -1 },
    good with { ResolvedSongId = 1 }, good with { OriginalCodeRestored = false },
    good with { RemoteMemoryRetained = false }, good with { RemoteMemoryReleased = true },
    good with { TargetProcessId = 987 }, good with { Error = "fixture failure" }
};
foreach (var bad in badResults) Check(!Type4OutcomePolicy.Accepted(request, bad, true), "damaged native success");
Check(!Type4OutcomePolicy.Accepted(request, good, false), "identity changed after callback");
Check(Type4OutcomePolicy.Accepted(request, good with { CurrentWindowTrackUnchanged = false, ForegroundUnchanged = false }, true),
    "window observations do not undo complete native evidence");
foreach (var bad in new[] { good with { RequiresRestart = true }, good with { PatchWriteAttempted = false },
    good with { GetCatManagerHresult = -1 }, good with { GetSongInfoHresult = -1 }, good with { AddSongsHresult = -1 },
    good with { ResolvedSongId = 1 }, good with { OriginalCodeRestored = false }, good with { RemoteMemoryReleased = true },
    good with { Error = "failure" }, good with { CurrentWindowTrackUnchanged = false }, good with { Verification = "unknown" } })
    Check(!QQMusicNativeInsertOutcomePolicy.IsAccepted(bad, request.SongId), "adapter rejects incomplete success");
Check(!Type4OutcomePolicy.RequiresRestart(true, true, true, false, true, true), "complete retained native callback may continue");
Check(Type4OutcomePolicy.RequiresRestart(false, false, true, false, true, true), "patch exposure without send still blocks");
Check(!Type4OutcomePolicy.RequiresRestart(false, false, true, true, false, false), "untouched rejection can continue");

var directory = Path.Combine(root, "sequential");
using (var first = Type4Journal.Begin(directory, request))
{
    Reject(() => { using var busy = Type4Journal.Begin(directory, request with { OperationId = Guid.NewGuid() }); }, "exclusive lease");
    var result = first.Finish(Receipt(request), evidence);
    Check(result.State == "complete" && result.CompletedCount == 1 && result.RetainedBytes == 4096, "first complete");
}
var secondRequest = request with { OperationId = Guid.NewGuid(), SongId = 108031940 };
using (var second = Type4Journal.Begin(directory, secondRequest))
{
    var result = second.Finish(Receipt(secondRequest) with { TransportReturnedSuccess = false,
        CurrentTrackUnchanged = false, ForegroundUnchanged = false }, evidence);
    Check(result.State == "complete" && result.CompletedCount == 2 && result.RetainedBytes == 8192,
        "different song after reopen succeeds despite delivery BOOL/window observations");
}
Reject(() => { using var replay = Type4Journal.Begin(directory, request); }, "same guid replay blocked");
for (var stage = 0; stage < 5; stage++)
{
    var folder = Path.Combine(root, "stage-" + stage);
    var r = request with { OperationId = Guid.NewGuid() };
    Check(!Type4OutcomePolicy.Accepted(r, Success(r) with { NativeStage = stage }, true), "stage incomplete " + stage);
    using (var pending = Type4Journal.Begin(folder, r))
    {
        var result = pending.Finish(Receipt(r) with { NativeStage = stage, Accepted = false, RequiresRestart = true,
            Code = "native-outcome-uncertain-restart-required" }, evidence);
        Check(result.State == "blocked" && !result.RetryAllowed, "stage persistently blocked " + stage);
    }
    Reject(() => { using var afterRestart = Type4Journal.Begin(folder, r with { OperationId = Guid.NewGuid() }); },
        "connector reopen cannot reset unknown stage " + stage);
    using var newEpoch = Type4Journal.Begin(folder, r with { OperationId = Guid.NewGuid(), StartTicks = r.StartTicks + 1 });
    Check(true, "new QQ process epoch may begin " + stage);
}
var crashedDirectory = Path.Combine(root, "crashed");
using (Type4Journal.Begin(crashedDirectory, request)) { }
Reject(() => { using var reopened = Type4Journal.Begin(crashedDirectory, request with { OperationId = Guid.NewGuid() }); }, "crashed pending remains blocked");
var orphanDirectory = Path.Combine(root, "orphan");
var orphanOperations = Path.Combine(orphanDirectory, "operations", request.ProcessId + "-" + request.StartTicks);
Directory.CreateDirectory(orphanOperations);
Type4Journal.AtomicWrite(Path.Combine(orphanOperations, request.OperationId + ".json"),
    new JournalRecord(1, "pending", request, null, null, false, 0, 0));
Reject(() => { using var reopened = Type4Journal.Begin(orphanDirectory, request with { OperationId = Guid.NewGuid() }); }, "orphan reservation blocked");
using (var wrong = Type4Journal.Begin(Path.Combine(root, "wrong-song"), request))
    Check(wrong.Finish(Receipt(request) with { ResolvedSongId = 1 }, evidence).State == "blocked", "wrong native ID cannot unlock");
using (var restored = Type4Journal.Begin(Path.Combine(root, "restore-failed"), request))
    Check(restored.Finish(Receipt(request) with { OriginalCodeRestored = false }, evidence).State == "blocked", "restore failure cannot unlock");
var safeDirectory = Path.Combine(root, "safe-reject");
using (var safe = Type4Journal.Begin(safeDirectory, request))
{
    var receipt = new Type4Receipt(request.OperationId, false, false, 0, true, true, false, 0, -1,
        null, null, false, false, "preflight-rejected", "fixture preflight");
    Check(safe.Finish(receipt, new(-1, -1, true, false)).State == "rejected", "no-write rejection durable and reusable");
}
using (Type4Journal.Begin(safeDirectory, request with { OperationId = Guid.NewGuid() })) { }
Check(true, "safe rejection may begin a new request");
Check(!Type4Journal.DefaultDirectory.Contains("22.71") && !Type4Journal.DefaultDirectory.Contains(".tmp"), "journal is version independent");
var corruptDirectory = Path.Combine(root, "corrupt");
var corruptOperations = Path.Combine(corruptDirectory, "operations", request.ProcessId + "-" + request.StartTicks);
Directory.CreateDirectory(corruptOperations);
File.WriteAllText(Path.Combine(corruptOperations, Guid.NewGuid() + ".json"), "{truncated");
Reject(() => { using var bad = Type4Journal.Begin(corruptDirectory, request with { OperationId = Guid.NewGuid() }); }, "current epoch corrupt archive blocks");
using (Type4Journal.Begin(corruptDirectory, request with { OperationId = Guid.NewGuid(), StartTicks = request.StartTicks + 1 })) { }
Check(true, "old epoch corruption cannot block fresh QQ process");
Reject(() => { using var replay = Type4Journal.Begin(directory, request with { StartTicks = request.StartTicks + 1 }); }, "operation GUID cannot replay across epochs");
var strictPath = Path.Combine(root, "strict-record.json");
var fullRecord = JsonSerializer.Serialize(new JournalRecord(1, "complete", request, Receipt(request), evidence, true, 1, 4096), Type4Journal.JsonOptions);
foreach (var missing in new[] { "\"getCatManagerHresult\":0,", "\"getSongInfoHresult\":0,", "\"requiresRestart\":false,", "\"songType\":0", "\"schemaVersion\":1," })
{
    var damaged = fullRecord.Replace(missing, "").Replace(",}", "}");
    Check(damaged != fullRecord, "missing-field test mutates fixture");
    File.WriteAllText(strictPath, damaged);
    Reject(() => Type4Journal.Read(strictPath), "missing required journal field");
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, processReads = 0, sends = 0, patchWrites = 0 }));
