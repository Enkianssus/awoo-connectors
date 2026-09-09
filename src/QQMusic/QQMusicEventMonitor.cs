using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace UnifiedPlayerControlPoc;

internal enum QQMusicEventKind
{
    Initialized,
    SessionsChanged,
    MediaPropertiesChanged,
    ArtworkChanged,
    PlaybackInfoChanged,
    TimelinePropertiesChanged,
    WindowTitleChanged,
    WindowStateChanged,
    SnapshotInvalidated
}

internal sealed record QQMusicPlayerEvent(
    QQMusicEventKind Kind,
    DateTimeOffset ObservedAt,
    nint WindowHandle = default,
    string WindowTitle = "");

/// <summary>
/// Broadcasts QQ playback changes from public Windows media-session events and
/// EVENT_OBJECT_NAMECHANGE. The WinEvent hook owns a message-pump thread so
/// out-of-context callbacks are delivered without polling a window title.
/// </summary>
internal sealed class QQMusicEventMonitor : IAsyncDisposable
{
    private readonly object _startSync = new();
    private readonly object _subscriberSync = new();
    private readonly object _mediaSync = new();
    private readonly Dictionary<long, Channel<QQMusicPlayerEvent>>
        _subscribers = [];
    private Task? _startTask;
    private long _nextSubscriberId;
    private volatile bool _disposed;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private QQMusicTimelineProbe? _timelineProbe;
    private PlayerTrack? _mediaTrack;
    private long _mediaGeneration;
    private ArtworkRequest? _pendingArtwork;
    private CancellationTokenSource? _artworkCancellation;
    private Task? _artworkWorker;
    private WinEventNameChangeHook? _windowHook;

    public string SourceStatus
    {
        get
        {
            lock (_mediaSync)
            {
                var media = _manager is null
                    ? "GSMTC unavailable"
                    : _session is null
                        ? "GSMTC waiting"
                        : "GSMTC events";
                var window = _windowHook?.IsActive == true
                    ? "WinEventHook"
                    : "WinEventHook unavailable";
                return $"{media} + {window}";
            }
        }
    }

    public Task EnsureStartedAsync()
    {
        lock (_startSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= StartAsync();
        }
    }

    public QQMusicEventSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<QQMusicPlayerEvent>(
            new BoundedChannelOptions(64)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        long id;
        lock (_subscriberSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            id = ++_nextSubscriberId;
            _subscribers[id] = channel;
        }

        return new QQMusicEventSubscription(
            channel.Reader,
            () => RemoveSubscriber(id));
    }

    public PlayerTrack? ReadMediaTrack()
    {
        lock (_mediaSync)
        {
            return _mediaTrack;
        }
    }

    public QQMusicTimelineSnapshot? ReadTimelineSnapshot()
    {
        QQMusicTimelineProbe? probe;
        GlobalSystemMediaTransportControlsSession? session;
        lock (_mediaSync)
        {
            probe = _timelineProbe;
            session = _session;
        }
        var snapshot = probe?.ReadSnapshot(session);
        lock (_mediaSync)
        {
            return !_disposed && ReferenceEquals(_session, session)
                ? snapshot
                : null;
        }
    }

    /// <summary>
    /// Refreshes only the already selected session, with a two-second wait
    /// limit. True means the read was committed, possibly as empty metadata;
    /// it is not a playback acknowledgement. This does not start the monitor.
    /// </summary>
    public async Task<bool> RefreshMediaAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GlobalSystemMediaTransportControlsSession? session;
        long mediaGeneration;
        lock (_mediaSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = _session;
            if (session is null)
            {
                return false;
            }
            mediaGeneration = ++_mediaGeneration;
            CancelArtworkLocked();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await RefreshMediaPropertiesAsync(
                session, mediaGeneration, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void NotifySnapshotInvalidated()
    {
        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.SnapshotInvalidated,
            DateTimeOffset.Now));
    }

