namespace QQMusicControlPoc;

// These are values only. The original interface address is borrowed while its
// RCW is alive; no message-provided pointer is ever dereferenced or released.
internal sealed record QQMusicWebDrainIdentity(
    string ApiSha256, nint Module, nint ClientInterface, uint ProcessId, uint ThreadId);

internal sealed record QQMusicWebDrainWindow(
    nint Handle, uint ProcessId, uint ThreadId, nint ClassModule, string ClassName, bool IsVisible);

/// <summary>
/// Version-gated sender-lifecycle optimization, not a new connection route or
/// recipient/queue/playback acknowledgment. Unknown evidence keeps the full drain.
/// </summary>
internal static class QQMusicWebDrainPolicy
{
    internal const string AuditedApiSha256 =
        "0F438C07F1BBD814FDF18350B166C946C858CCADD16A1D77F677C653D4D097DC";
    internal const uint SenderCompletionMessage = 0x465;
    internal const nuint WebPerformWorkerType = 2;

    internal static bool IsCompletionCandidate(QQMusicWebDrainIdentity identity,
        nint window, uint message, nuint wParam, nint lParam) =>
        string.Equals(identity.ApiSha256, AuditedApiSha256, StringComparison.OrdinalIgnoreCase)
        && identity.Module != 0 && identity.ClientInterface != 0
        && identity.ProcessId != 0 && identity.ThreadId != 0
        && window != 0 && message == SenderCompletionMessage
        && wParam == WebPerformWorkerType && lParam == identity.ClientInterface;

    internal static bool CanFinishDrain(QQMusicWebDrainIdentity identity,
        nint window, uint message, nuint wParam, nint lParam,
        QQMusicWebDrainWindow? before, QQMusicWebDrainWindow? after, nint dispatchResult) =>
        IsCompletionCandidate(identity, window, message, wParam, lParam)
        && dispatchResult == 1
        && IsOwnedHiddenWindow(identity, window, before)
        && IsOwnedHiddenWindow(identity, window, after)
        && string.Equals(before!.ClassName, after!.ClassName, StringComparison.Ordinal);

    private static bool IsOwnedHiddenWindow(QQMusicWebDrainIdentity identity,
        nint window, QQMusicWebDrainWindow? observed) =>
        observed is not null && observed.Handle == window
        && observed.ProcessId == identity.ProcessId && observed.ThreadId == identity.ThreadId
        && observed.ClassModule == identity.Module && !observed.IsVisible
        && observed.ClassName is { Length: > 4 }
        && observed.ClassName.StartsWith("ATL:", StringComparison.Ordinal);
}
