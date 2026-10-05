namespace UnifiedPlayerControlPoc;

internal readonly record struct QQMusicProcessEpoch(int ProcessId, long StartTimeTicks);

internal enum QQMusicNativeInsertionState
{
    SafeRejected,
    Accepted,
    Uncertain
}

internal sealed record QQMusicNativeEnsureResult(
    QQMusicNativeInsertionState State,
    bool InsertedNow,
    string Verification,
    string? Error,
    string? FailureCode)
{
    internal bool Accepted => State == QQMusicNativeInsertionState.Accepted;
    internal bool AutomationBlocked => State == QQMusicNativeInsertionState.Uncertain;
}

internal sealed record QQMusicAutomaticCommandResult(bool Sent, string Message);

/// <summary>
/// Stops automatic controls after an exposed native operation has no confirmed
/// outcome. Losing a window or observing a late success cannot clear the latch.
/// Explicit user controls do not use this gate; restoring captured audio mute
/// state is cleanup and must also remain possible.
/// </summary>
internal sealed class QQMusicNativeAutomation
{
    internal const string BlockedMessage =
        "QQ 原生插入结果尚未确认，已停止自动播放、切歌和补插；请先重启 QQ 音乐。";
    internal const string BlockedFailureCode = "qqmusic-native-outcome-uncertain";

    private readonly object _sync = new();
    private QQMusicProcessEpoch? _observedEpoch;
    private QQMusicProcessEpoch? _blockedEpoch;
    private bool _blocked;

    internal bool IsBlocked { get { lock (_sync) return _blocked; } }

    internal static QQMusicNativeInsertionState ClassifyOutcome(bool verified,
        bool sessionMatches, bool requiresRestart, bool commandSent,
        bool patchWriteAttempted, bool remoteMemoryRetained,
        bool originalCodeRestored, bool remoteMemoryReleased)
    {
        if (requiresRestart || (!verified || !sessionMatches)
            && (commandSent || patchWriteAttempted || remoteMemoryRetained
                || !originalCodeRestored || !remoteMemoryReleased))
            return QQMusicNativeInsertionState.Uncertain;
        return verified && sessionMatches ? QQMusicNativeInsertionState.Accepted
            : QQMusicNativeInsertionState.SafeRejected;
    }

    internal void ObserveProcess(QQMusicProcessEpoch? epoch)
    {
        lock (_sync)
        {
            if (epoch is null) return;
            if (_blocked && _blockedEpoch is { } blocked && epoch.Value != blocked)
            {
                _blocked = false;
                _blockedEpoch = null;
            }
            _observedEpoch = epoch;
        }
    }

    internal void Block(QQMusicProcessEpoch? operationEpoch)
    {
        lock (_sync)
        {
            if (_blocked) return;
            _blocked = true;
            _blockedEpoch = operationEpoch ?? _observedEpoch;
        }
    }

    internal QQMusicNativeEnsureResult BlockedResult() => new(
        QQMusicNativeInsertionState.Uncertain, false,
        "NativeOutcomeUncertainAutomaticControlsBlocked", BlockedMessage, BlockedFailureCode);

    internal async Task<QQMusicAutomaticCommandResult> SendAsync(
        Func<CancellationToken, Task<QQMusicAutomaticCommandResult>> send,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsBlocked) return new(false, BlockedMessage);
        return await send(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The actual pause/insert/next orchestration used by PlaySelected. Native
    /// insertion may complete after cancellation: classify it before permitting
    /// any next/resume compensation, then propagate cancellation.
    /// </summary>
    internal async Task<QQMusicNativePlaybackPreparation> PreparePlaybackAsync(
        Func<string, CancellationToken, Task<QQMusicAutomaticCommandResult>> send,
        Func<CancellationToken, Task<QQMusicNativeEnsureResult>> insert,
        Action stopGuard,
        CancellationToken cancellationToken)
    {
        try
        {
            var pause = await SendAsync(token => send("'pause'", token), cancellationToken)
                .ConfigureAwait(false);
            if (!pause.Sent) return new(pause, null, null);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            var native = await insert(cancellationToken).ConfigureAwait(false);
            if (native.AutomationBlocked) Block(null);
            cancellationToken.ThrowIfCancellationRequested();
            if (!native.Accepted || IsBlocked)
            {
                stopGuard();
                if (!IsBlocked)
                    await SendAsync(token => send("'play'", token), cancellationToken).ConfigureAwait(false);
                return new(pause, IsBlocked ? BlockedOutcome(native) : native, null);
            }
            var next = await SendAsync(token => send("'next'", token), cancellationToken)
                .ConfigureAwait(false);
            if (!next.Sent && !IsBlocked)
                await SendAsync(token => send("'play'", token), cancellationToken).ConfigureAwait(false);
            if (IsBlocked)
            {
                stopGuard();
                return new(pause, BlockedOutcome(native), null);
            }
            return new(pause, native, next);
        }
        catch
        {
            stopGuard();
            throw;
        }
    }

    private static QQMusicNativeEnsureResult BlockedOutcome(QQMusicNativeEnsureResult result) =>
        result with { State = QQMusicNativeInsertionState.Uncertain,
            Error = BlockedMessage, FailureCode = BlockedFailureCode };
}

internal sealed record QQMusicNativePlaybackPreparation(
    QQMusicAutomaticCommandResult Pause,
    QQMusicNativeEnsureResult? Native,
    QQMusicAutomaticCommandResult? Next);
