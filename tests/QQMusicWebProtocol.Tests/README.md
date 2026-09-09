# Pure QQ Web protocol, receipt and host-policy tests

This dependency-free `net8.0` console harness links only pure protocol/catalog/end/guard/receipt/host-policy
source. It must not link the COM client, process launcher, Web transport or
production connector entry point. It never sends its constructed strings,
activates QQ, reads player state, accesses the network or starts helper processes.

The tests lock the proposed single-song JSON shape, conservative receipt
classification and dedicated external-helper selection. Eight additional source
checks read a copied production project XML to ensure its Build/Publish copy
targets use the same helper name. They do not execute either target or a helper.
These tests do **not** demonstrate QQ's acceptance, song resolution,
actual playback, queue preservation, or cross-version compatibility.

Run from the connector repository, using an installed .NET 8 SDK/runtime:

```powershell
dotnet restore tests/QQMusicWebProtocol.Tests/QQMusicWebProtocol.Tests.csproj --configfile tests/QQMusicWebProtocol.Tests/NuGet.Config
dotnet run --project tests/QQMusicWebProtocol.Tests/QQMusicWebProtocol.Tests.csproj --no-restore
```

NuGet sources are cleared: the harness has no package dependencies. Restore
and build outputs remain within this test project's normal `obj` / `bin` paths.

If the sandbox denies access to the user's NuGet configuration, use an isolated
PowerShell subprocess environment rooted in this project's `obj` directory:

```powershell
$webTestRoot = Join-Path (Get-Location) 'tests\QQMusicWebProtocol.Tests'
$env:APPDATA = Join-Path $webTestRoot 'obj\isolated-appdata'
$env:DOTNET_CLI_HOME = Join-Path $webTestRoot 'obj\isolated-dotnet'
$env:NUGET_PACKAGES = Join-Path $webTestRoot 'obj\isolated-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
New-Item -ItemType Directory -Force -Path $env:APPDATA,$env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES | Out-Null
dotnet restore tests/QQMusicWebProtocol.Tests/QQMusicWebProtocol.Tests.csproj --configfile tests/QQMusicWebProtocol.Tests/NuGet.Config
if ($LASTEXITCODE -eq 0) {
    dotnet run --project tests/QQMusicWebProtocol.Tests/QQMusicWebProtocol.Tests.csproj --no-restore
}
```

The protocol/receipt baseline contains 454 assertions. This includes numeric UInt32 IDs above
Int32.MaxValue, exact song-type preservation, JSON Unicode/escaping roundtrips,
invalid input boundaries, complete-but-unverified receipts, partial/lost/
interrupted receipt ambiguity, out-of-order/duplicate/foreign events, and
explicit pre-dispatch failure evidence. Diagnostic tests retain fixed stage,
HRESULT, exit/interruption and receipt-validity fields without echoing arbitrary
event code, unknown stage paths or strings injected by a foreign operation.
These synthetic checks do not replay any live attempt. No player implementation
is executed.

Host-policy regressions cover the original connector name selecting QQ's internal
route, case-insensitive full-path matching (including a parent directory), the
dedicated `Awoo.QqWebBridge.exe` selection, unchanged dotnet host selection, and
the absence of a basename-only exception for a dotnet host in an unsafe directory.
All host paths are synthetic; no runtime or player installation is inspected.

Validated result after adding host-policy coverage: **490 assertions pass** —
454 existing protocol/receipt assertions, 28 pure host-policy assertions, and
8 packaging source checks. Restore used the isolated environment above with
cleared package sources; no dependency installation or native execution occurred.

Result after catalog projection and end-boundary additions: **2424
assertions pass**, including the same eight packaging source checks. Metadata
checks cover real-field projection, file MID independence, Int32 format sizes,
UInt32/UInt64 action flags, absent legacy-pay mapping and source-document
lifetime. End-policy checks cover time geometry, invalid/overflow boundaries
and tail-to-start rollover candidates. They do not turn a geometry match into
proof of natural completion; the adapter still enforces event freshness,
playing state, matching metadata/epoch and single consumption. A manual seek
from the very end to the beginning remains indistinguishable from a loop.

Current result after guard-registration coverage: **2510 assertions pass**.
The 86 added checks invoke the policy used by the adapter: paused/stale Playing
reservation without evidence, invalid identity/geometry/timestamps, pause-first
decision ordering and first-fresh-sample seed-only resume sequences. These are
pure policy tests, not proof of live playback. The separate source-contract
suite checks adapter wiring and consume-before-send behavior.
