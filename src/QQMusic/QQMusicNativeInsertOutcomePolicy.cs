namespace QQMusicControlPoc;

internal static class QQMusicNativeInsertOutcomePolicy
{
    internal static bool IsAccepted(QQMusicNativeNextResult result, long expectedSongId)
    {
        if (expectedSongId is <= 0 or > uint.MaxValue
            || result.RequiresRestart
            || result.NativeStage != 5
            || result.GetCatManagerHresult < 0
            || result.GetSongInfoHresult < 0
            || result.AddSongsHresult < 0
            || result.ResolvedSongId != expectedSongId)
            return false;

        if (result.RemoteMemoryRetained)
        {
            return result.PatchWriteAttempted && !result.RemoteMemoryReleased
                && result.OriginalCodeRestored && result.CurrentWindowTrackUnchanged
                && result.Error is null
                && (result.Verification == "NativeNextInsertedMemoryRetainedPendingNextVerification"
                    || result.Verification == "NativeNextInsertedCurrentTrackUnchangedPendingNextVerification");
        }

        return result.Verification
            == "NativeNextInsertedCurrentTrackUnchangedPendingNextVerification";
    }
}
