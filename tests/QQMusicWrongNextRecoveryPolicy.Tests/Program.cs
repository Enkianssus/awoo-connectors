using UnifiedPlayerControlPoc;

var actions = new List<QQMusicWrongNextRecoveryAction>();
var takeoverAttempted = false;

for (var step = 0; step < 3; step++)
{
    var action = QQMusicWrongNextRecoveryPolicy.Decide(
        takeoverAttempted);
    actions.Add(action);

    switch (action)
    {
        case QQMusicWrongNextRecoveryAction.QueuePreservingTakeover:
            if (takeoverAttempted)
            {
                throw new InvalidOperationException(
                    "A queue-preserving takeover must only be offered once.");
            }

            takeoverAttempted = true;
            break;
        case QQMusicWrongNextRecoveryAction.Stop:
            step = 3;
            break;
    }
}

var expected = new[]
{
    QQMusicWrongNextRecoveryAction.QueuePreservingTakeover,
    QQMusicWrongNextRecoveryAction.Stop
};
if (!actions.SequenceEqual(expected))
{
    throw new InvalidOperationException(
        "The first wrong track must perform one takeover and then stop "
        + "without another Next: "
        + string.Join(", ", actions));
}

if (QQMusicWrongNextRecoveryPolicy.Decide(
        takeoverAttempted)
    != QQMusicWrongNextRecoveryAction.Stop)
{
    throw new InvalidOperationException(
        "A completed takeover must terminate the recovery state machine.");
}

var expectedSteps = new[]
{
    QQMusicWrongNextRecoveryStep.Previous,
    QQMusicWrongNextRecoveryStep.VerifyAnchor,
    QQMusicWrongNextRecoveryStep.Pause,
    QQMusicWrongNextRecoveryStep.Reinsert,
    QQMusicWrongNextRecoveryStep.Next,
    QQMusicWrongNextRecoveryStep.VerifyTarget,
    QQMusicWrongNextRecoveryStep.RestoreMute
};
if (!QQMusicWrongNextRecoveryPolicy.QueuePreservingTakeoverSteps
        .SequenceEqual(expectedSteps))
{
    throw new InvalidOperationException(
        "Queue-preserving recovery must verify the previous anchor, "
        + "reinsert once, send exactly one Next, verify the target, and "
        + "restore mute.");
}

var nextCount = QQMusicWrongNextRecoveryPolicy
    .QueuePreservingTakeoverSteps
    .Count(step => step == QQMusicWrongNextRecoveryStep.Next);
if (nextCount != 1)
{
    throw new InvalidOperationException(
        $"Queue-preserving recovery must send exactly one Next, got {nextCount}.");
}

if (!QQMusicWrongNextRecoveryPolicy.ShouldReusePendingNativeNext(
        pendingAnchorSequence: 7,
        currentAnchorSequence: 7,
        pendingProcessId: 1234,
        currentProcessId: 1234,
        pendingSongId: 395562465,
        pendingSongType: 0,
        requestedSongId: 395562465,
        requestedSongType: 0))
{
    throw new InvalidOperationException(
        "A native target must be reused on the same playback anchor.");
}

if (QQMusicWrongNextRecoveryPolicy.ShouldReusePendingNativeNext(
        pendingAnchorSequence: 7,
        currentAnchorSequence: 8,
        pendingProcessId: 1234,
        currentProcessId: 1234,
        pendingSongId: 395562465,
        pendingSongType: 0,
        requestedSongId: 395562465,
        requestedSongType: 0))
{
    throw new InvalidOperationException(
        "A changed playback anchor must force a real native reinsertion.");
}

var repositoryDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (repositoryDirectory is not null
       && !File.Exists(
           Path.Combine(
               repositoryDirectory.FullName,
               "src",
               "QQMusic",
               "QQMusicPlayerAdapter.cs")))
{
    repositoryDirectory = repositoryDirectory.Parent;
}

if (repositoryDirectory is null)
{
    throw new InvalidOperationException(
        "Could not locate the QQ Music adapter source for the regression check.");
}

