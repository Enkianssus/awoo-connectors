namespace QQMusicControlPoc;

internal enum QQMusicWebSubmissionState
{
    RejectedBeforeDispatch,
    SubmittedUnverified,
    OutcomeUnknown
}

internal sealed record QQMusicWebSubmission(QQMusicWebSubmissionState State, string Code, string Diagnostics = "");

/// <summary>Pure, fail-closed interpretation of one isolated helper's receipt.</summary>
internal sealed class QQMusicWebReceipt(Guid operationId)
{
    private int sequence;
    private bool invalid;
    private bool rejectedBeforeDispatch;
    private string lastStage = "no-event";
    private int? lastHResult;

    internal void Observe(QQMusicWebBridgeEvent item)
    {
        if (item.OperationId == operationId)
        {
            // Diagnostics may be shown in the host. Keep only bounded known
            // stage labels and numbers, never arbitrary native/error text.
            lastStage = item.Stage switch
            {
                "request-read" or "target-validation" or "command-validation" or
                "client-open" or "target-recheck" or "dispatch-starting" or
                "submission-returned" or "sender-drain" or "drain-completed" or
                "helper-deadline" => item.Stage,
                _ => "unrecognized-event"
            };
            lastHResult = item.HResult;
        }
        if (item.OperationId != operationId || rejectedBeforeDispatch || invalid)
        {
            invalid = true;
            return;
        }

        switch (item.Stage, item.Code, sequence, item.HResult)
        {
            case ("dispatch-starting", "single-command-dispatch-reserved", 0, null):
                sequence = 1;
                return;
            case ("submission-returned", "transport-returned-unconfirmed", 1, null):
                sequence = 2;
                return;
            case ("drain-completed", "observation-required", 2, null):
                sequence = 3;
                return;
        }

        if (sequence == 0 && item.HResult.HasValue && item.Stage is
            "request-read" or "target-validation" or "command-validation" or "client-open" or "target-recheck")
        {
            rejectedBeforeDispatch = true;
            return;
        }
        invalid = true;
    }

    internal QQMusicWebSubmission Complete(int? exitCode, bool interrupted)
    {
        var diagnostics = $"stage={lastStage}; hr={(lastHResult.HasValue ? $"0x{lastHResult.Value:X8}" : "none")}; "
            + $"exit={(exitCode.HasValue ? exitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}; "
            + $"interrupted={interrupted}; invalid-receipt={invalid}";
        if (!invalid && rejectedBeforeDispatch)
            return new(QQMusicWebSubmissionState.RejectedBeforeDispatch, "bridge-rejected-before-dispatch", diagnostics);
        if (!invalid && !interrupted && exitCode == 0 && sequence == 3)
            return new(QQMusicWebSubmissionState.SubmittedUnverified, "transport-returned-observation-required", diagnostics);
        // Missing output is never evidence that nothing was sent. In particular,
        // cancellation/crash after stdin delivery must not authorize a retry.
        return new(QQMusicWebSubmissionState.OutcomeUnknown, "web-dispatch-outcome-unknown", diagnostics);
    }
}
