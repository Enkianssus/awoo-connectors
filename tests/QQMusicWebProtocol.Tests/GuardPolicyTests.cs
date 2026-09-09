using QQMusicControlPoc;

internal static class GuardPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Start = TimeSpan.Zero;
    private static readonly TimeSpan End = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(59);

    public static void Run()
    {
        // These invoke the policy linked into the real adapter. No adapter,
        // event monitor, native DLL, player process or media command is created.
        RegistrationBoundaries();
        ObservationPriority();
        ResumeSequences();
    }

    private static void RegistrationBoundaries()
    {
        Check.Equal(QQMusicWebGuardRegistration.FreshPlayingReady,
            Register("Playing", Now), "fresh Playing seeds evidence at registration");
        Check.Equal(QQMusicWebGuardRegistration.FreshPlayingReady,
            Register("Playing", Now - TimeSpan.FromSeconds(10)), "ten-second fresh boundary remains inclusive");
        Check.Equal(QQMusicWebGuardRegistration.WaitingForFreshPlaying,
            Register("Playing", Now - TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1)),
            "stale Playing is reserved without seeding evidence");
        foreach (var age in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(60), TimeSpan.FromDays(1) })
        {
            Check.Equal(QQMusicWebGuardRegistration.WaitingForFreshPlaying,
                Register("Paused", Now - age), "paused source age cannot seed Playing evidence");
            Check.Equal(QQMusicWebGuardRegistration.WaitingForFreshPlaying,
                Register("Paused", Now - age, position: End), "paused exactly at end is only a local reservation");
        }
        Check.Equal(QQMusicWebGuardRegistration.WaitingForFreshPlaying,
            Register("Playing", Now - TimeSpan.FromMinutes(1), position: Tail),
            "stale Playing at the tail is not end evidence");

        foreach (var status in new string?[] { null, "", "Unknown", "Stopped", "Closed", "Changing", "playing" })
            Check.Equal(QQMusicWebGuardRegistration.Rejected,
                Register(status, Now), "only known Playing/Paused states can register");
        foreach (var status in new[] { "Playing", "Paused" })
        {
            Check.Equal(QQMusicWebGuardRegistration.Rejected,
                Register(status, Now, identity: false), "unknown epoch/current identity cannot register");
            Check.Equal(QQMusicWebGuardRegistration.Rejected,
                Register(status, Now, agrees: false), "conflicting window/media metadata cannot register");
            foreach (var timestamp in new[] { default(DateTimeOffset), Now + TimeSpan.FromTicks(1),
                Now + TimeSpan.FromSeconds(1), DateTimeOffset.MaxValue })
                Check.Equal(QQMusicWebGuardRegistration.Rejected,
                    Register(status, timestamp), "default or future source timestamp cannot register");

            foreach (var bounds in new (TimeSpan Start, TimeSpan End, TimeSpan Position)[]
            {
                (Start, Start, Start), (End, Start, Start),
                (TimeSpan.FromTicks(-1), End, Tail), (Start, End, TimeSpan.FromTicks(-1)),
                (Start, End, End + TimeSpan.FromTicks(1)), (TimeSpan.FromSeconds(2), End, TimeSpan.FromSeconds(1)),
                (TimeSpan.MinValue, TimeSpan.MaxValue, Start)
            })
                Check.Equal(QQMusicWebGuardRegistration.Rejected,
                    QQMusicWebGuardPolicy.Registration(true, true, status, bounds.Start, bounds.End,
                        bounds.Position, Now, Now), "invalid timeline geometry is never a playback anchor");
        }
        Check.Equal(QQMusicWebGuardRegistration.WaitingForFreshPlaying,
            QQMusicWebGuardPolicy.Registration(true, true, "Paused", TimeSpan.Zero, TimeSpan.MaxValue,
                TimeSpan.MaxValue, Now - TimeSpan.FromDays(1), Now), "large valid paused geometry does not overflow");
    }

    private static void ObservationPriority()
    {
        foreach (var previous in new[] { false, true })
        foreach (var freshPlaying in new[] { false, true })
        foreach (var candidate in new[] { false, true })
        {
            Check.Equal(QQMusicWebGuardEventDecision.KeepTargetClearEvidence,
                QQMusicWebGuardPolicy.OnObservation(true, "Paused", previous, freshPlaying, candidate),
                "pause clears evidence before every possible same-anchor end candidate");
            Check.Equal(QQMusicWebGuardEventDecision.CancelTarget,
                QQMusicWebGuardPolicy.OnObservation(false, "Paused", previous, freshPlaying, candidate),
                "paused metadata change cancels without consuming or sending");
        }
        foreach (var candidate in new[] { false, true })
        {
            Check.Equal(QQMusicWebGuardEventDecision.SeedPlayingOnly,
                QQMusicWebGuardPolicy.OnObservation(true, "Playing", false, true, candidate),
                "first fresh sample only seeds, even when a callback batch looks like an end");
            Check.Equal(QQMusicWebGuardEventDecision.Continue,
                QQMusicWebGuardPolicy.OnObservation(true, "Playing", false, false, candidate),
                "stale Playing cannot seed or consume a waiting reservation");
            Check.Equal(QQMusicWebGuardEventDecision.Continue,
                QQMusicWebGuardPolicy.OnObservation(true, "Stopped", false, false, candidate),
                "Stopped after paused registration is not proof of resumed playback");
        }
        Check.Equal(QQMusicWebGuardEventDecision.Continue,
            QQMusicWebGuardPolicy.OnObservation(true, "Playing", true, true, false),
            "fresh Playing alone cannot consume a registered target");
        Check.Equal(QQMusicWebGuardEventDecision.ConsumeTarget,
            QQMusicWebGuardPolicy.OnObservation(true, "Stopped", true, false, true),
            "prior Playing plus a confirmed stopped-at-end candidate can consume");
    }

    private static void ResumeSequences()
    {
        foreach (var initialStatus in new[] { "Paused", "Playing" })
        foreach (var firstPosition in new[] { TimeSpan.FromSeconds(1), Tail })
        {
            var registration = Register(initialStatus, Now - TimeSpan.FromMinutes(1));
            var hasPreviousPlaying = registration == QQMusicWebGuardRegistration.FreshPlayingReady;
            Check.True(!hasPreviousPlaying, "paused/stale initial registration has no end evidence");
            var first = QQMusicWebGuardPolicy.OnObservation(true, "Playing", hasPreviousPlaying,
                freshPlaying: true, endTransitionCandidate: true);
            Check.Equal(QQMusicWebGuardEventDecision.SeedPlayingOnly, first,
                "first fresh head/tail sample cannot also dispatch");
            hasPreviousPlaying = first == QQMusicWebGuardEventDecision.SeedPlayingOnly;

            var atEnd = QQMusicWebEndPolicy.IsNearEnd(Start, End, firstPosition);
            var next = QQMusicWebGuardPolicy.OnObservation(true, "Stopped", hasPreviousPlaying,
                freshPlaying: false, endTransitionCandidate: atEnd);
            Check.Equal(atEnd ? QQMusicWebGuardEventDecision.ConsumeTarget : QQMusicWebGuardEventDecision.Continue,
                next, "resume must establish a near-end Playing sample before a later stopped event can consume");
        }

        // A pause discards an earlier Playing-at-tail observation. Coalesced
        // metadata/rollover events must not resurrect it or dispatch while paused.
        var paused = QQMusicWebGuardPolicy.OnObservation(true, "Paused", true, false, true);
        Check.Equal(QQMusicWebGuardEventDecision.KeepTargetClearEvidence, paused, "tail then pause keeps only target");
        var hasPlaying = paused != QQMusicWebGuardEventDecision.KeepTargetClearEvidence;
        var rollover = QQMusicWebEndPolicy.IsSameTrackRollover(Start, End, Tail, Start, End, TimeSpan.FromSeconds(1));
        Check.True(rollover, "synthetic tail-to-head geometry is a rollover candidate");
        var resumed = QQMusicWebGuardPolicy.OnObservation(true, "Playing", hasPlaying, true, rollover);
        Check.Equal(QQMusicWebGuardEventDecision.SeedPlayingOnly, resumed, "resume at head cannot use evidence from before pause");
        hasPlaying = true;
        Check.Equal(QQMusicWebGuardEventDecision.Continue,
            QQMusicWebGuardPolicy.OnObservation(true, "Playing", hasPlaying, true, false),
            "a later fresh tail sample remains observation-only until an actual transition");
        Check.Equal(QQMusicWebGuardEventDecision.ConsumeTarget,
            QQMusicWebGuardPolicy.OnObservation(true, "Playing", hasPlaying, true, rollover),
            "a later valid end transition may consume after fresh Playing evidence");
        Check.Equal(QQMusicWebGuardEventDecision.CancelTarget,
            QQMusicWebGuardPolicy.OnObservation(false, "Paused", true, false, true),
            "metadata change while paused cannot become a natural-next send");
    }

    private static QQMusicWebGuardRegistration Register(string? status, DateTimeOffset timestamp,
        bool identity = true, bool agrees = true, TimeSpan? position = null) =>
        QQMusicWebGuardPolicy.Registration(identity, agrees, status, Start, End, position ?? Tail, timestamp, Now);
}
