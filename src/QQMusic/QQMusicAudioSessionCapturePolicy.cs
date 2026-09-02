namespace UnifiedPlayerControlPoc;

/// <summary>
/// A small abstraction over Windows audio-session COM objects. Keeping the
/// endpoint/session selection policy independent from COM makes it possible
/// to exercise the multi-device behavior without requiring audio hardware.
/// </summary>
internal interface IQQMusicAudioVolume
{
    int SetMute(bool mute, ref Guid eventContext);
}

internal interface IQQMusicAudioSession
{
    uint ProcessId { get; }

    bool IsActive { get; }

    bool TryGetVolume(
        out IQQMusicAudioVolume volume,
        out bool wasMuted);
}

internal interface IQQMusicAudioEndpoint
{
    string Description { get; }

    bool TryGetSessions(
        out IReadOnlyList<IQQMusicAudioSession> sessions,
        out string error);
}

internal sealed record QQMusicAudioSessionHandle(
    IQQMusicAudioVolume Volume,
    bool WasMuted);

internal sealed class QQMusicAudioCaptureResult
{
    internal QQMusicAudioCaptureResult(
        IReadOnlyList<QQMusicAudioSessionHandle> sessions,
        int activeSessionCount,
        string captureError)
    {
        Sessions = sessions;
        ActiveSessionCount = activeSessionCount;
        CaptureError = captureError;
    }

    internal IReadOnlyList<QQMusicAudioSessionHandle> Sessions { get; }

    internal int ActiveSessionCount { get; }

    internal string CaptureError { get; }
}

/// <summary>
/// Selects QQ Music's sessions across every active render endpoint.
/// Endpoint-level failures are recorded and do not prevent the remaining
/// endpoints from contributing sessions.
/// </summary>
internal static class QQMusicAudioSessionCapturePolicy
{
    internal static QQMusicAudioCaptureResult Capture(
        IEnumerable<IQQMusicAudioEndpoint> endpoints,
        int? expectedProcessId,
        Func<uint, bool> isQqMusicProcess)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(isQqMusicProcess);

        var selectedSessions = new List<QQMusicAudioSessionHandle>();
        var errors = new List<string>();
        var activeSessionCount = 0;

        foreach (var endpoint in endpoints)
        {
            if (endpoint is null)
            {
                errors.Add("未知音频端点：端点对象为空。");
                continue;
            }

            var description = Describe(endpoint);
            IReadOnlyList<IQQMusicAudioSession> sessions;
            try
            {
                if (!endpoint.TryGetSessions(
                        out sessions,
                        out var endpointError))
                {
                    AddError(
                        errors,
                        description,
                        string.IsNullOrWhiteSpace(endpointError)
                            ? "枚举音频会话失败。"
                            : endpointError);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(endpointError))
                {
                    AddError(errors, description, endpointError);
                }
            }
            catch (Exception exception)
            {
                AddError(
                    errors,
                    description,
                    $"{exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (sessions is null)
            {
                AddError(errors, description, "枚举结果为空。");
                continue;
            }

            foreach (var session in sessions)
            {
                if (session is null)
                {
                    AddError(errors, description, "发现空的音频会话。");
                    continue;
                }

                try
                {
                    if (expectedProcessId is > 0
                        && session.ProcessId != (uint)expectedProcessId.Value)
                    {
                        continue;
                    }

                    if (!isQqMusicProcess(session.ProcessId))
                    {
                        continue;
                    }

                    // Keep active-session evidence independent from volume
                    // control. A session can be active even when the volume
                    // interface cannot be queried or muted.
                    if (session.IsActive)
                    {
                        activeSessionCount++;
                    }

                    if (!session.TryGetVolume(
                            out var volume,
                            out var wasMuted)
                        || volume is null)
                    {
                        continue;
                    }

                    selectedSessions.Add(
                        new QQMusicAudioSessionHandle(volume, wasMuted));
                }
                catch (Exception exception)
                {
                    AddError(
                        errors,
                        description,
                        $"会话处理失败：{exception.GetType().Name}: "
                        + exception.Message);
                }
            }
        }

        return new QQMusicAudioCaptureResult(
            selectedSessions,
            activeSessionCount,
            string.Join("; ", errors));
    }

    private static string Describe(IQQMusicAudioEndpoint endpoint)
    {
        try
        {
            return string.IsNullOrWhiteSpace(endpoint.Description)
                ? "未命名音频端点"
                : endpoint.Description;
        }
        catch
        {
            return "未知音频端点";
        }
    }

    private static void AddError(
        ICollection<string> errors,
        string description,
        string error)
    {
        errors.Add($"{description}: {error}");
    }
}
