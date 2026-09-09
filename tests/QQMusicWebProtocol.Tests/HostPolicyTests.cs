using System.Xml.Linq;
using System.Text.RegularExpressions;
using QQMusicControlPoc;

internal static class HostPolicyTests
{
    // Synthetic paths only. These tests never inspect an installed runtime,
    // execute an apphost, load QQ, or access a player process.
    private static readonly string Root = Path.DirectorySeparatorChar == '\\'
        ? @"C:\synthetic-web-host-tests"
        : "/synthetic-web-host-tests";
    private static readonly string ApplicationDirectory = Path.Combine(Root, "ordinary-app");

    public static int Run()
    {
        Check.Equal("Awoo.QqWebBridge.exe", QQMusicWebHostPolicy.HelperExecutableName,
            "dedicated helper apphost name");

        foreach (var path in new string?[]
        {
            null, "", " \t ", "QQMusic.exe", "Awoo.Connector.QQMusic.exe",
            Path.Combine(ApplicationDirectory, "aWoO.CoNnEcToR.qQmUsIc.ExE"),
            Path.Combine(Root, "prefix-QQMUSIC.EXE-suffix", "Awoo.QqWebBridge.exe"),
            Path.Combine(Root, "QQMusic.exe.backup", "nested", "dotnet.exe")
        })
        {
            Check.True(!QQMusicWebHostPolicy.IsExternalApiHost(path!),
                "empty or full-path internal-host substring is rejected");
        }

        foreach (var path in new[]
        {
            QQMusicWebHostPolicy.HelperExecutableName,
            Path.Combine(ApplicationDirectory, QQMusicWebHostPolicy.HelperExecutableName),
            Path.Combine(ApplicationDirectory, "AWOO.QQWEBBRIDGE.EXE"),
            Path.Combine(Root, "dotnet", "dotnet.exe"),
            Path.Combine(Root, "QQMusic", "Awoo.QqWebBridge.exe")
        })
        {
            Check.True(QQMusicWebHostPolicy.IsExternalApiHost(path),
                "unambiguous external helper path is accepted");
        }

        foreach (var path in new[]
        {
            Path.Combine(Root, "runtime", "dotnet.exe"),
            Path.Combine(Root, "runtime", "DoTnEt.ExE"),
            Path.Combine(Root, "runtime", "dotnet")
        })
        {
            Check.True(QQMusicWebHostPolicy.IsDotnetHost(path), "dotnet host detection");
            Check.Equal(path, QQMusicWebHostPolicy.SelectExecutable(path, ApplicationDirectory),
                "dotnet host is retained exactly");
        }
        Check.True(!QQMusicWebHostPolicy.IsDotnetHost(Path.Combine(Root, "dotnet.exe", "other.exe")),
            "dotnet in a parent directory is not the host executable");
        Check.True(!QQMusicWebHostPolicy.IsDotnetHost(Path.Combine(Root, "notdotnet.exe")),
            "host name comparison is exact rather than a dotnet substring");

        var selected = Path.Combine(ApplicationDirectory, QQMusicWebHostPolicy.HelperExecutableName);
        foreach (var original in new[]
        {
            Path.Combine(ApplicationDirectory, "Awoo.Connector.QQMusic.exe"),
            Path.Combine(ApplicationDirectory, "aWoO.CoNnEcToR.qQmUsIc.ExE"),
            Path.Combine(ApplicationDirectory, "arbitrary-host.exe"),
            selected
        })
        {
            Check.Equal(selected, QQMusicWebHostPolicy.SelectExecutable(original, ApplicationDirectory),
                "an apphost always selects the dedicated bridge in the application directory");
        }

        RejectInternalRoute(() => QQMusicWebHostPolicy.SelectExecutable(
            Path.Combine(ApplicationDirectory, "Awoo.Connector.QQMusic.exe"),
            Path.Combine(Root, "mixed-QqMuSiC.eXe-parent", "app")),
            "renaming only the helper cannot bypass an internal-host parent-directory match");
        RejectInternalRoute(() => QQMusicWebHostPolicy.SelectExecutable(
            Path.Combine(Root, "qqmusic.exe-runtime", "dotnet.exe"), ApplicationDirectory),
            "a retained dotnet host must still pass the full-path check");

        return CheckPackagingSource() + CheckBridgeSource();
    }

    private static void RejectInternalRoute(Action operation, string label)
    {
        string? code = null;
        try { operation(); }
        catch (InvalidOperationException error) { code = error.Message; }
        Check.Equal("helper-host-name-selects-internal-route", code, label);
    }

