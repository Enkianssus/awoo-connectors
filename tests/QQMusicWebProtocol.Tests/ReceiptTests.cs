using QQMusicControlPoc;

internal static class ReceiptTests
{
    private static readonly Guid Operation = Guid.Parse("35e9da12-1b6c-47d9-9671-b61c2bf22e45");
    private static readonly QQMusicWebBridgeEvent[] Good =
    [
        new(Operation, "dispatch-starting", "single-command-dispatch-reserved"),
        new(Operation, "submission-returned", "transport-returned-unconfirmed"),
        new(Operation, "drain-completed", "observation-required")
    ];
    private const int FailureHResult = unchecked((int)0x80004005);

    public static void Run()
    {
        AssertResult(Good, 0, false, QQMusicWebSubmissionState.SubmittedUnverified,
            "only full normal receipt is submitted, never verified");

        // A started helper with no event might already have sent the command.
        // Neither a clean exit nor an interruption authorizes another attempt.
        foreach (var exit in new int?[] { null, 0, 1, 2, 124, -1 })
        {
            AssertUnknown([], exit, false, "no event is not proof of rejection");
            AssertUnknown([], exit, true, "interrupted with no event");
        }

        AssertUnknown([Good[0]], 0, false, "dispatch reservation without return");
        AssertUnknown([Good[0], Good[1]], 0, false, "native return without drain");
        foreach (var exit in new int?[] { null, 1, 2, 124, -1 })
            AssertUnknown(Good, exit, false, "nonzero/missing exit overrides normal events");
        AssertUnknown(Good, 0, true, "interrupted full receipt is still unknown");

        var permutations = new[]
        {
            new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 },
            new[] { 2, 0, 1 }, new[] { 2, 1, 0 }
        };
        foreach (var order in permutations)
            AssertUnknown(order.Select(i => Good[i]), 0, false, "out-of-order stages");

        for (var position = 0; position < Good.Length; position++)
        {
            var duplicate = Good.ToList();
            duplicate.Insert(position, Good[position]);
            AssertUnknown(duplicate, 0, false, "duplicate stage cannot be a second dispatch");

            var mismatch = Good.ToArray();
            mismatch[position] = mismatch[position] with { OperationId = Guid.Empty };
            AssertUnknown(mismatch, 0, false, "foreign operation ID cannot complete receipt");

            var wrongCode = Good.ToArray();
            wrongCode[position] = wrongCode[position] with { Code = "success" };
            AssertUnknown(wrongCode, 0, false, "stage name without exact code is not enough");

            foreach (var hr in new[] { FailureHResult, 0, 1 })
            {
                var exceptionEvent = Good.ToArray();
                exceptionEvent[position] = exceptionEvent[position] with { HResult = hr };
                AssertUnknown(exceptionEvent, 0, false,
                    "normal event carrying exception HRESULT is not a normal receipt");
            }
        }

        var afterDispatchFailure = new QQMusicWebBridgeEvent(
            Operation, "dispatch-starting", "operation-failed", FailureHResult);
        AssertUnknown([Good[0], afterDispatchFailure], 2, false,
            "failure after reservation must not permit retry");
        AssertUnknown([Good[0], Good[1],
            new(Operation, "sender-drain", "operation-failed", FailureHResult)], 2, false,
            "failure while draining is ambiguous");
        AssertUnknown([Good[0],
            new(Operation, "target-validation", "operation-failed", FailureHResult)], 2, false,
            "a pre-dispatch stage cannot erase an earlier reservation");

        foreach (var stage in new[]
        {
            "request-read", "target-validation", "command-validation", "client-open", "target-recheck"
        })
        {
            var rejection = new QQMusicWebBridgeEvent(Operation, stage, "operation-failed", FailureHResult);
            AssertResult([rejection], 2, false, QQMusicWebSubmissionState.RejectedBeforeDispatch,
                "matching explicit failure before dispatch");
            AssertResult([rejection], null, true, QQMusicWebSubmissionState.RejectedBeforeDispatch,
                "proved pre-dispatch rejection survives interruption");
            AssertUnknown([rejection with { HResult = null }], 2, false,
                "pre-dispatch stage without failure evidence");
            AssertUnknown([rejection with { OperationId = Guid.Empty }], 2, false,
                "foreign failure cannot reject this operation");
            AssertUnknown(new[] { rejection }.Concat(Good), 0, false,
                "contradictory dispatch after rejection fails closed");
        }