var adapterSource = File.ReadAllText(
    Path.Combine(
        repositoryDirectory.FullName,
        "src",
        "QQMusic",
        "QQMusicPlayerAdapter.cs"))
    .ReplaceLineEndings("\n");
var recoveryStart = adapterSource.IndexOf(
    "private async Task<WrongNextRecoveryResult>",
    StringComparison.Ordinal);
var recoveryEnd = adapterSource.IndexOf(
    "private async Task<PlayerSnapshot?> WaitForPlaybackAnchorAsync",
    recoveryStart,
    StringComparison.Ordinal);
if (recoveryStart < 0 || recoveryEnd <= recoveryStart)
{
    throw new InvalidOperationException(
        "Could not isolate queue-preserving wrong-next recovery.");
}

if (!adapterSource.Contains(
        "MonitorSoftwareNextEventsAsync(",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "PlayerTrack initialAnchor",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "ObservationMatches(initialAnchor, observedTrack)",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "_lastObservedTrack",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "TracksRepresentSameObservation(",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "QQ next monitoring must retain a PlayerTrack anchor and use the "
        + "narrow track observation matcher for metadata enrichment.");
}

var recoverySource = adapterSource.Substring(
    recoveryStart,
    recoveryEnd - recoveryStart);
var previousIndex = recoverySource.IndexOf(
    "\"'prev'\"",
    StringComparison.Ordinal);
var pauseIndex = recoverySource.IndexOf(
    "\"'pause'\"",
    StringComparison.Ordinal);
var reinsertIndex = recoverySource.IndexOf(
    "EnsureNativeNextInsertedAsync(",
    StringComparison.Ordinal);
var nextIndex = recoverySource.IndexOf(
    "\"'next'\"",
    StringComparison.Ordinal);
var verifyTargetIndex = recoverySource.IndexOf(
    "WaitForTargetAsync(",
    StringComparison.Ordinal);
var nextCountInRecovery = recoverySource
    .Split("\"'next'\"", StringSplitOptions.None)
    .Length - 1;
if (previousIndex < 0
    || pauseIndex <= previousIndex
    || reinsertIndex <= pauseIndex
    || nextIndex <= reinsertIndex
    || verifyTargetIndex <= nextIndex
    || nextCountInRecovery != 1
    || !recoverySource.Contains(
        "audioMute.Restore();",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "_operationGate.WaitAsync",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "PlayerTrack initialAnchor",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "CancellationTokenSource owner",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "PlayerTrack observedWrongTrack",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "ObservationMatches(before.Current, observedWrongTrack)",
        StringComparison.Ordinal)
    || !recoverySource.Contains(
        "WaitForPlaybackAnchorAsync(\n                    initialAnchor,\n                    observedWrongTrack",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Recovery must serialize, run Previous -> Pause -> Reinsert -> one "
        + "Next -> target verification, and restore mute.");
}

