namespace QQMusicControlPoc;

internal enum QQMusicWebGuardRegistration
{
    Rejected,
    WaitingForFreshPlaying,
    FreshPlayingReady
}

internal enum QQMusicWebGuardEventDecision
{
    Continue,
    KeepTargetClearEvidence,
    CancelTarget,
    SeedPlayingOnly,
    ConsumeTarget
}

/// <summary>
/// Pure local-guard policy, not a playback or native queue acknowledgement.
/// Registration may retain a paused/stale source, but only a fresh Playing
/// observation can establish evidence for a later end-transition candidate.
/// The caller separately checks event freshness, process epoch and ownership.
/// </summary>
internal static class QQMusicWebGuardPolicy
{
    internal static QQMusicWebGuardRegistration Registration(
        bool hasIdentity, bool metadataAgrees, string? playbackStatus,
        TimeSpan start, TimeSpan end, TimeSpan position,
        DateTimeOffset sourceUpdatedAt, DateTimeOffset now)
    {
        if (!hasIdentity || !metadataAgrees || playbackStatus is not ("Playing" or "Paused") ||
            start < TimeSpan.Zero || end <= start || position < start || position > end ||
            sourceUpdatedAt == default || sourceUpdatedAt > now)
            return QQMusicWebGuardRegistration.Rejected;

        // A paused timeline normally stops changing. Old positive geometry can
        // identify a local target without granting any authority to dispatch it.
        return playbackStatus == "Playing" && now - sourceUpdatedAt <= TimeSpan.FromSeconds(10)
            ? QQMusicWebGuardRegistration.FreshPlayingReady
            : QQMusicWebGuardRegistration.WaitingForFreshPlaying;
    }

    internal static QQMusicWebGuardEventDecision OnObservation(
        bool sameAnchor, string? playbackStatus, bool hasPreviousPlaying,
        bool freshPlaying, bool endTransitionCandidate)
    {
        // Paused has priority even if a metadata-change or rollover candidate
        // was observed in the same callback batch as the pause.
        if (playbackStatus == "Paused")
            return sameAnchor ? QQMusicWebGuardEventDecision.KeepTargetClearEvidence
                : QQMusicWebGuardEventDecision.CancelTarget;
        if (sameAnchor && playbackStatus == "Playing" && freshPlaying && !hasPreviousPlaying)
            return QQMusicWebGuardEventDecision.SeedPlayingOnly;
        if (hasPreviousPlaying && endTransitionCandidate)
            return QQMusicWebGuardEventDecision.ConsumeTarget;
        return QQMusicWebGuardEventDecision.Continue;
    }
}
