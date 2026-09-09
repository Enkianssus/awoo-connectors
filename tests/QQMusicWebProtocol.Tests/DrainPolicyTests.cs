using System.Text.RegularExpressions;
using QQMusicControlPoc;

internal static class DrainPolicyTests
{
    private static readonly QQMusicWebDrainIdentity Identity =
        new(QQMusicWebDrainPolicy.AuditedApiSha256, 0x12340000, 0x56780000, 11, 22);
    private static readonly QQMusicWebDrainWindow Window =
        new(0x7654, 11, 22, 0x12340000, "ATL:00ABCDEF", false);

    internal static void Run()
    {
        Check.True(Finish(), "matching native handler return permits sender drain completion only");
        Check.True(Finish(Identity with { ApiSha256 = Identity.ApiSha256.ToLowerInvariant() }),
            "verified hash comparison accepts equivalent hexadecimal case");
        foreach (var identity in new[]
        {
            Identity with { ApiSha256 = null! }, Identity with { ApiSha256 = "" },
            Identity with { ApiSha256 = new string('0', 64) },
            Identity with { ApiSha256 = " " + Identity.ApiSha256 },
            Identity with { ApiSha256 = Identity.ApiSha256[..^1] },
            Identity with { Module = 0 }, Identity with { Module = 1 },
            Identity with { ClientInterface = 0 }, Identity with { ClientInterface = 1 },
            Identity with { ProcessId = 0 }, Identity with { ProcessId = 12 },
            Identity with { ThreadId = 0 }, Identity with { ThreadId = 23 }
        })
            Check.True(!Finish(identity), "unknown/malformed hash or wrong owning identity keeps fallback drain");

        foreach (var observed in new QQMusicWebDrainWindow?[]
        {
            null, Window with { Handle = 0 }, Window with { Handle = 1 },
            Window with { ProcessId = 0 }, Window with { ProcessId = 12 },
            Window with { ThreadId = 0 }, Window with { ThreadId = 23 },
            Window with { ClassModule = 0 }, Window with { ClassModule = 1 },
            Window with { ClassName = null! }, Window with { ClassName = "" },
            Window with { ClassName = "ATL:" }, Window with { ClassName = "atl:00ABCDEF" },
            Window with { ClassName = "prefixATL:00ABCDEF" }, Window with { IsVisible = true }
        })
        {
            Check.True(!Finish(before: observed, explicitBefore: true),
                "missing or unowned pre-dispatch window cannot finish drain");
            Check.True(!Finish(after: observed, explicitAfter: true),
                "destroyed/reused/unowned post-dispatch window cannot finish drain");
        }
        Check.True(!Finish(after: Window with { ClassName = "ATL:DIFFERENT" }),
            "a changed ATL class after native dispatch cannot finish drain");
        foreach (var result in new nint[] { -1, 0, 2, int.MinValue, int.MaxValue })
            Check.True(!Finish(dispatchResult: result), "only native handler return one finishes drain");
        foreach (var message in new uint[] { 0, 0x12, 0x464, 0x466, uint.MaxValue })
            Check.True(!Finish(message: message), "other messages including quit do not authorize early completion");
        foreach (var workerType in new nuint[] { 0, 1, 3, uint.MaxValue })
            Check.True(!Finish(wParam: workerType), "wrong native worker discriminator keeps full drain");
        foreach (var pointer in new nint[] { 0, 1, -1, 0x56780004 })
            Check.True(!Finish(lParam: pointer), "only the original borrowed interface address matches");
        Check.True(!Finish(handle: 0), "thread messages without a window cannot finish drain");
        Check.True(!Finish(handle: 1), "a different HWND cannot borrow the observed window evidence");

        // Native x86 addresses may have the high bit set: compare identity bits,
        // never infer validity from positivity or dereference a synthetic pointer.
        var highIdentity = Identity with { Module = int.MinValue, ClientInterface = int.MinValue + 16 };
        var highWindow = Window with { ClassModule = highIdentity.Module };
        Check.True(Finish(highIdentity, highWindow, highWindow, lParam: highIdentity.ClientInterface),
            "high-bit native addresses remain exact opaque comparison values");

        var messages = new (uint Message, nuint Type, nint Pointer, nint Result)[]
        {
            (0x464, 2, Identity.ClientInterface, 1),
            (0x465, 3, Identity.ClientInterface, 1),
            (0x465, 2, Identity.ClientInterface + 4, 1),
            (0x465, 2, Identity.ClientInterface, 0),
            (0x465, 2, Identity.ClientInterface, 1)
        };
        var firstCompletion = Array.FindIndex(messages, item => Finish(
            message: item.Message, wParam: item.Type, lParam: item.Pointer, dispatchResult: item.Result));
        Check.Equal(4, firstCompletion, "noise, wrong identity and failed handlers do not simulate completion");
        Check.True(!Array.Empty<int>().Any(_ => Finish()), "quiet/empty pump provides no completion evidence");

        CheckSourceBoundary();
    }

