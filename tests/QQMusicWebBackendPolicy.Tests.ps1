$ErrorActionPreference = 'Stop'

# Source-contract checks only. This script does not construct the adapter,
# load QQ DLLs, enumerate processes, invoke COM, send IPC, or play media.
# Passing does not prove natural-end detection or real-player compatibility.
# Native insertion intent and typed status are checked as source contracts,
# not as evidence that QQ preserved its queue or acknowledged an insertion.
# Metadata/read notifications are not all original Windows callbacks. A held
# notification never replaces the required fresh typed-ID and metadata fences.
# Fast drain checks concern the isolated sender's lifecycle only. They prove
# neither queue acknowledgment nor a measured one-second playback latency.
$sourceDirectory = Join-Path $PSScriptRoot '..\src\QQMusic'
function Read-Source([string]$name) {
    [IO.File]::ReadAllText((Join-Path $sourceDirectory $name), [Text.Encoding]::UTF8)
}
function Section([string]$source, [string]$begin, [string]$end) {
    $start = $source.IndexOf($begin, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Source boundary not found: $begin" }
    $finish = $source.IndexOf($end, $start + $begin.Length, [StringComparison]::Ordinal)
    if ($finish -lt 0) { throw "Source boundary not found: $end" }
    $source.Substring($start, $finish - $start)
}
$script:checks = 0
function Assert-Source([bool]$condition, [string]$description) {
    if (-not $condition) { throw "QQ Web source contract failed: $description" }
    $script:checks++
}

$adapterRaw = Read-Source 'QQMusicWebPlayerAdapter.cs'
$timingSource = Read-Source 'QQMusicWebTiming.cs'
# Normalize only the reviewed standalone diagnostic statements. Keep their
# line breaks, and never remove a whole line merely because it mentions timing:
# appended commands, unknown labels/arguments and conditional calls stay visible.
$timingStages = @(
    'ExecuteCancelGateWait', 'ExecuteObserverReady', 'ExecuteGateWait',
    'ExecuteRefreshMedia', 'ExecuteInitialIdentity',
    'ArmDetails', 'ArmIdentity', 'ArmTransport',
    'SubmitDetails', 'SubmitPreInsertIdentity', 'SubmitTransport',
    'SubmitPreNextIdentity', 'SubmitSendNext', 'SubmitPostNextRefresh', 'SubmitPostNextIdentity'
)
$timingStatements = @(
    'var cancelGateStarted = QQMusicWebTiming.Start();'
    'var timingStarted = QQMusicWebTiming.Start();'
    'timingStarted = QQMusicWebTiming.Start();'
)
$timingWrites = foreach ($stage in $timingStages) {
    $clockName = if ($stage -ceq 'ExecuteCancelGateWait') { 'cancelGateStarted' } else { 'timingStarted' }
    "QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.$stage, $clockName);"
}
$timingStatements += $timingWrites
$timingPattern = '(?m)^[\t ]*(?:' + (($timingStatements | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')[\t ]*\r?$'
$adapter = [regex]::Replace($adapterRaw, $timingPattern, '')
$invalidTimingCounts = @($timingWrites | Where-Object { [regex]::Matches($adapterRaw, [regex]::Escape($_)).Count -ne 1 })
Assert-Source ($invalidTimingCounts.Count -eq 0 -and
    [regex]::Matches($adapterRaw, 'QQMusicWebTiming.Start\(\)').Count -eq 15 -and
    [regex]::Matches($adapterRaw, 'QQMusicWebTiming.WriteElapsed\(').Count -eq 15 -and
    $adapter -notmatch 'QQMusicWebTiming\.') `
    'all fifteen reviewed timing stages remain present, and normalization removes only their exact standalone start/write statements'
$timingSentinel = $timingWrites[0]
$ordinaryStatement = 'cancellationToken.ThrowIfCancellationRequested();'
$appendedStatement = $timingSentinel + ' SendControl("next", observation, cancellationToken);'
$unknownTiming = 'QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.UnknownStage, timingStarted);'
$conditionalTiming = 'if (condition) ' + $timingSentinel
Assert-Source ([regex]::Replace($timingSentinel + "`n" + $ordinaryStatement, $timingPattern, '') -ceq "`n$ordinaryStatement" -and
    [regex]::Replace($appendedStatement, $timingPattern, '') -ceq $appendedStatement -and
    [regex]::Replace($unknownTiming, $timingPattern, '') -ceq $unknownTiming -and
    [regex]::Replace($conditionalTiming, $timingPattern, '') -ceq $conditionalTiming) `
    'timing normalization preserves business statements, appended commands, conditional calls and unreviewed stage labels'
Assert-Source ($timingSource -match 'Environment.GetEnvironmentVariable\("AWOO_QQMUSIC_WEB_TIMING"\), "1", StringComparison.Ordinal' -and
    $timingSource -match 'Start\(\) => Enabled \? Stopwatch.GetTimestamp\(\) : 0;' -and
    $timingSource -match 'if \(!Enabled\) return;' -and $timingSource -match 'if \(label is null\) return;' -and
    $timingSource -match 'Console.Error.WriteLine\("qq-web-timing " \+ label \+ " elapsedMs="' -and
    $timingSource -match 'catch \{' -and
    $timingSource -notmatch 'Console.Out|_transport|SendControl|SubmitOnceAsync|Process.Start\(|HttpClient|ThrowIfCancellationRequested') `
    'the normalized instrumentation itself is opt-in bounded stderr timing, not a command, receipt, cancellation boundary or data-bearing log'
$program = Read-Source 'Program.cs'
$bridgeSource = Read-Source 'QQMusicWebBridgeHost.cs'
$client = Read-Source 'QQMusicWebApiClient.cs'
$transport = Read-Source 'QQMusicWebPlayTransport.cs'
$statusProtocol = Read-Source 'QQMusicWebStatusProtocol.cs'
$nativeController = Read-Source 'QQMusicNativeController.cs'
$submissionPolicy = Read-Source 'QQMusicWebSubmissionPolicy.cs'
$drainPolicy = Read-Source 'QQMusicWebDrainPolicy.cs'
$receiptSource = Read-Source 'QQMusicWebReceipt.cs'
[xml]$qqProject = Read-Source 'BiliNCM.Connector.QQMusic.csproj'
$readyToRunProperties = @($qqProject.SelectNodes('/Project//PublishReadyToRun'))
$readyToRunCondition = '''$(Configuration)'' == ''Release'' and ''$(SelfContained)'' == ''false'' and ''$(PublishSingleFile)'' == ''false'' and ''$(PublishReadyToRun)'' == '''''
Assert-Source ($readyToRunProperties.Count -eq 1 -and
    $readyToRunProperties[0].InnerText.Trim() -ceq 'true' -and
    $readyToRunProperties[0].ParentNode.Name -ceq 'PropertyGroup' -and
    $readyToRunProperties[0].ParentNode.GetAttribute('Condition') -ceq '' -and
    [regex]::Replace($readyToRunProperties[0].GetAttribute('Condition'), '\s+', ' ').Trim() -ceq $readyToRunCondition) `
    'ReadyToRun defaults only for Release framework-dependent non-single-file builds when unset; explicit overrides, Debug and SelfContained defaults remain untouched'

Assert-Source ($adapter -notmatch 'QQMusicNativeNextTransport|QQMusicNativeNextAnalyzer|QQMusicNativeNextProfiles|AllowUnsafeNativeNext|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|new\s+QQMusicPlayerAdapter\s*\(') `
    'the independent Web adapter must not invoke the old profile/injection adapter or transport'
Assert-Source ($program -match 'args\.Length\s*==\s*1\s*&&\s*args\[0\]\s*==\s*QQMusicControlPoc\.QQMusicWebPlayTransport\.HelperArgument' -and
    $program -match 'QQMusicWebBridgeHost\.RunFromStandardInput\(\)' -and
    $program -match 'AWOO_QQMUSIC_BACKEND' -and $program -match 'new QQMusicWebPlayerAdapter\(\)') `
    'helper mode and independent backend selection must remain explicit'
Assert-Source ($program -match '(?s)if \(string\.IsNullOrWhiteSpace\(backend\) \|\|\s*string\.Equals\(backend, "web", StringComparison\.OrdinalIgnoreCase\)\)\s*return await ConnectorRuntime\.RunAsync\("qqmusic", new QQMusicWebPlayerAdapter\(\)\)' -and
    $program -match 'string\.Equals\(backend, "native", StringComparison\.OrdinalIgnoreCase\)') `
    'the new connector must default to Web while retaining only explicit native selection'

$execute = Section $adapter 'public async Task<PlayerOperationResult> ExecuteAsync(' 'private async Task<PlayerOperationResult> ArmAsync('
$probe = Section $adapter 'public async Task<PlayerSnapshot> ProbeAsync(' 'public async IAsyncEnumerable<PlayerSnapshot> WatchSnapshotsAsync('
$busyProbe = Section $probe 'var cached = Volatile.Read(ref _lastSnapshot);' 'await _mutation.WaitAsync(cancellationToken)'
Assert-Source ($probe -match '_mutation\.WaitAsync\(0, cancellationToken\)' -and
    $busyProbe -match 'return cached with' -and $busyProbe -match 'Status =' -and
    $busyProbe -match 'ObservationDeferred = true' -and
    $busyProbe -notmatch 'ObservedAt\s*=|Next\s*=|NextObservation\s*=|Observe\(\)|PlayAsync|SendControl|SubmitOnceAsync' -and
    $adapter -match 'Volatile\.Write\(ref _lastSnapshot, snapshot\)') `
    'a busy read may return the previous snapshot without renewing its timestamp or clearing the registered next target'
$observerStart = Section $adapter 'private void EnsureObserver()' 'private async Task ObserveEventsAsync()'
$eventLoop = Section $adapter 'private async Task ObserveEventsAsync()' 'private async Task<bool> HandleEventAsync('
Assert-Source ($observerStart -match '_observer\s*\?\?=\s*Task\.Run\(ObserveEventsAsync\)' -and
    $eventLoop -match '(?s)while \(!token\.IsCancellationRequested\).*?await Task\.Yield\(\)') `
    'a hot media-event stream must not run inline in first Probe and must yield between batches'
$manualNext = Section $execute 'if (command == PlayerCommand.Next && _pending is { } pending)' 'var control = command switch'
Assert-Source ($manualNext -match 'return SendControl\("next", observation, cancellationToken\)' -and
    $manualNext -notmatch 'ClearPending\(|SubmitOnceAsync\(|_transport\.|_pending\s*=') `
    'manual Next uses the already inserted native queue and retains its owner for actual-ID observation, never directly forces a second insertion'
Assert-Source ($execute.IndexOf('_mutation.WaitAsync(', [StringComparison]::Ordinal) -ge 0 -and
    $execute.IndexOf('_mutation.WaitAsync(', [StringComparison]::Ordinal) -lt
        $execute.IndexOf('SubmitOnceAsync(', [StringComparison]::Ordinal)) `
    'manual mutation must acquire the shared gate before submission'
Assert-Source ($execute -match 'CreateLinkedTokenSource\(cancellationToken, _lifetime\.Token\)' -and
    $execute -match '(?s)_mutation\.WaitAsync\(cancellationToken\).*?try\s*\{\s*ObjectDisposedException\.ThrowIf') `
    'queued manual work must retain lifetime cancellation and recheck disposal after acquiring the gate'
Assert-Source ($execute -match '_events\.EnsureStartedAsync\(\)\.WaitAsync\(cancellationToken\)') `
    'manual startup waiting must honor the linked cancellation token'
$cancelOnly = Section $execute 'if (command == PlayerCommand.ArmNextGuard && track is null)' 'EnsureObserver();'
Assert-Source ($cancelOnly -match 'ClearPending\(' -and $cancelOnly -match 'Next = null' -and
    $cancelOnly -match 'PublishSnapshot\(cancelled\)' -and
    $cancelOnly -notmatch 'Observe\(|ObserveWithIdentityAsync|EnsureStartedAsync|RefreshMediaAsync|SendControl\(|_transport\.|_songDetails\.|_statusReader\.|Process\.') `
    'cancel-only must run before observer startup and remain local without player, status or detail calls'
Assert-Source ($cancelOnly.IndexOf('CancelAutomaticWork();', [StringComparison]::Ordinal) -ge 0 -and
    $cancelOnly.IndexOf('CancelAutomaticWork();', [StringComparison]::Ordinal) -lt $cancelOnly.IndexOf('_mutation.WaitAsync(', [StringComparison]::Ordinal) -and
    $cancelOnly -match 'AutomaticStoppedStatus' -and $adapter -match '此前已插入或已发送的原生命令不能撤回') `
    'stop signals in-flight automatic preparation before waiting for its gate and never promises native command recall'
$stopHelper = Section $adapter 'private void CancelAutomaticWork()' 'private CancellationTokenSource CreateAutomaticWorkCancellation('
$guardTransport = Section $adapter 'private Task<QQMusicWebSubmission> StartGuardTransport(' 'private static bool SameSong('
Assert-Source ($stopHelper -match 'lock \(_automaticSync\) _automaticStop.Cancel\(\);' -and
    $stopHelper -notmatch 'Process\.|Observe|_events\.|_transport\.|_songDetails\.|_statusReader\.' -and
    $guardTransport -match '(?s)lock \(_automaticSync\).*?preDispatchToken.ThrowIfCancellationRequested\(\);.*?_automaticStop.Token.ThrowIfCancellationRequested\(\);.*?_transport.PlayAsync\(song, epoch.ProcessId, _lifetime.Token,' -and
    $guardTransport -notmatch '_transport.PlayAsync\([^;]*preDispatchToken') `
    'automatic stop is local; dispatch and cancellation have one lock boundary and an entered helper drains under lifetime, not the stop token'
$prepareObservation = Section $execute 'EnsureObserver();' 'var observation = await ObserveWithIdentityAsync('
Assert-Source ($prepareObservation -match '(?s)_mutation\.WaitAsync\(cancellationToken\).*?try\s*\{.*?await _events\.RefreshMediaAsync\(cancellationToken\)\.ConfigureAwait\(false\);\s*cancellationToken\.ThrowIfCancellationRequested\(\);' -and
    $prepareObservation -notmatch '\bcatch\b|\bif\s*\(\s*await _events\.RefreshMediaAsync') `
    'manual observation must follow one bounded media refresh under the gate without swallowing cancellation or treating its bool as playback evidence'

$arm = Section $adapter 'private async Task<PlayerOperationResult> ArmAsync(' 'private async Task<PlayerOperationResult> SubmitOnceAsync('
$clearPending = Section $adapter 'private void ClearPending(' 'private PlayerSnapshot Snapshot('
$samePending = Section $arm 'if (_pending is { } existing' 'ClearPending('
Assert-Source ($samePending -match 'existing.Epoch == observation.Epoch' -and
    $samePending -match 'TrySong\(track, out var repeated\)' -and $samePending -match 'SameSong\(existing.Song, repeated!\)' -and
    $samePending -match 'return Result\(OperationOutcome.Accepted,' -and
    $samePending -notmatch '_pending\s*=|_transport\.|ResolveAsync|SendControl|SubmitOnceAsync') `
    'an already pending same-epoch typed song is acknowledged locally before detail lookup or another native insertion'
$newArm = $arm.Substring($arm.IndexOf('ClearPending(', [StringComparison]::Ordinal))
Assert-Source ($newArm.IndexOf('ClearPending(', [StringComparison]::Ordinal) -lt $newArm.IndexOf('TrySong(', [StringComparison]::Ordinal) -and
    $newArm.IndexOf('ClearPending(', [StringComparison]::Ordinal) -lt $newArm.IndexOf('return Result(', [StringComparison]::Ordinal) -and
    $clearPending -match '_pending\s*=\s*null;' -and $clearPending -notmatch 'SubmitOnceAsync|PlayAsync|SendControl|ResolveAsync') `
    'a changed Arm target invalidates the previous local owner before validation; rejection cannot leave that stale owner'
$repeatArm = Section $arm 'if (_lastSubmission is { } previous' 'var now ='
Assert-Source ($repeatArm -match 'SameSong\(previous\.Song, song!\)' -and
    $repeatArm -match 'previous\.Epoch\s*==\s*observation\.Epoch' -and
    $repeatArm -match '!completedRearm' -and
    $repeatArm -match 'return Result\(OperationOutcome\.Unsupported,' -and
    $repeatArm -match '"qq-web-repeat-target-unsupported"' -and
    $repeatArm -notmatch '_pending\s*=|SubmitOnceAsync\(') `
    'an unresolved previously submitted same-epoch target cannot be registered as another occurrence'
Assert-Source ($arm -notmatch 'LatchedResult\(') `
    'Arm must not report a previous play result as successful next-target registration'
Assert-Source ($arm -match 'QQMusicWebGuardPolicy\.Registration\(' -and
    $arm -match 'registration == QQMusicWebGuardRegistration\.FreshPlayingReady \? observation : null' -and
    $arm -match 'timeline is null \? QQMusicWebGuardRegistration\.Rejected' -and
    $arm -match '!string.IsNullOrEmpty\(observation.Current\?\.Id\)' -and
    $arm -match 'observation.NativeStatus\?\.SongPosition is >= 0') `
    'registration requires a typed current ID, native position and tested timeline policy; only fresh Playing initializes end evidence'
Assert-Source ($arm -match '(?s)_songDetails.ResolveAsync\(song!, cancellationToken\).*?cancellationToken.ThrowIfCancellationRequested\(\);.*?ObserveWithIdentityAsync\(cancellationToken, forceRead: true\).*?resolved is null.*?observation.Epoch != recheck.Epoch.*?observation.Key != recheck.Key.*?recheck.NativeStatus\?\.SongPosition is not >= 0.*?return Result\(OperationOutcome.Rejected,' -and
    $arm -match '(?s)_pending = new\(.*?StartGuardTransport\(resolved, observation.Epoch!, cancellationToken, QQMusicWebIntent.InsertNext\)' -and
    [regex]::Matches($arm, 'StartGuardTransport\(').Count -eq 1 -and
    $arm -notmatch 'SubmitOnceAsync\(|SendControl\(') `
    'native insertion refreshes exact detail and the typed current-position anchor, reserves one pending owner, and sends one explicit InsertNext intent'
Assert-Source ($arm -match '(?s)insertion.State == QQMusicWebSubmissionState.RejectedBeforeDispatch.*?ClearPending\(.*?return Result\(OperationOutcome.Rejected,' -and
    $arm -match 'return Result\(OperationOutcome.Accepted, _status, recheck\)' -and
    $arm -match '(?s)catch \(Exception error\) when \(error is not OutOfMemoryException\).*?insertion = new\(QQMusicWebSubmissionState.OutcomeUnknown, "web-insert-outcome-unknown"\);' -and
    $arm -notmatch 'OperationOutcome.Applied|OperationOutcome.Confirmed') `
    'definitely unsent insertion clears its owner; other receipts retain a no-retry owner without claiming confirmed playback'
$restoreInsertionGuard = Section $arm 'var priorInsertion = _lastInsertion;' '_lastInsertion = new('
Assert-Source ($restoreInsertionGuard -match 'priorInsertion.Epoch == recheck.Epoch && SameSong\(priorInsertion.Song, resolved\)' -and
    $restoreInsertionGuard -match 'priorInsertion.TargetObserved && !MatchesSong\(recheck, resolved, track!.Artist\)' -and
    $restoreInsertionGuard -match 'IsFreshTimeline\(recheck, DateTimeOffset.UtcNow\) && recheck.Timeline!.PlaybackStatus == "Playing"' -and
    $restoreInsertionGuard -match 'return Result\(OperationOutcome.Accepted, _status, recheck\)' -and
    $restoreInsertionGuard -notmatch '_transport\.|SubmitOnceAsync|SendControl|_lastSubmission\s*=' -and
    $arm.IndexOf('_pending = new(', [StringComparison]::Ordinal) -lt $arm.IndexOf('var priorInsertion = _lastInsertion;', [StringComparison]::Ordinal)) `
    'same-head rearm restores only its guard after possible insertion; a new insertion cycle requires observed target playback followed by different fresh Playing'
Assert-Source ($arm -match '(?s)_lastInsertion = new\(resolved,.*?StartGuardTransport\(' -and
    $arm -match '(?s)insertion.State == QQMusicWebSubmissionState.RejectedBeforeDispatch.*?_lastInsertion = priorInsertion;.*?ClearPending\(.*?return Result\(OperationOutcome.Rejected,' -and
    $clearPending -notmatch '_lastInsertion\s*=' -and $cancelOnly -notmatch '_lastInsertion\s*=') `
    'insertion has an independent pre-send reservation retained across local cancellation; only definitely-unsent insertion restores its predecessor'
$finalRearm = Section $arm 'completedRearm = _lastSubmission is { } finalCompleted' '_known['
Assert-Source ($arm.IndexOf('completedRearm = _lastSubmission is { } finalCompleted', [StringComparison]::Ordinal) -gt
        $arm.IndexOf('ObserveWithIdentityAsync(cancellationToken, forceRead: true)', [StringComparison]::Ordinal) -and
    $finalRearm -match 'finalCompleted.Epoch == recheck.Epoch' -and
    $finalRearm -match 'finalCompleted.TargetObserved, !string.IsNullOrEmpty\(recheck.Current\?\.Id\), recheck.MetadataAgrees' -and
    $finalRearm -match 'MatchesSong\(recheck, song!, track!.Artist\)' -and
    $finalRearm -match 'IsFreshTimeline\(recheck, DateTimeOffset.UtcNow\)' -and
    $finalRearm -match 'finalPrevious.Epoch == recheck.Epoch && !completedRearm' -and
    $finalRearm -match 'return Result\(OperationOutcome.Rejected,' -and
    $arm -match '(?s)if \(insertion.State == QQMusicWebSubmissionState.RejectedBeforeDispatch\).*?return Result\(OperationOutcome.Rejected,.*?\}\s*if \(completedRearm\) _lastSubmission = null;') `
    'completed-target rearm is revalidated after detail/status waits and releases the submission latch only after a possibly sent insertion'

$submit = Section $adapter 'private async Task<PlayerOperationResult> SubmitOnceAsync(' 'private PlayerOperationResult LatchedResult('
Assert-Source ($submit -match '(?s)return LatchedResult.*?_songDetails.ResolveAsync\(song, cancellationToken\).*?cancellationToken.ThrowIfCancellationRequested\(\);.*?if \(resolvedSong is null\).*?qq-web-song-details-unavailable.*?song = resolvedSong;.*?ObserveWithIdentityAsync\(cancellationToken, forceRead: true\).*?ReconcileSession\(recheck\);.*?recheck.Epoch != before.Epoch.*?_lastSubmission = new\(' -and
    [regex]::Matches($adapter, '_songDetails.ResolveAsync\(').Count -eq 2 -and
    $submit -match 'var alreadyMatched = MatchesSong\(recheck, song, artist\);') `
    'every new send must resolve exact current detail before reservation, recheck cancellation/epoch, and reject without sparse-data fallback'
$submitEntry = Section $submit 'var recheck = before;' 'var resolvedSong = await _songDetails.ResolveAsync('
$manualReuse = Section $execute 'var observation = await ObserveWithIdentityAsync(' 'return await SubmitOnceAsync('
$eventReuse = Section $adapter 'private async Task<bool> HandleEventAsync(' 'await SubmitOnceAsync(pending.Song'
Assert-Source ($submitEntry -match 'ReconcileSession\(recheck\);' -and
    $submitEntry -match '(?s)cancellationToken.ThrowIfCancellationRequested\(\);.*?QQMusicWebSubmissionPolicy.BlocksSameTarget\(.*?return LatchedResult' -and
    $submitEntry -notmatch '\bObserve\(|ObserveWithIdentityAsync|\bawait\b|_transport\.|SendControl\(' -and
    [regex]::Matches($adapter, 'SubmitOnceAsync\(').Count -eq 3 -and
    [regex]::Matches($manualReuse, '\bawait\b').Count -eq 1 -and $eventReuse -notmatch '\bawait\b' -and
    $execute -match '(?s)_mutation.WaitAsync\(cancellationToken\).*?var observation = await ObserveWithIdentityAsync.*?SubmitOnceAsync\(song!, track!.Artist, observation' -and
    $eventLoop -match '(?s)_mutation.WaitAsync\(token\).*?var observation = await ObserveWithIdentityAsync.*?HandleEventAsync\(latest, timelineEvent, metadataEvent, observation, evidence, workToken\)') `
    'submission entry reuses only either caller freshly observed under the same gate, without intervening async work, and retains cancellation and latch checks'
$failedDetail = Section $submit 'if (resolvedSong is null)' 'song = resolvedSong;'
Assert-Source ([regex]::Matches($failedDetail, '\bObserve\(').Count -eq 1 -and
    $failedDetail -match '(?s)recheck = Observe\(\);.*?ReconcileSession\(recheck\);.*?return Result\(OperationOutcome.Rejected,.*?qq-web-song-details-unavailable' -and
    $failedDetail -notmatch '\bawait\b|ObserveWithIdentityAsync|StartGuardTransport|_transport\.|PlayAsync|SendControl|SubmitOnceAsync|_lastSubmission\s*=') `
    'failed detail preparation observes a fresh rejection snapshot only, without a typed helper, insertion, blind Next, reservation or retry'
$successfulDetail = Section $submit 'song = resolvedSong;' '_known.TryAdd('
Assert-Source ([regex]::Matches($successfulDetail, 'ObserveWithIdentityAsync\(cancellationToken, forceRead: true\)').Count -eq 1 -and
    $successfulDetail -match '(?s)ObserveWithIdentityAsync\(cancellationToken, forceRead: true\).*?ReconcileSession\(recheck\);.*?if \(recheck.Epoch != before.Epoch\).*?return Result\(OperationOutcome.Rejected,' -and
    $successfulDetail -match 'recheck.Epoch != before.Epoch \|\| string.IsNullOrEmpty\(recheck.Current\?\.Id\)' -and
    $successfulDetail -match '!recheck.MetadataAgrees \|\| recheck.NativeStatus\?\.SongPosition is not >= 0' -and
    $successfulDetail -notmatch '\bObserve\(|_transport\.|StartGuardTransport|SendControl\(|_lastSubmission\s*=') `
    'successful detail preparation performs one fresh typed read before reservation, preserving epoch, agreeing current ID and native-position fences without another desktop scan'
Assert-Source ($probe -notmatch '_songDetails|ResolveAsync' -and $eventLoop -notmatch '_songDetails|ResolveAsync' -and
    $adapter -match '_songDetails.Dispose\(\)') `
    'detail requests belong only to new media intents, never polling or callback observation, and have owned cleanup'
Assert-Source ($submit -match '(?s)ObserveWithIdentityAsync\(cancellationToken, forceRead: true\).*?recheck.Epoch != before.Epoch \|\| string.IsNullOrEmpty\(recheck.Current\?\.Id\).*?recheck.NativeStatus\?\.SongPosition is not >= 0.*?qq-web-insert-anchor-unavailable.*?_transport.PlayAsync\(' -and
    $adapter -notmatch 'QueueSetting|QQMusicWebIntent.PlaySelected' -and
    $submit -match '(?s)_transport.PlayAsync\(.*?intent: QQMusicWebIntent.InsertNext\).*?StartGuardTransport\(song, before.Epoch, cancellationToken, QQMusicWebIntent.InsertNext\)') `
    'manual and automatic force both insert next from a typed ID/position anchor without depending on or changing QQ global queue settings'
$alreadyAtTarget = Section $submit 'if (!freshManualIntent && recheck.Epoch == before.Epoch && MatchesSong(recheck, song, artist))' 'if (recheck.Epoch != before.Epoch || string.IsNullOrEmpty(recheck.Current?.Id)'
Assert-Source ($submit.IndexOf('if (!freshManualIntent &&', [StringComparison]::Ordinal) -gt
        $submit.IndexOf('ObserveWithIdentityAsync(cancellationToken, forceRead: true)', [StringComparison]::Ordinal) -and
    $alreadyAtTarget -match 'return Result\(OperationOutcome.Applied, _status, recheck\)' -and
    $alreadyAtTarget -notmatch '_transport\.|SubmitOnceAsync|SendControl|_lastSubmission\s*=') `
    'automatic preparation that finds the actual same-epoch target ID returns without another force send; explicit manual intent is not silently suppressed'
$explicitSelection = Section $execute 'if (command is PlayerCommand.PlaySelected or PlayerCommand.InterruptSelected)' 'if (command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard)'
Assert-Source ($explicitSelection -match '(?s)if \(!TrySong\(track, out var song\)\).*?return Result\(.*?cancellationToken\.ThrowIfCancellationRequested\(\);.*?QQMusicWebSubmissionPolicy\.AllowsFreshManualAttempt\(.*?freshManualIntent: freshManualIntent' -and
    $submit -match 'bool freshManualIntent = false' -and
    $submit -match '(?s)recheck\.Epoch != before\.Epoch.*?return Result\(.*?cancellationToken\.ThrowIfCancellationRequested\(\);.*?QQMusicWebSubmissionPolicy\.BlocksSameTarget\(' -and
    [regex]::Matches($adapter, 'freshManualIntent: freshManualIntent').Count -eq 1) `
    'only validated uncancelled explicit selections may bypass the old song latch, after gate and epoch recheck'
Assert-Source ($submit -match 'var previousLatch = _lastSubmission;' -and
    $submit -match 'QQMusicWebSubmissionPolicy\.RestorePreviousLatch\(freshManualIntent,' -and
    $submit -match 'previousLatch\.Epoch == recheck\.Epoch' -and
    $arm -notmatch 'freshManualIntent|AllowsFreshManualAttempt' -and
    $arm -match 'if \(completedRearm\) _lastSubmission = null;' -and
    $arm -match '(?s)QQMusicWebSubmissionPolicy.CanRearmCompletedTarget\(.*?completed.TargetObserved,.*?MatchesSong\(observation, song!, track!.Artist\)') `
    'a definitely-unsent explicit attempt preserves the older latch; Arm releases it only through the completed-target policy, never a manual-intent exemption'
Assert-Source ($submit -match '(?s)_lastSubmission is \{ \} previous.*?SameSong\(previous\.Song, song\).*?return LatchedResult' -and
    $submit -match '(?s)_lastSubmission\s*=\s*new\(.*?QQMusicWebSubmissionState\.OutcomeUnknown\).*?_transport\.PlayAsync\(') `
    'same-target dispatch must be latched before transport entry, including ambiguous outcomes'
Assert-Source ($submit -match '(?s)_transport\.PlayAsync\(.*?before\.Epoch\.StartedAtUtcTicks' -and
    $transport -match 'expectedProcessStartTimeUtcTicks' -and
    $transport -match 'epoch\s*!=\s*expectedProcessStartTimeUtcTicks') `
    'transport must compare the adapter-captured epoch instead of rebinding a recycled PID'
Assert-Source ($submit -match '(?s)if \(submission.State != QQMusicWebSubmissionState.SubmittedUnverified\).*?return Result\(OperationOutcome.Indeterminate,.*?qq-web-insert-outcome-unknown.*?var afterInsert = await ObserveWithIdentityAsync' -and
    $submit -match '(?s)DecideInsertedTargetAdvance\(submission.State,.*?afterInsert.Epoch == recheck.Epoch, afterInsert.Key == recheck.Key,.*?afterInsert.NativeStatus\?\.SongPosition is >= 0' -and
    $submit -match '(?s)advance == QQMusicWebInsertAdvance.TargetObserved.*?return Result\(OperationOutcome.Applied,.*?advance != QQMusicWebInsertAdvance.AdvanceOnce.*?return Result\(OperationOutcome.Indeterminate,.*?SendControl\("next"' -and
    $submit -match '(?s)SendControl\("next".*?RefreshMediaAsync\(confirmationToken\).*?var afterNext = await ObserveWithIdentityAsync' -and
    $submit -notmatch '\bwhile\s*\(|\bfor\s*\(') `
    'only a completed insertion and unchanged confirmed anchor may advance once; target arrival skips Next and one post-Next observation never retries writes'
Assert-Source ($submit -match '(?s)!freshManualIntent && cancellationToken.IsCancellationRequested.*?qq-web-stopped-after-insert.*?var afterInsert' -and
    $submit -match '(?s)lock \(_automaticSync\).*?cancellationToken.ThrowIfCancellationRequested\(\);.*?_automaticStop.Token.ThrowIfCancellationRequested\(\);.*?SendControl\("next", afterInsert, _lifetime.Token\)' -and
    $execute -match 'CreateAutomaticWorkCancellation\(cancellationToken, renewStopped: true\)') `
    'stop during insert drain prevents phase-two Next; a new registration binds its own cancellation generation before waiting'

$handleEvent = Section $adapter 'private async Task<bool> HandleEventAsync(' 'private static bool EventIsFresh('
Assert-Source ($eventLoop -match 'timelineEvent = item' -and $eventLoop -match 'metadataEvent = item' -and
    $eventLoop -match 'var observation = await ObserveWithIdentityAsync\(workToken,' -and $eventLoop -match 'Snapshot\(observation\)' -and
    $eventLoop -match 'forceRead: pendingMetadata \|\| convergingMetadata \|\| metadataEvent is not null && metadataEvent.ObservedAt > _nativeStatusReadAt' -and
    $eventLoop -notmatch 'Snapshot\(Observe\(\)\)' -and $handleEvent -notmatch '\bObserve\(\)') `
    'an event batch preserves both notification kinds, forces typed identity for new or converging metadata, and publishes the same final observation used by its handler'
Assert-Source ($eventLoop -match 'automaticWork = _pending is not null \? CreateAutomaticWorkCancellation\(token\)' -and
    $eventLoop -match 'HandleEventAsync\(latest, timelineEvent, metadataEvent, observation, evidence, workToken\)' -and
    $eventLoop -match 'catch \(OperationCanceledException\) when \(!token.IsCancellationRequested && automaticWork\?\.IsCancellationRequested == true\)' -and
    $eventLoop -match 'automaticWork\?\.Dispose\(\)' -and
    $eventLoop -match 'ClearPending\(AutomaticStoppedStatus\)') `
    'automatic stop cancels only the current event preparation batch, not the long-lived observer or normal manual controls'
$automaticDispatch = Section $handleEvent 'if (transition != QQMusicWebAutomaticTransition.None)' 'if (freshPlaying)'
$rollover = Section $handleEvent 'var restartedAtEnd =' 'var decision ='
Assert-Source ($rollover -match 'freshEvent && IsFreshTimeline\(current, now\)' -and
    $rollover -match 'PlaybackStatus == "Playing"' -and
    $rollover -match 'LastUpdatedTime > previousTimeline\.LastUpdatedTime' -and
    $rollover -match 'QQMusicWebEndPolicy\.IsSameTrackRollover' -and
    $rollover -notmatch 'item\.Kind ==|hasTimelineEvent &&') `
    'rollover requires an actually newer playing timeline but must not depend on which fresh callback arrives first'
$consumeIndex = $automaticDispatch.IndexOf('ClearPending(', [StringComparison]::Ordinal)
$dispatchIndex = $automaticDispatch.IndexOf('SubmitOnceAsync(pending.Song', [StringComparison]::Ordinal)
Assert-Source ($consumeIndex -ge 0 -and $dispatchIndex -gt $consumeIndex) `
    'an automatic end or track-change transition must consume the pending target before dispatch'
Assert-Source ($handleEvent -match 'QQMusicWebSubmissionPolicy\.ClassifyAutomaticTransition\(decision,' -and
    $handleEvent -match '_pending\?\.Owner == pending\.Owner' -and
    $handleEvent -match 'eventFreshAtReceipt \|\| deferredMetadataConfirmed, sameTrack' -and
    $handleEvent -match 'sameTrack, !string.IsNullOrEmpty\(current.Current\?\.Id\)' -and
    $handleEvent -match 'current\.Timeline\?\.PlaybackStatus, IsFreshTimeline\(current, now\), hasMetadataEvent' -and
    $automaticDispatch -match 'QQMusicWebSubmissionPolicy\.ShouldSubmitConsumedTarget' -and
    $automaticDispatch -match '!sameTrack && current.Current\?\.Id == pending.Song.SongId.ToString\(\)' -and
    $automaticDispatch -notmatch 'freshManualIntent:\s*true|_pending\s*=') `
    'automatic transitions retain owner/epoch policy, consume an already matching target ID without another send, and never restore or free an automatic attempt'
Assert-Source ($handleEvent -match 'item\.ObservedAt\s*<\s*pending\.CreatedAt' -and
    $handleEvent -match 'current\.Epoch\s*!=\s*pending\.Epoch' -and
    $handleEvent -match 'pending.Owner != evidence.Owner' -and $handleEvent -match 'current.Epoch != evidence.Epoch' -and
    $handleEvent -match 'EventIsFresh\(item, pending.CreatedAt, evidence.ReceivedAt\)' -and
    $handleEvent -match 'if \(!eventFreshAtReceipt && !deferredMetadataConfirmed\)' -and
    $adapter -match 'now - item.ObservedAt <= TimeSpan.FromSeconds\(3\)') `
    'old-owner and wrong-epoch notifications are rejected; delayed metadata needs independent typed confirmation instead of a widened ordinary freshness window'
Assert-Source ($eventLoop -match '(?s)var evidence = new EventEvidence\(_pending\?\.Owner, beforeRead.Epoch,\s*TrackKey\(beforeRead.Current\), receivedAt, false\);.*?var readStartedAt = DateTimeOffset.UtcNow;.*?ObserveWithIdentityAsync\(workToken,.*?IdentityReadConfirmed = _nativeStatusReadAt >= readStartedAt' -and
    $eventLoop -match 'observation.NativeStatus is \{ Succeeded: true, SongId: > 0, SongType: >= 0 \}' -and
    $eventLoop -match '!string.IsNullOrEmpty\(observation.Current\?\.Id\)' -and
    $eventLoop -match 'HandleEventAsync\(latest, timelineEvent, metadataEvent, observation, evidence, workToken\)') `
    'metadata evidence binds owner/epoch/key before a new identity query and records success only from that query, not cached title data'
$delayedMetadata = Section $handleEvent 'var deferredMetadataConfirmed =' 'var eventFreshAtReceipt ='
Assert-Source ($delayedMetadata -match 'metadataWitness is not null && !sameTrack' -and
    $delayedMetadata -match 'QQMusicWebSubmissionPolicy.IsPendingConvergenceActive\(transitionStarted, now\)' -and
    $delayedMetadata -match 'QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition\(' -and
    $delayedMetadata -match 'pending.Owner == evidence.Owner, current.Epoch == evidence.Epoch' -and
    $delayedMetadata -match 'pending.CreatedAt, metadataWitness.Callback.ObservedAt, evidence.ReceivedAt' -and
    $delayedMetadata -match 'TrackKey\(current.Current\) == evidence.MetadataKey' -and
    $delayedMetadata -match 'evidence.IdentityReadConfirmed, current.MetadataAgrees' -and
    $delayedMetadata -notmatch 'freshPlaying|PlaybackStatus|IsFreshTimeline' -and
    $handleEvent -match 'now - previous.ObservedAt <= TimeSpan.FromSeconds\(3\)' -and
    $handleEvent -match 'if \(string.IsNullOrEmpty\(current.Current\?\.Id\)\) return false;') `
    'cross-ID confirmation requires a bounded owner-bound metadata witness and this successful typed read, independent of late playback status; natural-end freshness is unchanged'
Assert-Source ($eventLoop -match '(?s)if \(pendingMetadata && _pending is \{ \} witnessedPending.*?_pendingMetadataWitness = new\(witnessedPending.Owner, witnessedPending.Epoch,\s*evidence.MetadataKey, metadataEvent!\)' -and
    $eventLoop -match 'witness.Owner == convergingPending.Owner' -and
    $eventLoop -match 'witness.Epoch == beforeRead.Epoch && witness.MetadataKey == evidence.MetadataKey' -and
    $handleEvent -match 'heldWitness.Owner == pending.Owner' -and
    $handleEvent -match 'heldWitness.Epoch == current.Epoch && heldWitness.MetadataKey == evidence.MetadataKey' -and
    $clearPending -match '_pendingMetadataWitness = null;' -and
    $clearPending -match '_unprocessedTransitionAt = null;') `
    'held metadata/read evidence remains tied to one owner, process and metadata key and is cleared with its owner'
$pendingAlignment = Section $handleEvent 'if (!sameTrack && !deferredMetadataConfirmed)' 'if (sameTrack && TrackKey('
Assert-Source ($pendingAlignment -match 'return false;' -and
    $pendingAlignment -notmatch 'ClearPending\(|SubmitOnceAsync|PlayAsync|SendControl|_pending\s*=' -and
    $handleEvent.IndexOf('if (!sameTrack && !deferredMetadataConfirmed)', [StringComparison]::Ordinal) -lt
        $handleEvent.IndexOf('QQMusicWebGuardPolicy.OnObservation', [StringComparison]::Ordinal)) `
    'a different ID awaiting metadata alignment retains its owner before pause handling and cannot dispatch from an unconfirmed or cached observation'
Assert-Source ($handleEvent -match 'var decision = deferredMetadataConfirmed \? QQMusicWebGuardEventDecision.Continue :' -and
    $handleEvent -match 'var transition = deferredMetadataConfirmed \? QQMusicWebAutomaticTransition.TrackChangeFallback :' -and
    $handleEvent -match 'var sameTrack = IsSamePendingAnchor\(pending, current\);' -and
    $adapter -match 'QQMusicWebSubmissionPolicy.SameNumericCurrentId\(pending.AnchorSongId, observation.Current\?\.Id\)' -and
    $submissionPolicy -match 'SameNumericCurrentId\(string anchorId, string\? currentId\)' -and
    $submissionPolicy -match '!string.IsNullOrEmpty\(anchorId\) && !string.IsNullOrEmpty\(currentId\) && anchorId == currentId') `
    'only independently confirmed different numeric IDs bypass old pause decisions; a type-only change cannot become a cross-song fallback'
Assert-Source ($handleEvent -match 'QQMusicWebGuardPolicy\.OnObservation\(sameTrack, current\.Timeline\?\.PlaybackStatus,' -and
    $handleEvent -match '(?s)QQMusicWebGuardEventDecision\.KeepTargetClearEvidence\).*?LastPlaying = null.*?return false;' -and
    $handleEvent -match '(?s)QQMusicWebGuardEventDecision\.SeedPlayingOnly\).*?LastPlaying = current.*?return false;' -and
    $handleEvent.IndexOf('QQMusicWebGuardEventDecision.KeepTargetClearEvidence', [StringComparison]::Ordinal) -lt
        $handleEvent.IndexOf('QQMusicWebSubmissionPolicy.ClassifyAutomaticTransition', [StringComparison]::Ordinal)) `
    'same-ID pause clears end evidence and the first fresh same-ID sample only seeds it, returning before automatic dispatch'
$sameIdTypeChange = Section $handleEvent 'if (sameTrack && !QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity(' 'if (!sameTrack) _unprocessedTransitionAt'
Assert-Source ($sameIdTypeChange -match 'pending.AnchorSongId, current.Current\?\.Id, pending.AnchorKey, current.Key' -and
    $sameIdTypeChange -match '_pending = pending with \{ LastPlaying = null \};' -and
    $sameIdTypeChange -match '_unprocessedTransitionAt = null;' -and $sameIdTypeChange -match '_pendingMetadataWitness = null;' -and
    $sameIdTypeChange -match 'return false;' -and
    $sameIdTypeChange -notmatch 'ClearPending\(|SubmitOnceAsync|PlayAsync|SendControl|_pending\s*=\s*null' -and
    $submissionPolicy -match 'SameNumericCurrentId\(anchorId, currentId\) && !string.IsNullOrEmpty\(anchorKey\) && anchorKey == currentKey' -and
    $handleEvent.IndexOf('SameTypedCurrentIdentity(', [StringComparison]::Ordinal) -lt $handleEvent.IndexOf('var wasNearEnd =', [StringComparison]::Ordinal) -and
    $handleEvent.IndexOf('SameTypedCurrentIdentity(', [StringComparison]::Ordinal) -lt $handleEvent.IndexOf('var transition =', [StringComparison]::Ordinal)) `
    'same numeric ID with changed type clears old end evidence and returns before any natural-end or fallback decision, retaining its owner and sending nothing'

$matchesSong = Section $adapter 'private static bool MatchesSong(' 'private PlayerOperationResult SendControl('
$observe = Section $adapter 'private Observation Observe()' 'private void ReconcileSession('
$identityRead = Section $adapter 'private async Task<Observation> ObserveWithIdentityAsync(' 'private void RecordTargetObservation('
$snapshot = Section $adapter 'private PlayerSnapshot Snapshot(' 'private void PublishSnapshot('
$targetObservation = Section $adapter 'private void RecordTargetObservation(' 'private Observation Observe()'
$artworkEnrichment = Section $observe 'if (current is not null && string.IsNullOrEmpty(current.CoverUrl))' 'var metadataKey = TrackKey(current);'
Assert-Source ($matchesSong -match 'observation.NativeStatus\?\.SongId == song.SongId' -and
    $matchesSong -match 'observation.NativeStatus.SongType == song.SongType' -and
    $matchesSong -match 'current.Id == song.SongId.ToString\(\)' -and
    $matchesSong -match 'QQMusicTrackMatchPolicy.MetadataRepresentsSameSong' -and
    $adapter -match 'first.SongId == second.SongId && first.SongType == second.SongType && first.SongMid == second.SongMid') `
    'target matching requires real numeric ID and type plus agreeing metadata, while request deduplication also retains MID'
Assert-Source ($observe -match 'epoch == _nativeStatusEpoch && metadataKey == _nativeStatusMetadataKey' -and
    $observe -match 'native is \{ SongId: > 0, SongType: >= 0 \}' -and
    $observe -match '_known.TryGetValue\(\(native.SongId.Value, native.SongType.Value\),' -and
    $observe -match 'QQMusicWebSubmissionPolicy.ProjectCurrentIdentity\(TrackKey\(current\),' -and
    $observe -match 'observation.MetadataAgrees, native\?\.Succeeded == true,' -and
    $observe -match 'current with \{ Id = projected.Id \}' -and
    $observe -match 'Key = projected.Key' -and
    $artworkEnrichment -notmatch '\bId\s*=') `
    'only successful same-epoch native status populates the typed identity; catalog metadata may enrich artwork but never invent the ID'
Assert-Source ($identityRead -match '_statusReader.ReadAsync\(before.Epoch.ProcessId, before.Epoch.StartedAtUtcTicks, token\)' -and
    $identityRead -match '(?s)token.ThrowIfCancellationRequested\(\);.*?after.Epoch == before.Epoch && TrackKey\(after.Current\) == metadataKey.*?_nativeStatus = status;' -and
    $identityRead -notmatch 'SubmitOnceAsync\(|PlayAsync\(|SendControl\(' -and
    $snapshot -match 'string.IsNullOrEmpty\(observation.Current\?\.Id\) \? null : observation.Current, observation.ObservedAt') `
    'read-only identity binding rechecks cancellation/epoch/metadata and never publishes metadata-only Current without a real ID'
$identityProjection = Section $adapter 'private Observation ReprojectIdentity(' 'private void ReconcileSession('
Assert-Source ($nativeController -match 'DateTimeOffset ObservedAt,\s*int\? ProcessId = null' -and
    $nativeController -match 'DateTimeOffset.Now,\s*window.ProcessId\)' -and
    $observe -match 'Process.GetProcessById\(state.ProcessId.Value\)' -and
    [regex]::Matches($observe, 'QQMusicNativeController.ReadPlaybackState\(').Count -eq 1 -and
    $observe -notmatch 'InspectWindows\(') `
    'current playback carries its selected window PID compatibly and the Web observation never enumerates all windows twice'
Assert-Source ([regex]::Matches($identityRead, '\bObserve\(\)').Count -eq 2 -and
    $identityRead -match 'ReprojectIdentity\(after, identityBound \? status : null\)' -and
    $identityProjection -match 'return observation with' -and
    $identityProjection -match 'current with \{ Id = projected.Id \}' -and
    $identityProjection -notmatch '\bObserve\(|ReadPlaybackState|InspectWindows|_events\.|DateTimeOffset\.|ObservedAt\s*=|Timeline\s*=|_transport\.') `
    'typed status reuses the actual post-read observation without a third scan, stale ID reuse, new timestamp, timeline or media mutation'
$cancelledConfirmation = $submit.Substring($submit.IndexOf('// A read deadline after a write', [StringComparison]::Ordinal))
Assert-Source ($submit -match '(?s)var latestConfirmation = recheck;.*?try\s*\{.*?var afterInsert = await ObserveWithIdentityAsync.*?var afterNext = await ObserveWithIdentityAsync.*?catch \(OperationCanceledException\)' -and
    $cancelledConfirmation -match 'return Result\(OperationOutcome.Indeterminate, _status, latestConfirmation,' -and
    $cancelledConfirmation -notmatch '_lastSubmission\s*=|SubmitOnceAsync|PlayAsync|SendControl|throw') `
    'confirmation cancellation after insertion/Next returns indeterminate with its original reservation and timestamp rather than an escaping error or retry'
Assert-Source ($probe -match '_pending is \{ \} pending && !string.IsNullOrEmpty\(observation.Current\?\.Id\) && !IsSamePendingAnchor\(pending, observation\)' -and
    $snapshot -match '_pending is \{ \} pending && !string.IsNullOrEmpty\(observation.Current\?\.Id\) && !IsSamePendingAnchor\(pending, observation\)') `
    'temporary absence of a typed current ID does not become an anchor mismatch that cancels the pending target'
$probeTransition = Section $probe 'if (_pending is { } pending' 'return Snapshot(observation);'
$snapshotTransition = Section $snapshot 'if (_pending is { } pending' 'var snapshot = new PlayerSnapshot('
foreach ($readTransition in @($probeTransition, $snapshotTransition)) {
    Assert-Source ($readTransition -match '_unprocessedTransitionAt \?\?= DateTimeOffset.UtcNow;' -and
        $readTransition -match '(?s)IsPendingConvergenceActive\(_unprocessedTransitionAt.Value, DateTimeOffset.UtcNow\).*?return previous;.*?ClearPending\(' -and
        $readTransition -notmatch 'TimeSpan.FromSeconds\(3\)|ObservedAt\s*=|SubmitOnceAsync|PlayAsync|SendControl|_pending\s*=') `
        'read-side transition holding uses the shared fixed deadline and unchanged previous snapshot, never an early three-second cancellation or playback command'
}
Assert-Source ($submissionPolicy -match 'startedAt != default && now >= startedAt && now - startedAt <= TimeSpan.FromSeconds\(8\)' -and
    $eventLoop -match 'if \(evidence.MetadataKey != witnessedPending.AnchorMetadataKey\)\s*_unprocessedTransitionAt \?\?= receivedAt;' -and
    $handleEvent -match 'if \(!sameTrack\) _unprocessedTransitionAt \?\?= evidence.ReceivedAt;' -and
    $adapter -notmatch '(?m)^\s*_unprocessedTransitionAt\s*=(?!\s*null;)' -and
    $handleEvent -match '(?s)if \(sameTrack && TrackKey\(current.Current\) == pending.AnchorMetadataKey\).*?_unprocessedTransitionAt = null;.*?_pendingMetadataWitness = null;') `
    'alignment starts once when processed, never extends on later reads or notifications, and can reset after the original anchor is aligned again'
Assert-Source ($targetObservation -match '(?s)_lastInsertion is \{ TargetObserved: false \} insertion && observation.Epoch == insertion.Epoch.*?observation.ObservedAt >= insertion.InsertedAt && MatchesSong\(observation, insertion.Song, insertion.Artist\).*?IsFreshTimeline\(observation, DateTimeOffset.UtcNow\) && observation.Timeline!.PlaybackStatus == "Playing".*?_lastInsertion = insertion with \{ TargetObserved = true \};' -and
    [regex]::Matches($adapter, '_lastInsertion = .*TargetObserved = true').Count -eq 1) `
    'an insertion cycle is marked observed only by post-insert fresh Playing with its exact same-epoch target identity'

$reconcile = Section $adapter 'private void ReconcileSession(' 'private void ExpirePending()'
Assert-Source ($reconcile -match '(?s)_epoch\s*!=\s*observation\.Epoch.*?ClearPending\(.*?_lastSubmission = null;.*?_lastInsertion = null;.*?_epoch\s*=\s*observation\.Epoch') `
    'process-epoch replacement invalidates the old target and both epoch-scoped submission/insertion reservations'
$expiry = Section $adapter 'private void ExpirePending()' 'private void ClearPending('
Assert-Source ($expiry -match 'TimeSpan\.FromHours\(12\)' -and $expiry -match 'ClearPending\(' -and
    $expiry -match '!QQMusicWebSubmissionPolicy.IsPendingConvergenceActive\(started, DateTimeOffset.UtcNow\)' -and
    $expiry -notmatch 'SubmitOnceAsync|PlayAsync|SendControl') `
    'fixed convergence-window or twelve-hour expiry may clear state but must not send a playback command'
Assert-Source ($adapter -match 'PlaybackAnchorReady:\s*false' -and
    $adapter -notmatch 'QQMusicWebSubmissionState\.SubmittedUnverified\s*=>\s*OperationOutcome\.(Applied|Confirmed)' -and
    $submit -match 'observedTarget \? OperationOutcome.Applied : OperationOutcome.Indeterminate' -and
    $submit -match '(?s)var observedTarget = afterNext.Epoch == recheck.Epoch && MatchesSong\(afterNext, song, artist\).*?afterNext.Timeline!.PlaybackStatus == "Playing"') `
    'unverified submission must not manufacture a native playback anchor or confirmed playback'

Assert-Source ([regex]::Matches($bridgeSource, '\bclient\.SendOnce\(').Count -eq 1 -and
    $bridgeSource -match '(?s)Write\(new\(request\.OperationId, stage, "single-command-dispatch-reserved"\)\);.*?client\.SendOnce\(command\)') `
    'one host invocation has one native send and emits its reservation first'
Assert-Source ($client -match '(?s)budget.ReserveWrite\(\);.*?client!\.WebPerform\(' -and
    $statusProtocol -match 'Interlocked.CompareExchange\(ref reserved, before \| bit, before\)' -and
    $statusProtocol -match '\(before & bit\) != 0 \|\| \(bit == 4 \? before != 0 : \(before & 4\) != 0\)' -and
    [regex]::Matches($client, 'client!\.WebPerform\(').Count -eq 1 -and
    $client -notmatch 'client!?\.WebPerform2\(' -and $client -notmatch 'NativeLibrary\.Free\(') `
    'the COM wrapper atomically reserves a write exclusive of either fixed read, never double-sends or explicitly unloads its DLL'
Assert-Source ($bridgeSource -match 'ApartmentState\.STA' -and $bridgeSource -match 'PeekMessage' -and
    $bridgeSource -match 'MsgWaitForMultipleObjectsEx' -and $bridgeSource -match 'ExitSelf\(124\)' -and
    $bridgeSource -match 'TerminateProcess\(GetCurrentProcess\(\), unchecked\(\(uint\)exitCode\)\)' -and
    $bridgeSource -notmatch 'System\.Windows\.Forms|Application\.Run\(') `
    'the helper must keep its independent native STA pump and process-level deadline without WinForms'
$apiOpen = Section $client 'public static QQMusicWebApiClient Open(' 'public void SendOnce('
$captureDrain = Section $client 'internal QQMusicWebDrainIdentity CaptureDrainIdentity(' 'internal string? QueryCurrentOnce('
Assert-Source ($apiOpen -match '(?s)factory.CreateInstance\(0, ref clientId, out var clientPointer\).*?Marshal.GetObjectForIUnknown\(clientPointer\).*?finally \{ Marshal.Release\(clientPointer\); \}.*?return new\(module, factory, client, clientPointer\);' -and
    $client -match 'private readonly nint borrowedClientInterface;' -and
    $client -match 'ownerNativeThreadId = GetCurrentThreadId\(\);' -and
    $captureDrain -match 'ObjectDisposedException.ThrowIf\(client is null, this\)' -and
    $captureDrain -match 'Environment.CurrentManagedThreadId != ownerThreadId' -and
    $captureDrain -match 'GetCurrentThreadId\(\) != ownerNativeThreadId' -and
    $captureDrain -match '(?s)return new\(validatedApiSha256, module, borrowedClientInterface,\s*checked\(\(uint\)Environment.ProcessId\), ownerNativeThreadId\)' -and
    $captureDrain -notmatch 'Marshal\.|Process.GetProcessById|request.ProcessId' -and
    $bridgeSource -match '(?s)using var lockedApi = LockAndValidateApi\(request\).*?using \(var client = QQMusicWebApiClient.Open\(request.ApiDllPath\)\).*?client.CaptureDrainIdentity\(request.ApiSha256\).*?client.SendOnce\(command\)') `
    'drain identity uses the live RCW original opaque interface value, locked validated DLL, this helper PID and its owning native STA, not a QQ PID or message-provided pointer'
$completionCandidate = Section $drainPolicy 'internal static bool IsCompletionCandidate(' 'internal static bool CanFinishDrain('
Assert-Source ($drainPolicy -match '"0F438C07F1BBD814FDF18350B166C946C858CCADD16A1D77F677C653D4D097DC"' -and
    $drainPolicy -match 'SenderCompletionMessage = 0x465;' -and $drainPolicy -match 'WebPerformWorkerType = 2;' -and
    $completionCandidate -match 'string.Equals\(identity.ApiSha256, AuditedApiSha256, StringComparison.OrdinalIgnoreCase\)' -and
    $completionCandidate -match 'identity.Module != 0 && identity.ClientInterface != 0' -and
    $completionCandidate -match 'identity.ProcessId != 0 && identity.ThreadId != 0' -and
    $completionCandidate -match 'window != 0 && message == SenderCompletionMessage' -and
    $completionCandidate -match 'wParam == WebPerformWorkerType && lParam == identity.ClientInterface') `
    'early-drain candidates require the one audited API hash and exact nonzero module/interface/owning process/thread with WM465 type2 and equal opaque lParam'
$finishDrain = Section $drainPolicy 'internal static bool CanFinishDrain(' 'private static bool IsOwnedHiddenWindow('
$readDrainWindow = Section $bridgeSource 'private static QQMusicWebDrainWindow? ReadDrainWindow(' 'private static void CheckDeadline('
Assert-Source ($finishDrain -match 'IsCompletionCandidate\(identity, window, message, wParam, lParam\)' -and
    $finishDrain -match 'dispatchResult == 1' -and
    $finishDrain -match 'IsOwnedHiddenWindow\(identity, window, before\)' -and $finishDrain -match 'IsOwnedHiddenWindow\(identity, window, after\)' -and
    $finishDrain -match 'string.Equals\(before!.ClassName, after!.ClassName, StringComparison.Ordinal\)' -and
    $drainPolicy -match 'observed.Handle == window' -and
    $drainPolicy -match 'observed.ProcessId == identity.ProcessId && observed.ThreadId == identity.ThreadId' -and
    $drainPolicy -match 'observed.ClassModule == identity.Module && !observed.IsVisible' -and
    $drainPolicy -match 'observed.ClassName.StartsWith\("ATL:", StringComparison.Ordinal\)' -and
    $readDrainWindow -match 'GetWindowThreadProcessId\(window, out var processId\)' -and
    $readDrainWindow -match 'GetClassLong\(window, -16\)' -and $readDrainWindow -match 'IsWindowVisible\(window\)' -and
    $readDrainWindow -notmatch '\bEnumWindows\(|GetWindowLong|GetWindowLongPtr|Marshal.Read|Marshal.Write|PtrToStructure') `
    'completion additionally requires Dispatch result one and the same hidden ATL window in the owning helper STA and loaded class module before and after dispatch'
$drainPump = Section $bridgeSource 'private static bool PumpForDrain(' 'private static void TraceDrain('
Assert-Source ($bridgeSource -match 'DrainMilliseconds = 6_000;' -and
    $drainPump -match 'while \(drainClock.ElapsedMilliseconds < DrainMilliseconds\)' -and
    $drainPump -match '(?s)var candidate = QQMusicWebDrainPolicy.IsCompletionCandidate\(.*?var before = candidate \? ReadDrainWindow\(message.Window\) : null;.*?TranslateMessage\(ref message\);.*?var dispatchResult = DispatchMessage\(ref message\);.*?CheckDeadline\(overallClock\);.*?if \(candidate && QQMusicWebDrainPolicy.CanFinishDrain\(.*?before, ReadDrainWindow\(message.Window\), dispatchResult\)\)\s*return true;' -and
    [regex]::Matches($drainPump, 'return true;').Count -eq 1 -and
    $drainPump -match 'DrainMilliseconds - \(int\)drainClock.ElapsedMilliseconds' -and
    $drainPump -match 'return false;' -and
    $drainPump -notmatch 'Marshal.Release|FinalReleaseComObject|GetObjectForIUnknown|Marshal.Read|Marshal.Write|PtrToStructure|SendOnce|SendMessage|PostMessage') `
    'only the fully matched dispatched tuple exits drain early; missing or mismatched evidence keeps the six-second pump without pointer dereference, manual Release or another command'
$normalBridge = Section $bridgeSource 'using (var client = QQMusicWebApiClient.Open(' 'catch (Exception error)'
$clientDispose = Section $client 'void IDisposable.Dispose()' '[UnmanagedFunctionPointer('
Assert-Source ($normalBridge -match '(?s)client.SendOnce\(command\);.*?"submission-returned".*?"transport-returned-unconfirmed".*?var matchedCompletion = PumpForDrain\(clock, drainIdentity\);.*?VerifyTargetProcess\(request\);.*?stage = "client-dispose";\s*\}\s*CheckDeadline\(clock\);\s*stage = "drain-completed";\s*Write\(new\(request.OperationId, stage, "observation-required"\)\);\s*exitCode = 0;' -and
    $clientDispose -match 'Marshal.FinalReleaseComObject\(client\)' -and $clientDispose -match 'Marshal.FinalReleaseComObject\(factory\)' -and
    $clientDispose -notmatch 'NativeLibrary.Free\(' -and
    $bridgeSource -match '(?s)finally \{ completed.Set\(\); \}.*?completed.Wait\(remaining\).*?ExitSelf\(exitCode\)' -and
    $bridgeSource -match '(?s)Console.Out.WriteLine\(JsonSerializer.Serialize\(item, JsonOptions\)\);\s*Console.Out.Flush\(\);' -and
    $normalBridge -notmatch 'ExitSelf\(|TerminateProcess\(') `
    'either drain path keeps normal STA Dispose and target recheck before the unchanged flushed final receipt; only completed cleanup permits normal self exit'
$transportPlay = Section $transport 'internal async Task<QQMusicWebSubmission> PlayAsync(' 'private static ProcessStartInfo CreateStartInfo()'
Assert-Source ($bridgeSource -match 'DeadlineMilliseconds = 15_000;' -and
    $bridgeSource -match 'ExitSelf\(124\)' -and
    $bridgeSource -match 'TerminateProcess\(GetCurrentProcess\(\), unchecked\(\(uint\)exitCode\)\)' -and
    $bridgeSource -notmatch 'Process.Kill|OpenProcess\(' -and
    $transportPlay -match 'deadline.CancelAfter\(TimeSpan.FromSeconds\(17\)\)' -and
    $transportPlay -match 'new CancellationTokenSource\(TimeSpan.FromSeconds\(2\)\)' -and
    [regex]::Matches($transportPlay, 'child.Start\(\)').Count -eq 1 -and
    [regex]::Matches($transportPlay, 'child.StandardInput.WriteLineAsync\(').Count -eq 1 -and
    $transportPlay -notmatch '\bwhile\s*\(|\bfor\s*\(|PumpForDrain|matchedCompletion') `
    'fast drain does not change helper/parent/cleanup deadlines, single-child single-input delivery, no retry, or self-only helper termination'
$traceDrain = Section $bridgeSource 'private static void TraceDrain(' 'private static QQMusicWebDrainWindow? ReadDrainWindow('
Assert-Source ($traceDrain -match 'Environment.GetEnvironmentVariable\("AWOO_QQMUSIC_WEB_TRACE"\)' -and
    $traceDrain -match 'Console.Error.WriteLine\(matchedCompletion' -and
    $traceDrain -match '"qq-web-drain:matched-completion" : "qq-web-drain:fallback-timeout"' -and
    $traceDrain -notmatch 'Console.Out|(?<!\w)Write\(|TryWrite\(|QQMusicWebBridgeEvent|request\.|identity\.' -and
    [regex]::Matches($bridgeSource, '(?<!\w)Write\(new\(request.OperationId, stage,').Count -eq 3 -and
    [regex]::Matches($receiptSource, 'case \("').Count -eq 3 -and
    $receiptSource -match 'case \("dispatch-starting", "single-command-dispatch-reserved", 0, null\)' -and
    $receiptSource -match 'case \("submission-returned", "transport-returned-unconfirmed", 1, null\)' -and
    $receiptSource -match 'case \("drain-completed", "observation-required", 2, null\)' -and
    $receiptSource -match '(?s)!invalid && !interrupted && exitCode == 0 && sequence == 3.*?QQMusicWebSubmissionState.SubmittedUnverified' -and
    $receiptSource -notmatch 'matched-completion|fallback-timeout|CanFinishDrain|QQMusicWebSubmissionState\.(Applied|Confirmed)') `
    'matched completion is optional bounded stderr only; stdout retains the same three unconfirmed receipt stages and never promotes sender lifecycle to queue or playback acknowledgment'
Assert-Source ($transport -match 'RedirectStandardError\s*=\s*true' -and
    $transport -match 'DrainErrorsAsync\(' -and $transport -match 'child\.Kill\(entireProcessTree:\s*false\)' -and
    $transport -notmatch 'QQMusicNativeNextTransport|CreateRemoteThread|WriteProcessMemory') `
    'the supervisor must suppress native stderr and terminate only its exact helper, without old-native fallback'

$artworkOnly = Section $adapter 'private void PublishArtworkOnlySnapshot()' 'private PlayerOperationResult Result('
Assert-Source ($artworkOnly -match 'var snapshot = _lastSnapshot;' -and
    $artworkOnly -match 'snapshot.ProcessId != _epoch.ProcessId' -and
    $artworkOnly -match 'QQMusicEventMonitor.SameMediaTrack\(snapshot.Current, media\)' -and
    $artworkOnly -match 'PublishSnapshot\(snapshot with \{ Current = snapshot.Current with \{ CoverUrl = media.CoverUrl \} \}\)' -and
    $artworkOnly -notmatch '\bObserve\(|\bSnapshot\(|HandleEventAsync\(|ClearPending\(|SubmitOnceAsync\(|ObservedAt\s*=|_pending\s*=') `
    'artwork-only publication changes only current cover on the latest same-track/process snapshot without playback evidence or queue mutation'
Assert-Source ($eventLoop -match '(?s)if \(item.Kind == QQMusicEventKind.ArtworkChanged\).*?artworkChanged = true;\s*continue;' -and
    $eventLoop -match '(?s)if \(latest is null\).*?PublishArtworkOnlySnapshot\(\);\s*continue;' -and
    $adapter -match 'current is not null && string.IsNullOrEmpty\(current.CoverUrl\)') `
    'decorative events cannot mask real callbacks or overwrite same-session artwork with catalog guesses'
$legacyAdapter = Read-Source 'QQMusicPlayerAdapter.cs'
Assert-Source ([regex]::Matches($legacyAdapter, 'Kind != QQMusicEventKind.ArtworkChanged').Count -eq 3 -and
    $legacyAdapter -match 'if \(playerEvent.Kind == QQMusicEventKind.ArtworkChanged\)\s*continue;') `
    'legacy event batches ignore decorative updates without losing genuine transition evidence'

Write-Output "QQMusicWebBackendPolicy.Tests passed: $script:checks source-contract checks (not functional or live playback tests)."
