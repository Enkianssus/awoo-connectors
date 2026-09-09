using QQMusicControlPoc;

internal static class SubmissionPolicyTests
{
    public static void Run()
    {
        // Behavioral tests invoke the exact pure decisions used by the adapter.
        // Adapter gate ordering and one-send placement also have source contracts;
        // these tests do not instantiate a player, adapter, COM object or helper.
        ManualIntentBoundaries();
        CompletedTargetRearmBoundaries();
        DelayedMetadataBoundaries();
        InsertThenNextBoundaries();
        IdentityProjectionBoundaries();
        PendingConvergenceBoundaries();
        TrackChangeBoundaries();
        ConsumeOnceSequences();
    }

    private static void ManualIntentBoundaries()
    {
        foreach (var command in new[] { "PlaySelected", "InterruptSelected", "Next", "InsertNext",
            "ArmNextGuard", "Pause", "Resume", "Previous", "Toggle", "", "playselected" })
        foreach (var valid in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            var allowed = QQMusicWebSubmissionPolicy.AllowsFreshManualAttempt(command, valid, cancelled);
            var expected = valid && !cancelled && command is "PlaySelected" or "InterruptSelected";
            Check.Equal(expected, allowed, "only a validated uncancelled explicit selection is a new attempt");
            Check.Equal(!expected, QQMusicWebSubmissionPolicy.BlocksSameTarget(true, allowed),
                "validation/cancellation failure and automatic commands cannot free a same-target latch");
            Check.True(!QQMusicWebSubmissionPolicy.BlocksSameTarget(false, allowed),
                "an unrelated song/epoch is not a same-target duplicate");
        }
        foreach (var command in new[] { "PlaySelected", "InterruptSelected" })
        {
            for (var separateRequest = 0; separateRequest < 2; separateRequest++)
            {
                var freshIntent = QQMusicWebSubmissionPolicy.AllowsFreshManualAttempt(command, true, false);
                Check.True(!QQMusicWebSubmissionPolicy.BlocksSameTarget(true, freshIntent),
                    "each separate explicit request may replace a prior same-song attempt");
            }
            Check.True(QQMusicWebSubmissionPolicy.BlocksSameTarget(true, false),
                "a manual attempt does not free a later automatic Next or rearm");
        }

        foreach (var manual in new[] { false, true })
        foreach (var sameEpoch in new[] { false, true })
        foreach (var state in Enum.GetValues<QQMusicWebSubmissionState>())
            Check.Equal(manual && sameEpoch && state == QQMusicWebSubmissionState.RejectedBeforeDispatch,
                QQMusicWebSubmissionPolicy.RestorePreviousLatch(manual, sameEpoch, state),
                "a definitely-unsent manual attempt restores only the same-epoch older latch");
    }

    private static void IdentityProjectionBoundaries()
    {
        const string metadata = "TITLE|ARTIST";
        foreach (var songId in new uint?[] { null, 0, 1, (uint)int.MaxValue + 1, uint.MaxValue })
        foreach (var songType in new int?[] { null, -1, 0, 112 })
        for (var mask = 0; mask < 8; mask++)
        {
            var projected = QQMusicWebSubmissionPolicy.ProjectCurrentIdentity(metadata,
                (mask & 1) != 0, (mask & 2) != 0, songId, songType, (mask & 4) != 0);
            var valid = mask == 7 && songId is > 0 && songType is >= 0;
            var id = valid ? songId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            Check.Equal(id, projected.Id, "failed, unknown, mismatched and zero status never retain an older typed ID");
            Check.Equal(valid ? $"{id}:{songType!.Value}" : metadata, projected.Key,
                "pure identity projection preserves metadata key until a genuine typed status is valid");
        }
        var oldIdentity = QQMusicWebSubmissionPolicy.ProjectCurrentIdentity(metadata, true, true, 42, 0, true);
        var invalidReplacement = QQMusicWebSubmissionPolicy.ProjectCurrentIdentity(metadata, true, false, 42, 0, true);
        Check.Equal("42", oldIdentity.Id, "valid identity projects the actual numeric value");
        Check.Equal("", invalidReplacement.Id, "invalid replacement clears rather than reuses a previously valid ID");
    }