    private static bool Finish(QQMusicWebDrainIdentity? identity = null,
        QQMusicWebDrainWindow? before = null, QQMusicWebDrainWindow? after = null,
        nint? handle = null, uint message = 0x465, nuint wParam = 2,
        nint? lParam = null, nint? dispatchResult = null,
        bool explicitBefore = false, bool explicitAfter = false) =>
        QQMusicWebDrainPolicy.CanFinishDrain(identity ?? Identity,
            handle ?? Window.Handle, message, wParam, lParam ?? Identity.ClientInterface,
            explicitBefore ? before : before ?? Window,
            explicitAfter ? after : after ?? Window, dispatchResult ?? 1);

    private static void CheckSourceBoundary()
    {
        var host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "QQMusicWebBridgeHost.source.txt"));
        var api = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "QQMusicWebApiClient.source.txt"));
        Check.True(Regex.IsMatch(host,
            @"(?s)var before = candidate \? ReadDrainWindow\(message.Window\) : null;.*?var dispatchResult = DispatchMessage\(ref message\);.*?CheckDeadline\(overallClock\);.*?CanFinishDrain\(identity,.*?before, ReadDrainWindow\(message.Window\), dispatchResult\)\).*?return true;"),
            "pump checks exact completion tuple around the real handler and rechecks the HWND after dispatch");
        Check.True(host.Contains("while (drainClock.ElapsedMilliseconds < DrainMilliseconds)") &&
            host.Contains("DrainMilliseconds = 6_000;") && host.Contains("DeadlineMilliseconds = 15_000;") &&
            host.Contains("var matchedCompletion = PumpForDrain(clock, drainIdentity);"),
            "unmatched completion keeps existing six-second drain and overall deadline");
        Check.True(host.Contains("GetClassLong(window, -16)") && host.Contains("EntryPoint = \"GetClassLongW\"") &&
            host.Contains("GetWindowThreadProcessId(window, out var processId)") && host.Contains("IsWindowVisible(window)"),
            "x86 window ownership checks use class module, native thread, PID and hidden state");
        Check.True(!Regex.IsMatch(host, @"GetWindowLong|ReadProcessMemory|PtrToStructure|Marshal.Read|Marshal.Release\(message|EnumThreadWindows"),
            "completion observation never follows message pointers or inspects ATL thunk code");
        Check.True(api.Contains("return new(module, factory, client, clientPointer);") &&
            api.Contains("finally { Marshal.Release(clientPointer); }") &&
            api.Contains("ObjectDisposedException.ThrowIf(client is null, this);") &&
            api.Contains("GetCurrentThreadId() != ownerNativeThreadId"),
            "original factory interface pointer is borrowed only while RCW is alive on its owning STA");
        Check.True(host.Contains("qq-web-drain:matched-completion") && host.Contains("qq-web-drain:fallback-timeout") &&
            Regex.IsMatch(host, @"(?s)private static void TraceDrain.*?Console.Error.WriteLine\(matchedCompletion\s*\? ""qq-web-drain:matched-completion"" : ""qq-web-drain:fallback-timeout""\);"),
            "optional trace emits fixed stderr labels, not native pointers or a new receipt event");
    }
}
