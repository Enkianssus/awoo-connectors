namespace UnifiedPlayerControlPoc;

internal enum QQMusicWrongNextRecoveryAction
{
    QueuePreservingTakeover,
    Stop
}

internal enum QQMusicWrongNextRecoveryStep
{
    Previous,
    VerifyAnchor,
    Pause,
    Reinsert,
    Next,
    VerifyTarget,
    RestoreMute
}

/// <summary>
/// Keeps wrong-next recovery finite. The first wrong track is handled by one
/// queue-preserving takeover. If that takeover was already attempted, the
/// guard stops instead of sending another blind Next command.
/// </summary>
internal static class QQMusicWrongNextRecoveryPolicy
{
    internal static TimeSpan LateRecoveryGracePeriod =>
        TimeSpan.FromSeconds(2);

    internal static IReadOnlyList<QQMusicWrongNextRecoveryStep>
        QueuePreservingTakeoverSteps { get; } =
        [
            QQMusicWrongNextRecoveryStep.Previous,
            QQMusicWrongNextRecoveryStep.VerifyAnchor,
            QQMusicWrongNextRecoveryStep.Pause,
            QQMusicWrongNextRecoveryStep.Reinsert,
            QQMusicWrongNextRecoveryStep.Next,
            QQMusicWrongNextRecoveryStep.VerifyTarget,
            QQMusicWrongNextRecoveryStep.RestoreMute
        ];

    internal static QQMusicWrongNextRecoveryAction Decide(
        bool takeoverAttempted)
    {
        return takeoverAttempted
            ? QQMusicWrongNextRecoveryAction.Stop
            : QQMusicWrongNextRecoveryAction.QueuePreservingTakeover;
    }

    internal static bool ShouldReusePendingNativeNext(
        long pendingAnchorSequence,
        long currentAnchorSequence,
        int pendingProcessId,
        int currentProcessId,
        long pendingSongId,
        int pendingSongType,
        long requestedSongId,
        int requestedSongType) =>
        pendingAnchorSequence == currentAnchorSequence
        && pendingProcessId == currentProcessId
        && pendingSongId == requestedSongId
        && pendingSongType == requestedSongType;
}
