using System.Diagnostics;
using System.Globalization;

namespace QQMusicControlPoc;

// Opt-in diagnostics only. No caller-supplied label or player/user data is emitted.
// Disabled operation avoids timestamps, formatting and I/O. A stage is emitted
// only after that operation returned; interrupted awaits emit no completion stage.
internal static class QQMusicWebTiming
{
    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("AWOO_QQMUSIC_WEB_TIMING"), "1", StringComparison.Ordinal);

    internal enum Stage
    {
        ExecuteCancelGateWait, ExecuteObserverReady, ExecuteGateWait,
        ExecuteRefreshMedia, ExecuteInitialIdentity,
        ArmDetails, ArmIdentity, ArmTransport,
        SubmitDetails, SubmitPreInsertIdentity, SubmitTransport,
        SubmitPreNextIdentity, SubmitSendNext, SubmitPostNextRefresh, SubmitPostNextIdentity
    }

    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void WriteElapsed(Stage stage, long started)
    {
        if (!Enabled) return;
        try
        {
            var label = stage switch
            {
                Stage.ExecuteCancelGateWait => "execute-cancel-gate-wait",
                Stage.ExecuteObserverReady => "execute-observer-ready",
                Stage.ExecuteGateWait => "execute-gate-wait",
                Stage.ExecuteRefreshMedia => "execute-refresh-media",
                Stage.ExecuteInitialIdentity => "execute-initial-identity",
                Stage.ArmDetails => "arm-details",
                Stage.ArmIdentity => "arm-identity",
                Stage.ArmTransport => "arm-transport",
                Stage.SubmitDetails => "submit-details",
                Stage.SubmitPreInsertIdentity => "submit-pre-insert-identity",
                Stage.SubmitTransport => "submit-transport",
                Stage.SubmitPreNextIdentity => "submit-pre-next-identity",
                Stage.SubmitSendNext => "submit-send-next",
                Stage.SubmitPostNextRefresh => "submit-post-next-refresh",
                Stage.SubmitPostNextIdentity => "submit-post-next-identity",
                _ => null
            };
            if (label is null) return;
            var elapsedMs = Math.Max(0L, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Console.Error.WriteLine("qq-web-timing " + label + " elapsedMs=" +
                elapsedMs.ToString(CultureInfo.InvariantCulture));
        }
        catch { /* Diagnostics must not alter playback, cancellation or gate release. */ }
    }
}
