namespace QQMusicControlPoc;

internal enum QQMusicWebAutomaticTransition
{
    None,
    NaturalEnd,
    TrackChangeFallback
}

internal enum QQMusicWebInsertAdvance { DoNotAdvance, TargetObserved, AdvanceOnce }

/// <summary>
/// Pure intent/transition decisions used by the Web adapter. The paired core
/// sends PlaySelected/InterruptSelected only for a new explicit user intent;
/// automatic recovery must remain connector-owned and must not use that path.
/// </summary>
internal static class QQMusicWebSubmissionPolicy
{
    internal static bool SameNumericCurrentId(string anchorId, string? currentId) =>
        !string.IsNullOrEmpty(anchorId) && !string.IsNullOrEmpty(currentId) && anchorId == currentId;

    internal static bool SameTypedCurrentIdentity(string anchorId, string? currentId, string anchorKey, string currentKey) =>
        SameNumericCurrentId(anchorId, currentId) && !string.IsNullOrEmpty(anchorKey) && anchorKey == currentKey;

    // Starts when this owner first processes a potential metadata/ID change,
    // not while a prior bounded helper holds the gate. Later callbacks never
    // extend it; expiry only cancels local state, never authorizes a write.
    internal static bool IsPendingConvergenceActive(DateTimeOffset startedAt, DateTimeOffset now) =>
        startedAt != default && now >= startedAt && now - startedAt <= TimeSpan.FromSeconds(8);

    internal static (string Id, string Key) ProjectCurrentIdentity(string metadataKey,
        bool metadataAgrees, bool statusSucceeded, uint? songId, int? songType, bool catalogMetadataAgrees)
    {
        if (!metadataAgrees || !statusSucceeded || songId is not > 0 || songType is not >= 0 || !catalogMetadataAgrees)
            return (string.Empty, metadataKey);
        var id = songId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (id, $"{id}:{songType.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    internal static bool AllowsFreshManualAttempt(
        string command, bool targetValidated, bool cancellationRequested) =>
        targetValidated && !cancellationRequested &&
        command is "PlaySelected" or "InterruptSelected";

    internal static bool BlocksSameTarget(bool sameSongAndEpoch, bool freshManualIntent) =>
        sameSongAndEpoch && !freshManualIntent;

    /// <summary>
    /// Allows registering a new pending cycle after this target was actually
    /// observed in fresh Playing after its earlier submission, and the player
    /// has since moved to another known, agreeing, fresh Playing track. This is
    /// not permission to replay the current request or resend an unknown attempt.
    /// The caller must consume the prior completion evidence atomically when
    /// registration succeeds; a new Guid, timer or repeated Arm is not evidence.
    /// </summary>
    internal static bool CanRearmCompletedTarget(
        bool sameSongAndEpoch, bool priorTargetObservedFreshPlayingAfterSubmission,
        bool currentHasKnownIdentity, bool currentMetadataAgrees,
        bool currentMatchesTarget, bool currentFreshPlaying, bool pendingSameTarget) =>
        sameSongAndEpoch && priorTargetObservedFreshPlayingAfterSubmission &&
        currentHasKnownIdentity && currentMetadataAgrees && !currentMatchesTarget &&
        currentFreshPlaying && !pendingSameTarget;

    internal static bool RestorePreviousLatch(bool freshManualIntent, bool samePreviousEpoch,
        QQMusicWebSubmissionState state) =>
        freshManualIntent && samePreviousEpoch && state == QQMusicWebSubmissionState.RejectedBeforeDispatch;

    internal static bool ShouldSubmitConsumedTarget(
        QQMusicWebAutomaticTransition transition, bool targetAlreadyObserved) =>
        transition != QQMusicWebAutomaticTransition.None && !targetAlreadyObserved;

    internal static QQMusicWebInsertAdvance DecideInsertedTargetAdvance(
        QQMusicWebSubmissionState insertionState, bool sameEpoch, bool sameAnchor,
        bool currentIdentityAndPositionKnown, bool targetMatchesIdentity, bool targetFreshPlaying, bool automaticStopped)
    {
        if (insertionState != QQMusicWebSubmissionState.SubmittedUnverified || !sameEpoch || automaticStopped)
            return QQMusicWebInsertAdvance.DoNotAdvance;
        // A same-current-song insertion may be deduplicated by QQ. If that
        // target is paused/stale, Next could skip it; do not invent Resume.
        if (targetMatchesIdentity) return targetFreshPlaying
            ? QQMusicWebInsertAdvance.TargetObserved : QQMusicWebInsertAdvance.DoNotAdvance;
        return sameAnchor && currentIdentityAndPositionKnown
            ? QQMusicWebInsertAdvance.AdvanceOnce : QQMusicWebInsertAdvance.DoNotAdvance;
    }

    // This is a current-ID confirmation of a real metadata callback, not a
    // larger natural-end time window. The callback belongs to this pending
    // generation, and a new bounded identity read must still agree afterwards.
    internal static bool CanConfirmDelayedMetadataTransition(
        bool sameOwner, bool sameProcessEpoch, DateTimeOffset ownerCreatedAt,
        DateTimeOffset callbackObservedAt, DateTimeOffset receivedAt,
        bool metadataKeyUnchanged, bool identityReadConfirmed, bool metadataAgrees) =>
        sameOwner && sameProcessEpoch && ownerCreatedAt != default &&
        callbackObservedAt >= ownerCreatedAt &&
        callbackObservedAt - receivedAt <= TimeSpan.FromSeconds(1) &&
        metadataKeyUnchanged && identityReadConfirmed && metadataAgrees;

    internal static QQMusicWebAutomaticTransition ClassifyAutomaticTransition(
        QQMusicWebGuardEventDecision guardDecision,
        bool ownsPendingTarget, bool sameProcessEpoch, bool eventIsFresh,
        bool sameAnchor, bool hasCurrentIdentity, bool metadataAgrees,
        string? playbackStatus, bool timelineIsFresh, bool metadataCallbackIsFresh)
    {
        if (!ownsPendingTarget || !sameProcessEpoch || !eventIsFresh || playbackStatus == "Paused" ||
            guardDecision is QQMusicWebGuardEventDecision.KeepTargetClearEvidence or
                QQMusicWebGuardEventDecision.CancelTarget or QQMusicWebGuardEventDecision.SeedPlayingOnly)
            return QQMusicWebAutomaticTransition.None;
        if (guardDecision == QQMusicWebGuardEventDecision.ConsumeTarget)
            return QQMusicWebAutomaticTransition.NaturalEnd;

        // A fresh, independently agreeing metadata change may consume the local
        // queued head once even away from the previous track's end. It is an
        // intentional track-change fallback, not evidence of natural completion.
        return !sameAnchor && hasCurrentIdentity && metadataAgrees &&
            playbackStatus == "Playing" && timelineIsFresh && metadataCallbackIsFresh
                ? QQMusicWebAutomaticTransition.TrackChangeFallback
                : QQMusicWebAutomaticTransition.None;
    }
}
