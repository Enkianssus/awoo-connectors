namespace UnifiedPlayerControlPoc;

/// <summary>Describes executable queue support, independently of basic player connectivity.</summary>
internal sealed record QQMusicNativeSupportState(bool Supported, string Message, string? FailureCode)
{
    internal static readonly QQMusicNativeSupportState Ready = new(true,
        "兼容画像和原生调用约定已校验；原生插队可用。", null);
    internal static readonly QQMusicNativeSupportState Disconnected = new(false,
        "未连接 QQ 音乐，原生插队不可用。", null);

    internal static QQMusicNativeSupportState Unsupported(string version) => new(false,
        $"QQ 音乐 {(string.IsNullOrWhiteSpace(version) ? "当前版本" : version)} 尚无通过完整校验的兼容画像；"
        + "搜索和手动播放控制仍可使用，点歌插入及自动纠正已停止。请检查连接器或兼容画像更新。",
        "player-version-unsupported");

    internal static QQMusicNativeSupportState AccessDenied => new(false,
        "无法读取 QQ 音乐进程以验证兼容画像，已停止原生点歌和自动纠正。",
        "process-access-denied");

    internal static QQMusicNativeSupportState Disabled => new(false,
        "未启用经过画像校验的 QQ 原生队列能力，已停止点歌插入和自动纠正。",
        "qqmusic-native-control-disabled");

    internal PlayerCapabilities Capabilities => new(
        Search: true, PlaySelected: Supported, Previous: true,
        Pause: true, Resume: true, Toggle: false, Next: true,
        InsertNext: Supported,
        InsertNextLevel: Supported
            ? "精确文件画像及调用约定原生插队；静音+暂停守卫防漏音"
            : Message);

    internal static bool RequiresNativeSupport(PlayerCommand command) =>
        command is PlayerCommand.PlaySelected or PlayerCommand.InterruptSelected
            or PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard;

    internal PlayerOperationResult? RejectIfUnavailable(PlayerCommand command,
        PlayerSnapshot before, Action<string> stopGuard)
    {
        if (Supported || !RequiresNativeSupport(command)) return null;
        stopGuard(Message);
        return new PlayerOperationResult(OperationOutcome.Unsupported, Message, before, FailureCode);
    }
}

/// <summary>
/// Keys include process epoch, installation file identities and loaded-profile revision.
/// A failed match remains refreshable when either the files or effective profile set changes.
/// Hashing and structural inspection occur once per identity, never once per timeline event.
/// </summary>
internal sealed class QQMusicNativeSupportCache
{
    private readonly object _sync = new();
    private string? _key;
    private QQMusicNativeSupportState? _state;

    internal QQMusicNativeSupportState Get(string key, Func<QQMusicNativeSupportState> inspect)
    {
        lock (_sync)
        {
            if (_key == key && _state is not null) return _state;
            var state = inspect();
            _key = key;
            _state = state;
            return state;
        }
    }
}
