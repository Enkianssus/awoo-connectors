using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QQMusicControlPoc;

internal sealed record Type4Receipt(Guid OperationId, bool Attempted, bool TransportReturnedSuccess,
    int NativeStage, bool OriginalCodeRestored, bool RemoteMemoryReleased, bool RemoteMemoryRetained,
    uint ResolvedSongId, int AddSongsHresult, bool? CurrentTrackUnchanged, bool? ForegroundUnchanged,
    bool Accepted, bool RequiresRestart, string Code, string? Error);

internal static class Type4OutcomePolicy
{
    internal const string AcceptedVerification = "NativeNextInsertedMemoryRetainedPendingNextVerification";
    internal const string TransportName = "QQMusic 22.71 Type4 validated UI callback trampoline -> AddSongs(mode=0)";
    internal static bool MustObserveStage(Type4Transport.DeliveryReceipt receipt) => receipt.SendAttempted;
    internal static bool CanReleaseRemoteMemory(bool patchWriteAttempted) => !patchWriteAttempted;

    internal static bool Accepted(Type4Request request, QQMusicNativeNextResult result, bool identityUnchanged) =>
        result.CommandSent && result.PatchApplied && result.PatchWriteAttempted
        && result.NativeStage == 5 && result.GetCatManagerHresult >= 0
        && result.GetSongInfoHresult >= 0 && result.AddSongsHresult >= 0
        && result.ResolvedSongId == request.SongId && result.TargetProcessId == request.ProcessId
        && result.OriginalCodeRestored && result.RemoteMemoryRetained && !result.RemoteMemoryReleased
        && identityUnchanged && result.Error is null;

    internal static bool RequiresRestart(bool attempted, bool accepted, bool restored, bool released,
        bool patchWriteAttempted, bool retained) =>
        !accepted && (attempted || patchWriteAttempted || retained || !restored || !released);
}

/// <summary>
/// Uses the validated Type4 envelope only on the exact 22.71 installation.
/// Every exposed operation has a durable process-epoch journal before mutation;
/// connector restart cannot turn an uncertain callback into a safe retry.
/// </summary>
internal static class QQMusicType4Insertion
{
    internal static async Task<QQMusicNativeNextResult> InsertAsync(
        QQMusicSongReference song, int anchorProcessId, TimeSpan? responseWindow = null)
    {
        Type4Request request;
        try
        {
            using var process = Process.GetProcessById(anchorProcessId);
            var executable = process.MainModule?.FileName
                ?? throw new InvalidOperationException("QQ executable path is unavailable.");
            var version = FileVersionInfo.GetVersionInfo(
                Path.Combine(Path.GetDirectoryName(executable)!, "QQMusic.dll")).FileVersion;
            if (version != "22.71")
                return await QQMusicNativeNextTransport.InsertAsync(song, anchorProcessId, responseWindow)
                    .ConfigureAwait(false);
            request = new(Guid.NewGuid(), anchorProcessId, process.StartTime.ToUniversalTime().Ticks,
                executable, song.SongId, song.SongType);
            Type4Contract.ValidateRequest(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Rejected(song, anchorProcessId, "type4-preflight-rejected", exception.Message);
        }

        QQMusicNativeNextResult? result = null;
        Type4Transport.DeliveryReceipt? delivery = null;
        Type4Journal? journal = null;
        List<FileStream>? files = null;
        var nativeCallEntered = false;
        var attempted = false;
        var identityUnchanged = false;
        var journalDenied = false;
        var code = "preflight-rejected";
        string? error = null;
        try
        {
            journal = Type4Journal.Begin(Type4Journal.DefaultDirectory, request);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X86)
                throw new InvalidOperationException("Type4 insertion requires Windows x86.");
            files = Type4Contract.LockAndVerifyInstallation(request);
            void VerifyIdentity() => Type4Contract.VerifyIdentity(request);
            var sender = new Type4Transport(request.Executable, () => { VerifyIdentity(); attempted = true; });
            nativeCallEntered = true;
            result = await QQMusicNativeNextTransport.InsertType4Async(song, request.ProcessId,
                (pid, reference) => delivery = sender.SendOnce(pid, reference), VerifyIdentity,
                handle => Type4Contract.VerifyOpenedHandle(request, handle),
                responseWindow ?? TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            error = result.Error;
        }
        catch (JournalRejectedException exception)
        {
            journalDenied = true;
            code = exception.Code;
            error = "QQ 原生插入存在未完成事务或并发操作；未重试发送。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.Message;
            if (journal is null) { journalDenied = true; code = "journal-begin-failed"; }
        }
        finally
        {
            try { Type4Contract.VerifyIdentity(request); identityUnchanged = true; }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            if (files is not null) foreach (var file in files) file.Dispose();
        }

        attempted |= delivery?.SendAttempted ?? false;
        var exposed = result?.PatchWriteAttempted ?? nativeCallEntered;
        var restored = result?.OriginalCodeRestored ?? !nativeCallEntered;
        var released = result?.RemoteMemoryReleased ?? !nativeCallEntered;
        var retained = result?.RemoteMemoryRetained ?? false;
        var accepted = result is not null && error is null
            && Type4OutcomePolicy.Accepted(request, result, identityUnchanged);
        var restart = journalDenied || Type4OutcomePolicy.RequiresRestart(
            attempted, accepted, restored, released, exposed, retained);
        if (accepted) code = "native-add-accepted-memory-retained";
        else if (!journalDenied) code = restart ? "native-outcome-uncertain-restart-required" : "preflight-rejected";
        var receipt = new Type4Receipt(request.OperationId, attempted, delivery?.TransportReturnedSuccess ?? false,
            result?.NativeStage ?? 0, restored, released, retained, result?.ResolvedSongId ?? 0,
            result?.AddSongsHresult ?? unchecked((int)0x80004005), result?.CurrentWindowTrackUnchanged,
            result?.ForegroundUnchanged, accepted, restart, code, error);
        if (journal is not null)
        {
            try
            {
                var final = journal.Finish(receipt, new NativeEvidence(
                    result?.GetCatManagerHresult ?? unchecked((int)0x80004005),
                    result?.GetSongInfoHresult ?? unchecked((int)0x80004005), identityUnchanged, exposed));
                if (!final.RetryAllowed) { accepted = false; restart = true; }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                accepted = false; restart = true; code = "journal-finalization-failed";
                error = "QQ 插入事务无法持久保存，已阻止此 QQ 进程内的后续插入。";
            }
            finally { journal.Dispose(); }
        }

        result ??= Rejected(song, anchorProcessId, code, error);
        return result with
        {
            CommandSent = attempted,
            PatchWriteAttempted = exposed,
            OriginalCodeRestored = restored,
            RemoteMemoryReleased = released,
            RemoteMemoryRetained = retained,
            RequiresRestart = restart,
            Verification = accepted ? Type4OutcomePolicy.AcceptedVerification : code,
            FailureCode = accepted ? null : restart ? "qqmusic-native-outcome-uncertain" : result.FailureCode,
            Error = error,
            Transport = Type4OutcomePolicy.TransportName
        };
    }

    private static QQMusicNativeNextResult Rejected(QQMusicSongReference song, int pid, string code, string? error)
    {
        var empty = new QQMusicPlaybackState(false, null, null, null, null, DateTimeOffset.UtcNow);
        return new(song, false, false, false, true, false, false, empty, empty, pid, "", "", "", "",
            0, unchecked((int)0x80004005), unchecked((int)0x80004005), unchecked((int)0x80004005),
            0, 0, 0, true, code, 0, Type4OutcomePolicy.TransportName, error, null);
    }
}