    private static void InsertThenNextBoundaries()
    {
        foreach (var state in Enum.GetValues<QQMusicWebSubmissionState>())
        for (var mask = 0; mask < 64; mask++)
        {
            var epoch = (mask & 1) != 0;
            var anchor = (mask & 2) != 0;
            var known = (mask & 4) != 0;
            var target = (mask & 8) != 0;
            var playing = (mask & 16) != 0;
            var stopped = (mask & 32) != 0;
            var expected = state != QQMusicWebSubmissionState.SubmittedUnverified || !epoch || stopped
                ? QQMusicWebInsertAdvance.DoNotAdvance : target ? playing
                    ? QQMusicWebInsertAdvance.TargetObserved : QQMusicWebInsertAdvance.DoNotAdvance
                : anchor && known ? QQMusicWebInsertAdvance.AdvanceOnce : QQMusicWebInsertAdvance.DoNotAdvance;
            Check.Equal(expected, QQMusicWebSubmissionPolicy.DecideInsertedTargetAdvance(state,
                epoch, anchor, known, target, playing, stopped),
                "insert receipt, epoch, anchor, identity and stop independently fence the one possible Next");
        }
        Check.Equal(QQMusicWebInsertAdvance.DoNotAdvance,
            QQMusicWebSubmissionPolicy.DecideInsertedTargetAdvance(QQMusicWebSubmissionState.OutcomeUnknown,
                true, true, true, false, false, false), "an unknown insertion never authorizes blind Next");
        Check.Equal(QQMusicWebInsertAdvance.TargetObserved,
            QQMusicWebSubmissionPolicy.DecideInsertedTargetAdvance(QQMusicWebSubmissionState.SubmittedUnverified,
                true, false, true, true, true, false), "native arrival at target terminates without replay or Next");
        Check.Equal(QQMusicWebInsertAdvance.DoNotAdvance,
            QQMusicWebSubmissionPolicy.DecideInsertedTargetAdvance(QQMusicWebSubmissionState.SubmittedUnverified,
                true, true, true, true, false, false), "paused same-current target cannot authorize Next after a possibly deduplicated insertion");
        Check.True(QQMusicWebSubmissionPolicy.BlocksSameTarget(true, false),
            "both phases remain one automatically latched transaction, not separate retry permissions");
    }

