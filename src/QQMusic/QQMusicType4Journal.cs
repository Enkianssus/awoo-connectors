using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQMusicControlPoc;

internal sealed record NativeEvidence(int GetCatManagerHresult, int GetSongInfoHresult,
    bool IdentityUnchanged, bool PatchWriteAttempted);
internal sealed record JournalRecord(int SchemaVersion, string State, Type4Request Request,
    Type4Receipt? Receipt, NativeEvidence? NativeEvidence, bool RetryAllowed, int CompletedCount, long RetainedBytes);
internal sealed class JournalRejectedException(string code) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
}

internal static class JournalPolicy
{
    internal static bool SameIdentity(Type4Request a, Type4Request b) =>
        a.ProcessId == b.ProcessId && a.StartTicks == b.StartTicks
        && string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase);
    internal static bool CompleteReceipt(Type4Request request, Type4Receipt r, NativeEvidence e) =>
        r.OperationId == request.OperationId && r.Attempted && r.Accepted && !r.RequiresRestart
        && r.NativeStage == 5 && r.AddSongsHresult >= 0 && r.ResolvedSongId == request.SongId
        && r.OriginalCodeRestored && !r.RemoteMemoryReleased && r.RemoteMemoryRetained
        && r.CurrentTrackUnchanged is not null && r.ForegroundUnchanged is not null
        && r.Error is null && r.Code == "native-add-accepted-memory-retained"
        && e.GetCatManagerHresult >= 0 && e.GetSongInfoHresult >= 0
        && e.IdentityUnchanged && e.PatchWriteAttempted;
    internal static bool SafeRejection(Type4Request request, Type4Receipt r, NativeEvidence e) =>
        r.OperationId == request.OperationId && !r.Attempted && !r.TransportReturnedSuccess
        && !r.Accepted && !r.RequiresRestart && r.NativeStage == 0 && r.OriginalCodeRestored
        && r.RemoteMemoryReleased && !r.RemoteMemoryRetained && !e.PatchWriteAttempted
        && r.Code == "preflight-rejected";
    internal static bool CanContinue(JournalRecord record, Type4Request next)
    {
        if (record.SchemaVersion != 1 || record.Request is null || !SameIdentity(record.Request, next)
            || record.Request.OperationId == Guid.Empty || record.Request.SongId is <= 0 or > uint.MaxValue
            || record.Request.SongType != 0 || record.CompletedCount < 0 || record.RetainedBytes < 0
            || record.RetainedBytes % 4096 != 0 || record.RetainedBytes < (long)record.CompletedCount * 4096
            || !record.RetryAllowed || record.Receipt is null || record.NativeEvidence is null)
            return false;
        return record.State switch
        {
            "complete" => record.CompletedCount > 0 && CompleteReceipt(record.Request, record.Receipt, record.NativeEvidence),
            "rejected" => SafeRejection(record.Request, record.Receipt, record.NativeEvidence),
            _ => false
        };
    }
}

