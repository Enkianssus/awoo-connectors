using System.Text.Json;
using System.Text.Json.Serialization;
using UnifiedPlayerControlPoc;

var checks = 0;
void Check(bool passed, string name)
{
    checks++;
    if (!passed) throw new InvalidOperationException(name);
}

var unsupported = QQMusicNativeSupportState.Unsupported("22.99");
var before = new PlayerSnapshot(true, "QQ 音乐", 123, "22.99", unsupported.Message,
    null, DateTimeOffset.UtcNow, Capabilities: unsupported.Capabilities,
    CompatibilityFailureCode: unsupported.FailureCode);

foreach (var command in Enum.GetValues<PlayerCommand>())
{
    var stops = 0;
    var result = unsupported.RejectIfUnavailable(command, before, _ => stops++);
    var requiresNative = command is PlayerCommand.PlaySelected or PlayerCommand.InterruptSelected
        or PlayerCommand.InsertNext or PlayerCommand.ArmNextGuard;
    Check((result is not null) == requiresNative, $"admission matches command {command}");
    Check(stops == (requiresNative ? 1 : 0), $"only native commands cancel the guard {command}");
    if (result is not null)
    {
        Check(result.Outcome == OperationOutcome.Unsupported && !result.IsSuccess, $"structured unsupported {command}");
        Check(result.FailureCode == "player-version-unsupported", $"core understood failure code {command}");
        Check(ReferenceEquals(result.Snapshot, before), $"preserve basic connection snapshot {command}");
        Check(!result.Message.Contains("重启"), $"unknown build never requires restart {command}");
    }
    Check(QQMusicNativeSupportState.Ready.RejectIfUnavailable(command, before,
        _ => throw new InvalidOperationException("supported admission cancelled guard")) is null,
        $"validated native contract admits {command}");
}

foreach (var state in new[] { unsupported, QQMusicNativeSupportState.Ready,
    QQMusicNativeSupportState.Disconnected, QQMusicNativeSupportState.AccessDenied,
    QQMusicNativeSupportState.Disabled })
{
    var capabilities = state.Capabilities;
    Check(capabilities.InsertNext == state.Supported && capabilities.PlaySelected == state.Supported,
        "native capability consistency " + state.FailureCode);
    Check(capabilities.Search && capabilities.Next && capabilities.Previous && capabilities.Pause
        && capabilities.Resume && !capabilities.Toggle,
        "basic controls and search remain independent " + state.FailureCode);
}

var json = JsonSerializer.Serialize(unsupported.RejectIfUnavailable(PlayerCommand.InsertNext, before, _ => { }),
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } });
using (var document = JsonDocument.Parse(json))
{
    var root = document.RootElement;
    Check(root.GetProperty("outcome").GetString() == "unsupported", "wire outcome");
    Check(root.GetProperty("failureCode").GetString() == "player-version-unsupported", "wire failure code");
    var snapshot = root.GetProperty("snapshot");
    Check(snapshot.GetProperty("connected").GetBoolean(), "wire connected remains true");
    Check(snapshot.GetProperty("compatibilityFailureCode").GetString() == "player-version-unsupported",
        "wire probe compatibility code");
    Check(!snapshot.GetProperty("capabilities").GetProperty("insertNext").GetBoolean(), "wire native ability disabled");
    Check(snapshot.GetProperty("capabilities").GetProperty("search").GetBoolean(), "wire search ability retained");
}

// Real admission policy is evaluated before every callback representing pause,
// insertion or correction; a legacy core repeating RPCs cannot trigger one.
var mutations = 0;
var guardStops = 0;
for (var attempt = 0; attempt < 10; attempt++)
{
    foreach (var command in new[] { PlayerCommand.PlaySelected, PlayerCommand.InterruptSelected,
        PlayerCommand.InsertNext, PlayerCommand.ArmNextGuard })
    {
        if (unsupported.RejectIfUnavailable(command, before, _ => guardStops++) is null) mutations++;
    }
}
Check(mutations == 0 && guardStops == 40, "repeated native requests are rejected before mutations");

var cache = new QQMusicNativeSupportCache();
var inspections = 0;
QQMusicNativeSupportState Inspect(QQMusicNativeSupportState result) { inspections++; return result; }
const string sameFiles = "pid:123:epoch:1|files:1|profile-revision:1";
Check(!cache.Get(sameFiles, () => Inspect(unsupported)).Supported, "unknown first inspection");
for (var i = 0; i < 100; i++) cache.Get(sameFiles, () => Inspect(unsupported));
Check(inspections == 1, "timeline observations do not rehash unchanged DLLs");
Check(cache.Get("pid:123:epoch:1|files:1|profile-revision:2", () => Inspect(QQMusicNativeSupportState.Ready)).Supported,
    "new effective profile recovers previously unknown player without restart");
Check(inspections == 2, "profile changes invalidate cached failure");
Check(!cache.Get("pid:123:epoch:1|files:2|profile-revision:2", () => Inspect(unsupported)).Supported,
    "file changes invalidate successful support");
cache.Get("pid:123:epoch:2|files:2|profile-revision:2", () => Inspect(unsupported));
Check(inspections == 4, "process epoch refresh survives reused PID");
var failures = 0;
try { cache.Get("read-error", () => throw new IOException("simulated read failure")); }
catch (IOException) { failures++; }
Check(cache.Get("read-error", () => Inspect(QQMusicNativeSupportState.Ready)).Supported && failures == 1,
    "transient inspection exception is not permanently cached");
var parallelInspections = 0;
var parallelCache = new QQMusicNativeSupportCache();
Parallel.For(0, 40, _ => parallelCache.Get("same", () =>
{
    Interlocked.Increment(ref parallelInspections);
    return QQMusicNativeSupportState.Ready;
}));
Check(parallelInspections == 1, "concurrent probes share one installation inspection");
Console.WriteLine($"QQMusic native support policy: {checks} checks passed.");
