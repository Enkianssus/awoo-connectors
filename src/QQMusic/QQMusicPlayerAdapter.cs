using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using QQMusicControlPoc;

namespace UnifiedPlayerControlPoc;

internal sealed class QQMusicPlayerAdapter :
    IPlayerAdapter,
    IPlayerSnapshotEventSource
{
    private static readonly TimeSpan NaturalEndPreMuteLead =
        TimeSpan.FromMilliseconds(450);

    private readonly QQMusicCatalogClient _catalogClient = new();
    private readonly QQMusicEventMonitor _eventMonitor = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _nativeNextInsertGate = new(1, 1);
    private readonly object _trackSync = new();
    private readonly object _softwareNextSync = new();
    private readonly object _nativeNextSync = new();
    private readonly Dictionary<(long SongId, int SongType), PlayerTrack>
        _knownTracks = [];
    private readonly Dictionary<string, PlayerTrack> _resolvedCurrentTracks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _artworkLookups =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _artworkLookupOrder = new();
    private readonly List<Task> _artworkTasks = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _softwareNextCancellation;
    private Task? _softwareNextTask;
    private readonly HashSet<Task> _softwareNextTasks = [];
    private readonly List<PendingQqNativeNext> _pendingNativeNext = [];
    private PlayerTrack? _softwareNextTarget;
    private PlayerTrack? _lateRecoveryTarget;
    private PlayerTrack? _lastObservedTrack;
    private NextGuardState _softwareNextState = NextGuardState.None;
    private long _softwareNextGeneration;
    private long _softwareNextGuardIdSequence;
    private long _softwareNextGuardId;
    private volatile string _softwareNextStatus = string.Empty;
    private long _observedTrackSequence;
    private int? _nativeSessionProcessId;
    private bool _sessionObservedPlaying;
    private int _snapshotSuppressionDepth;
    private int _disposing;
    private int _cachedVersionProcessId;
    private string _cachedVersion = string.Empty;
    private int _compatibilityReportStarted;

    public string Key => "qqmusic";

    public string DisplayName => "QQ 音乐";

    public string TestedVersion =>
        "22.22 / 22.41 / 22.51 / 22.52 / 22.60 / 22.61";

    public bool AllowUnsafeNativeNext { get; set; }

    public PlayerCapabilities Capabilities { get; } = new(
        Search: true,
        PlaySelected: true,
        Previous: true,
        Pause: true,
        Resume: true,
        Toggle: false,
        Next: true,
        InsertNext: true,
        InsertNextLevel: "精确版本画像原生插队；静音+暂停守卫防漏音");

    public Task<PlayerSnapshot> ProbeAsync(
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var state = QQMusicNativeController.ReadPlaybackState();
            if (!state.IsRunning)
            {
                ObserveNativeSession(null);
                return new PlayerSnapshot(
                    false,
                    DisplayName,
                    null,
                    string.Empty,
                    "未连接：没有发现可见 QQ 音乐窗口",
                    null,
                    DateTimeOffset.Now,
                    PlaybackAnchorReady: false);
            }

            StartCompatibilityReportOnce();

            var processId = state.WindowHandle is null
                ? null
                : FindProcessId(state.WindowHandle.Value);
            ObserveNativeSession(processId);
            var version = processId is null
                ? string.Empty
                : GetCachedVersion(processId.Value);
            var current = ResolveKnownTrack(
                state.Title,
                state.Artist)
                ?? (!string.IsNullOrWhiteSpace(state.Title)
                    ? new PlayerTrack(
                        string.Empty,
                        state.Title,
                        state.Artist ?? string.Empty,
                        string.Empty)
                    : null);
            var mediaTrack = _eventMonitor.ReadMediaTrack();
            if (CurrentMetadataRepresentsSameSong(current, mediaTrack))
            {
                // GSMTC carries structured title/artist fields and is more
                // reliable than splitting the QQ window caption. Only reuse
                // it when it agrees with the current window song so a delayed
                // media-session update cannot resurrect the previous track.
                current = ResolveKnownTrack(
                        mediaTrack!.Title,
                        mediaTrack.Artist)
                    ?? mediaTrack;
            }
            var anchor = EvaluatePlaybackAnchor(state);
            if (current is not null
                && string.IsNullOrWhiteSpace(current.CoverUrl))
            {
                ScheduleArtworkLookup(current);
            }
            ClearPendingNativeNextIfPlaying(current);
            var guardedNext = GetSoftwareNextSnapshot();
            return new PlayerSnapshot(
                true,
                DisplayName,
                processId,
                version,
                "QQMusic.exe 单实例控制可用；实时状态="
                + _eventMonitor.SourceStatus
                + (string.IsNullOrWhiteSpace(_softwareNextStatus)
                    ? string.Empty
                    : $"；{_softwareNextStatus}"),
                current,
                DateTimeOffset.Now,
                guardedNext.Target,
                guardedNext.Target is null ? string.Empty : "qq-logical-guard",
                guardedNext.Target is null ? "unknown" : "track",
                PlaybackAnchorReady: anchor.IsReliable,
                NextGuardState: guardedNext.State,
                NextGuardId: guardedNext.GuardId);
        }, cancellationToken);
    }

    public async IAsyncEnumerable<PlayerSnapshot> WatchSnapshotsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var subscription = _eventMonitor.Subscribe();
        await _eventMonitor.EnsureStartedAsync().ConfigureAwait(false);

        var snapshot = await ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        var fingerprint = BuildSnapshotFingerprint(snapshot);
        yield return snapshot;

        while (await subscription.Reader.WaitToReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            QQMusicPlayerEvent? latest = null;
            while (subscription.Reader.TryRead(out var playerEvent))
            {
                latest = playerEvent;
            }

            // GSMTC commonly emits playback, timeline and media-property
            // notifications as one burst. Coalesce that burst into one exact
            // snapshot without turning the delay into a repeating poll.
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            while (subscription.Reader.TryRead(out var additionalEvent))
            {
                latest = additionalEvent;
            }

            if (Volatile.Read(ref _snapshotSuppressionDepth) > 0)
            {
                continue;
            }

            snapshot = await ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (latest?.Kind == QQMusicEventKind.MediaPropertiesChanged
                && snapshot.Connected
                && _eventMonitor.ReadMediaTrack() is { } mediaTrack
                && CurrentMetadataRepresentsSameSong(
                    snapshot.Current,
                    mediaTrack))
            {
                var current = ResolveKnownTrack(
                        mediaTrack.Title,
                        mediaTrack.Artist)
                    ?? mediaTrack;
                if (string.IsNullOrWhiteSpace(current.CoverUrl))
                {
                    ScheduleArtworkLookup(current);
                }
                ClearPendingNativeNextIfPlaying(current);
                snapshot = snapshot with
                {
                    Current = current,
                    ObservedAt = DateTimeOffset.Now
                };
            }

            var nextFingerprint = BuildSnapshotFingerprint(snapshot);
            if (nextFingerprint == fingerprint)
            {
                continue;
            }
            fingerprint = nextFingerprint;
            yield return snapshot;
        }
    }

    private void StartCompatibilityReportOnce()
    {
        if (Interlocked.Exchange(ref _compatibilityReportStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var completed = await QQMusicCompatibilityReporter
                .ReportCurrentIfNeededAsync(_lifetimeCancellation.Token)
                .ConfigureAwait(false);
            if (!completed && !_lifetimeCancellation.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _compatibilityReportStarted, 0);
            }
        });
    }

    public async Task<IReadOnlyList<PlayerTrack>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var songs = await _catalogClient.SearchAsync(
            query,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var tracks = songs.Select(CreateTrack).ToArray();
        lock (_trackSync)
        {
            foreach (var track in tracks)
            {
                var payload = ParsePayload(track);
                _knownTracks[(payload.SongId, payload.SongType)] = track;
            }
            TrimTrackCachesLocked();
        }

        return tracks;
    }

    public async Task<PlayerOperationResult> ExecuteAsync(
        PlayerCommand command,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (command is PlayerCommand.InsertNext
                or PlayerCommand.ArmNextGuard
                or PlayerCommand.PlaySelected)
            {
                // Direct commands can arrive before the snapshot event pump has
                // initialized GSMTC. Start it here so an existing Playing
                // timeline can establish the QQ playback anchor.
                await _eventMonitor.EnsureStartedAsync()
                    .ConfigureAwait(false);
            }
            var before = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!before.Connected)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "QQ 音乐未连接。",
                    before);
            }

            if (track is not null)
            {
                RememberTrack(track);
            }
            if (command == PlayerCommand.InsertNext)
            {
                return await ExecuteInsertNextAsync(
                    before,
                    track,
                    cancellationToken).ConfigureAwait(false);
            }
            if (command == PlayerCommand.ArmNextGuard)
            {
                var guardResult = ArmSoftwareNext(
                    before,
                    track,
                    cancellationToken);
                return guardResult with
                {
                    Message = guardResult.IsSuccess
                        ? $"未重复提交 QQ 原生下一首；{guardResult.Message}"
                        : guardResult.Message
                };
            }
            if (command == PlayerCommand.InterruptSelected)
            {
                return await ExecuteInterruptSelectedAsync(
                    before,
                    track,
                    cancellationToken).ConfigureAwait(false);
            }
            if (command == PlayerCommand.PlaySelected)
            {
                return await ExecutePlaySelectedAsync(
                    before,
                    track,
                    cancellationToken).ConfigureAwait(false);
            }

            var executable = FindExecutablePath();
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "无法从运行进程或常见安装目录定位 QQMusic.exe。",
                    before);
            }

            string switchName;
            string argument;
            switch (command)
            {
                case PlayerCommand.Previous:
                    switchName = "/playcontrol";
                    argument = "'prev'";
                    break;
                case PlayerCommand.Next:
                    switchName = "/playcontrol";
                    argument = "'next'";
                    break;
                case PlayerCommand.Pause:
                    switchName = "/playcontrol";
                    argument = "'pause'";
                    break;
                case PlayerCommand.Resume:
                    switchName = "/playcontrol";
                    argument = "'play'";
                    break;
                default:
                    return new PlayerOperationResult(
                        OperationOutcome.Unsupported,
                        "QQ 音乐适配器不支持该命令。",
                        before);
            }

            var foregroundBefore = GetForegroundWindow();
            var send = await Task.Run(
                () => SendSingleInstanceCommand(
                    executable,
                    switchName,
                    argument),
                cancellationToken).ConfigureAwait(false);
            if (!send.Sent)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    send.Message,
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            if (command is PlayerCommand.Pause or PlayerCommand.Resume)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Accepted,
                    $"{send.Message}（{stopwatch.ElapsedMilliseconds} ms）。"
                    + $"命令为明确的 {command}，但标题状态不能验证暂停位。",
                    before);
            }

            var verificationWindow = TimeSpan.FromMilliseconds(1400);
            var deadline = DateTimeOffset.UtcNow + verificationWindow;
            var after = before;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                after = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (HasTrackChanged(before.Current, after.Current))
                {
                    return new PlayerOperationResult(
                        OperationOutcome.Applied,
                        $"已观察到 QQ 音乐切歌：{after.Current?.DisplayName ?? "未知歌曲"}；"
                        + $"耗时={stopwatch.ElapsedMilliseconds} ms；"
                        + $"前台未变={foregroundBefore == GetForegroundWindow()}。",
                        after);
                }
            }

            return new PlayerOperationResult(
                OperationOutcome.Accepted,
                $"{send.Message}（{stopwatch.ElapsedMilliseconds} ms）。"
                + "快速验证窗口内未观察到标题变化；后台轮询仍会继续更新状态。",
                after);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return;
        }

        Task[] softwareNextTasks;
        lock (_softwareNextSync)
        {
            softwareNextTasks = [.. _softwareNextTasks];
        }
        CancelSoftwareNext(string.Empty);
        _lifetimeCancellation.Cancel();
        if (softwareNextTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(softwareNextTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal connector shutdown.
            }
        }
        await _eventMonitor.DisposeAsync().ConfigureAwait(false);
        Task[] artworkTasks;
        lock (_trackSync)
        {
            artworkTasks = [.. _artworkTasks];
        }
        try
        {
            await Task.WhenAll(artworkTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown cancels optional artwork resolution.
        }
        catch
        {
            // Artwork is best-effort and must not block connector shutdown.
        }

        _lifetimeCancellation.Dispose();
        _catalogClient.Dispose();
        _nativeNextInsertGate.Dispose();
        _operationGate.Dispose();
    }

    private async Task<PlayerOperationResult> ExecuteInterruptSelectedAsync(
        PlayerSnapshot before,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (track is null)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "请先选择一条 QQ 搜索结果。",
                before);
        }
        if (!AllowUnsafeNativeNext)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "当前未启用经过画像校验的 QQ 原生队列能力；"
                + "为避免重建播放器队列，已拒绝插队播放。",
                before);
        }
        if (before.Current is null)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 当前歌曲不可识别，无法安全执行上一首—插入—切换事务。",
                before);
        }

        var payload = ParsePayload(track);
        if (!payload.IsPlayable)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 目录接口把该结果标记为不可播放。",
                before);
        }

        var executable = FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "找不到 QQMusic.exe，无法执行插队事务。",
                before);
        }

        Interlocked.Increment(ref _snapshotSuppressionDepth);
        using var audioMute = QQMusicAudioMuteScope.Capture();
        var muted = audioMute.Mute();
        try
        {
            // Once the transaction starts, finish its bounded native sequence
            // even if the caller's request timeout is cancelled. Exposing the
            // temporary previous-track snapshot would make the host mistake
            // its own operation for a manual user skip.
            var transactionToken = CancellationToken.None;
            CancelSoftwareNext("QQ 插队事务正在重排当前歌曲。等待最终目标后再更新守卫。");
            var pause = await Task.Run(
                () => SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'pause'",
                    helperWaitMilliseconds: 100),
                transactionToken).ConfigureAwait(false);
            if (!pause.Sent)
            {
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "QQ 插队事务未能暂停当前歌曲：" + pause.Message,
                    before);
            }

            var previous = await Task.Run(
                () => SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'prev'",
                    helperWaitMilliseconds: 100),
                transactionToken).ConfigureAwait(false);
            if (!previous.Sent)
            {
                _ = SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'play'",
                    helperWaitMilliseconds: 100);
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "QQ 插队事务未能切到当前歌曲的上一首："
                    + previous.Message,
                    before);
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            var anchor = before;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(40, transactionToken).ConfigureAwait(false);
                anchor = await ProbeAsync(transactionToken).ConfigureAwait(false);
                if (anchor.Current is not null
                    && HasTrackChanged(before.Current, anchor.Current))
                {
                    break;
                }
            }
            if (anchor.Current is null
                || !HasTrackChanged(before.Current, anchor.Current))
            {
                _ = SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'play'",
                    helperWaitMilliseconds: 100);
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "已发送上一首，但 3 秒内没有确认新的插入锚点；"
                    + "为避免在错误位置插入，事务已停止。",
                    anchor);
            }

            var result = await ExecutePlaySelectedAsync(
                anchor,
                track,
                transactionToken).ConfigureAwait(false);
            return result with
            {
                Message = "QQ 插队事务已隐藏内部上一首过渡；"
                    + (muted ? "过渡期间已静音；" : "未捕获到可静音音频会话；")
                    + result.Message
            };
        }
        finally
        {
            audioMute.Restore();
            if (Interlocked.Decrement(ref _snapshotSuppressionDepth) == 0)
            {
                _eventMonitor.NotifySnapshotInvalidated();
            }
        }
    }

    private async Task<PlayerOperationResult> ExecutePlaySelectedAsync(
        PlayerSnapshot before,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        if (track is null)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "请先选择一条 QQ 搜索结果。",
                before);
        }

        if (!AllowUnsafeNativeNext)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "当前未启用经过画像校验的 QQ 原生队列能力；"
                + "为避免重建播放器队列，已拒绝立即播放。",
                before);
        }

        var payload = ParsePayload(track);
        if (!payload.IsPlayable)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 目录接口把该结果标记为不可播放。",
                before);
        }

        var executable = FindExecutablePath();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "找不到 QQMusic.exe，无法执行暂停—插入—切换事务。",
                before);
        }

        var pause = await Task.Run(
            () => SendSingleInstanceCommand(
                executable,
                "/playcontrol",
                "'pause'",
                helperWaitMilliseconds: 100),
            cancellationToken).ConfigureAwait(false);
        if (!pause.Sent)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 立即点歌未能先暂停，已取消插入以避免漏音："
                + pause.Message,
                before);
        }

        await Task.Delay(20, cancellationToken).ConfigureAwait(false);

        CancelSoftwareNext("正在立即播放新的目标歌曲。");
        var guard = ArmSoftwareNext(before, track, cancellationToken);
        var native = await EnsureNativeNextInsertedAsync(
            track,
            payload,
            cancellationToken).ConfigureAwait(false);
        if (!native.Accepted)
        {
            CancelSoftwareNext("QQ 原生插入被画像校验拒绝。");
            _ = await Task.Run(
                () => SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'play'",
                    helperWaitMilliseconds: 100),
                cancellationToken).ConfigureAwait(false);
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 原生插入下一首被拒绝；为保护播放器原有队列，"
                + "没有回退到会重建队列的 /playbysongid。"
                + $" 验证={native.Verification}；"
                + (native.Error ?? "底层校验未通过。"),
                await ProbeAsync(cancellationToken).ConfigureAwait(false),
                native.FailureCode);
        }

        var foregroundBefore = GetForegroundWindow();
        var next = await Task.Run(
            () => SendSingleInstanceCommand(
                executable,
                "/playcontrol",
                "'next'",
                helperWaitMilliseconds: 100),
            cancellationToken).ConfigureAwait(false);
        if (!next.Sent)
        {
            _ = await Task.Run(
                () => SendSingleInstanceCommand(
                    executable,
                    "/playcontrol",
                    "'play'",
                    helperWaitMilliseconds: 100),
                cancellationToken).ConfigureAwait(false);
            return new PlayerOperationResult(
                OperationOutcome.Accepted,
                "目标已安全插入 QQ 下一首，但 next 命令发送失败；"
                + "已恢复原歌曲播放，且不会重复插入："
                + next.Message,
                await ProbeAsync(cancellationToken).ConfigureAwait(false));
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        var after = before;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            after = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (TrackMatches(after.Current, track))
            {
                _ = await Task.Run(
                    () => SendSingleInstanceCommand(
                        executable,
                        "/playcontrol",
                        "'play'",
                        helperWaitMilliseconds: 100),
                    cancellationToken).ConfigureAwait(false);
                return new PlayerOperationResult(
                    OperationOutcome.Verified,
                    $"已先暂停，{(native.InsertedNow ? "原生插入" : "复用已插入事务")}"
                    + $"并切到目标：{track.DisplayName}；"
                    + $"前台未变={foregroundBefore == GetForegroundWindow()}。",
                    after);
            }
        }

        _ = await Task.Run(
            () => SendSingleInstanceCommand(
                executable,
                "/playcontrol",
                "'play'",
                helperWaitMilliseconds: 100),
            cancellationToken).ConfigureAwait(false);

        return new PlayerOperationResult(
            guard.IsSuccess
                ? OperationOutcome.Accepted
                : OperationOutcome.Applied,
            "已先暂停、确认插入事务并发送 next，但 3 秒内未从标题确认目标；"
            + "静音守卫会继续核对，且没有重建 QQ 原有队列。",
            after);
    }

    private async Task<PlayerOperationResult> ExecuteInsertNextAsync(
        PlayerSnapshot before,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        if (!AllowUnsafeNativeNext)
        {
            return ArmSoftwareNext(before, track, cancellationToken);
        }

        if (track is null)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "请先选择一条 QQ 搜索结果。",
                before);
        }

        var guardResult =
            ArmSoftwareNext(before, track, cancellationToken);
        if (!guardResult.IsSuccess)
        {
            return guardResult;
        }
        var payload = ParsePayload(track);
        if (!payload.IsPlayable)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 目录接口把该结果标记为不可播放。",
                before);
        }

        var result = await EnsureNativeNextInsertedAsync(
            track,
            payload,
            cancellationToken).ConfigureAwait(false);
        var after = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var accepted = result.Accepted;
        if (!accepted)
        {
            CancelSoftwareNext("QQ 原生插入被画像校验拒绝。");
        }
        return new PlayerOperationResult(
            accepted
                ? result.Indeterminate
                    ? OperationOutcome.Indeterminate
                    : OperationOutcome.Accepted
                : OperationOutcome.Rejected,
            accepted
                ? result.InsertedNow
                    ? result.Indeterminate
                        ? $"QQ 原生 AddSongs 可能已完成，songID={payload.SongId}；"
                          + "为防止重复歌曲，本次按不确定成功记账且禁止自动重插。"
                        : $"QQ 已按精确版本画像提交原生下一首，songID={payload.SongId}；"
                          + "当前歌曲未变化；静音防漏音守卫同时待命。"
                    : $"QQ 下一首事务已包含 songID={payload.SongId}；"
                      + "本次没有重复插入，静音防漏音守卫继续待命。"
                : $"QQ 原生下一首被拒绝：{result.Verification}；"
                  + (result.Error ?? "底层校验未通过。")
                  + " 未回退到会重建队列的播放命令。",
            after,
            result.FailureCode);
    }

    private static bool IsNativeInsertAccepted(
        QQMusicNativeNextResult result,
        long expectedSongId) =>
        result.Verification
            == "NativeNextInsertedCurrentTrackUnchangedPendingNextVerification"
        && result.NativeStage == 5
        && result.GetCatManagerHresult >= 0
        && result.GetSongInfoHresult >= 0
        && result.AddSongsHresult >= 0
        && result.ResolvedSongId == expectedSongId;

    private PlayerOperationResult ArmSoftwareNext(
        PlayerSnapshot before,
        PlayerTrack? track,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposing) != 0)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 连接器正在关闭，未登记新的下一首守卫。",
                before);
        }
        if (track is null)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "请先选择一条 QQ 搜索结果。",
                before);
        }

        var payload = ParsePayload(track);
        if (!payload.IsPlayable)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "QQ 目录接口把该结果标记为不可播放。",
                before);
        }

        var currentState = QQMusicNativeController.ReadPlaybackState();
        var anchor = EvaluatePlaybackAnchor(currentState);
        if (!anchor.IsReliable)
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                anchor.Message ?? QQMusicPlaybackAnchorPolicy.MissingMessage,
                before,
                anchor.FailureCode);
        }
        var currentKey = BuildPlaybackKey(currentState);
        if (string.IsNullOrWhiteSpace(currentKey))
        {
            return new PlayerOperationResult(
                OperationOutcome.Rejected,
                "当前 QQ 窗口标题没有可识别歌曲，无法可靠监测下一次切歌。",
                before,
                QQMusicPlaybackAnchorPolicy.MissingFailureCode);
        }

        var initialAnchor = ResolveKnownTrack(
                currentState.Title,
                currentState.Artist)
            ?? before.Current
            ?? new PlayerTrack(
                string.Empty,
                currentState.Title!,
                currentState.Artist ?? string.Empty,
                string.Empty);

        var pendingCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
        CancellationTokenSource? previousCancellation;
        long guardId;
        lock (_softwareNextSync)
        {
            if (Volatile.Read(ref _disposing) != 0)
            {
                pendingCancellation.Dispose();
                return new PlayerOperationResult(
                    OperationOutcome.Rejected,
                    "QQ 连接器正在关闭，未登记新的下一首守卫。",
                    before);
            }
            previousCancellation = _softwareNextCancellation;
            _softwareNextCancellation = pendingCancellation;
            _softwareNextTarget = track;
            _lateRecoveryTarget = null;
            _softwareNextState = NextGuardState.Armed;
            _softwareNextGeneration++;
            _softwareNextGuardIdSequence++;
            if (_softwareNextGuardIdSequence <= 0)
            {
                _softwareNextGuardIdSequence = 1;
            }
            _softwareNextGuardId = _softwareNextGuardIdSequence;
            guardId = _softwareNextGuardId;
            _softwareNextStatus = $"下一首静音防漏音守卫待命：{track.DisplayName}";
            var task = Task.Run(
                () => MonitorSoftwareNextEventsAsync(
                    pendingCancellation,
                    currentState.WindowTitle!,
                    initialAnchor,
                    track,
                    payload));
            _softwareNextTask = task;
            _softwareNextTasks.Add(task);
            _ = task.ContinueWith(
                completedTask =>
                {
                    lock (_softwareNextSync)
                    {
                        _softwareNextTasks.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        if (previousCancellation is not null)
        {
            previousCancellation.Cancel();
        }

        _eventMonitor.NotifySnapshotInvalidated();

        var armedSnapshot = before with
        {
            Next = track,
            NextSource = "qq-logical-guard",
            NextObservation = "track",
            NextGuardState = NextGuardState.Armed,
            NextGuardId = guardId
        };

        return new PlayerOperationResult(
            OperationOutcome.Accepted,
            $"已登记静音防漏音下一首：{track.DisplayName}。"
            + "已订阅 Windows 媒体会话与 WinEventHook；发生切换时会先静音 QQ，"
            + "若歌曲错误会在静音中暂停、原生插入目标，再切到下一首。",
            armedSnapshot);
    }

    private async Task MonitorSoftwareNextEventsAsync(
        CancellationTokenSource owner,
        string initialWindowTitle,
        PlayerTrack initialAnchor,
        PlayerTrack track,
        QqTrackPayload payload)
    {
        var cancellationToken = owner.Token;
        using var audioMute = QQMusicAudioMuteScope.Capture();
        await using var subscription = _eventMonitor.Subscribe();
        _ = _eventMonitor.EnsureStartedAsync();
        CancellationTokenSource? preMuteTimerCancellation = null;
        var preserveLateRecoveryTarget = false;
        try
        {
            var baselineWindowTitle = initialWindowTitle;
            var transitionDetected = false;
            var preMuted = false;
            var preMuteSuppressedUntil = DateTimeOffset.MinValue;
            var takeoverAttempted = false;
            Task? transitionObservationTimeout = null;
            Task? preMuteRestoreTimeout = null;
            Task? preMuteTimer = null;
            var expiration = Task.Delay(
                TimeSpan.FromHours(12),
                cancellationToken);
            var eventTask = subscription.Reader
                .ReadAsync(cancellationToken)
                .AsTask();

            void CancelPreMuteTimer()
            {
                preMuteTimerCancellation?.Cancel();
                preMuteTimerCancellation?.Dispose();
                preMuteTimerCancellation = null;
                preMuteTimer = null;
            }

            void SchedulePreMuteTimer()
            {
                CancelPreMuteTimer();
                if (preMuted || transitionDetected)
                {
                    return;
                }

                var timeline = _eventMonitor.ReadTimelineSnapshot();
                if (timeline is null
                    || !timeline.PlaybackStatus.Equals(
                        "Playing",
                        StringComparison.Ordinal)
                    || timeline.EndTime <= timeline.StartTime
                    || timeline.ReportedPosition < timeline.StartTime
                    || timeline.ReportedPosition > timeline.EndTime)
                {
                    return;
                }

                var due = timeline.EstimatedRemaining
                    - NaturalEndPreMuteLead;
                if (due < TimeSpan.Zero)
                {
                    if (timeline.EstimatedRemaining
                        < TimeSpan.FromSeconds(-1))
                    {
                        return;
                    }
                    due = TimeSpan.Zero;
                }

                var suppressionDelay =
                    preMuteSuppressedUntil - DateTimeOffset.UtcNow;
                if (suppressionDelay > due)
                {
                    due = suppressionDelay;
                }
                if (due > TimeSpan.FromHours(12))
                {
                    return;
                }

                preMuteTimerCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                preMuteTimer = Task.Delay(
                    due < TimeSpan.Zero ? TimeSpan.Zero : due,
                    preMuteTimerCancellation.Token);
            }

            SchedulePreMuteTimer();

            while (true)
            {
                var waiters = new List<Task>
                {
                    eventTask,
                    expiration
                };
                if (preMuteTimer is not null)
                {
                    waiters.Add(preMuteTimer);
                }
                if (preMuteRestoreTimeout is not null)
                {
                    waiters.Add(preMuteRestoreTimeout);
                }
                if (transitionObservationTimeout is not null)
                {
                    waiters.Add(transitionObservationTimeout);
                }

                var completed = await Task.WhenAny(waiters)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (ReferenceEquals(completed, expiration))
                {
                    SetSoftwareNextTerminalState(
                        owner,
                        NextGuardState.Expired,
                        "软件下一首已过期（12 小时未发生切歌）。");
                    return;
                }

                if (preMuteTimer is not null
                    && ReferenceEquals(completed, preMuteTimer))
                {
                    preMuteTimerCancellation?.Dispose();
                    preMuteTimerCancellation = null;
                    preMuteTimer = null;
                    var timeline = _eventMonitor.ReadTimelineSnapshot();
                    var remaining = timeline?.EstimatedRemaining
                        ?? TimeSpan.MaxValue;
                    if (timeline is not null
                        && timeline.PlaybackStatus.Equals(
                            "Playing",
                            StringComparison.Ordinal)
                        && remaining <= NaturalEndPreMuteLead
                        && remaining >= TimeSpan.FromSeconds(-1)
                        && audioMute.Mute())
                    {
                        preMuted = true;
                        preMuteRestoreTimeout = Task.Delay(
                            TimeSpan.FromSeconds(2),
                            cancellationToken);
                        SetSoftwareNextStatus(
                            owner,
                            "QQ 即将自然切歌，已按媒体时间线的一次性定时器提前静音"
                            + $"（预计剩余 {Math.Max(0, remaining.TotalMilliseconds):F0} ms）。");
                    }
                    else
                    {
                        SchedulePreMuteTimer();
                    }
                    continue;
                }

                if (preMuteRestoreTimeout is not null
                    && ReferenceEquals(completed, preMuteRestoreTimeout))
                {
                    preMuteRestoreTimeout = null;
                    if (preMuted && !transitionDetected)
                    {
                        audioMute.Restore();
                        preMuted = false;
                        preMuteSuppressedUntil =
                            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
                        SetSoftwareNextStatus(
                            owner,
                            "QQ 时间线预静音后 2 秒内未发生切歌，"
                            + "已恢复原静音状态并继续等待事件。");
                    }
                    SchedulePreMuteTimer();
                    continue;
                }

                if (transitionObservationTimeout is not null
                    && ReferenceEquals(
                        completed,
                        transitionObservationTimeout))
                {
                    transitionObservationTimeout = null;
                    audioMute.Restore();
                    preMuted = false;
                    transitionDetected = false;
                    SetSoftwareNextStatus(
                        owner,
                        "收到 QQ 切换信号但 2 秒内没有可识别歌曲事件；"
                        + "已恢复原静音状态并继续等待事件。");
                    SchedulePreMuteTimer();
                    continue;
                }

                var playerEvent = await eventTask.ConfigureAwait(false);
                eventTask = subscription.Reader
                    .ReadAsync(cancellationToken)
                    .AsTask();
                while (subscription.Reader.TryRead(out var additionalEvent))
                {
                    playerEvent = additionalEvent;
                }

                if (playerEvent.Kind is QQMusicEventKind.Initialized
                    or QQMusicEventKind.SessionsChanged
                    or QQMusicEventKind.MediaPropertiesChanged
                    or QQMusicEventKind.PlaybackInfoChanged
                    or QQMusicEventKind.TimelinePropertiesChanged)
                {
                    SchedulePreMuteTimer();
                }

                PlayerTrack? observedTrack = null;
                var observedWindowTitle = string.Empty;
                if (playerEvent.Kind == QQMusicEventKind.WindowTitleChanged)
                {
                    observedWindowTitle = playerEvent.WindowTitle;
                    observedTrack = ParseQqWindowTrack(observedWindowTitle);
                }
                else if (playerEvent.Kind
                         == QQMusicEventKind.MediaPropertiesChanged)
                {
                    var mediaTrack = _eventMonitor.ReadMediaTrack();
                    var windowTrack = ParseQqWindowTrack(
                        QQMusicNativeController
                            .ReadPlaybackState()
                            .WindowTitle);
                    if (CurrentMetadataRepresentsSameSong(
                            windowTrack,
                            mediaTrack))
                    {
                        observedTrack = mediaTrack;
                    }
                }

                if (playerEvent.Kind == QQMusicEventKind.WindowTitleChanged
                    && !string.Equals(
                        observedWindowTitle,
                        baselineWindowTitle,
                        StringComparison.Ordinal)
                    && !(observedTrack is not null
                        && ObservationMatches(initialAnchor, observedTrack))
                    && !transitionDetected)
                {
                    CancelPreMuteTimer();
                    transitionDetected = true;
                    transitionObservationTimeout = Task.Delay(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);
                    var muted = preMuted || audioMute.Mute();
                    preMuted = muted;
                    SetSoftwareNextStatus(
                        owner,
                        muted
                            ? $"WinEventHook 检测到 QQ 标题切换，已静音 "
                              + $"{audioMute.CapturedSessionCount} 个音频会话。"
                            : "WinEventHook 检测到 QQ 标题切换，但没有捕获到可静音的 QQ 音频会话；"
                              + "将继续用 pause 接管。");
                }

                if (observedTrack is null)
                {
                    continue;
                }

                if (ObservationMatches(initialAnchor, observedTrack))
                {
                    if (playerEvent.Kind
                        == QQMusicEventKind.WindowTitleChanged)
                    {
                        baselineWindowTitle = observedWindowTitle;
                    }
                    if (transitionDetected)
                    {
                        audioMute.Restore();
                        preMuted = false;
                        preMuteRestoreTimeout = null;
                        transitionObservationTimeout = null;
                        transitionDetected = false;
                        SchedulePreMuteTimer();
                    }
                    continue;
                }

                if (!transitionDetected)
                {
                    CancelPreMuteTimer();
                    transitionDetected = true;
                    transitionObservationTimeout = Task.Delay(
                        TimeSpan.FromSeconds(2),
                        cancellationToken);
                    var muted = preMuted || audioMute.Mute();
                    preMuted = muted;
                    SetSoftwareNextStatus(
                        owner,
                        muted
                            ? $"Windows 媒体会话检测到 QQ 切歌，已静音 "
                              + $"{audioMute.CapturedSessionCount} 个音频会话。"
                            : "Windows 媒体会话检测到 QQ 切歌，但没有捕获到可静音的 QQ 音频会话；"
                              + "将继续用 pause 接管。");
                }

                if (TrackMatches(observedTrack, track))
                {
                    ClearPendingNativeNextIfPlaying(track);
                    audioMute.Restore();
                    SetSoftwareNextTerminalState(
                        owner,
                        NextGuardState.Completed,
                        $"下一首已正确命中：{track.DisplayName}");
                    return;
                }

                var executable = FindExecutablePath();
                if (string.IsNullOrWhiteSpace(executable))
                {
                    audioMute.Restore();
                    SetSoftwareNextTerminalState(
                        owner,
                        NextGuardState.TerminalFailure,
                        "软件下一首失败：未找到 QQMusic.exe");
                    return;
                }

                var recoveryAction = QQMusicWrongNextRecoveryPolicy.Decide(
                    takeoverAttempted);
                if (recoveryAction == QQMusicWrongNextRecoveryAction.Stop)
                {
                    audioMute.Restore();
                    SetSoftwareNextTerminalState(
                        owner,
                        NextGuardState.TerminalFailure,
                        "错误下一首已停止自动切歌："
                        + "队列保留接管已经尝试过，不再发送新的 Next。"
                        + "已恢复原静音状态。");
                    return;
                }

                takeoverAttempted = true;
                SetSoftwareNextStatus(
                    owner,
                    $"检测到错误下一首：{observedTrack.DisplayName}；"
                    + "QQ 音频已静音，正在执行唯一一次队列保留接管。");
                var recovery =
                    await ExecuteQueuePreservingWrongNextRecoveryAsync(
                        owner,
                        executable,
                        initialAnchor,
                        observedTrack,
                        track,
                        payload,
                        audioMute,
                        cancellationToken).ConfigureAwait(false);
                preserveLateRecoveryTarget =
                    recovery.LateRecoveryTargetRecorded;
                if (!recovery.LateRecoveryTargetRecorded)
                {
                    SetSoftwareNextTerminalState(
                        owner,
                        recovery.Verified
                            ? NextGuardState.Completed
                            : NextGuardState.TerminalFailure,
                        recovery.Message);
                }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced, manually cancelled, or the application is closing.
        }
        catch (Exception exception)
        {
            SetSoftwareNextTerminalState(
                owner,
                NextGuardState.TerminalFailure,
                $"软件下一首事件守卫异常：{exception.Message}");
        }
        finally
        {
            audioMute.Restore();
            var ownerWasCanceled = owner.IsCancellationRequested;
            owner.Cancel();
            preMuteTimerCancellation?.Cancel();
            preMuteTimerCancellation?.Dispose();
            lock (_softwareNextSync)
            {
                if (ReferenceEquals(_softwareNextCancellation, owner))
                {
                    _softwareNextCancellation = null;
                    _softwareNextTask = null;
                    if (_softwareNextState is not NextGuardState.WaitingLateTarget
                        and not NextGuardState.TerminalFailure)
                    {
                        _softwareNextTarget = null;
                    }
                    if (!preserveLateRecoveryTarget || ownerWasCanceled)
                    {
                        _lateRecoveryTarget = null;
                    }
                }
            }
            // Publish the final state after the monitor has released its
            // owner and cleared its active task. This keeps a terminal event
            // from racing the host while connector guard work is in flight,
            // and also exposes target removal to the host immediately.
            _eventMonitor.NotifySnapshotInvalidated();
            owner.Dispose();
        }
    }

    private async Task<WrongNextRecoveryResult>
        ExecuteQueuePreservingWrongNextRecoveryAsync(
            CancellationTokenSource owner,
            string executable,
            PlayerTrack initialAnchor,
            PlayerTrack observedWrongTrack,
            PlayerTrack track,
            QqTrackPayload payload,
            QQMusicAudioMuteScope audioMute,
            CancellationToken cancellationToken)
    {
        var operationAcquired = false;
        var snapshotsSuppressed = false;
        try
        {
            // The event guard normally runs outside ExecuteAsync. Hold the
            // same gate while repairing the queue so an ordinary Previous or
            // Next command cannot interleave with the anchor reset.
            await _operationGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            operationAcquired = true;
            Interlocked.Increment(ref _snapshotSuppressionDepth);
            snapshotsSuppressed = true;

            var before = await ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (TrackMatches(before.Current, track))
            {
                return new WrongNextRecoveryResult(
                    true,
                    $"错误下一首期间目标已自行命中：{track.DisplayName}；"
                    + "已恢复原静音状态。",
                    before);
            }

            if (!ObservationMatches(before.Current, observedWrongTrack))
            {
                return new WrongNextRecoveryResult(
                    false,
                    "错误下一首期间 QQ 当前歌曲已经被其他操作改变；"
                    + "为避免破坏队列，未发送 Previous/Next，"
                    + "已恢复原静音状态。",
                    before);
            }

            var previous = await Task.Run(
                    () => SendSingleInstanceCommand(
                        executable,
                        "/playcontrol",
                        "'prev'",
                        helperWaitMilliseconds: 100),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!previous.Sent)
            {
                return new WrongNextRecoveryResult(
                    false,
                    "错误下一首已静音，但 Previous 发送失败；"
                    + previous.Message
                    + "，已恢复原静音状态。",
                    before);
            }

            var anchor = await WaitForPlaybackAnchorAsync(
                    initialAnchor,
                    observedWrongTrack,
                    cancellationToken)
                .ConfigureAwait(false);
            if (anchor is null)
            {
                var latest = await ProbeAsync(cancellationToken)
                    .ConfigureAwait(false);
                var lateRecoveryStatus =
                    "已发送 Previous，但未确认回到原播放锚点；"
                    + "接管已停止，等待目标迟到到达后仅更新状态，"
                    + "不再发送播放或切歌命令。";
                var lateRecoveryTargetRecorded =
                    TryMarkLateRecoveryTarget(
                        owner,
                        track,
                        lateRecoveryStatus);
                return new WrongNextRecoveryResult(
                    false,
                    "已发送 Previous，但未确认回到原播放锚点；"
                    + "为避免继续向下切歌，接管已停止，"
                    + "已恢复原静音状态。",
                    latest,
                    lateRecoveryTargetRecorded);
            }

            var pause = await Task.Run(
                    () => SendSingleInstanceCommand(
                        executable,
                        "/playcontrol",
                        "'pause'",
                        helperWaitMilliseconds: 100),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!pause.Sent)
            {
                return new WrongNextRecoveryResult(
                    false,
                    "已回到原播放锚点，但暂停发送失败；"
                    + pause.Message
                    + "，未插入目标，已恢复原静音状态。",
                    anchor);
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            var native = await EnsureNativeNextInsertedAsync(
                    track,
                    payload,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!native.Accepted)
            {
                return new WrongNextRecoveryResult(
                    false,
                    "已回到原播放锚点并暂停，但原生目标插入被拒绝；"
                    + "为保护 QQ 原队列，没有使用 /playbysongid。"
                    + $" 验证={native.Verification}；已恢复原静音状态。",
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            // This is the only Next emitted by wrong-track recovery. There is
            // deliberately no retry timer: a failed or unverified takeover
            // must stop instead of walking down the user's existing queue.
            var next = await Task.Run(
                    () => SendSingleInstanceCommand(
                        executable,
                        "/playcontrol",
                        "'next'",
                        helperWaitMilliseconds: 100),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!next.Sent)
            {
                return new WrongNextRecoveryResult(
                    false,
                    "目标已重新插入 QQ 下一首，但唯一一次 Next 发送失败；"
                    + next.Message
                    + "，已恢复原静音状态。",
                    await ProbeAsync(cancellationToken).ConfigureAwait(false));
            }

            var target = await WaitForTargetAsync(
                    track,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is not null)
            {
                ClearPendingNativeNextIfPlaying(track);
                var play = await Task.Run(
                        () => SendSingleInstanceCommand(
                            executable,
                            "/playcontrol",
                            "'play'",
                            helperWaitMilliseconds: 100),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!play.Sent)
                {
                    return new WrongNextRecoveryResult(
                        false,
                        $"已确认目标：{track.DisplayName}，但恢复播放失败；"
                        + play.Message
                        + "，已恢复原静音状态。",
                        target);
                }

                return new WrongNextRecoveryResult(
                    true,
                    $"已通过队列保留接管回到原锚点，重新插入并仅发送一次 Next；"
                    + $"已确认目标：{track.DisplayName}；已恢复原静音状态。",
                    target);
            }

            return new WrongNextRecoveryResult(
                false,
                "已回到原锚点、重新插入目标并仅发送一次 Next，"
                + "但未在 3 秒内确认目标；已停止自动切歌，"
                + "已恢复原静音状态。",
                await ProbeAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            // Every terminal path, including cancellation or an unexpected
            // native exception, must release the captured audio state.
            audioMute.Restore();
            if (snapshotsSuppressed
                && Interlocked.Decrement(ref _snapshotSuppressionDepth) == 0)
            {
                _eventMonitor.NotifySnapshotInvalidated();
            }
            if (operationAcquired)
            {
                _operationGate.Release();
            }
        }
    }

    private async Task<PlayerSnapshot?> WaitForPlaybackAnchorAsync(
        PlayerTrack expectedAnchor,
        PlayerTrack previousWrongTrack,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        PlayerSnapshot latest = await ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ObservationMatches(latest.Current, expectedAnchor)
                && !ObservationMatches(latest.Current, previousWrongTrack))
            {
                return latest;
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            latest = await ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private async Task<PlayerSnapshot?> WaitForTargetAsync(
        PlayerTrack target,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        PlayerSnapshot latest = await ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TrackMatches(latest.Current, target))
            {
                return latest;
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            latest = await ProbeAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }


    private static PlayerTrack? ParseQqWindowTrack(
        string? windowTitle)
    {
        var parsed = QQMusicWindowTitleParser.Parse(windowTitle);

        return parsed is null
            ? null
            : new PlayerTrack(
                string.Empty,
                parsed.Title,
                parsed.Artist,
                string.Empty);
    }

    private QQMusicPlaybackAnchorDecision EvaluatePlaybackAnchor(
        QQMusicPlaybackState state)
    {
        var processId = state.WindowHandle is null
            ? null
            : FindProcessId(state.WindowHandle.Value);
        ObserveNativeSession(processId);
        var hasActiveAudioSession = false;
        if (processId is not null)
        {
            using var audio = QQMusicAudioMuteScope.Capture(processId.Value);
            hasActiveAudioSession = audio.HasActiveAudioSession;
        }
        var timeline = _eventMonitor.ReadTimelineSnapshot();
        var evidence = timeline is null
            ? null
            : new QQMusicTimelineEvidence(
                timeline.PlaybackStatus,
                timeline.StartTime,
                timeline.EndTime,
                timeline.ReportedPosition);
        lock (_nativeNextSync)
        {
            var decision = QQMusicPlaybackAnchorPolicy.Evaluate(
                state,
                evidence,
                hasActiveAudioSession,
                _sessionObservedPlaying);
            if (decision.ObservedPlaying)
            {
                _sessionObservedPlaying = true;
            }

            return decision;
        }
    }

    private void SetSoftwareNextStatus(
        CancellationTokenSource owner,
        string status)
    {
        lock (_softwareNextSync)
        {
            if (ReferenceEquals(_softwareNextCancellation, owner))
            {
                _softwareNextStatus = status;
            }
        }
        _eventMonitor.NotifySnapshotInvalidated();
    }

    private void SetSoftwareNextTerminalState(
        CancellationTokenSource owner,
        NextGuardState state,
        string status)
    {
        lock (_softwareNextSync)
        {
            if (ReferenceEquals(_softwareNextCancellation, owner))
            {
                _softwareNextState = state;
                _softwareNextStatus = status;
            }
        }
    }

    private void CancelSoftwareNext(string status)
    {
        CancellationTokenSource? cancellation;
        lock (_softwareNextSync)
        {
            cancellation = _softwareNextCancellation;
            _softwareNextCancellation = null;
            _softwareNextTask = null;
            _softwareNextTarget = null;
            _lateRecoveryTarget = null;
            _softwareNextState = NextGuardState.None;
            _softwareNextGeneration++;
            _softwareNextGuardId = 0;
            _softwareNextStatus = status;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }
        _eventMonitor.NotifySnapshotInvalidated();
    }

    private static string BuildPlaybackKey(QQMusicPlaybackState state)
    {
        if (!state.IsRunning || string.IsNullOrWhiteSpace(state.Title))
        {
            return string.Empty;
        }

        return $"{Normalize(state.Title)}|{Normalize(state.Artist ?? string.Empty)}";
    }

    private PlayerTrack? ResolveKnownTrack(
        string? title,
        string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        lock (_trackSync)
        {
            var lookupKey = BuildTrackLookupKey(
                title,
                artist ?? string.Empty);
            if (_resolvedCurrentTracks.TryGetValue(
                    lookupKey,
                    out var resolved))
            {
                return resolved;
            }

            var exactMatches = _knownTracks.Values
                .Where(track =>
                    Normalize(track.Title) == Normalize(title)
                    && (string.IsNullOrWhiteSpace(artist)
                        || Normalize(track.Artist) == Normalize(artist)))
                .ToArray();
            if (exactMatches.Length == 1)
            {
                return exactMatches[0];
            }

            var aliasMatches = _knownTracks.Values
                .Where(track => QQMusicTrackMatchPolicy
                    .MetadataRepresentsSameSong(
                        track.Title,
                        track.Artist,
                        title,
                        artist))
                .ToArray();
            return aliasMatches.Length == 1 ? aliasMatches[0] : null;
        }
    }

    private void RememberTrack(PlayerTrack track)
    {
        lock (_trackSync)
        {
            _resolvedCurrentTracks[
                BuildTrackLookupKey(track.Title, track.Artist)] = track;
            try
            {
                var payload = ParsePayload(track);
                _knownTracks[(payload.SongId, payload.SongType)] = track;
            }
            catch
            {
                // Tracks observed from the QQ window may not contain catalog ids.
            }
            TrimTrackCachesLocked();
        }
    }

    private void TrimTrackCachesLocked()
    {
        while (_knownTracks.Count > 1024)
        {
            _knownTracks.Remove(_knownTracks.Keys.First());
        }
        while (_resolvedCurrentTracks.Count > 1024)
        {
            _resolvedCurrentTracks.Remove(
                _resolvedCurrentTracks.Keys.First());
        }
    }

    private void ScheduleArtworkLookup(PlayerTrack current)
    {
        var lookupKey = BuildTrackLookupKey(current.Title, current.Artist);
        lock (_trackSync)
        {
            if (!_artworkLookups.Add(lookupKey))
            {
                return;
            }

            _artworkLookupOrder.Enqueue(lookupKey);
            while (_artworkLookupOrder.Count > 512)
            {
                _artworkLookups.Remove(_artworkLookupOrder.Dequeue());
            }

            var task = ResolveArtworkAsync(
                lookupKey,
                current,
                _lifetimeCancellation.Token);
            _artworkTasks.Add(task);
            _ = task.ContinueWith(
                completed =>
                {
                    lock (_trackSync)
                    {
                        _artworkTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ResolveArtworkAsync(
        string lookupKey,
        PlayerTrack current,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(current.Artist)
                ? current.Title
                : $"{current.Title} {current.Artist}";
            var songs = await _catalogClient.SearchAsync(
                query,
                count: 12,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var normalizedTitle = Normalize(current.Title);
            var normalizedArtist = Normalize(current.Artist);
            var match = songs
                .Where(song => Normalize(song.Title) == normalizedTitle)
                .OrderByDescending(song =>
                    string.IsNullOrWhiteSpace(normalizedArtist)
                    || Normalize(song.Artist).Contains(
                        normalizedArtist,
                        StringComparison.Ordinal)
                    || normalizedArtist.Contains(
                        Normalize(song.Artist),
                        StringComparison.Ordinal))
                .ThenByDescending(song =>
                    !string.IsNullOrWhiteSpace(song.AlbumMid))
                .FirstOrDefault();
            if (match is null)
            {
                return;
            }

            var track = CreateTrack(match) with { Id = current.Id };
            lock (_trackSync)
            {
                _knownTracks[(match.SongId, match.SongType)] = track;
                _resolvedCurrentTracks[lookupKey] = track;
                TrimTrackCachesLocked();
            }
            _eventMonitor.NotifySnapshotInvalidated();
        }
        catch (OperationCanceledException)
        {
            // Connector shutdown does not affect playback state.
        }
        catch
        {
            // Missing artwork is non-fatal; title monitoring still works.
        }
    }

    private static PlayerTrack CreateTrack(QQMusicCatalogSong song)
    {
        var nativeData = JsonSerializer.Serialize(new QqTrackPayload(
            song.SongId,
            song.SongType,
            song.SongMid,
            song.IsPlayable));
        return new PlayerTrack(
            song.SongId.ToString(),
            song.Title,
            song.Artist,
            song.Album,
            nativeData,
            QQMusicAlbumArtwork.BuildCoverUrl(song.AlbumMid));
    }

    private static string BuildTrackLookupKey(
        string title,
        string artist)
    {
        return $"{Normalize(title)}|{Normalize(artist)}";
    }

    private static QqTrackPayload ParsePayload(PlayerTrack track)
    {
        return JsonSerializer.Deserialize<QqTrackPayload>(track.NativeData)
            ?? throw new InvalidDataException("QQ 搜索结果缺少原生 songID 数据。");
    }

    private async Task<NativeNextEnsureResult> EnsureNativeNextInsertedAsync(
        PlayerTrack track,
        QqTrackPayload payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _nativeNextInsertGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var currentState = QQMusicNativeController.ReadPlaybackState();
            var anchor = EvaluatePlaybackAnchor(currentState);
            if (!anchor.IsReliable)
            {
                return new NativeNextEnsureResult(
                    false,
                    false,
                    false,
                    "QQPlaybackAnchorMissing",
                    anchor.Message,
                    anchor.FailureCode);
            }
            var anchorProcessId = currentState.WindowHandle is null
                ? null
                : FindProcessId(currentState.WindowHandle.Value);
            if (anchorProcessId is null)
            {
                return new NativeNextEnsureResult(
                    false,
                    false,
                    false,
                    "QQPlaybackAnchorMissing",
                    QQMusicPlaybackAnchorPolicy.MissingMessage,
                    QQMusicPlaybackAnchorPolicy.MissingFailureCode);
            }

            long insertedAtSequence;
            int? nativeSessionProcessId;
            lock (_nativeNextSync)
            {
                PrunePendingNativeNextLocked(DateTimeOffset.UtcNow);
                if (_pendingNativeNext.Any(pending =>
                        QQMusicWrongNextRecoveryPolicy
                            .ShouldReusePendingNativeNext(
                                pending.InsertedAtSequence,
                                _observedTrackSequence,
                                pending.ProcessId,
                                _nativeSessionProcessId ?? 0,
                                pending.Payload.SongId,
                                pending.Payload.SongType,
                                payload.SongId,
                                payload.SongType)))
                {
                    return new NativeNextEnsureResult(
                        true,
                        false,
                        false,
                        "PendingNativeNextAlreadyInserted",
                        null,
                        null);
                }
                insertedAtSequence = _observedTrackSequence;
                nativeSessionProcessId = _nativeSessionProcessId;
            }

            // Native AddSongs is not cancellable. Once this adapter-level gate
            // is acquired, finish the mutation and ledger update before
            // releasing it; otherwise a timed-out caller can start a duplicate
            // insertion while the first native task is still running.
            var result = await QQMusicNativeNextTransport.InsertAsync(
                    new QQMusicSongReference(
                        payload.SongId,
                        payload.SongType),
                    anchorProcessId.Value,
                    TimeSpan.FromSeconds(6))
                .ConfigureAwait(false);
            var verified = IsNativeInsertAccepted(result, payload.SongId);
            var sideEffectPossible = !verified
                && result.NativeStage >= 4
                && result.AddSongsHresult >= 0
                && result.ResolvedSongId == payload.SongId;
            var sessionMatches = nativeSessionProcessId is not null
                && result.TargetProcessId == nativeSessionProcessId.Value;
            var accepted = (verified || sideEffectPossible) && sessionMatches;
            if (accepted)
            {
                lock (_nativeNextSync)
                {
                    PrunePendingNativeNextLocked(DateTimeOffset.UtcNow);
                    _pendingNativeNext.RemoveAll(pending =>
                        pending.Payload.SongId == payload.SongId
                        && pending.Payload.SongType == payload.SongType);
                    _pendingNativeNext.Insert(
                        0,
                        new PendingQqNativeNext(
                            track,
                            payload,
                            DateTimeOffset.UtcNow,
                            insertedAtSequence,
                            result.TargetProcessId,
                            sideEffectPossible));
                }
                _eventMonitor.NotifySnapshotInvalidated();
            }

            return new NativeNextEnsureResult(
                accepted,
                true,
                sideEffectPossible,
                !sessionMatches
                    ? "QQMusicProcessChangedDuringNativeInsert"
                    : sideEffectPossible
                    ? "NativeAddSongsMayHaveCompleted;DuplicateRetrySuppressed"
                    : result.Verification,
                result.Error,
                result.FailureCode);
        }
        finally
        {
            _nativeNextInsertGate.Release();
        }
    }

    private void ClearPendingNativeNextIfPlaying(PlayerTrack? current)
    {
        if (current is null)
        {
            return;
        }

        var changed = false;
        lock (_nativeNextSync)
        {
            if (_lastObservedTrack is null
                || !ObservationMatches(_lastObservedTrack, current))
            {
                _lastObservedTrack = current;
                _observedTrackSequence++;
            }

            changed |= PrunePendingNativeNextLocked(DateTimeOffset.UtcNow);
            var matchingIndex = _pendingNativeNext.FindIndex(pending =>
                _observedTrackSequence > pending.InsertedAtSequence
                && TrackMatches(current, pending.Target));
            if (matchingIndex >= 0)
            {
                _pendingNativeNext.RemoveRange(0, matchingIndex + 1);
                changed = true;
            }
        }
        var lateRecoveryConfirmed =
            ConfirmLateRecoveryTargetIfPlaying(current);
        if (changed || lateRecoveryConfirmed)
        {
            _eventMonitor.NotifySnapshotInvalidated();
        }
    }

    private bool TryMarkLateRecoveryTarget(
        CancellationTokenSource owner,
        PlayerTrack target,
        string status)
    {
        var shouldScheduleGrace = false;
        var generation = 0L;
        lock (_softwareNextSync)
        {
            if (!ReferenceEquals(_softwareNextCancellation, owner))
            {
                return false;
            }

            _lateRecoveryTarget = target;
            _softwareNextTarget = target;
            _softwareNextState = NextGuardState.WaitingLateTarget;
            _softwareNextGeneration++;
            generation = _softwareNextGeneration;
            _softwareNextStatus = status;
            shouldScheduleGrace = true;
        }

        if (shouldScheduleGrace)
        {
            ScheduleLateRecoveryGrace(target, generation);
        }
        return true;
    }

    private bool ConfirmLateRecoveryTargetIfPlaying(PlayerTrack current)
    {
        var confirmed = false;
        lock (_softwareNextSync)
        {
            if (_lateRecoveryTarget is null
                || !TrackMatches(current, _lateRecoveryTarget))
            {
                return false;
            }

            _softwareNextStatus =
                $"目标延迟到达，已确认：{current.DisplayName}；"
                + "未重复发送播放或切歌命令。";
            _lateRecoveryTarget = null;
            _softwareNextTarget = null;
            _softwareNextState = NextGuardState.Completed;
            _softwareNextGeneration++;
            confirmed = true;
        }
        if (confirmed)
        {
            _eventMonitor.NotifySnapshotInvalidated();
        }
        return confirmed;
    }

    private void ScheduleLateRecoveryGrace(
        PlayerTrack target,
        long generation)
    {
        var task = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(
                            QQMusicWrongNextRecoveryPolicy
                                .LateRecoveryGracePeriod,
                            _lifetimeCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var terminal = false;
                lock (_softwareNextSync)
                {
                    if (_softwareNextGeneration == generation
                        && ReferenceEquals(_lateRecoveryTarget, target)
                        && ReferenceEquals(_softwareNextTarget, target)
                        && _softwareNextState
                            == NextGuardState.WaitingLateTarget)
                    {
                        _softwareNextState = NextGuardState.TerminalFailure;
                        _softwareNextStatus =
                            "上一首锚点未确认且迟到目标宽限期已结束；"
                            + "守卫已终止，等待宿主执行一次队列保留兜底。";
                        terminal = true;
                    }
                }

                if (terminal)
                {
                    _eventMonitor.NotifySnapshotInvalidated();
                }
            },
            _lifetimeCancellation.Token);

        lock (_softwareNextSync)
        {
            _softwareNextTasks.Add(task);
        }
        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_softwareNextSync)
                {
                    _softwareNextTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool PrunePendingNativeNextLocked(DateTimeOffset now)
    {
        var removed = _pendingNativeNext.RemoveAll(pending =>
            now - pending.InsertedAt > TimeSpan.FromHours(12));
        return removed > 0;
    }

    private (PlayerTrack? Target, NextGuardState State, long GuardId)
        GetSoftwareNextSnapshot()
    {
        lock (_softwareNextSync)
        {
            return (
                _softwareNextTarget,
                _softwareNextState,
                _softwareNextGuardId);
        }
    }

    private void ObserveNativeSession(int? processId)
    {
        lock (_nativeNextSync)
        {
            if (_nativeSessionProcessId == processId)
            {
                return;
            }

            _nativeSessionProcessId = processId;
            _pendingNativeNext.Clear();
            _lastObservedTrack = null;
            _observedTrackSequence = 0;
            _sessionObservedPlaying = false;
        }
        // A guard is scoped to one concrete QQ process/session. Never carry
        // even an Armed guard across a restart or transient process loss.
        CancelSoftwareNext(string.Empty);
    }

    private static string? FindExecutablePath()
    {
        foreach (var process in Process.GetProcessesByName("QQMusic"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)
                        && File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                catch
                {
                    // Try the next process or known install locations.
                }
            }
        }

        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "QQMusic",
                "QQMusic.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Tencent",
                "QQMusic",
                "QQMusic.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "QQMusic",
                "QQMusic.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static SingleInstanceSendResult SendSingleInstanceCommand(
        string executable,
        string switchName,
        string argument,
        int helperWaitMilliseconds = 400)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(switchName);
            startInfo.ArgumentList.Add(argument);
            using var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "QQMusic.exe 单实例命令进程未启动。");
            var exited = helper.WaitForExit(
                Math.Clamp(helperWaitMilliseconds, 0, 5000));

            return new SingleInstanceSendResult(
                true,
                exited
                    ? $"QQ 单实例命令已发送：{switchName} {argument}"
                    : "QQ 单实例命令已启动；不再等待辅助进程退出。");
        }
        catch (Exception exception)
        {
            return new SingleInstanceSendResult(
                false,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static int? FindProcessId(long windowHandle)
    {
        _ = GetWindowThreadProcessId(
            (nint)windowHandle,
            out var processId);
        return processId == 0 ? null : checked((int)processId);
    }

    private string GetCachedVersion(int processId)
    {
        if (_cachedVersionProcessId == processId)
        {
            return _cachedVersion;
        }

        _cachedVersion = TryGetVersion(processId);
        _cachedVersionProcessId = processId;
        return _cachedVersion;
    }

    private static string TryGetVersion(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : FileVersionInfo.GetVersionInfo(path).FileVersion
                  ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TrackMatches(PlayerTrack? actual, PlayerTrack expected)
    {
        return actual is not null
            && QQMusicTrackMatchPolicy.TracksRepresentSameSong(
                actual.Id,
                actual.Title,
                actual.Artist,
                expected.Id,
                expected.Title,
                expected.Artist);
    }

    private static bool ObservationMatches(
        PlayerTrack? actual,
        PlayerTrack expected)
    {
        return actual is not null
            && QQMusicTrackMatchPolicy
                .TracksRepresentSameObservation(
                    actual.Id,
                    actual.Title,
                    actual.Artist,
                    expected.Id,
                    expected.Title,
                    expected.Artist);
    }

    private static bool CurrentMetadataRepresentsSameSong(
        PlayerTrack? windowTrack,
        PlayerTrack? mediaTrack)
    {
        return windowTrack is not null
            && mediaTrack is not null
            && QQMusicTrackMatchPolicy
                .TracksRepresentSameObservation(
                    windowTrack.Id,
                    windowTrack.Title,
                    windowTrack.Artist,
                    mediaTrack.Id,
                    mediaTrack.Title,
                    mediaTrack.Artist);
    }

    private static bool HasTrackChanged(
        PlayerTrack? before,
        PlayerTrack? after)
    {
        if (after is null)
        {
            return false;
        }

        if (before is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(before.Id)
            && !string.IsNullOrWhiteSpace(after.Id))
        {
            return before.Id != after.Id;
        }

        return Normalize(before.DisplayName) != Normalize(after.DisplayName);
    }

    private static string BuildSnapshotFingerprint(PlayerSnapshot snapshot)
    {
        var current = snapshot.Current;
        var next = snapshot.Next;
        return string.Join(
            '\u001F',
            snapshot.Connected.ToString(),
            snapshot.ProcessId?.ToString(),
            snapshot.Version,
            snapshot.Status,
            snapshot.PlaybackAnchorReady.ToString(),
            current?.Id,
            current?.Title,
            current?.Artist,
            current?.Album,
            current?.CoverUrl,
            next?.Id,
            next?.Title,
            next?.Artist,
            next?.Album,
            next?.CoverUrl,
            snapshot.NextSource,
            snapshot.NextObservation,
            snapshot.NextGuardState.ToString(),
            snapshot.NextGuardId.ToString());
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private sealed record QqTrackPayload(
        long SongId,
        int SongType,
        string SongMid,
        bool IsPlayable);

    private sealed record PendingQqNativeNext(
        PlayerTrack Target,
        QqTrackPayload Payload,
        DateTimeOffset InsertedAt,
        long InsertedAtSequence,
        int ProcessId,
        bool VerificationIndeterminate);

    private sealed record NativeNextEnsureResult(
        bool Accepted,
        bool InsertedNow,
        bool Indeterminate,
        string Verification,
        string? Error,
        string? FailureCode);

    private sealed record WrongNextRecoveryResult(
        bool Verified,
        string Message,
        PlayerSnapshot Snapshot,
        bool LateRecoveryTargetRecorded = false);

    private sealed record SingleInstanceSendResult(
        bool Sent,
        string Message);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

}