    private static void CompletedTargetRearmBoundaries()
    {
        // All seven inputs are independently required; neither a new pending
        // Guid nor transport S_OK replaces the prior target's playback witness.
        for (var mask = 0; mask < 128; mask++)
        {
            var sameEpoch = (mask & 1) != 0;
            var observedAfter = (mask & 2) != 0;
            var knownIdentity = (mask & 4) != 0;
            var metadataAgrees = (mask & 8) != 0;
            var matchesTarget = (mask & 16) != 0;
            var freshPlaying = (mask & 32) != 0;
            var pendingSame = (mask & 64) != 0;
            Check.Equal(mask == 47, QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(
                sameEpoch, observedAfter, knownIdentity, metadataAgrees, matchesTarget, freshPlaying, pendingSame),
                "completed-target rearm requires every independent witness and no existing same-target owner");
        }

        foreach (var state in Enum.GetValues<QQMusicWebSubmissionState>())
            Check.True(!QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(true, false, true, true, false, true, false),
                $"{state} alone cannot unlock a target that was never observed Playing after submission");

        var completedEvidence = true;
        var samePending = false;
        Check.True(QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(true, completedEvidence, true, true, false, true, samePending),
            "an observed target returned to a fresh different-track queue cycle is not blocked forever");
        samePending = true;
        completedEvidence = false; // Adapter consumes the witness at successful registration, under its gate.
        Check.True(!QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(true, completedEvidence, true, true, false, true, samePending),
            "repeated Arm cannot create another owner for the same pending target");
        samePending = false; // The new target is consumed before its next submission attempt.
        Check.True(!QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(true, completedEvidence, true, true, false, true, samePending),
            "a later unknown attempt remains locked despite another fresh different-track event");
        completedEvidence = true; // Only a new actual post-submission fresh Playing witness can renew it.
        Check.True(QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(true, completedEvidence, true, true, false, true, samePending),
            "another genuinely observed occurrence may later establish a separate registration cycle");
        Check.True(QQMusicWebSubmissionPolicy.BlocksSameTarget(true, false),
            "rearm policy does not globally relax the existing automatic-submission latch");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(ownsTarget: false),
            "completed current requests without a pending target never gain a rescue command");
    }

    private static void TrackChangeBoundaries()
    {
        Check.Equal(QQMusicWebAutomaticTransition.TrackChangeFallback, Classify(),
            "fresh agreeing different-track Playing triggers fallback without a near-end sample");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(ownsTarget: false), "consumed owner cannot dispatch again");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(sameEpoch: false), "another process epoch cannot dispatch");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(eventFresh: false), "stale/future callback cannot dispatch");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(sameTrack: true), "same-track Playing is not fallback");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(hasIdentity: false), "empty current identity is not fallback");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(agrees: false), "conflicting metadata is not fallback");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(timelineFresh: false), "stale timeline is not fallback");
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(metadataFresh: false), "timeline-only callback is not fallback");
        foreach (var status in new string?[] { null, "", "Paused", "Stopped", "Closed", "Unknown", "playing" })
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(status: status), "fallback requires actual Playing");

        foreach (var decision in Enum.GetValues<QQMusicWebGuardEventDecision>())
        {
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: decision, status: "Paused"),
                "pause has priority over natural-end and fallback candidates");
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: decision, ownsTarget: false),
                "every automatic branch retains owner gating");
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: decision, sameEpoch: false),
                "every automatic branch retains epoch gating");
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: decision, eventFresh: false),
                "every automatic branch retains event-age gating");
        }
        foreach (var decision in new[] { QQMusicWebGuardEventDecision.KeepTargetClearEvidence,
            QQMusicWebGuardEventDecision.CancelTarget, QQMusicWebGuardEventDecision.SeedPlayingOnly })
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: decision),
                "pause/first-fresh decisions cannot fall through into fallback");

        Check.Equal(QQMusicWebAutomaticTransition.NaturalEnd,
            Classify(decision: QQMusicWebGuardEventDecision.ConsumeTarget, sameTrack: true,
                status: "Stopped", metadataFresh: false), "existing stopped-at-end path remains supported");
        Check.Equal(QQMusicWebAutomaticTransition.NaturalEnd,
            Classify(decision: QQMusicWebGuardEventDecision.ConsumeTarget),
                "natural completion keeps its distinct classification when both predicates agree");
        foreach (var firstPosition in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(59) })
        {
            var first = QQMusicWebGuardPolicy.OnObservation(true, "Playing", false, true,
                QQMusicWebEndPolicy.IsNearEnd(TimeSpan.Zero, TimeSpan.FromMinutes(1), firstPosition));
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: first, sameTrack: true),
                "first fresh same-track head/tail sample after waiting only seeds evidence");
        }
        var midSeekCandidate = QQMusicWebEndPolicy.IsSameTrackRollover(TimeSpan.Zero, TimeSpan.FromMinutes(1),
            TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        var midSeek = QQMusicWebGuardPolicy.OnObservation(true, "Playing", true, true, midSeekCandidate);
        Check.Equal(QQMusicWebAutomaticTransition.None, Classify(decision: midSeek, sameTrack: true),
            "a same-track middle seek is neither natural end nor track-change fallback");
    }

    private static void DelayedMetadataBoundaries()
    {
        var created = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var callback = created.AddSeconds(1);
        foreach (var delay in new[] { 0, 5, 17, 30 })
        for (var mask = 0; mask < 32; mask++)
            Check.Equal(mask == 31, QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
                (mask & 1) != 0, (mask & 2) != 0, created, callback, callback.AddSeconds(delay),
                (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0),
                "helper/gate delay requires unchanged owner, epoch, metadata and a new confirmed typed identity");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, created.AddTicks(-1), callback, true, true, true),
            "a callback before this pending generation cannot acquire the new owner");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, default, callback, callback, true, true, true), "missing owner time fails closed");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, callback.AddSeconds(1).AddTicks(1), callback, true, true, true),
            "a callback more than one second in the future is not evidence");
        Check.True(QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, callback.AddSeconds(1), callback, true, true, true),
            "existing one-second clock tolerance is inclusive");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, DateTimeOffset.MaxValue, callback, true, true, true),
            "extreme future callback does not overflow or become valid");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, DateTimeOffset.MinValue, callback, true, true, true),
            "extreme old callback does not overflow or enter a new owner");
        var confirmed = QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, callback, callback.AddSeconds(17), true, true, true);
        Check.Equal(QQMusicWebAutomaticTransition.TrackChangeFallback,
            Classify(eventFresh: confirmed, metadataFresh: confirmed),
            "a delayed confirmed ID transition uses the existing one-shot fallback classifier");
        Check.Equal(QQMusicWebAutomaticTransition.None,
            Classify(eventFresh: confirmed, metadataFresh: confirmed, sameTrack: true),
            "delayed metadata confirmation does not invent same-track natural completion");
        Check.Equal(QQMusicWebAutomaticTransition.None,
            Classify(eventFresh: confirmed, metadataFresh: confirmed, ownsTarget: false),
            "a consumed target never replays on a later confirmed callback");
    }

    private static void ConsumeOnceSequences()
    {
        foreach (var alreadyTarget in new[] { false, true })
        foreach (var outcome in Enum.GetValues<QQMusicWebSubmissionState>())
        {
            var ownsTarget = true;
            var decision = Classify(ownsTarget: ownsTarget);
            Check.Equal(QQMusicWebAutomaticTransition.TrackChangeFallback, decision, "pending target may transition once");
            ownsTarget = false; // Same ordering as ClearPending before SubmitOnceAsync.
            Check.Equal(!alreadyTarget, QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(decision, alreadyTarget),
                "already observed target is consumed without another selection");
            Check.Equal(QQMusicWebAutomaticTransition.None, Classify(ownsTarget: ownsTarget),
                $"consumption stays final after {outcome}; later callbacks do not retry");
        }
        Check.True(!QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(QQMusicWebAutomaticTransition.None, false),
            "no transition never creates a send");
        Check.True(!QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(QQMusicWebAutomaticTransition.NaturalEnd, true),
            "natural transition already on target also avoids another send");
    }

    private static void PendingConvergenceBoundaries()
    {
        var created = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        var callback = created.AddSeconds(1);
        var received = callback.AddSeconds(17); // A prior helper can hold the gate.
        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(8).Add(TimeSpan.FromTicks(1)), TimeSpan.FromTicks(-1) })
            Check.Equal(elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(8),
                QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(received, received + elapsed),
                "fixed convergence starts at first processing and expires without renewal by later callbacks");
        Check.True(!QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(default, received), "missing convergence origin fails closed");
        Check.True(!QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(received, DateTimeOffset.MaxValue), "extreme expiry is safe");
        Check.True(!QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(received, DateTimeOffset.MinValue), "clock reversal is not a fresh window");
        foreach (var status in new[] { "Playing", "Paused", "Stopped", "Unknown" })
        {
            var aligned = QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
                true, true, created, callback, received, true, true, true);
            var changedId = !QQMusicWebSubmissionPolicy.SameNumericCurrentId("1", "2");
            Check.True(aligned && changedId, $"confirmed different ID is independent of delayed {status} playback info");
            Check.True(QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(
                QQMusicWebAutomaticTransition.TrackChangeFallback, false), "mismatched pending head may submit once");
            Check.True(!QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(
                QQMusicWebAutomaticTransition.TrackChangeFallback, true), $"already matched target consumes without Next or Resume even when {status}");
        }
        foreach (var type in new[] { 0, 112, 255 })
        {
            Check.True(QQMusicWebSubmissionPolicy.SameNumericCurrentId("1", "1"), $"type {type} cannot turn one numeric ID into a transition");
            var paused = QQMusicWebGuardPolicy.OnObservation(true, "Paused", true, false, false);
            Check.Equal(QQMusicWebGuardEventDecision.KeepTargetClearEvidence, paused, "same-ID pause still retains target and clears old end evidence");
        }
        foreach (var status in new[] { "Playing", "Paused", "Stopped" })
        {
            Check.True(!QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity("1", "1", "1:0", "1:112"),
                $"same ID with changed type cannot reuse natural-end evidence in {status}");
            Check.True(QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity("1", "1", "1:0", "1:0"),
                $"unchanged typed identity may continue to the existing {status} guard policy");
        }
        Check.True(!QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity("1", "2", "1:0", "1:0"),
            "equal text keys cannot authorize end evidence for a different numeric identity");
        Check.True(!QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity("1", "1", "", ""),
            "missing typed keys cannot reuse old end evidence");
        Check.True(!QQMusicWebSubmissionPolicy.SameNumericCurrentId("1", null), "unknown identity is not an anchor match");
        Check.True(!QQMusicWebSubmissionPolicy.SameNumericCurrentId("", ""), "empty IDs do not match");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, callback, received, false, true, true), "window/media conflict retains rather than authorizes a transition");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, true, created, callback, received, true, false, true), "a cached or failed typed read cannot confirm a retained witness");
        Check.True(QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(received, received.AddSeconds(2)) &&
            QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(true, true, created, callback,
                received.AddSeconds(2), true, true, true), "a later timeline callback may complete the same owner-bound metadata witness");
        Check.True(!QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(received, received.AddSeconds(9)),
            "a new callback at nine seconds cannot restart the original fixed window");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            false, true, created, callback, received, true, true, true), "Stop or replacement removes the pending owner before dispatch");
        Check.True(!QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
            true, false, created, callback, received, true, true, true), "process replacement invalidates convergence");
    }

    private static QQMusicWebAutomaticTransition Classify(
        QQMusicWebGuardEventDecision decision = QQMusicWebGuardEventDecision.Continue,
        bool ownsTarget = true, bool sameEpoch = true, bool eventFresh = true, bool sameTrack = false,
        bool hasIdentity = true, bool agrees = true, string? status = "Playing",
        bool timelineFresh = true, bool metadataFresh = true) =>
        QQMusicWebSubmissionPolicy.ClassifyAutomaticTransition(decision, ownsTarget, sameEpoch, eventFresh,
            sameTrack, hasIdentity, agrees, status, timelineFresh, metadataFresh);
}