        AssertUnknown([new(Operation, "helper-deadline", "helper-deadline")], 124, true,
            "helper deadline alone does not prove pre-dispatch failure");
        AssertUnknown([new(Operation, "unknown-stage", "operation-failed", FailureHResult)], 2, false,
            "unknown failure stage");
        AssertUnknown(Good.Append(new(Operation, "unknown-stage", "unknown-code")), 0, false,
            "unexpected trailing event invalidates earlier receipt");
        AssertUnknown(new[] { new QQMusicWebBridgeEvent(Guid.Empty, "unknown-stage", "unknown-code") }
            .Concat(Good), 0, false, "invalid event is sticky");
        CheckDiagnostics();
    }

    private static void CheckDiagnostics()
    {
        const string privateStage = @"C:\private-session\do-not-log-stage";
        const string privateCode = "do-not-log-code: songList, secret-token-123";
        var receipt = new QQMusicWebReceipt(Operation);
        receipt.Observe(Good[0]);
        receipt.Observe(new(Operation, "sender-drain", privateCode, FailureHResult));
        var failed = receipt.Complete(124, true);
        Check.Equal(QQMusicWebSubmissionState.OutcomeUnknown, failed.State,
            "diagnostics must not upgrade unknown submission");
        Check.True(failed.Diagnostics.Contains("sender-drain", StringComparison.Ordinal),
            "fixed last stage survives an ambiguous failure");
        Check.True(failed.Diagnostics.Contains("80004005", StringComparison.OrdinalIgnoreCase),
            "HRESULT survives as a numeric diagnostic");
        Check.True(failed.Diagnostics.Contains("124", StringComparison.Ordinal),
            "helper exit code survives");
        Check.True(failed.Diagnostics.Contains("interrupted=True", StringComparison.OrdinalIgnoreCase),
            "diagnostics label interruption");
        Check.True(failed.Diagnostics.Contains("invalid-receipt=True", StringComparison.OrdinalIgnoreCase),
            "diagnostics label receipt validation");
        Check.True(!failed.Diagnostics.Contains(privateCode, StringComparison.Ordinal),
            "raw event code never enters diagnostics");
        Check.True(!failed.Diagnostics.Contains("secret-token-123", StringComparison.Ordinal),
            "diagnostics do not leak code fragments");

        var unknownStage = new QQMusicWebReceipt(Operation);
        unknownStage.Observe(new(Operation, privateStage, privateCode, FailureHResult));
        var malformed = unknownStage.Complete(2, false);
        Check.Equal(QQMusicWebSubmissionState.OutcomeUnknown, malformed.State,
            "malicious stage still fails closed");
        Check.True(!malformed.Diagnostics.Contains(privateStage, StringComparison.Ordinal),
            "unknown stage paths must not be echoed");
        Check.True(!malformed.Diagnostics.Contains("private-session", StringComparison.Ordinal),
            "unknown stage fragments must not be echoed");
        Check.True(!malformed.Diagnostics.Contains(privateCode, StringComparison.Ordinal),
            "unknown stage's code must not be echoed");

        var foreign = new QQMusicWebReceipt(Operation);
        foreign.Observe(Good[0]);
        foreign.Observe(new(Guid.Empty, privateStage, privateCode, FailureHResult));
        var stale = foreign.Complete(2, false);
        Check.Equal(QQMusicWebSubmissionState.OutcomeUnknown, stale.State,
            "foreign operation remains unknown");
        Check.True(stale.Diagnostics.Contains("dispatch-starting", StringComparison.Ordinal),
            "foreign operation cannot replace the matching last stage");
        Check.True(!stale.Diagnostics.Contains(privateStage, StringComparison.Ordinal) &&
            !stale.Diagnostics.Contains(privateCode, StringComparison.Ordinal),
            "foreign operation cannot inject raw diagnostic strings");

        var absent = new QQMusicWebReceipt(Operation).Complete(null, true);
        Check.Equal(QQMusicWebSubmissionState.OutcomeUnknown, absent.State,
            "diagnostics do not reinterpret a lost receipt as rejection");
        Check.True(!string.IsNullOrWhiteSpace(absent.Diagnostics),
            "lost receipt still includes fixed diagnostic fields");
    }

    private static void AssertUnknown(IEnumerable<QQMusicWebBridgeEvent> events,
        int? exit, bool interrupted, string label) =>
        AssertResult(events, exit, interrupted, QQMusicWebSubmissionState.OutcomeUnknown, label);

    private static void AssertResult(IEnumerable<QQMusicWebBridgeEvent> events,
        int? exit, bool interrupted, QQMusicWebSubmissionState expected, string label)
    {
        var receipt = new QQMusicWebReceipt(Operation);
        foreach (var item in events) receipt.Observe(item);
        var result = receipt.Complete(exit, interrupted);
        Check.Equal(expected, result.State, label);
        var expectedCode = expected switch
        {
            QQMusicWebSubmissionState.RejectedBeforeDispatch => "bridge-rejected-before-dispatch",
            QQMusicWebSubmissionState.SubmittedUnverified => "transport-returned-observation-required",
            _ => "web-dispatch-outcome-unknown"
        };
        Check.Equal(expectedCode, result.Code, $"{label}: machine-readable reason");
    }
}