// One FileShare.None lease remains owned by this object across await; unlike a
// Windows Mutex it has no thread-affinity requirement. Never delete the lease.
internal sealed class Type4Journal : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly FileStream lease;
    private readonly string directory;
    private readonly string statePath;
    private readonly string operationPath;
    private readonly JournalRecord pending;
    private bool settled;
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AwooMusicBot", "QQMusicNativeNext");

    private Type4Journal(FileStream lease, string directory, string statePath, string operationPath, JournalRecord pending)
    {
        this.lease = lease; this.directory = directory; this.statePath = statePath;
        this.operationPath = operationPath; this.pending = pending;
    }

    internal static Type4Journal Begin(string directory, Type4Request request)
    {
        Type4Contract.ValidateRequest(request);
        Directory.CreateDirectory(directory);
        var epoch = request.ProcessId + "-" + request.StartTicks;
        var prefix = Path.Combine(directory, epoch);
        var operations = Path.Combine(directory, "operations", epoch);
        FileStream lease;
        try { lease = new FileStream(prefix + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new JournalRejectedException("journal-lease-busy"); }
        try
        {
            var statePath = prefix + ".json";
            var previous = ReadValidatedHistory(directory, request);
            Directory.CreateDirectory(operations);
            var operationPath = Path.Combine(operations, request.OperationId.ToString("D") + ".json");
            // CreateNew is also the global operation-Guid reservation: a repeated
            // Guid cannot execute even against another process journal.
            var pending = new JournalRecord(1, "pending", request, null, null, false,
                previous?.CompletedCount ?? 0, previous?.RetainedBytes ?? 0);
            try
            {
                // The identity-independent reservation prevents GUID reuse across
                // epochs, without parsing unrelated historical receipt archives.
                var ids = Path.Combine(directory, "operation-ids");
                Directory.CreateDirectory(ids);
                using (var unique = new FileStream(Path.Combine(ids, request.OperationId.ToString("D")),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { unique.Flush(true); }
                using var reservation = new FileStream(operationPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(reservation, pending, JsonOptions); reservation.Flush(true);
            }
            catch (IOException) { throw new JournalRejectedException("operation-guid-replayed-or-unwritable"); }
            AtomicWrite(statePath, pending);
            return new Type4Journal(lease, directory, statePath, operationPath, pending);
        }
        catch { lease.Dispose(); throw; }
    }

    // Read-only admission for automatic playback controls, including after a
    // connector reconnect. Never reserve an operation, create a directory, or
    // rewrite receipts here. Begin still revalidates under its mutation lease.
    internal static void CheckAutomaticAdmission(string directory, int processId,
        long startTicks, string executable)
    {
        var request = new Type4Request(Guid.NewGuid(), processId, startTicks, executable, 1, 0);
        Type4Contract.ValidateRequest(request);
        var epoch = processId + "-" + startTicks;
        var leasePath = Path.Combine(directory, epoch + ".lease");
        FileStream? admissionLease = null;
        try
        {
            if (File.Exists(leasePath))
            {
                try { admissionLease = new FileStream(leasePath, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { throw new JournalRejectedException("journal-lease-busy"); }
            }
            else if (File.Exists(Path.Combine(directory, epoch + ".json"))
                || Directory.Exists(Path.Combine(directory, "operations", epoch)))
            {
                throw new JournalRejectedException("journal-lease-missing");
            }
            ReadValidatedHistory(directory, request);
        }
        finally { admissionLease?.Dispose(); }
    }

    private static JournalRecord? ReadValidatedHistory(string directory, Type4Request request)
    {
        var epoch = request.ProcessId + "-" + request.StartTicks;
        var statePath = Path.Combine(directory, epoch + ".json");
        var operations = Path.Combine(directory, "operations", epoch);
        JournalRecord? previous = null;
        if (File.Exists(statePath))
        {
            try { previous = Read(statePath); }
            catch { throw new JournalRejectedException("journal-invalid-or-incomplete"); }
            if (!JournalPolicy.CanContinue(previous, request))
                throw new JournalRejectedException("journal-pending-or-blocked");
            var archivedPath = Path.Combine(operations, previous.Request.OperationId.ToString("D") + ".json");
            try
            {
                if (JsonSerializer.Serialize(previous, JsonOptions) != JsonSerializer.Serialize(Read(archivedPath), JsonOptions))
                    throw new JournalRejectedException("journal-archive-mismatch");
            }
            catch { throw new JournalRejectedException("journal-archive-mismatch"); }
        }
        // A crash between operation reservation and process-state publication
        // leaves an orphan archive. Absence of process state is not permission
        // to continue in that case.
        if (Directory.Exists(operations)) foreach (var archivePath in Directory.EnumerateFiles(operations, "*.json"))
        {
            JournalRecord archived;
            try { archived = Read(archivePath); }
            catch { throw new JournalRejectedException("journal-invalid-or-incomplete"); }
            if (archived.Request is null || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(archivePath), "D", out var archivedId)
                || archivedId != archived.Request.OperationId)
                throw new JournalRejectedException("journal-archive-mismatch");
            if (!JournalPolicy.SameIdentity(archived.Request, request))
                throw new JournalRejectedException("journal-archive-mismatch");
            if (!JournalPolicy.CanContinue(archived, request))
                throw new JournalRejectedException("journal-pending-or-blocked");
            if (previous is null || archived.CompletedCount > previous.CompletedCount || archived.RetainedBytes > previous.RetainedBytes)
                throw new JournalRejectedException("journal-archive-mismatch");
        }
        return previous;
    }

    internal JournalRecord Finish(Type4Receipt result, NativeEvidence evidence)
    {
        if (settled) throw new InvalidOperationException("Journal already settled.");
        var complete = JournalPolicy.CompleteReceipt(pending.Request, result, evidence);
        var safe = JournalPolicy.SafeRejection(pending.Request, result, evidence);
        // Any result outside the two proved-safe final forms is persistently blocked.
        var state = complete ? "complete" : safe ? "rejected" : "blocked";
        var final = new JournalRecord(1, state, pending.Request, result, evidence, complete || safe,
            checked(pending.CompletedCount + (complete ? 1 : 0)),
            checked(pending.RetainedBytes + (result.RemoteMemoryRetained ? 4096 : 0)));
        try
        {
            // Publish the operation archive first and the process state last.
            // A crash anywhere earlier leaves pending/invalid state, never an unlock.
            AtomicWrite(operationPath, final);
            AtomicWrite(statePath, final);
            settled = true;
            return final;
        }
        catch
        {
            var blockedResult = result with { Accepted = false, RequiresRestart = true,
                Code = "journal-finalization-failed", Error = "Could not durably finalize the operation journal." };
            var blocked = final with { State = "blocked", RetryAllowed = false, Receipt = blockedResult };
            try { AtomicWrite(statePath, blocked); } catch { }
            try { AtomicWrite(operationPath, blocked); } catch { }
            throw;
        }
    }

    internal static JournalRecord Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 32768) throw new InvalidDataException("Invalid journal size.");
        using var doc = JsonDocument.Parse(file);
        CheckDuplicateProperties(doc.RootElement);
        RequireFields(doc.RootElement, ["schemaVersion", "state", "request", "receipt", "nativeEvidence",
            "retryAllowed", "completedCount", "retainedBytes"]);
        RequireFields(doc.RootElement.GetProperty("request"), ["operationId", "processId", "startTicks",
            "executable", "songId", "songType"]);
        var receipt = doc.RootElement.GetProperty("receipt");
        if (receipt.ValueKind != JsonValueKind.Null)
            RequireFields(receipt, ["operationId", "attempted", "transportReturnedSuccess", "nativeStage",
                "originalCodeRestored", "remoteMemoryReleased", "remoteMemoryRetained", "resolvedSongId",
                "addSongsHresult", "currentTrackUnchanged", "foregroundUnchanged", "accepted",
                "requiresRestart", "code", "error"]);
        var evidence = doc.RootElement.GetProperty("nativeEvidence");
        if (evidence.ValueKind != JsonValueKind.Null)
            RequireFields(evidence, ["getCatManagerHresult", "getSongInfoHresult", "identityUnchanged", "patchWriteAttempted"]);
        return doc.RootElement.Deserialize<JournalRecord>(JsonOptions) ?? throw new InvalidDataException();
    }

    private static void RequireFields(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected journal object.");
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Remove(property.Name)) throw new InvalidDataException("Unexpected journal field.");
        if (expected.Count != 0) throw new InvalidDataException("Missing journal field.");
    }

    private static void CheckDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new InvalidDataException("Duplicate journal field.");
                CheckDuplicateProperties(p.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) CheckDuplicateProperties(value);
    }

    internal static void AtomicWrite(string path, JournalRecord record)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, record, JsonOptions); file.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public void Dispose() => lease.Dispose(); // An unsettled pending record remains locked.
}
