using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using QQMusicControlPoc;

namespace UnifiedPlayerControlPoc;

/// <summary>
/// Exported Web command adapter. Inserts one native next target and owns its
/// ID-based transition fallback. Never uses the RVA transport. Command receipt,
/// current-song identity and full queue preservation are separate evidence.
/// </summary>
internal sealed class QQMusicWebPlayerAdapter : IPlayerAdapter, IPlayerSnapshotEventSource
{
    private const string GuardReadyStatus = "已登记待播队首；切歌后按 QQ 歌曲 ID 核对，不一致时执行一次保队列补播。";
    private const string GuardWaitingStatus = "已登记待播队首；同一 ID 暂停不启动播放，切到其他 ID 后仍按队首核对兜底。";
    private const string AutomaticStoppedStatus = "已停止新的自动插入和待播队首兜底；本次停止未发送 QQ 命令。此前已插入或已发送的原生命令不能撤回。";
    private readonly QQMusicCatalogClient _catalog = new();
    private readonly QQMusicWebSongDetails _songDetails = new();
    private readonly QQMusicWebStatusTransport _statusReader = new();
    private readonly QQMusicEventMonitor _events = new();
    private readonly QQMusicWebPlayTransport _transport = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _startSync = new();
    private readonly object _subscriberSync = new();
    private readonly object _automaticSync = new();
    private CancellationTokenSource _automaticStop = new();
    private readonly HashSet<Channel<PlayerSnapshot>> _subscribers = [];
    private readonly Dictionary<(long, int), PlayerTrack> _known = [];
    private Task? _observer;
    private PendingNext? _pending;
    private PendingMetadataWitness? _pendingMetadataWitness;
    private ProcessEpoch? _epoch;
    private SubmissionLatch? _lastSubmission;
    private InsertionLatch? _lastInsertion;
    private PlayerSnapshot? _lastSnapshot;
    private DateTimeOffset? _unprocessedTransitionAt;
    private string _status = "Web 选曲实验后端；播放结果以实际媒体观测为准";
    private QQMusicWebStatus? _nativeStatus;
    private ProcessEpoch? _nativeStatusEpoch;
    private string _nativeStatusMetadataKey = string.Empty;
    private string _identityAttemptKey = string.Empty;
    private int _identityAttempts;
    private DateTimeOffset _identityAttemptAt;
    private DateTimeOffset _nativeStatusReadAt;
    private int _disposed;

    public string Key => "qqmusic";
    public string DisplayName => "QQ 音乐";
    public string TestedVersion => "22.61 (Web API; cross-version unverified)";
    public PlayerCapabilities Capabilities { get; } = new(
        Search: true, PlaySelected: true, Previous: true, Pause: true,
        Resume: true, Toggle: true, Next: true, InsertNext: true,
        InsertNextLevel: "QQ 原生下一首插入；按实际歌曲 ID 进行单次待播队首兜底");

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(
        string query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        var songs = await _catalog.SearchAsync(query, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var tracks = songs.Select(song => new PlayerTrack(
            song.SongId.ToString(), song.Title, song.Artist, song.Album,
            JsonSerializer.Serialize(new Payload(song.SongId, song.SongType, song.SongMid, song.IsPlayable, song.WebMetadata)),
            QQMusicAlbumArtwork.BuildCoverUrl(song.AlbumMid))).ToArray();
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            for (var i = 0; i < songs.Count; i++)
                _known[(songs[i].SongId, songs[i].SongType)] = tracks[i];
            while (_known.Count > 256) _known.Remove(_known.Keys.First());
        }
        finally { _mutation.Release(); }
        return tracks;
    }