    private static int CheckPackagingSource()
    {
        // This is a source contract, not an apphost launch or a publish smoke test.
        // The test project copies only the production XML for static inspection.
        var before = Check.Count;
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "QQMusic.production.csproj"));
        foreach (var (targetName, afterTarget, outputProperty) in new[]
        {
            ("CopyQQWebBridgeBuildHost", "Build", "TargetDir"),
            ("CopyQQWebBridgePublishHost", "Publish", "PublishDir")
        })
        {
            var target = project.Descendants("Target")
                .SingleOrDefault(item => (string?)item.Attribute("Name") == targetName)
                ?? throw new InvalidOperationException($"Missing packaging source target: {targetName}");
            var copy = target.Element("Copy")
                ?? throw new InvalidOperationException($"Missing Copy in {targetName}");
            Check.Equal(afterTarget, (string?)target.Attribute("AfterTargets"),
                "helper copy runs for the intended build/publish phase");
            Check.Equal($"$({outputProperty})$(AssemblyName).exe", (string?)copy.Attribute("SourceFiles"),
                "helper reuses the connector apphost without changing its managed identity");
            Check.Equal($"$({outputProperty}){QQMusicWebHostPolicy.HelperExecutableName}",
                (string?)copy.Attribute("DestinationFiles"), "packaged helper name agrees with selection policy");
            Check.Equal("true", (string?)copy.Attribute("SkipUnchangedFiles"),
                "helper copying preserves unchanged artifacts");
        }
        return Check.Count - before;
    }

    private static int CheckBridgeSource()
    {
        // Source contracts only: the production host is copied as text, never
        // compiled into this harness, launched or used to load a native library.
        var before = Check.Count;
        var protocol = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "QQMusicWebProtocol.source.txt"));
        var host = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "QQMusicWebBridgeHost.source.txt"));
        var transport = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "QQMusicWebPlayTransport.source.txt"));
        Check.True(protocol.Contains("IsDispatchIntentAllowed(QQMusicWebIntent intent) => intent == QQMusicWebIntent.InsertNext"),
            "runtime host intent policy is an explicit insertion-only allowlist");
        Check.True(host.Contains("!QQMusicWebProtocol.IsDispatchIntentAllowed(request.Intent)") &&
            host.IndexOf("ValidateRequest(request);", StringComparison.Ordinal) < host.IndexOf("LockAndValidateApi(request)", StringComparison.Ordinal) &&
            host.IndexOf("BuildRequest(request.Song, request.Intent)", StringComparison.Ordinal) < host.IndexOf("QQMusicWebApiClient.Open(", StringComparison.Ordinal),
            "host rejects retired/unknown intents before loading the API or creating a client");
        Check.True(transport.Contains("QQMusicWebIntent intent = QQMusicWebIntent.InsertNext") &&
            transport.IndexOf("BuildRequest(song, intent)", StringComparison.Ordinal) < transport.IndexOf("Process.GetProcessById(", StringComparison.Ordinal) &&
            !protocol.Contains("QQMusicWebIntent.PlaySelected") && !host.Contains("QQMusicWebIntent.PlaySelected") &&
            !transport.Contains("QQMusicWebIntent.PlaySelected"),
            "parent preflight and all host dispatch surfaces have no retired playback intent");
        Check.Equal(1, Regex.Matches(host, @"\bclient\.SendOnce\(").Count,
            "one isolated insertion helper contains only one native write call");
        Check.True(host.Contains("DrainMilliseconds = 6_000;") && host.Contains("DeadlineMilliseconds = 15_000;") &&
            transport.Contains("deadline.CancelAfter(TimeSpan.FromSeconds(17))"),
            "sender fallback drain and established helper/parent deadlines remain unchanged");
        Check.True(Regex.IsMatch(host, @"(?s)using \(var client = QQMusicWebApiClient.Open\(request.ApiDllPath\)\).*?PumpForDrain\(clock, drainIdentity\);.*?stage = ""client-dispose"";\s*\}\s*CheckDeadline\(clock\);\s*stage = ""drain-completed"";\s*Write\(new\(request.OperationId, stage, ""observation-required""\)\);\s*exitCode = 0;.*?finally \{ completed.Set\(\); \}"),
            "normal completion finishes audited/fallback drain and owning-STA Dispose before flushed final receipt and completion signal");
        Check.True(host.Contains("Console.Out.Flush();") &&
            Regex.IsMatch(host, @"(?s)if \(!completed.Wait\(remaining\)\).*?ExitSelf\(124\);\s*\}.*?ExitSelf\(exitCode\);\s*return exitCode;"),
            "normal self exit follows the completion wait; watchdog uses the same self-only exit path");
        Check.True(Regex.Matches(host, @"TerminateProcess\(GetCurrentProcess\(\), unchecked\(\(uint\)exitCode\)\)").Count == 1 &&
            !Regex.IsMatch(host, @"\bOpenProcess\(|\.Kill\(|TerminateProcess\(request\.") &&
            host.Contains("Environment.Exit(exitCode);"),
            "termination uses only this helper's pseudo-handle with a local fallback, never the QQ PID");
        Check.True(Regex.IsMatch(host, @"(?s)catch \(Exception error\)\s*\{\s*exitCode = 2;.*?TryWrite\("),
            "cleanup or reporting errors cannot leave a success exit code");
        return Check.Count - before;
    }
}