    public async ValueTask DisposeAsync()
    {
        Task? startTask;
        GlobalSystemMediaTransportControlsSessionManager? manager;
        GlobalSystemMediaTransportControlsSession? session;
        WinEventNameChangeHook? windowHook;
        lock (_startSync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            startTask = _startTask;
        }

        lock (_mediaSync)
        {
            manager = _manager;
            session = _session;
            windowHook = _windowHook;
            _manager = null;
            _session = null;
            _timelineProbe = null;
            _mediaTrack = null;
            CancelArtworkLocked();
            _windowHook = null;
        }

        if (manager is not null)
        {
            manager.SessionsChanged -= OnSessionsChanged;
            manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
        DetachSessionEvents(session);
        windowHook?.Dispose();

        Channel<QQMusicPlayerEvent>[] channels;
        lock (_subscriberSync)
        {
            channels = [.. _subscribers.Values];
            _subscribers.Clear();
        }
        foreach (var channel in channels)
        {
            channel.Writer.TryComplete();
        }

        if (startTask is not null)
        {
            try
            {
                await startTask.ConfigureAwait(false);
            }
            catch
            {
                // Startup is best-effort. Disposal must complete even when a
                // native API failed while startup was in flight.
            }
        }
    }

    private async Task StartAsync()
    {
        var windowHook = new WinEventNameChangeHook(OnWindowEvent);
        windowHook.Start();
        lock (_mediaSync)
        {
            if (_disposed)
            {
                windowHook.Dispose();
                return;
            }
            _windowHook = windowHook;
        }

        try
        {
            var manager =
                await GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync();
            lock (_mediaSync)
            {
                if (_disposed)
                {
                    return;
                }
                _manager = manager;
                _timelineProbe = new QQMusicTimelineProbe(manager);
                manager.SessionsChanged += OnSessionsChanged;
                manager.CurrentSessionChanged += OnCurrentSessionChanged;
            }
            RefreshSession();
        }
        catch
        {
            // WinEventHook remains a complete title-change event source when
            // Windows media-session access is unavailable.
        }

        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.Initialized,
            DateTimeOffset.Now));
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        RefreshSession();
        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.SessionsChanged,
            DateTimeOffset.Now));
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        RefreshSession();
        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.SessionsChanged,
            DateTimeOffset.Now));
    }

    private void RefreshSession()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_mediaSync)
        {
            manager = _manager;
        }
        if (manager is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? nextSession;
        try
        {
            nextSession = manager.GetSessions().FirstOrDefault(candidate =>
                candidate.SourceAppUserModelId.Contains(
                    "qqmusic",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? previousSession;
        long mediaGeneration;
        lock (_mediaSync)
        {
            if (_disposed)
            {
                return;
            }
            previousSession = _session;
            if (ReferenceEquals(previousSession, nextSession))
            {
                return;
            }
            _session = nextSession;
            // A newly selected session must not inherit the previous session's
            // local fallback-clock origin, even if its timestamps look equal.
            _timelineProbe = new QQMusicTimelineProbe(manager);
            _mediaTrack = null;
            CancelArtworkLocked();
            mediaGeneration = ++_mediaGeneration;
        }

        DetachSessionEvents(previousSession);
        if (nextSession is null)
        {
            return;
        }

        lock (_mediaSync)
        {
            if (_disposed || !ReferenceEquals(_session, nextSession))
            {
                return;
            }
            nextSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            nextSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            nextSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        _ = RefreshMediaPropertiesAsync(nextSession, mediaGeneration);
    }

    private void DetachSessionEvents(
        GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }
        session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        long mediaGeneration;
        lock (_mediaSync)
        {
            if (_disposed || !ReferenceEquals(_session, sender)) return;
            mediaGeneration = ++_mediaGeneration;
            CancelArtworkLocked();
        }
        _ = RefreshMediaPropertiesAsync(sender, mediaGeneration);
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (!IsCurrentSession(sender))
        {
            return;
        }
        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.PlaybackInfoChanged,
            DateTimeOffset.Now));
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (!IsCurrentSession(sender))
        {
            return;
        }
        Publish(new QQMusicPlayerEvent(
            QQMusicEventKind.TimelinePropertiesChanged,
            DateTimeOffset.Now));
    }

    private async Task<bool> RefreshMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session,
        long mediaGeneration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await ReadMediaPropertiesAsync(session)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            var title = properties.Title?.Trim() ?? string.Empty;
            PlayerTrack? track = string.IsNullOrWhiteSpace(title) ? null : new(
                string.Empty, title, properties.Artist?.Trim() ?? string.Empty,
                properties.AlbumTitle?.Trim() ?? string.Empty);
            lock (_mediaSync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_session, session)
                    || _disposed
                    || mediaGeneration != _mediaGeneration)
                {
                    return false;
                }
                if (SameMediaTrack(_mediaTrack, track))
                    track = track! with { CoverUrl = _mediaTrack!.CoverUrl };
                _mediaTrack = track;
                CancelArtworkLocked();
                if (track is not null && properties.Thumbnail is { } thumbnail)
                {
                    _pendingArtwork = new(session, mediaGeneration, track, thumbnail);
                    // One worker and one latest-only pending request. Metadata
                    // commits never wait for thumbnail I/O or an older request.
                    if (_artworkWorker is null)
                        _artworkWorker = Task.Run(ReadArtworkAsync);
                }
            }
            Publish(new QQMusicPlayerEvent(
                QQMusicEventKind.MediaPropertiesChanged,
                DateTimeOffset.Now));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A later media-property or session event retries the read.
            return false;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionMediaProperties> ReadMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        return await session.TryGetMediaPropertiesAsync();
    }

    private void CancelArtworkLocked()
    {
        _pendingArtwork = null;
        _artworkCancellation?.Cancel();
    }

    private async Task ReadArtworkAsync()
    {
        while (true)
        {
            ArtworkRequest request;
            CancellationTokenSource cancellation;
            lock (_mediaSync)
            {
                if (_disposed || _pendingArtwork is null)
                {
                    _artworkWorker = null;
                    return;
                }
                request = _pendingArtwork;
                _pendingArtwork = null;
                cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _artworkCancellation = cancellation;
            }
            try
            {
                var cover = await QQMusicMediaArtwork.ReadAsync(
                    request.Thumbnail, cancellation.Token).ConfigureAwait(false);
                var changed = false;
                lock (_mediaSync)
                {
                    if (!_disposed && !cancellation.IsCancellationRequested &&
                        ReferenceEquals(_session, request.Session) &&
                        request.Generation == _mediaGeneration &&
                        ReferenceEquals(_mediaTrack, request.Track) &&
                        !string.IsNullOrEmpty(cover) && _mediaTrack.CoverUrl != cover)
                    {
                        _mediaTrack = _mediaTrack with { CoverUrl = cover };
                        changed = true;
                    }
                }
                if (changed) Publish(new(QQMusicEventKind.ArtworkChanged, DateTimeOffset.Now));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Artwork is decorative only: failure is not a failed metadata
                // observation and never provides playback-transition evidence.
            }
            finally
            {
                lock (_mediaSync)
                {
                    if (ReferenceEquals(_artworkCancellation, cancellation))
                        _artworkCancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    internal static bool SameMediaTrack(PlayerTrack? left, PlayerTrack? right) =>
        left is not null && right is not null && left.Title == right.Title &&
        left.Artist == right.Artist && left.Album == right.Album;

    private sealed record ArtworkRequest(
        GlobalSystemMediaTransportControlsSession Session, long Generation,
        PlayerTrack Track, IRandomAccessStreamReference Thumbnail);

    private bool IsCurrentSession(
        GlobalSystemMediaTransportControlsSession session)
    {
        lock (_mediaSync)
        {
            return ReferenceEquals(_session, session) && !_disposed;
        }
    }

    private void OnWindowEvent(uint eventType, nint window, string title)
    {
        Publish(new QQMusicPlayerEvent(
            eventType == WinEventNameChangeHook.EventObjectNameChange
                ? QQMusicEventKind.WindowTitleChanged
                : QQMusicEventKind.WindowStateChanged,
            DateTimeOffset.Now,
            window,
            title));
    }

    private void Publish(QQMusicPlayerEvent playerEvent)
    {
        Channel<QQMusicPlayerEvent>[] channels;
        lock (_subscriberSync)
        {
            if (_disposed)
            {
                return;
            }
            channels = [.. _subscribers.Values];
        }
        foreach (var channel in channels)
        {
            channel.Writer.TryWrite(playerEvent);
        }
    }

    private void RemoveSubscriber(long id)
    {
        Channel<QQMusicPlayerEvent>? channel;
        lock (_subscriberSync)
        {
            _subscribers.Remove(id, out channel);
        }
        channel?.Writer.TryComplete();
    }

    internal sealed class QQMusicEventSubscription : IAsyncDisposable
    {
        private Action? _dispose;

        internal QQMusicEventSubscription(
            ChannelReader<QQMusicPlayerEvent> reader,
            Action dispose)
        {
            Reader = reader;
            _dispose = dispose;
        }

        public ChannelReader<QQMusicPlayerEvent> Reader { get; }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WinEventNameChangeHook : IDisposable
    {
        internal const uint EventObjectNameChange = 0x800C;
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectDestroy = 0x8001;
        private const uint EventObjectHide = 0x8003;
        private const int ObjectIdWindow = 0;
        private const uint WinEventOutOfContext = 0;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const uint WindowMessageQuit = 0x0012;
        private const uint PeekMessageNoRemove = 0;

        private readonly Action<uint, nint, string> _onWindowEvent;
        private readonly HashSet<nint> _knownQQWindows = [];
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private WinEventDelegate? _callback;
        private nint _nameHook;
        private nint _lifecycleHook;
        private uint _threadId;
        private int _disposed;

        internal WinEventNameChangeHook(
            Action<uint, nint, string> onWindowEvent)
        {
            _onWindowEvent = onWindowEvent;
            _thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "QQMusic WinEventHook"
            };
        }

        internal bool IsActive => _nameHook != 0;

        internal void Start()
        {
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            var threadId = Volatile.Read(ref _threadId);
            if (threadId != 0)
            {
                _ = PostThreadMessage(
                    threadId,
                    WindowMessageQuit,
                    0,
                    0);
            }
            if (_thread.IsAlive)
            {
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }

        private void ThreadMain()
        {
            _threadId = GetCurrentThreadId();
            // Force creation of this thread's native message queue before
            // Start() returns, so PostThreadMessage(WM_QUIT) cannot race the
            // first GetMessage call during connector shutdown.
            _ = PeekMessage(
                out _,
                0,
                0,
                0,
                PeekMessageNoRemove);
            _callback = OnWinEvent;
            _nameHook = SetWinEventHook(
                EventObjectNameChange,
                EventObjectNameChange,
                0,
                _callback,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            _lifecycleHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectHide,
                0,
                _callback,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            _ready.Set();
            if (_nameHook == 0 && _lifecycleHook == 0)
            {
                return;
            }

            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessage(in message);
            }
            if (_nameHook != 0)
            {
                _ = UnhookWinEvent(_nameHook);
                _nameHook = 0;
            }
            if (_lifecycleHook != 0)
            {
                _ = UnhookWinEvent(_lifecycleHook);
                _lifecycleHook = 0;
            }
        }

        private void OnWinEvent(
            nint hook,
            uint eventType,
            nint window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (window == 0 || objectId != ObjectIdWindow || childId != 0)
            {
                return;
            }
            _ = GetWindowThreadProcessId(window, out var processId);
            var isQQMusic = processId != 0
                && IsQQMusicProcess(processId);
            if (isQQMusic)
            {
                _knownQQWindows.Add(window);
            }
            else if (!_knownQQWindows.Contains(window))
            {
                return;
            }

            var title = eventType == EventObjectDestroy
                ? string.Empty
                : ReadWindowText(window);
            _onWindowEvent(eventType, window, title);
            if (eventType == EventObjectDestroy)
            {
                _knownQQWindows.Remove(window);
            }
        }

        private static bool IsQQMusicProcess(uint processId)
        {
            try
            {
                using var process = Process.GetProcessById(
                    checked((int)processId));
                return process.ProcessName.Equals(
                    "QQMusic",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadWindowText(nint window)
        {
            var length = GetWindowTextLength(window);
            if (length <= 0)
            {
                return string.Empty;
            }
            var text = new StringBuilder(length + 1);
            _ = GetWindowText(window, text, text.Capacity);
            return text.ToString().Trim();
        }

        private delegate void WinEventDelegate(
            nint hook,
            uint eventType,
            nint window,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativePoint
        {
            internal readonly int X;
            internal readonly int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct NativeMessage
        {
            internal readonly nint Window;
            internal readonly uint Message;
            internal readonly nuint WParam;
            internal readonly nint LParam;
            internal readonly uint Time;
            internal readonly NativePoint Point;
            internal readonly uint Private;
        }

        [DllImport("user32.dll")]
        private static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint module,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(nint hook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(
            out NativeMessage message,
            nint window,
            uint messageMin,
            uint messageMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(
            out NativeMessage message,
            nint window,
            uint messageMin,
            uint messageMax,
            uint removeMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(in NativeMessage message);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(in NativeMessage message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            nuint wParam,
            nint lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            nint window,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(nint window);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern int GetWindowText(
            nint window,
            StringBuilder text,
            int maximum);
    }
}
