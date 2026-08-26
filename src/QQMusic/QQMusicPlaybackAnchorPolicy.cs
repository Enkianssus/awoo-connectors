namespace QQMusicControlPoc;

internal sealed record QQMusicTimelineEvidence(
    string PlaybackStatus,
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan ReportedPosition);

internal sealed record QQMusicPlaybackAnchorDecision(
    bool IsReliable,
    string? FailureCode,
    string? Message,
    bool ObservedPlaying);

/// <summary>
/// Decides whether QQ Music has enough observable state for a safe native
/// AddSongs(mode=0) operation. A freshly opened QQ Music window normally has
/// no caption until its first song starts; treating that empty state as an
/// insertion point makes QQ place the item at the beginning of the playlist.
/// </summary>
internal static class QQMusicPlaybackAnchorPolicy
{
    internal const string MissingFailureCode =
        "qq-playback-anchor-missing";

    internal const string MissingMessage =
        "QQ 音乐尚未确认本次启动的当前播放位置；"
        + "请先在 QQ 音乐播放任意一首歌，再重试点歌。";

    internal static QQMusicPlaybackAnchorDecision Evaluate(
        QQMusicPlaybackState state,
        QQMusicTimelineEvidence? timeline,
        bool sessionObservedPlaying)
    {
        if (!state.IsRunning)
        {
            return new QQMusicPlaybackAnchorDecision(
                false,
                null,
                "QQ 音乐未连接，无法确定当前播放位置。",
                false);
        }

        if (state.WindowHandle is null
            || string.IsNullOrWhiteSpace(state.WindowTitle)
            || string.IsNullOrWhiteSpace(state.Title))
        {
            return Missing();
        }

        // A paused timeline can be stale metadata left by a previous QQ
        // session. Only a Playing observation establishes the native cursor;
        // after that same-process observation, a later paused state remains a
        // valid anchor.
        var observedPlaying = sessionObservedPlaying
            || IsCrediblePlayingTimeline(timeline);
        if (!observedPlaying)
        {
            return Missing();
        }

        return new QQMusicPlaybackAnchorDecision(
            true,
            null,
            null,
            observedPlaying);
    }

    internal static bool IsCrediblePlayingTimeline(
        QQMusicTimelineEvidence? timeline) =>
        IsCredibleTimeline(timeline)
        && string.Equals(
            timeline!.PlaybackStatus,
            "Playing",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCredibleTimeline(
        QQMusicTimelineEvidence? timeline) =>
        timeline is not null
        && timeline.EndTime > timeline.StartTime
        && timeline.ReportedPosition >= timeline.StartTime
        && timeline.ReportedPosition <= timeline.EndTime;

    private static QQMusicPlaybackAnchorDecision Missing() =>
        new(
            false,
            MissingFailureCode,
            MissingMessage,
            false);
}