    public async Task<PlayerSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        Trace("probe-observer-starting");
        EnsureObserver();
        Trace("probe-observer-returned");
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            var cached = Volatile.Read(ref _lastSnapshot);
            if (cached is not null)
            {
                // Automatic selection owns the gate while its one-shot helper
                // drains. A read must not time out behind it, restart the owner,
                // or present a freshly dated version of old playback metadata.
                return cached with
                {
                    ObservationDeferred = true,
                    Status = "QQ Web 操作处理中；以下为上一份带原时间戳的观测，尚未刷新。 " + cached.Status
                };
            }
            await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        Trace("probe-gate-acquired");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var observation = await ObserveWithIdentityAsync(cancellationToken).ConfigureAwait(false);
            Trace("probe-observation-returned");
            ReconcileSession(observation);
            if (_pending is { } pending && !string.IsNullOrEmpty(observation.Current?.Id) && !IsSamePendingAnchor(pending, observation) &&
                _lastSnapshot is { } previous && previous.ProcessId == observation.Epoch?.ProcessId)
            {
                _unprocessedTransitionAt ??= DateTimeOffset.UtcNow;
                // A read never starts playback. Give the event owner a short chance to
                // classify the transition before the host sees an intermediate song.
                if (QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(_unprocessedTransitionAt.Value, DateTimeOffset.UtcNow))
                    return previous;
                ClearPending("歌曲过渡缺少及时事件证据；取消自动接管并报告实际元数据。");
            }
            return Snapshot(observation);
        }
        finally { _mutation.Release(); }
    }

    public async IAsyncEnumerable<PlayerSnapshot> WatchSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<PlayerSnapshot>(new BoundedChannelOptions(4)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });
        lock (_subscriberSync) _subscribers.Add(channel);
        try
        {
            var snapshot = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            var fingerprint = Fingerprint(snapshot);
            yield return snapshot;
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var update)) snapshot = update;
                var next = Fingerprint(snapshot);
                if (next == fingerprint) continue;
                fingerprint = next;
                yield return snapshot;
            }
        }
        finally { lock (_subscriberSync) _subscribers.Remove(channel); }
    }

    public async Task<PlayerOperationResult> ExecuteAsync(
        PlayerCommand command, PlayerTrack? track, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        if (command == PlayerCommand.ArmNextGuard && track is null)
        {
            // Cancel-only is an additive local command: no process enumeration,
            // media-session startup, QQ control, Web transport or network call.
            // Signal before waiting for a guard's detail/status work to release
            // the gate. A dispatch that has already started must finish draining.
            CancelAutomaticWork();
            var cancelGateStarted = QQMusicWebTiming.Start();
            await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ExecuteCancelGateWait, cancelGateStarted);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _status = AutomaticStoppedStatus;
                ClearPending(_status);
                var cancelled = _lastSnapshot is { } previous
                    ? previous with { Next = null, NextSource = string.Empty, NextObservation = "unknown", Status = _status }
                    : new PlayerSnapshot(false, DisplayName, null, string.Empty,
                        "仅取消本地逻辑下一首，未读取 QQ。", null, DateTimeOffset.UtcNow,
                        RequiresPlaybackAnchor: false, OwnsLogicalNext: true);
                PublishSnapshot(cancelled);
                return new(OperationOutcome.Applied, _status, cancelled);
            }
            finally { _mutation.Release(); }
        }
        // Capture a registration's cancellation generation before it queues for
        // the gate. A later explicit Arm may renew a stopped generation, but it
        // cannot revive an older request that already received cancellation.
        using var registrationCancellation = command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard
            ? CreateAutomaticWorkCancellation(cancellationToken, renewStopped: true) : null;
        if (registrationCancellation is not null) cancellationToken = registrationCancellation.Token;
        var timingStarted = QQMusicWebTiming.Start();
        EnsureObserver();
        await _events.EnsureStartedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ExecuteObserverReady, timingStarted);
        timingStarted = QQMusicWebTiming.Start();
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ExecuteGateWait, timingStarted);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Startup only schedules its initial media read. Refresh once under the
            // mutation gate; completion (or false) is not playback evidence.
            timingStarted = QQMusicWebTiming.Start();
            await _events.RefreshMediaAsync(cancellationToken).ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ExecuteRefreshMedia, timingStarted);
            cancellationToken.ThrowIfCancellationRequested();
            timingStarted = QQMusicWebTiming.Start();
            var observation = await ObserveWithIdentityAsync(cancellationToken).ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ExecuteInitialIdentity, timingStarted);
            ReconcileSession(observation);
            if (observation.Epoch is null)
                return Result(OperationOutcome.Rejected, "QQ 进程身份不可确认，未发送命令。", observation,
                    "qq-web-process-unavailable");

            if (command is PlayerCommand.PlaySelected or PlayerCommand.InterruptSelected)
            {
                if (!TrySong(track, out var song))
                    return Result(OperationOutcome.Rejected, "选曲缺少有效 QQ 标识、真实歌手/媒体格式信息，或曲库标为不可播放；未发送。", observation,
                        "qq-web-song-invalid");
                cancellationToken.ThrowIfCancellationRequested();
                var freshManualIntent = QQMusicWebSubmissionPolicy.AllowsFreshManualAttempt(
                    command.ToString(), targetValidated: true, cancellationToken.IsCancellationRequested);
                ClearPending("新的显式选曲已取消旧逻辑下一首。");
                return await SubmitOnceAsync(song!, track!.Artist, observation, cancellationToken,
                    freshManualIntent: freshManualIntent).ConfigureAwait(false);
            }
            if (command is PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard)
                return await ArmAsync(track, observation, cancellationToken).ConfigureAwait(false);
            if (command == PlayerCommand.Next && _pending is { } pending)
            {
                // The native queue already contains the insertion. Let Next run
                // normally; only the independently observed mismatching ID can
                // consume the guard and force the requested head.
                return SendControl("next", observation, cancellationToken);
            }

            var control = command switch
            {
                PlayerCommand.Previous => "prev",
                PlayerCommand.Next => "next",
                PlayerCommand.Pause => "pause",
                PlayerCommand.Resume => "play",
                PlayerCommand.Toggle when IsFreshTimeline(observation, DateTimeOffset.UtcNow) &&
                    observation.Timeline!.PlaybackStatus == "Playing" => "pause",
                PlayerCommand.Toggle when IsFreshTimeline(observation, DateTimeOffset.UtcNow) &&
                    observation.Timeline!.PlaybackStatus == "Paused" => "play",
                _ => null
            };
            if (control is null)
                return Result(OperationOutcome.Unsupported, "该命令或当前播放状态不支持可靠的明确控制。", observation,
                    "qq-web-control-state-unknown");
            if (command is PlayerCommand.Previous or PlayerCommand.Next)
                ClearPending("手动跳转已取消旧逻辑下一首。");
            else if (_pending is { } held)
                _pending = held with { LastPlaying = null }; // A user pause/resume is not a natural end.
            return SendControl(control, observation, cancellationToken);
        }
        finally
        {
            _mutation.Release();
            _events.NotifySnapshotInvalidated();
        }
    }

    private async Task<PlayerOperationResult> ArmAsync(PlayerTrack? track, Observation observation,
        CancellationToken cancellationToken)
    {
        if (_pending is { } existing && existing.Epoch == observation.Epoch &&
            TrySong(track, out var repeated) && SameSong(existing.Song, repeated!))
            return Result(OperationOutcome.Accepted, "同一待播目标已登记；未重复向 QQ 插入。", observation);
        // A new logical-head request supersedes the previous target even when
        // validation fails. Never leave an old target available to manual Next.
        ClearPending("新的下一首登记请求已取消旧逻辑目标；尚未登记新目标。");
        if (!TrySong(track, out var song))
            return Result(OperationOutcome.Rejected, "下一首缺少有效 QQ 标识、真实歌手/媒体格式信息，或曲库标为不可播放；未登记。", observation,
                "qq-web-song-invalid");
        var completedRearm = _lastSubmission is { } completed &&
            QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(
                SameSong(completed.Song, song!) && completed.Epoch == observation.Epoch,
                completed.TargetObserved, !string.IsNullOrEmpty(observation.Current?.Id), observation.MetadataAgrees,
                MatchesSong(observation, song!, track!.Artist),
                IsFreshTimeline(observation, DateTimeOffset.UtcNow) && observation.Timeline!.PlaybackStatus == "Playing", false);
        if (_lastSubmission is { } previous && SameSong(previous.Song, song!) && previous.Epoch == observation.Epoch && !completedRearm)
            // A previous play result does not register another occurrence as Next.
            // Preserve the automatic latch without claiming repeated-occurrence support.
            return Result(OperationOutcome.Unsupported,
                "同一 QQ 会话已提交过该目标，暂不支持再次登记同曲为下一首；本次未登记下一首。", observation,
                "qq-web-repeat-target-unsupported");
        var now = DateTimeOffset.UtcNow;
        var timeline = observation.Timeline;
        var registration = timeline is null ? QQMusicWebGuardRegistration.Rejected
            : QQMusicWebGuardPolicy.Registration(
                observation.Epoch is not null && !string.IsNullOrEmpty(observation.Current?.Id) && observation.NativeStatus?.SongPosition is >= 0,
                observation.MetadataAgrees, timeline.PlaybackStatus,
                timeline.StartTime, timeline.EndTime, timeline.ReportedPosition, timeline.LastUpdatedTime, now);
        if (registration == QQMusicWebGuardRegistration.Rejected)
            return Result(OperationOutcome.Unsupported,
                "当前歌曲身份或播放时间线不明确，未登记逻辑下一首；仍可直接选曲。", observation,
                "qq-web-natural-end-unobservable");
        var timingStarted = QQMusicWebTiming.Start();
        var resolved = await _songDetails.ResolveAsync(song!, cancellationToken).ConfigureAwait(false);
        QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ArmDetails, timingStarted);
        cancellationToken.ThrowIfCancellationRequested();
        timingStarted = QQMusicWebTiming.Start();
        var recheck = await ObserveWithIdentityAsync(cancellationToken, forceRead: true).ConfigureAwait(false);
        QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ArmIdentity, timingStarted);
        if (resolved is null || observation.Epoch != recheck.Epoch || observation.Key != recheck.Key ||
            recheck.NativeStatus?.SongPosition is not >= 0)
            return Result(OperationOutcome.Rejected, "歌曲详情或当前队列锚点已变化；没有向 QQ 插入。", recheck,
                "qq-web-insert-anchor-unavailable");
        // Detail/status reads can cross a track change. Re-evaluate completion
        // against the final typed observation, and keep its latch if no send occurs.
        completedRearm = _lastSubmission is { } finalCompleted &&
            QQMusicWebSubmissionPolicy.CanRearmCompletedTarget(
                SameSong(finalCompleted.Song, song!) && finalCompleted.Epoch == recheck.Epoch,
                finalCompleted.TargetObserved, !string.IsNullOrEmpty(recheck.Current?.Id), recheck.MetadataAgrees,
                MatchesSong(recheck, song!, track!.Artist),
                IsFreshTimeline(recheck, DateTimeOffset.UtcNow) && recheck.Timeline!.PlaybackStatus == "Playing", false);
        if (_lastSubmission is { } finalPrevious && SameSong(finalPrevious.Song, song!) &&
            finalPrevious.Epoch == recheck.Epoch && !completedRearm)
            return Result(OperationOutcome.Rejected, "此前目标的完成证据已变化；未重新插入下一首。", recheck,
                "qq-web-repeat-target-unconfirmed");
        _known[(resolved.SongId, resolved.SongType)] = track!;
        _pending = new(Guid.NewGuid(), track!, resolved, observation.Epoch!, observation.Key,
            now, registration == QQMusicWebGuardRegistration.FreshPlayingReady ? observation : null,
            observation.Current!.Id, TrackKey(observation.Current));
        _pendingMetadataWitness = null;
        _unprocessedTransitionAt = null;
        _status = registration == QQMusicWebGuardRegistration.FreshPlayingReady
            ? GuardReadyStatus : GuardWaitingStatus;
        var priorInsertion = _lastInsertion;
        if (priorInsertion is not null && priorInsertion.Epoch == recheck.Epoch && SameSong(priorInsertion.Song, resolved) &&
            !(priorInsertion.TargetObserved && !MatchesSong(recheck, resolved, track!.Artist) &&
              IsFreshTimeline(recheck, DateTimeOffset.UtcNow) && recheck.Timeline!.PlaybackStatus == "Playing"))
        {
            // Cancelling/losing the guard is not evidence that an earlier insert
            // failed. Restore only its guard, never duplicate an unknown insertion.
            _status = "已恢复同一待播队首的 ID 兜底；此前插入可能已生效，未重复插入。";
            return Result(OperationOutcome.Accepted, _status, recheck);
        }
        _lastInsertion = new(resolved, track!.Artist, observation.Epoch!, DateTimeOffset.UtcNow);
        QQMusicWebSubmission insertion;
        Task<QQMusicWebSubmission>? insertionTask = null;
        try
        {
            timingStarted = QQMusicWebTiming.Start();
            insertionTask = StartGuardTransport(resolved, observation.Epoch!, cancellationToken, QQMusicWebIntent.InsertNext);
            insertion = await insertionTask.ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.ArmTransport, timingStarted);
        }
        catch (OperationCanceledException) when (insertionTask is null && cancellationToken.IsCancellationRequested)
        {
            insertion = new(QQMusicWebSubmissionState.RejectedBeforeDispatch, "cancelled-before-dispatch");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            insertion = new(QQMusicWebSubmissionState.OutcomeUnknown, "web-insert-outcome-unknown");
        }
        if (insertion.State == QQMusicWebSubmissionState.RejectedBeforeDispatch)
        {
            _lastInsertion = priorInsertion;
            ClearPending("向 QQ 插入下一首在发送前被拒绝；待播歌曲仍由点歌机保留。");
            return Result(OperationOutcome.Rejected, _status, recheck, insertion.Code);
        }
        if (completedRearm) _lastSubmission = null; // Consume only proven prior completion after a possible insertion.
        _status = "已向 QQ 发送一次原生下一首插入，并登记待播队首兜底；插入结果待实际歌曲 ID 确认，不会重复插入。";
        return Result(OperationOutcome.Accepted, _status, recheck);
    }

    private async Task<PlayerOperationResult> SubmitOnceAsync(
        QQMusicWebSong song, string artist, Observation before, CancellationToken cancellationToken,
        bool freshManualIntent = false)
    {
        // Both callers pass their just-read observation under this same gate;
        // no asynchronous work separates that read from entering this method.
        // The mandatory typed read after detail lookup remains the send fence.
        var recheck = before;
        ReconcileSession(recheck);
        if (before.Epoch is null || recheck.Epoch != before.Epoch)
            return Result(OperationOutcome.Rejected, "QQ 进程已变化，未发送选曲。", recheck,
                "qq-web-process-changed");
        cancellationToken.ThrowIfCancellationRequested();
        var previousLatch = _lastSubmission;
        if (_lastSubmission is { } previous && QQMusicWebSubmissionPolicy.BlocksSameTarget(
            previous.Epoch == before.Epoch && SameSong(previous.Song, song), freshManualIntent))
            return LatchedResult(previous, recheck);
        // Search rows (and queued payloads created by older builds) can omit
        // audio-format/action fields. QQ may then fall back to MV. Resolve the
        // exact ID/MID/type through the official detail schema before reserving
        // a send. A failed bounded read is not permission to use the sparse row.
        var timingStarted = QQMusicWebTiming.Start();
        var resolvedSong = await _songDetails.ResolveAsync(song, cancellationToken).ConfigureAwait(false);
        QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitDetails, timingStarted);
        cancellationToken.ThrowIfCancellationRequested();
        if (resolvedSong is null)
        {
            // No typed read is needed to authorize a failed preparation, but
            // return fresh observed metadata rather than a pre-lookup snapshot.
            recheck = Observe();
            ReconcileSession(recheck);
            return Result(OperationOutcome.Rejected,
                "未取得身份一致的完整歌曲详情；为避免 QQ 转播 MV，本次未发送选曲。", recheck,
                "qq-web-song-details-unavailable");
        }
        song = resolvedSong;
        timingStarted = QQMusicWebTiming.Start();
        recheck = await ObserveWithIdentityAsync(cancellationToken, forceRead: true).ConfigureAwait(false);
        QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitPreInsertIdentity, timingStarted);
        ReconcileSession(recheck);
        if (recheck.Epoch != before.Epoch)
            return Result(OperationOutcome.Rejected, "读取歌曲详情期间 QQ 进程已变化，未发送选曲。", recheck,
                "qq-web-process-changed");
        if (!freshManualIntent && recheck.Epoch == before.Epoch && MatchesSong(recheck, song, artist))
        {
            _status = "最终核对时 QQ 已播放待播队首 ID；未重复强制播放。";
            return Result(OperationOutcome.Applied, _status, recheck);
        }
        if (recheck.Epoch != before.Epoch || string.IsNullOrEmpty(recheck.Current?.Id) ||
            !recheck.MetadataAgrees || recheck.NativeStatus?.SongPosition is not >= 0)
            return Result(OperationOutcome.Rejected,
                "当前 QQ 歌曲 ID 或队列位置未确认；未插入，也未发送 Next。", recheck,
                "qq-web-insert-anchor-unavailable");
        _known.TryAdd((song.SongId, song.SongType), new PlayerTrack(song.SongId.ToString(), song.Title, artist, string.Empty));
        // A validated new explicit request may replace an earlier attempt, even
        // for the same song. Automatic Next/rearm never receive this exemption.
        // Replace atomically under the gate, retaining the old reservation if the
        // new attempt is proven not to have dispatched; there is no timed unlock.
        // The user/player may have switched during the detail read. Only the
        // post-read, pre-send observation is a valid transition baseline.
        var alreadyMatched = MatchesSong(recheck, song, artist);
        _lastSubmission = new(song, artist, before.Epoch, DateTimeOffset.UtcNow, alreadyMatched,
            QQMusicWebSubmissionState.OutcomeUnknown);
        QQMusicWebSubmission submission;
        Task<QQMusicWebSubmission>? submissionTask = null;
        try
        {
            timingStarted = QQMusicWebTiming.Start();
            submissionTask = freshManualIntent
                ? _transport.PlayAsync(song, before.Epoch.ProcessId, cancellationToken,
                    expectedProcessStartTimeUtcTicks: before.Epoch.StartedAtUtcTicks, intent: QQMusicWebIntent.InsertNext)
                : StartGuardTransport(song, before.Epoch, cancellationToken, QQMusicWebIntent.InsertNext);
            submission = await submissionTask.ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitTransport, timingStarted);
        }
        catch (OperationCanceledException) when (submissionTask is null && cancellationToken.IsCancellationRequested)
        {
            submission = new(QQMusicWebSubmissionState.RejectedBeforeDispatch, "cancelled-before-dispatch");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            submission = new(QQMusicWebSubmissionState.OutcomeUnknown, "web-dispatch-outcome-unknown");
        }
        if (submission.State == QQMusicWebSubmissionState.RejectedBeforeDispatch)
        {
            _lastSubmission = QQMusicWebSubmissionPolicy.RestorePreviousLatch(freshManualIntent,
                previousLatch is not null && previousLatch.Epoch == recheck.Epoch, submission.State)
                    ? previousLatch : null;
            _status = "原生下一首插入在发送前被拒绝；未发送 Next。";
            return Result(OperationOutcome.Rejected, _status, recheck, submission.Code);
        }
        if (_lastSubmission is { } reserved) _lastSubmission = reserved with { State = submission.State };
        if (!freshManualIntent && cancellationToken.IsCancellationRequested)
        {
            _status = AutomaticStoppedStatus;
            return Result(OperationOutcome.Indeterminate, _status, recheck, "qq-web-stopped-after-insert");
        }
        if (submission.State != QQMusicWebSubmissionState.SubmittedUnverified)
        {
            _status = "原生下一首插入结果不确定；可能已插入，但没有盲目发送 Next，也不会自动重试。";
            if (!string.IsNullOrEmpty(submission.Diagnostics)) _status += $" 诊断：{submission.Diagnostics}";
            return Result(OperationOutcome.Indeterminate, _status, recheck, "qq-web-insert-outcome-unknown");
        }

        var latestConfirmation = recheck;
        var nextMayHaveBeenSent = false;
        try
        {
            // InsertNext is phase one, never a direct play or a global setting
            // change. Only a completed receipt permits a new current-anchor read.
            timingStarted = QQMusicWebTiming.Start();
            var afterInsert = await ObserveWithIdentityAsync(cancellationToken, forceRead: true).ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitPreNextIdentity, timingStarted);
            latestConfirmation = afterInsert;
            ReconcileSession(afterInsert);
            var targetMatchesIdentity = MatchesSong(afterInsert, song, artist);
            var targetPlaying = targetMatchesIdentity &&
                IsFreshTimeline(afterInsert, DateTimeOffset.UtcNow) && afterInsert.Timeline!.PlaybackStatus == "Playing";
            var advance = QQMusicWebSubmissionPolicy.DecideInsertedTargetAdvance(submission.State,
                afterInsert.Epoch == recheck.Epoch, afterInsert.Key == recheck.Key,
                !string.IsNullOrEmpty(afterInsert.Current?.Id) && afterInsert.MetadataAgrees &&
                    afterInsert.NativeStatus?.SongPosition is >= 0,
                targetMatchesIdentity, targetPlaying, !freshManualIntent && cancellationToken.IsCancellationRequested);
            if (advance == QQMusicWebInsertAdvance.TargetObserved)
            {
                _status = "插入后 QQ 已播放目标 ID；没有额外发送 Next。";
                return Result(OperationOutcome.Applied, _status, afterInsert);
            }
            if (advance != QQMusicWebInsertAdvance.AdvanceOnce)
            {
                _status = "插入已提交，但当前歌曲锚点变化或身份未确认；没有发送 Next，不会自动重复插入。";
                return Result(OperationOutcome.Indeterminate, _status, afterInsert, "qq-web-next-anchor-unavailable");
            }
            PlayerOperationResult next;
            timingStarted = QQMusicWebTiming.Start();
            if (freshManualIntent)
            {
                nextMayHaveBeenSent = true;
                next = SendControl("next", afterInsert, cancellationToken);
            }
            else
            {
                lock (_automaticSync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _automaticStop.Token.ThrowIfCancellationRequested();
                    nextMayHaveBeenSent = true;
                    next = SendControl("next", afterInsert, _lifetime.Token);
                }
            }
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitSendNext, timingStarted);
            if (next.Outcome != OperationOutcome.Accepted)
            {
                _status = "插入已提交，但 Next 未确认发送；不重试插入或 Next。";
                return Result(OperationOutcome.Indeterminate, _status, afterInsert, "qq-web-next-not-confirmed");
            }
            if (!freshManualIntent && cancellationToken.IsCancellationRequested)
                return Result(OperationOutcome.Indeterminate, AutomaticStoppedStatus, afterInsert, "qq-web-stopped-after-next");
            // Next is already in flight: one bounded read may confirm it, never a
            // second write. Stop cannot recall a native command already accepted.
            var confirmationToken = freshManualIntent ? cancellationToken : _lifetime.Token;
            timingStarted = QQMusicWebTiming.Start();
            await _events.RefreshMediaAsync(confirmationToken).ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitPostNextRefresh, timingStarted);
            timingStarted = QQMusicWebTiming.Start();
            var afterNext = await ObserveWithIdentityAsync(confirmationToken, forceRead: true).ConfigureAwait(false);
            QQMusicWebTiming.WriteElapsed(QQMusicWebTiming.Stage.SubmitPostNextIdentity, timingStarted);
            latestConfirmation = afterNext;
            ReconcileSession(afterNext);
            var observedTarget = afterNext.Epoch == recheck.Epoch && MatchesSong(afterNext, song, artist) &&
                IsFreshTimeline(afterNext, DateTimeOffset.UtcNow) && afterNext.Timeline!.PlaybackStatus == "Playing";
            _status = observedTarget ? "已插入原生下一首并发送一次 Next；QQ 实际播放 ID 已与目标一致。"
                : "已插入原生下一首并发送一次 Next；实际播放仍待确认，不会自动重试。";
            return Result(observedTarget ? OperationOutcome.Applied : OperationOutcome.Indeterminate,
                _status, afterNext, observedTarget ? null : "web-awaiting-observation");
        }
        catch (OperationCanceledException)
        {
            // A read deadline after a write is not a rejected transaction. Keep
            // its reservation and the last timestamped observation; later real
            // events can confirm arrival without repeating either media command.
            _status = nextMayHaveBeenSent
                ? "插入已提交，Next 可能已发送；确认读取超时或取消，实际播放待后续事件确认，不会重试。"
                : "插入已提交，确认读取超时或取消；尚未发送 Next，不会自动重试。";
            return Result(OperationOutcome.Indeterminate, _status, latestConfirmation,
                "qq-web-confirmation-cancelled");
        }
    }

    private PlayerOperationResult LatchedResult(SubmissionLatch previous, Observation observation)
    {
        var observed = !previous.AlreadyMatched && observation.Epoch == previous.Epoch &&
            observation.ObservedAt >= previous.SubmittedAt && MatchesSong(observation, previous.Song, previous.Artist);
        _status = observed
            ? "已核对此前目标的 QQ 原生歌曲 ID；本次未重复发送，未声称完整原生队列已验证。"
            : "同一 QQ 会话的该目标已有可能生效的提交；仍待观察，本次未自动重复发送。新的显式手动选曲可发起独立的一次尝试。";
        return Result(observed ? OperationOutcome.Applied : OperationOutcome.Indeterminate,
            _status, observation, observed ? null : "web-awaiting-observation");
    }

    private void CancelAutomaticWork()
    {
        lock (_automaticSync) _automaticStop.Cancel();
    }

    private CancellationTokenSource CreateAutomaticWorkCancellation(CancellationToken parent, bool renewStopped = false)
    {
        lock (_automaticSync)
        {
            if (renewStopped && _automaticStop.IsCancellationRequested)
            {
                _automaticStop.Dispose();
                _automaticStop = new();
            }
            return CancellationTokenSource.CreateLinkedTokenSource(parent, _automaticStop.Token);
        }
    }

    private Task<QQMusicWebSubmission> StartGuardTransport(QQMusicWebSong song, ProcessEpoch epoch,
        CancellationToken preDispatchToken, QQMusicWebIntent intent = QQMusicWebIntent.InsertNext)
    {
        lock (_automaticSync)
        {
            // This is the local point of no return. Stop wins before this lock,
            // or waits for the already-entered bounded helper to drain afterwards.
            preDispatchToken.ThrowIfCancellationRequested();
            _automaticStop.Token.ThrowIfCancellationRequested();
            return _transport.PlayAsync(song, epoch.ProcessId, _lifetime.Token,
                expectedProcessStartTimeUtcTicks: epoch.StartedAtUtcTicks, intent: intent);
        }
    }

    private static bool SameSong(QQMusicWebSong first, QQMusicWebSong second) =>
        first.SongId == second.SongId && first.SongType == second.SongType && first.SongMid == second.SongMid;

    private static bool MatchesSong(Observation observation, QQMusicWebSong song, string artist) =>
        observation.MetadataAgrees && observation.Current is { } current &&
        observation.NativeStatus?.SongId == song.SongId && observation.NativeStatus.SongType == song.SongType &&
        current.Id == song.SongId.ToString() &&
        QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(current.Title, current.Artist, song.Title, artist);

    private PlayerOperationResult SendControl(
        string control, Observation before, CancellationToken cancellationToken)
    {
        var after = Observe();
        ReconcileSession(after);
        if (before.Epoch is null || before.Epoch != after.Epoch || string.IsNullOrWhiteSpace(after.Executable))
            return Result(OperationOutcome.Rejected, "QQ 进程已变化，未发送控制命令。", after,
                "qq-web-process-changed");
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo
            {
                FileName = after.Executable, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add("/playcontrol");
            info.ArgumentList.Add($"'{control}'");
            using var helper = Process.Start(info);
            if (helper is null) throw new InvalidOperationException("Control helper did not start.");
            started = true;
            _status = $"已发送明确的 QQ {control} 命令；未独立确认播放状态。";
            return Result(OperationOutcome.Accepted, _status, after);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Result(started ? OperationOutcome.Indeterminate : OperationOutcome.Rejected,
                started ? "控制结果不确定；不会重试。" : "控制命令发送前失败。", after,
                started ? "qq-web-control-unknown" : "qq-web-control-rejected");
        }
    }

    private void EnsureObserver()
    {
        lock (_startSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // A playing session can continuously replenish the event channel.
            // Do not run its synchronous async prefix inside the first Probe:
            // cached reads may otherwise keep completing without ever yielding.
            _observer ??= Task.Run(ObserveEventsAsync);
        }
    }

    private async Task ObserveEventsAsync()
    {
        try
        {
            await using var subscription = _events.Subscribe();
            Trace("events-monitor-starting");
            await _events.EnsureStartedAsync().ConfigureAwait(false);
            Trace("events-monitor-ready");
            var token = _lifetime.Token;
            var read = subscription.Reader.WaitToReadAsync(token).AsTask();
            var expiryTick = Task.Delay(TimeSpan.FromMinutes(1), token);
            while (!token.IsCancellationRequested)
            {
                // A hot Windows event stream must not monopolize its caller or
                // starve manual commands even when channel reads complete inline.
                await Task.Yield();
                // This timer only expires state. It never reads a timeline or dispatches media.
                if (await Task.WhenAny(read, expiryTick).ConfigureAwait(false) == expiryTick)
                {
                    await _mutation.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        ExpirePending();
                        if (_lastSnapshot is not null && _pending is null)
                            PublishSnapshot(_lastSnapshot with { Next = null, NextSource = string.Empty, NextObservation = "unknown", Status = _status });
                    }
                    finally { _mutation.Release(); }
                    expiryTick = Task.Delay(TimeSpan.FromMinutes(1), token);
                    continue;
                }
                if (!await read.ConfigureAwait(false)) return;
                QQMusicPlayerEvent? latest = null;
                QQMusicPlayerEvent? timelineEvent = null;
                QQMusicPlayerEvent? metadataEvent = null;
                var artworkChanged = false;
                while (subscription.Reader.TryRead(out var item))
                {
                    if (item.Kind == QQMusicEventKind.ArtworkChanged)
                    {
                        artworkChanged = true;
                        continue; // Never replace real transition evidence in this batch.
                    }
                    if (item.Kind != QQMusicEventKind.SnapshotInvalidated) latest = item;
                    if (item.Kind == QQMusicEventKind.TimelinePropertiesChanged) timelineEvent = item;
                    if (item.Kind is QQMusicEventKind.MediaPropertiesChanged or QQMusicEventKind.WindowTitleChanged)
                        metadataEvent = item;
                }
                read = subscription.Reader.WaitToReadAsync(token).AsTask();
                if (latest is null && !artworkChanged) continue;
                await _mutation.WaitAsync(token).ConfigureAwait(false);
                CancellationTokenSource? automaticWork = null;
                try
                {
                    if (latest is null)
                    {
                        PublishArtworkOnlySnapshot();
                        continue;
                    }
                    automaticWork = _pending is not null ? CreateAutomaticWorkCancellation(token) : null;
                    var workToken = automaticWork?.Token ?? token;
                    workToken.ThrowIfCancellationRequested();
                    // Capture the pending generation before the bounded read. A
                    // callback queued behind InsertNext is still a real callback;
                    // it must not expire merely because our own helper held the gate.
                    var receivedAt = DateTimeOffset.UtcNow;
                    var beforeRead = Observe();
                    var evidence = new EventEvidence(_pending?.Owner, beforeRead.Epoch,
                        TrackKey(beforeRead.Current), receivedAt, false);
                    var pendingMetadata = _pending is { } pending && metadataEvent is not null &&
                        metadataEvent.ObservedAt >= pending.CreatedAt &&
                        metadataEvent.ObservedAt - receivedAt <= TimeSpan.FromSeconds(1);
                    if (pendingMetadata && _pending is { } witnessedPending && !string.IsNullOrEmpty(evidence.MetadataKey))
                    {
                        // Metadata/read notifications may precede the playback
                        // callback. Keep their owner-bound key for convergence;
                        // later timeline callbacks cannot renew its fixed window.
                        _pendingMetadataWitness = new(witnessedPending.Owner, witnessedPending.Epoch,
                            evidence.MetadataKey, metadataEvent!);
                        if (evidence.MetadataKey != witnessedPending.AnchorMetadataKey)
                            _unprocessedTransitionAt ??= receivedAt;
                    }
                    var convergingMetadata = _pending is { } convergingPending &&
                        _unprocessedTransitionAt is { } convergenceStarted &&
                        QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(convergenceStarted, receivedAt) &&
                        _pendingMetadataWitness is { } witness && witness.Owner == convergingPending.Owner &&
                        witness.Epoch == beforeRead.Epoch && witness.MetadataKey == evidence.MetadataKey;
                    var readStartedAt = DateTimeOffset.UtcNow;
                    // A pending metadata transition gets a new typed read, even
                    // when a previous read was cached under the same title.
                    var observation = await ObserveWithIdentityAsync(workToken,
                        forceRead: pendingMetadata || convergingMetadata || metadataEvent is not null && metadataEvent.ObservedAt > _nativeStatusReadAt)
                        .ConfigureAwait(false);
                    evidence = evidence with
                    {
                        IdentityReadConfirmed = _nativeStatusReadAt >= readStartedAt &&
                            observation.NativeStatus is { Succeeded: true, SongId: > 0, SongType: >= 0 } &&
                            !string.IsNullOrEmpty(observation.Current?.Id)
                    };
                    if (!await HandleEventAsync(latest, timelineEvent, metadataEvent, observation, evidence, workToken).ConfigureAwait(false))
                        Snapshot(observation);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && automaticWork?.IsCancellationRequested == true)
                {
                    // Stopping one automatic generation is not failure of the
                    // long-lived observer. The cancel-only command owns its ack.
                    ClearPending(AutomaticStoppedStatus);
                }
                finally
                {
                    automaticWork?.Dispose();
                    _mutation.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await _mutation.WaitAsync().ConfigureAwait(false);
            try { ClearPending("Web 自动下一首观察器不可用；停止自动选曲，可手动操作。"); }
            finally { _mutation.Release(); }
        }
    }

    // True means SubmitOnceAsync has already published a newer post-command
    // observation. Otherwise the caller publishes this one shared read.
    private async Task<bool> HandleEventAsync(QQMusicPlayerEvent item,
        QQMusicPlayerEvent? timelineEvent, QQMusicPlayerEvent? metadataEvent,
        Observation current, EventEvidence evidence, CancellationToken token)
    {
        ReconcileSession(current);
        if (_pending is not { } pending || item.ObservedAt < pending.CreatedAt ||
            current.Epoch != pending.Epoch || pending.Owner != evidence.Owner ||
            current.Epoch != evidence.Epoch) return false;
        var now = DateTimeOffset.UtcNow;
        // Missing identity is not a different song. In particular, a failed
        // query must not turn an ID anchor into a title-key transition.
        if (string.IsNullOrEmpty(current.Current?.Id)) return false;
        var sameTrack = IsSamePendingAnchor(pending, current);
        if (sameTrack && !QQMusicWebSubmissionPolicy.SameTypedCurrentIdentity(
            pending.AnchorSongId, current.Current?.Id, pending.AnchorKey, current.Key))
        {
            // A type-only change is neither a new song nor permission to reuse
            // the old type's near-end evidence for a synthetic Next.
            _pending = pending with { LastPlaying = null };
            _unprocessedTransitionAt = null;
            _pendingMetadataWitness = null;
            _status = "同一歌曲 ID 的类型信息发生变化；保留待播目标并清除旧结束证据，没有发送 Next。";
            return false;
        }
        if (!sameTrack) _unprocessedTransitionAt ??= evidence.ReceivedAt;
        var metadataWitness = _pendingMetadataWitness is { } heldWitness && heldWitness.Owner == pending.Owner &&
            heldWitness.Epoch == current.Epoch && heldWitness.MetadataKey == evidence.MetadataKey
                ? heldWitness : null;
        var freshPlaying = IsFreshTimeline(current, now) && current.MetadataAgrees &&
            current.Timeline!.PlaybackStatus == "Playing";
        var deferredMetadataConfirmed = metadataWitness is not null && !sameTrack &&
            _unprocessedTransitionAt is { } transitionStarted &&
            QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(transitionStarted, now) &&
            QQMusicWebSubmissionPolicy.CanConfirmDelayedMetadataTransition(
                pending.Owner == evidence.Owner, current.Epoch == evidence.Epoch,
                pending.CreatedAt, metadataWitness.Callback.ObservedAt, evidence.ReceivedAt,
                !string.IsNullOrEmpty(evidence.MetadataKey) && TrackKey(current.Current) == evidence.MetadataKey,
                evidence.IdentityReadConfirmed, current.MetadataAgrees);
        if (!sameTrack && !deferredMetadataConfirmed)
        {
            // A new ID without an aligned metadata/read witness is pending,
            // not a cancellation. Probe and Snapshot share this same deadline.
            _status = "已观察到切歌，正在等待本次歌曲 ID 与元数据对齐；未发送兜底命令。";
            return false;
        }
        if (sameTrack && TrackKey(current.Current) == pending.AnchorMetadataKey)
        {
            _unprocessedTransitionAt = null;
            _pendingMetadataWitness = null;
        }
        var eventFreshAtReceipt = EventIsFresh(item, pending.CreatedAt, evidence.ReceivedAt);
        if (!eventFreshAtReceipt && !deferredMetadataConfirmed)
        {
            return false; // A delayed event cannot operate on a newer owner.
        }
        var hasTimelineEvent = EventIsFresh(timelineEvent, pending.CreatedAt, evidence.ReceivedAt);
        var hasMetadataEvent = EventIsFresh(metadataEvent, pending.CreatedAt, evidence.ReceivedAt) ||
            deferredMetadataConfirmed;
        var freshEvent = hasTimelineEvent || hasMetadataEvent || item.Kind == QQMusicEventKind.PlaybackInfoChanged;
        var previous = pending.LastPlaying;
        var wasNearEnd = previous is not null && IsFreshTimeline(previous, now) &&
            previous.Key == pending.AnchorKey && previous.Timeline!.PlaybackStatus == "Playing" &&
            QQMusicWebEndPolicy.IsNearEnd(previous.Timeline.StartTime, previous.Timeline.EndTime,
                previous.Timeline.ReportedPosition) &&
            now - previous.ObservedAt <= TimeSpan.FromSeconds(3);
        var stoppedAtEnd = sameTrack && IsFreshTimeline(current, now) &&
            current.Timeline!.PlaybackStatus == "Stopped" &&
            current.Timeline.EndTime - current.Timeline.ReportedPosition <= TimeSpan.FromMilliseconds(250);
        var changedMetadata = !sameTrack && !string.IsNullOrEmpty(current.Key) && current.MetadataAgrees &&
            hasMetadataEvent;
        // A one-song QQ list loops the current track without changing metadata.
        // Playback/metadata callbacks can arrive before the timeline callback.
        // Require a fresh event AND a newly updated source timeline crossing the
        // observed tail -> start boundary, not one particular callback ordering.
        // Arbitrary middle-of-song seeks never trigger this path.
        // A user's near-end seek to the beginning is still indistinguishable.
        var restartedAtEnd = sameTrack && wasNearEnd && previous?.Timeline is { } previousTimeline &&
            freshEvent && IsFreshTimeline(current, now) &&
            current.Timeline!.PlaybackStatus == "Playing" &&
            current.Timeline.LastUpdatedTime > previousTimeline.LastUpdatedTime &&
            QQMusicWebEndPolicy.IsSameTrackRollover(previousTimeline.StartTime, previousTimeline.EndTime,
                previousTimeline.ReportedPosition, current.Timeline.StartTime, current.Timeline.EndTime,
                current.Timeline.ReportedPosition);
        // A confirmed numeric-ID transition is independent of a late/old QQ
        // Playing/Paused callback. Pause rules below apply only to the same ID.
        var decision = deferredMetadataConfirmed ? QQMusicWebGuardEventDecision.Continue :
            QQMusicWebGuardPolicy.OnObservation(sameTrack, current.Timeline?.PlaybackStatus,
            previous is not null, freshPlaying,
            freshEvent && wasNearEnd && (stoppedAtEnd || changedMetadata || restartedAtEnd));
        if (decision == QQMusicWebGuardEventDecision.KeepTargetClearEvidence)
        {
            _pending = pending with { LastPlaying = null };
            _status = GuardWaitingStatus;
            return false;
        }
        if (decision == QQMusicWebGuardEventDecision.SeedPlayingOnly)
        {
            // The first fresh sample after paused/stale registration or Resume
            // only establishes evidence; it cannot also consume the target.
            _pending = pending with { LastPlaying = current };
            _status = GuardReadyStatus;
            return false;
        }
        var transition = deferredMetadataConfirmed ? QQMusicWebAutomaticTransition.TrackChangeFallback :
            QQMusicWebSubmissionPolicy.ClassifyAutomaticTransition(decision,
            _pending?.Owner == pending.Owner, current.Epoch == pending.Epoch,
            eventFreshAtReceipt || deferredMetadataConfirmed, sameTrack, !string.IsNullOrEmpty(current.Current?.Id),
            current.MetadataAgrees, current.Timeline?.PlaybackStatus, IsFreshTimeline(current, now), hasMetadataEvent);
        if (transition != QQMusicWebAutomaticTransition.None)
        {
            // Consumption and the one possible media mutation share the manual-command gate.
            var transitionLabel = transition == QQMusicWebAutomaticTransition.NaturalEnd
                ? "可信结束过渡" : "切歌兜底";
            ClearPending($"已观察到{transitionLabel}；消费一次逻辑下一首。");
            if (!QQMusicWebSubmissionPolicy.ShouldSubmitConsumedTarget(transition,
                !sameTrack && current.Current?.Id == pending.Song.SongId.ToString()))
            {
                _status = $"{transitionLabel}：QQ 歌曲 ID 已与待播队首一致；未额外发送选曲。";
                return false;
            }
            await SubmitOnceAsync(pending.Song, pending.Track.Artist, current, token).ConfigureAwait(false);
            _status = $"{transitionLabel}：{_status}";
            if (_lastSnapshot is { } submittedSnapshot)
                PublishSnapshot(submittedSnapshot with { Status = $"{transitionLabel}：{submittedSnapshot.Status}" });
            _events.NotifySnapshotInvalidated();
            return true;
        }
        if (freshPlaying)
        {
            if (previous is not null && wasNearEnd &&
                current.Timeline!.ReportedPosition + TimeSpan.FromSeconds(2) < previous.Timeline!.ReportedPosition)
            {
                ClearPending("同名歌曲的播放位置回退，无法区分循环或跳转；停止自动接管。");
                return false;
            }
            _pending = pending with { LastPlaying = current };
        }
        return false;
    }

    private static bool EventIsFresh(QQMusicPlayerEvent? item, DateTimeOffset createdAt, DateTimeOffset now) =>
        item is not null && item.ObservedAt >= createdAt && item.ObservedAt <= now + TimeSpan.FromSeconds(1) &&
            now - item.ObservedAt <= TimeSpan.FromSeconds(3);

    private static bool IsSamePendingAnchor(PendingNext pending, Observation observation) =>
        QQMusicWebSubmissionPolicy.SameNumericCurrentId(pending.AnchorSongId, observation.Current?.Id);

    private async Task<Observation> ObserveWithIdentityAsync(CancellationToken token, bool forceRead = false)
    {
        var before = Observe();
        if (before.Epoch is null) return before;
        var metadataKey = TrackKey(before.Current);
        var attemptKey = $"{before.Epoch.ProcessId}:{before.Epoch.StartedAtUtcTicks}:{metadataKey}";
        if (_identityAttemptKey != attemptKey)
        {
            _identityAttemptKey = attemptKey;
            _identityAttempts = 0;
            _identityAttemptAt = default;
        }
        if (!forceRead && (!string.IsNullOrEmpty(before.Current?.Id) ||
            _identityAttempts >= 3 || DateTimeOffset.UtcNow - _identityAttemptAt < TimeSpan.FromSeconds(2)))
        {
            RecordTargetObservation(before);
            return before;
        }
        // Fixed read-only queries, not playback retries. Bind the answer to the
        // same process epoch and metadata on both sides of the asynchronous read.
        _identityAttempts++;
        _identityAttemptAt = DateTimeOffset.UtcNow;
        var status = await _statusReader.ReadAsync(before.Epoch.ProcessId, before.Epoch.StartedAtUtcTicks, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = Observe();
        var identityBound = after.Epoch == before.Epoch && TrackKey(after.Current) == metadataKey;
        if (identityBound)
        {
            _nativeStatus = status;
            _nativeStatusEpoch = before.Epoch;
            _nativeStatusMetadataKey = metadataKey;
            _nativeStatusReadAt = _identityAttemptAt;
        }
        // Reuse the actual post-read observation. Its epoch/metadata already
        // form the second fence; projecting the new status needs no third scan.
        var observed = ReprojectIdentity(after, identityBound ? status : null);
        RecordTargetObservation(observed);
        return observed;
    }

    private void RecordTargetObservation(Observation observation)
    {
        if (_lastInsertion is { TargetObserved: false } insertion && observation.Epoch == insertion.Epoch &&
            observation.ObservedAt >= insertion.InsertedAt && MatchesSong(observation, insertion.Song, insertion.Artist) &&
            IsFreshTimeline(observation, DateTimeOffset.UtcNow) && observation.Timeline!.PlaybackStatus == "Playing")
            _lastInsertion = insertion with { TargetObserved = true };
        if (_lastSubmission is { TargetObserved: false, AlreadyMatched: false } submission &&
            observation.Epoch == submission.Epoch && observation.ObservedAt >= submission.SubmittedAt &&
            MatchesSong(observation, submission.Song, submission.Artist) &&
            IsFreshTimeline(observation, DateTimeOffset.UtcNow) && observation.Timeline!.PlaybackStatus == "Playing")
            _lastSubmission = submission with { TargetObserved = true };
    }

    private Observation Observe()
    {
        Trace("observe-native-starting");
        var state = QQMusicNativeController.ReadPlaybackState();
        Trace("observe-native-returned");
        ProcessEpoch? epoch = null;
        var executable = string.Empty;
        var version = string.Empty;
        if (state.IsRunning && state.WindowHandle is not null && state.ProcessId is > 0)
        {
            try
            {
                // PID came from the exact window selected in ReadPlaybackState.
                // Recheck that process, not every desktop window a second time.
                using var process = Process.GetProcessById(state.ProcessId.Value);
                executable = process.MainModule?.FileName ?? string.Empty;
                if (!process.HasExited && Path.GetFileName(executable).Equals("QQMusic.exe", StringComparison.OrdinalIgnoreCase))
                {
                    epoch = new(process.Id, process.StartTime.ToUniversalTime().Ticks);
                    version = FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
        PlayerTrack? current = string.IsNullOrWhiteSpace(state.Title) ? null :
            new(string.Empty, state.Title, state.Artist ?? string.Empty, string.Empty);
        Trace("observe-media-starting");
        var media = _events.ReadMediaTrack();
        Trace("observe-media-returned");
        var agrees = current is not null && media is not null &&
            QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(current.Title, current.Artist, media.Title, media.Artist);
        if (agrees) current = media;
        if (current is not null && string.IsNullOrEmpty(current.CoverUrl))
        {
            var matches = _known.Values.Where(x => QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(
                current.Title, current.Artist, x.Title, x.Artist)).Take(2).ToArray();
            // Artwork is useful, but a metadata match must not manufacture a native song ID.
            if (matches.Length == 1) current = current with { CoverUrl = matches[0].CoverUrl };
        }
        var metadataKey = TrackKey(current);
        var native = epoch is not null && epoch == _nativeStatusEpoch && metadataKey == _nativeStatusMetadataKey
            ? _nativeStatus : null;
        return ReprojectIdentity(new(epoch, executable, version, current, metadataKey, _events.ReadTimelineSnapshot(),
            agrees, DateTimeOffset.UtcNow), native);
    }

    private Observation ReprojectIdentity(Observation observation, QQMusicWebStatus? native)
    {
        var current = observation.Current;
        var catalogAgrees = native is { SongId: > 0, SongType: >= 0 } && current is not null &&
            (!_known.TryGetValue((native.SongId.Value, native.SongType.Value), out var catalogTrack) ||
             QQMusicTrackMatchPolicy.MetadataRepresentsSameSong(current.Title, current.Artist, catalogTrack.Title, catalogTrack.Artist));
        var projected = QQMusicWebSubmissionPolicy.ProjectCurrentIdentity(TrackKey(current),
            current is not null && observation.MetadataAgrees, native?.Succeeded == true,
            native?.SongId, native?.SongType, catalogAgrees);
        // Always replace the old ID, including with empty on an invalid read.
        // Preserve the exact post-read text, artwork, timeline and timestamp.
        return observation with
        {
            Current = current is null ? null : current with { Id = projected.Id },
            Key = projected.Key,
            NativeStatus = native
        };
    }

    private void ReconcileSession(Observation observation)
    {
        if (_epoch != observation.Epoch)
        {
            ClearPending("QQ 进程身份发生变化；旧逻辑下一首已取消。");
            _lastSubmission = null;
            _lastInsertion = null;
            _lastSnapshot = null;
            _epoch = observation.Epoch;
        }
        ExpirePending();
    }

    private void ExpirePending()
    {
        if (_pending is not null && _unprocessedTransitionAt is { } started &&
            !QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(started, DateTimeOffset.UtcNow))
            ClearPending("切歌身份与元数据在固定 8 秒窗口内未对齐；取消本次待播守卫，没有发送兜底或重试。");
        if (_pending is { } pending && DateTimeOffset.UtcNow - pending.CreatedAt >= TimeSpan.FromHours(12))
            ClearPending("逻辑下一首已过期（12 小时），没有发送选曲。");
    }

    private void ClearPending(string status)
    {
        if (_pending is null) return;
        _pending = null;
        _pendingMetadataWitness = null;
        _unprocessedTransitionAt = null;
        _status = status;
        _events.NotifySnapshotInvalidated();
    }

    private PlayerSnapshot Snapshot(Observation observation)
    {
        if (_pending is { } pending && !string.IsNullOrEmpty(observation.Current?.Id) && !IsSamePendingAnchor(pending, observation) &&
            _lastSnapshot is { } previous && previous.ProcessId == observation.Epoch?.ProcessId)
        {
            _unprocessedTransitionAt ??= DateTimeOffset.UtcNow;
            if (QQMusicWebSubmissionPolicy.IsPendingConvergenceActive(_unprocessedTransitionAt.Value, DateTimeOffset.UtcNow)) return previous;
            ClearPending("媒体过渡证据未及时一致，已取消自动接管。");
        }
        var snapshot = new PlayerSnapshot(
            observation.Epoch is not null, DisplayName, observation.Epoch?.ProcessId, observation.Version,
            $"{_status}；{_events.SourceStatus}；" + (string.IsNullOrEmpty(observation.Current?.Id)
                ? "当前 QQ 歌曲 ID 未确认，不用歌名冒充歌曲身份"
                : $"QQ 歌曲 ID={observation.Current.Id}，位置={observation.NativeStatus?.SongPosition}（非完整队列快照）"),
            string.IsNullOrEmpty(observation.Current?.Id) ? null : observation.Current, observation.ObservedAt, _pending?.Track,
            _pending is null ? string.Empty : "qq-logical-guard",
            _pending is null ? "unknown" : "track", PlaybackAnchorReady: false, RequiresPlaybackAnchor: false,
            OwnsLogicalNext: true);
        PublishSnapshot(snapshot);
        return snapshot;
    }

    private void PublishSnapshot(PlayerSnapshot snapshot)
    {
        Volatile.Write(ref _lastSnapshot, snapshot);
        lock (_subscriberSync)
            foreach (var subscriber in _subscribers) subscriber.Writer.TryWrite(snapshot);
    }

    private void PublishArtworkOnlySnapshot()
    {
        // Called under the mutation gate. A decorative update must not read a
        // new timeline, renew observation time, classify transitions or mutate
        // the pending owner. Always copy from the latest accepted snapshot.
        var snapshot = _lastSnapshot;
        var media = _events.ReadMediaTrack();
        if (_epoch is null || snapshot is null || snapshot.ProcessId != _epoch.ProcessId ||
            !QQMusicEventMonitor.SameMediaTrack(snapshot.Current, media) ||
            string.IsNullOrEmpty(media!.CoverUrl) || snapshot.Current!.CoverUrl == media.CoverUrl)
            return;
        PublishSnapshot(snapshot with { Current = snapshot.Current with { CoverUrl = media.CoverUrl } });
    }

    private PlayerOperationResult Result(OperationOutcome outcome, string message, Observation observation,
        string? code = null) => new(outcome, message, Snapshot(observation), code);

    private static bool IsFreshTimeline(Observation observation, DateTimeOffset now)
    {
        var timeline = observation.Timeline;
        return timeline is not null && observation.MetadataAgrees && timeline.EndTime > timeline.StartTime &&
            timeline.ReportedPosition >= timeline.StartTime && timeline.ReportedPosition <= timeline.EndTime &&
            timeline.LastUpdatedTime <= now + TimeSpan.FromSeconds(1) &&
            now - timeline.LastUpdatedTime <= TimeSpan.FromSeconds(10);
    }

    private static bool TrySong(PlayerTrack? track, out QQMusicWebSong? song)
    {
        song = null;
        if (track is null) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(track.NativeData,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null || !payload.IsPlayable || payload.Metadata is null || payload.SongId <= 0 || payload.SongId > uint.MaxValue ||
                string.IsNullOrWhiteSpace(payload.SongMid) || string.IsNullOrWhiteSpace(track.Title)) return false;
            song = new((uint)payload.SongId, payload.SongMid, payload.SongType, track.Title, payload.Metadata);
            // Validate the entire catalog projection before reserving a possible
            // dispatch, not after the helper has started.
            _ = QQMusicWebProtocol.BuildPlayRequest(song);
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return false; }
    }

    private static string TrackKey(PlayerTrack? track) => track is null ? string.Empty :
        $"{track.Title.Trim().ToUpperInvariant()}|{track.Artist.Trim().ToUpperInvariant()}";
    private static string Fingerprint(PlayerSnapshot snapshot) => JsonSerializer.Serialize(snapshot with { ObservedAt = default });

    private static void Trace(string stage)
    {
        if (Environment.GetEnvironmentVariable("AWOO_QQMUSIC_WEB_TRACE") == "1")
            Console.Error.WriteLine($"qq-web-stage:{stage}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _mutation.WaitAsync().ConfigureAwait(false);
        try { ClearPending("Web 后端已关闭。"); }
        finally { _mutation.Release(); }
        Task? observer;
        lock (_startSync) observer = _observer;
        if (observer is not null) await observer.ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
        lock (_subscriberSync)
        {
            foreach (var subscriber in _subscribers) subscriber.Writer.TryComplete();
            _subscribers.Clear();
        }
        _catalog.Dispose();
        _songDetails.Dispose();
        // Do not dispose the gate/CTS underneath already queued cancelled callers.
    }

    private sealed record Payload(long SongId, int SongType, string SongMid, bool IsPlayable, QQMusicWebMetadata? Metadata = null);
    private sealed record ProcessEpoch(int ProcessId, long StartedAtUtcTicks);
    private sealed record Observation(ProcessEpoch? Epoch, string Executable, string Version,
        PlayerTrack? Current, string Key, QQMusicTimelineSnapshot? Timeline, bool MetadataAgrees,
        DateTimeOffset ObservedAt, QQMusicWebStatus? NativeStatus = null);
    private sealed record EventEvidence(Guid? Owner, ProcessEpoch? Epoch, string MetadataKey,
        DateTimeOffset ReceivedAt, bool IdentityReadConfirmed);
    private sealed record PendingMetadataWitness(Guid Owner, ProcessEpoch Epoch, string MetadataKey,
        QQMusicPlayerEvent Callback);
    private sealed record PendingNext(Guid Owner, PlayerTrack Track, QQMusicWebSong Song,
        ProcessEpoch Epoch, string AnchorKey, DateTimeOffset CreatedAt, Observation? LastPlaying,
        string AnchorSongId, string AnchorMetadataKey);
    private sealed record SubmissionLatch(QQMusicWebSong Song, string Artist, ProcessEpoch Epoch, DateTimeOffset SubmittedAt,
        bool AlreadyMatched, QQMusicWebSubmissionState State, bool TargetObserved = false);
    private sealed record InsertionLatch(QQMusicWebSong Song, string Artist, ProcessEpoch Epoch, DateTimeOffset InsertedAt,
        bool TargetObserved = false);
}
