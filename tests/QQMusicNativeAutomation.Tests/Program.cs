using UnifiedPlayerControlPoc;

var checks = 0;
void Check(bool value, string name)
{
    checks++;
    if (!value) throw new InvalidOperationException(name);
}
QQMusicNativeEnsureResult Result(QQMusicNativeInsertionState state) =>
    new(state, true, state.ToString(), null, null);
async Task ExpectCancel(Func<Task> action, string name)
{
    try { await action(); }
    catch (OperationCanceledException) { Check(true, name); return; }
    throw new InvalidOperationException("Expected cancellation: " + name);
}
var epoch = new QQMusicProcessEpoch(123, 639000000000000000);
var accepted = Result(QQMusicNativeInsertionState.Accepted);
var rejected = Result(QQMusicNativeInsertionState.SafeRejected);
var uncertain = Result(QQMusicNativeInsertionState.Uncertain);

foreach (var state in Enum.GetValues<QQMusicNativeInsertionState>())
{
    var gate = new QQMusicNativeAutomation();
    gate.ObserveProcess(epoch);
    var sent = new List<string>();
    var stops = 0;
    var prepared = await gate.PreparePlaybackAsync((command, token) =>
    {
        sent.Add(command); return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => { sent.Add("insert"); return Task.FromResult(Result(state)); },
    () => stops++, CancellationToken.None);
    var expected = state switch
    {
        QQMusicNativeInsertionState.Accepted => new[] { "'pause'", "insert", "'next'" },
        QQMusicNativeInsertionState.SafeRejected => new[] { "'pause'", "insert", "'play'" },
        _ => new[] { "'pause'", "insert" }
    };
    Check(sent.SequenceEqual(expected), "real preparation dispatch order " + state);
    Check(stops == (state == QQMusicNativeInsertionState.Accepted ? 0 : 1), "guard stop " + state);
    Check(gate.IsBlocked == (state == QQMusicNativeInsertionState.Uncertain), "uncertainty latch " + state);
    Check(prepared.Native?.State == state, "returned state " + state);
}

{
    var gate = new QQMusicNativeAutomation();
    gate.ObserveProcess(epoch);
    gate.Block(epoch);
    var calls = 0;
    await gate.PreparePlaybackAsync((command, token) => { calls++; return Task.FromResult(new QQMusicAutomaticCommandResult(true, command)); },
        token => { calls++; return Task.FromResult(accepted); }, () => { }, CancellationToken.None);
    Check(calls == 0, "already blocked means no pause, insertion, next or play");
    gate.ObserveProcess(null);
    gate.ObserveProcess(epoch);
    Check(gate.IsBlocked, "window loss and reconnect cannot unlock");
    gate.Block(epoch);
    await gate.SendAsync(token => { calls++; return Task.FromResult(new QQMusicAutomaticCommandResult(true, "late success")); }, CancellationToken.None);
    Check(calls == 0, "late observation cannot resume controls");
    gate.ObserveProcess(epoch with { StartTimeTicks = epoch.StartTimeTicks + 1 });
    Check(!gate.IsBlocked, "new verified process epoch can recover even with recycled PID");
}

{
    var gate = new QQMusicNativeAutomation();
    gate.Block(null);
    gate.ObserveProcess(epoch);
    Check(gate.IsBlocked, "unknown blocked process identity must not infer restart");
}

{
    var gate = new QQMusicNativeAutomation();
    gate.ObserveProcess(epoch);
    var sent = new List<string>();
    var prepared = await gate.PreparePlaybackAsync((command, token) =>
    {
        sent.Add(command); return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => { gate.Block(epoch); return Task.FromResult(accepted); }, () => { }, CancellationToken.None);
    Check(sent.SequenceEqual(new[] { "'pause'" }), "late accepted result cannot bypass existing uncertainty");
    Check(prepared.Native?.AutomationBlocked == true, "late accepted is surfaced as blocked");
}

{
    var gate = new QQMusicNativeAutomation();
    var sent = new List<string>();
    await gate.PreparePlaybackAsync((command, token) =>
    {
        sent.Add(command); return Task.FromResult(new QQMusicAutomaticCommandResult(command != "'next'", command));
    }, token => Task.FromResult(accepted), () => { }, CancellationToken.None);
    Check(sent.SequenceEqual(new[] { "'pause'", "'next'", "'play'" }), "confirmed insertion + failed next can resume");
}

{
    var gate = new QQMusicNativeAutomation();
    var insertCalls = 0;
    await gate.PreparePlaybackAsync((command, token) => Task.FromResult(new QQMusicAutomaticCommandResult(false, command)),
        token => { insertCalls++; return Task.FromResult(accepted); }, () => { }, CancellationToken.None);
    Check(insertCalls == 0, "failed pause never starts insertion");
}

foreach (var rejectedInsertion in new[] { false, true })
{
    var gate = new QQMusicNativeAutomation();
    gate.ObserveProcess(epoch);
    var sent = new List<string>();
    var stops = 0;
    var prepared = await gate.PreparePlaybackAsync((command, token) =>
    {
        // The adapter repeats read-only journal admission before each automatic
        // command. A newly observed blocked journal must stop next or resume.
        if (command != "'pause'")
        {
            gate.Block(epoch);
            return Task.FromResult(new QQMusicAutomaticCommandResult(false, "journal-pending-or-blocked"));
        }
        sent.Add(command);
        return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => Task.FromResult(rejectedInsertion ? rejected : accepted),
    () => stops++, CancellationToken.None);
    Check(sent.SequenceEqual(new[] { "'pause'" }), "admission changes stop post-native command " + rejectedInsertion);
    Check(prepared.Native?.AutomationBlocked == true && stops > 0
        && prepared.Native.FailureCode == QQMusicNativeAutomation.BlockedFailureCode,
        "late journal rejection is returned as uncertain " + rejectedInsertion);
}

{
    var gate = new QQMusicNativeAutomation();
    using var canceled = new CancellationTokenSource();
    var insertCalls = 0;
    var stopped = false;
    await ExpectCancel(() => gate.PreparePlaybackAsync((command, token) =>
    {
        canceled.Cancel();
        return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => { insertCalls++; return Task.FromResult(accepted); },
    () => stopped = true, canceled.Token), "cancel after pause before insertion");
    Check(insertCalls == 0 && stopped, "no insertion or resume after between-step cancellation");
}

foreach (var state in Enum.GetValues<QQMusicNativeInsertionState>())
{
    var gate = new QQMusicNativeAutomation();
    gate.ObserveProcess(epoch);
    using var canceled = new CancellationTokenSource();
    var sent = new List<string>();
    var stopped = false;
    await ExpectCancel(() => gate.PreparePlaybackAsync((command, token) =>
    {
        sent.Add(command); return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => { canceled.Cancel(); return Task.FromResult(Result(state)); },
    () => stopped = true, canceled.Token), "cancel while native completes " + state);
    Check(sent.SequenceEqual(new[] { "'pause'" }) && stopped, "cancel stops guard and all post-native commands " + state);
    Check(gate.IsBlocked == (state == QQMusicNativeInsertionState.Uncertain), "uncertain cancellation preserves latch " + state);
}

{
    var gate = new QQMusicNativeAutomation();
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    var calls = 0;
    await ExpectCancel(() => gate.PreparePlaybackAsync((command, token) =>
    {
        calls++; return Task.FromResult(new QQMusicAutomaticCommandResult(true, command));
    }, token => { calls++; return Task.FromResult(accepted); }, () => { }, canceled.Token), "pre-cancel");
    Check(calls == 0, "pre-cancel dispatches nothing");
}

Check(QQMusicNativeAutomation.ClassifyOutcome(false, true, false, false, false, false, true, true)
    == QQMusicNativeInsertionState.SafeRejected, "only unexposed rejection is safe");
Check(QQMusicNativeAutomation.ClassifyOutcome(false, true, true, false, false, false, true, true)
    == QQMusicNativeInsertionState.Uncertain, "journal pending/restart denial is not safe rejection");
Check(QQMusicNativeAutomation.ClassifyOutcome(true, true, false, true, true, true, true, false)
    == QQMusicNativeInsertionState.Accepted, "complete retained callback can continue");
Check(QQMusicNativeAutomation.ClassifyOutcome(true, false, false, true, true, true, true, false)
    == QQMusicNativeInsertionState.Uncertain, "changed session after exposed dispatch remains uncertain");
foreach (var exposure in Enumerable.Range(0, 5))
    Check(QQMusicNativeAutomation.ClassifyOutcome(false, true, false,
        exposure == 0, exposure == 1, exposure == 2, exposure != 3, exposure != 4)
        == QQMusicNativeInsertionState.Uncertain, "each native exposure blocks resume " + exposure);
Console.WriteLine($"QQ native automation: {checks} checks passed; no process reads or player commands.");