if (adapterSource.Contains("correctionTimeout", StringComparison.Ordinal)
    || adapterSource.Contains("correctionAttempts", StringComparison.Ordinal)
    || adapterSource.Contains("retryNative", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Wrong-next recovery must not retain a repeating retry timer or loop.");
}

if (!recoverySource.Contains(
        "TryMarkLateRecoveryTarget(",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "LateRecoveryTargetRecorded",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "_lateRecoveryTarget = null;",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "CancelSoftwareNext(string.Empty);",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Only the Previous-anchor timeout may arm a late target, and the "
        + "marker must be cleared by guard/session lifecycle paths.");
}

var monitorStart = adapterSource.IndexOf(
    "private async Task MonitorSoftwareNextEventsAsync",
    StringComparison.Ordinal);
if (monitorStart < 0
    || monitorStart >= recoveryStart)
{
    throw new InvalidOperationException(
        "Could not isolate QQ software-next monitor lifecycle.");
}

var monitorSource = adapterSource.Substring(
    monitorStart,
    recoveryStart - monitorStart);
if (!monitorSource.Contains(
        "preserveLateRecoveryTarget =\n                    recovery.LateRecoveryTargetRecorded",
        StringComparison.Ordinal)
    || !monitorSource.Contains(
        "var ownerWasCanceled = owner.IsCancellationRequested;",
        StringComparison.Ordinal)
    || !monitorSource.Contains(
        "if (!preserveLateRecoveryTarget || ownerWasCanceled)",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Monitor teardown must preserve only its own recorded late target "
        + "and clear it when the owner was cancelled.");
}

var armStart = adapterSource.IndexOf(
    "_softwareNextTarget = track;",
    StringComparison.Ordinal);
var armEnd = adapterSource.IndexOf(
    "_softwareNextStatus =",
    armStart,
    StringComparison.Ordinal);
var cancelStart = adapterSource.IndexOf(
    "private void CancelSoftwareNext",
    StringComparison.Ordinal);
var cancelEnd = adapterSource.IndexOf(
    "private static string BuildPlaybackKey",
    cancelStart,
    StringComparison.Ordinal);
var sessionStart = adapterSource.IndexOf(
    "private void ObserveNativeSession",
    StringComparison.Ordinal);
var sessionEnd = adapterSource.IndexOf(
    "private static string? FindExecutablePath",
    sessionStart,
    StringComparison.Ordinal);
if (armStart < 0
    || armEnd <= armStart
    || !adapterSource
        .Substring(armStart, armEnd - armStart)
        .Contains("_lateRecoveryTarget = null;", StringComparison.Ordinal)
    || cancelStart < 0
    || cancelEnd <= cancelStart
    || !adapterSource
        .Substring(cancelStart, cancelEnd - cancelStart)
        .Contains("_lateRecoveryTarget = null;", StringComparison.Ordinal)
    || sessionStart < 0
    || sessionEnd <= sessionStart
    || !adapterSource
        .Substring(sessionStart, sessionEnd - sessionStart)
        .Contains("CancelSoftwareNext(string.Empty);", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Late-target marker must be cleared on a new guard, cancellation, "
        + "and native session reset.");
}

var confirmationStart = adapterSource.IndexOf(
    "private bool ConfirmLateRecoveryTargetIfPlaying",
    StringComparison.Ordinal);
var confirmationEnd = adapterSource.IndexOf(
    "private bool PrunePendingNativeNextLocked",
    confirmationStart,
    StringComparison.Ordinal);
if (confirmationStart < 0 || confirmationEnd <= confirmationStart)
{
    throw new InvalidOperationException(
        "Could not isolate late-target confirmation logic.");
}

var confirmationSource = adapterSource.Substring(
    confirmationStart,
    confirmationEnd - confirmationStart);
if (!confirmationSource.Contains(
        "TrackMatches(current, _lateRecoveryTarget)",
        StringComparison.Ordinal)
    || !confirmationSource.Contains(
        "目标延迟到达，已确认",
        StringComparison.Ordinal)
    || !confirmationSource.Contains(
        "_lateRecoveryTarget = null;",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "SendSingleInstanceCommand",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "EnsureNativeNextInsertedAsync",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "playbysongid",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "'next'",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "'prev'",
        StringComparison.Ordinal)
    || confirmationSource.Contains(
        "'play'",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Late-target confirmation must only update status and clear its marker.");
}

if (!adapterSource.Contains(
        "_softwareNextGuardIdSequence++;",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "NextGuardId = guardId",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "NextGuardId: guardedNext.GuardId",
        StringComparison.Ordinal)
    || !adapterSource.Contains(
        "snapshot.NextGuardId.ToString()",
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Every QQ guard cycle must publish a distinct stable id, including "
        + "otherwise identical repeated requests.");
}

var cancelGuardIdStart = adapterSource.IndexOf(
    "private void CancelSoftwareNext",
    StringComparison.Ordinal);
var cancelGuardIdEnd = adapterSource.IndexOf(
    "private static string BuildPlaybackKey",
    cancelGuardIdStart,
    StringComparison.Ordinal);
if (cancelGuardIdStart < 0
    || cancelGuardIdEnd <= cancelGuardIdStart
    || !adapterSource
        .Substring(cancelGuardIdStart, cancelGuardIdEnd - cancelGuardIdStart)
        .Contains("_softwareNextGuardId = 0;", StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "Cancelling a QQ guard must clear its published cycle id.");
}

Console.WriteLine("QQMusicWrongNextRecoveryPolicy.Tests passed.");
