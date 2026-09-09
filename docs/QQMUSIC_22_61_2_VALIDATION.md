# QQ Music 22.61.2 release preparation

The default backend uses QQ's exported Web API and preserves the existing
single-instance playback controls. It submits one native InsertNext operation;
forced playback follows a completed insertion receipt with at most one Next,
after rechecking process, current identity and position. Unknown outcomes do
not authorize a retry. No list-replacing play command or global QQ setting
change is used. Current identity comes from the typed status channel, with
agreeing media/window observations; artwork completion is not playback evidence.

This release requires Awoo MusicBot 1.2.1. The core must understand
`requiresPlaybackAnchor`, `ownsLogicalNext` and `observationDeferred`, and must
confirm guard cancellation when automatic playback is stopped. These are real
behavior dependencies; protocol version remains 1. Other connector catalog
minimums remain 1.1.10. The default backend has live coverage on QQ 22.61 only.
Older signed profiles and the explicit `AWOO_QQMUSIC_BACKEND=native` option are
retained, including the 22.60.2 native wrong-next recovery fixes. Web failures do
not switch to the native backend automatically.

## Candidate integration

The implementation originated in the local QQ investigation checkout based on
39f68e1. Its final ReadyToRun candidate DLL was
`3595E52417BF6E87F87D4033BCD19C715C242192EA252963298D5479F8C50856`.
Production changes were merged selectively onto the newer connector sources;
the 22.61 profile, native recovery state/guard IDs and existing release pipeline
were preserved. Research tools, user configuration, player binaries and old
unsigned ZIPs are excluded from this release.

Release framework-dependent publishing defaults to ReadyToRun while respecting
an explicit override. `Awoo.QqWebBridge.exe` is a byte-identical apphost copy with
a name that QQ's API recognizes as external. The package continues to require
only Microsoft.NETCore.App 8.0.0 on win-x86; it contains no private runtime and
does not require WindowsDesktop.App. All six existing QQ profiles, including
22.61, remain bundled.

## Local validation on the integrated source

On 2026-09-09:

- Full `dotnet build BiliNCM.Connectors.slnx -c Release --no-restore`: all four
  connectors passed, zero warnings/errors. The primary checkout already had
  the pinned CEF headers missing in the investigation checkout; no NetEase
  source change or validation bypass was required.
- Web protocol/receipt/host harness: 4927 assertions, including 17 packaging
  source checks; fixed status harness: 118 checks.
- Metadata and in-memory media observation harnesses, 23 artwork checks,
  native wrong-next recovery and external-profile harnesses passed.
- Web adapter/build/timing source checks: 81; native window source checks: 13.
  Existing playback-anchor and native-transport profile policies passed.
- Catalog generation tests check the QQ 1.2.1 boundary, the unchanged 1.1.10
  boundary of the other connectors, protocol 1, and preservation of unrelated
  entries. The production catalog itself is left to the signing workflow.
- Release framework-dependent publish and ping/shutdown smoke passed at
  version 22.61.2. The package has 13 files, matching public/bridge apphost
  hashes, six profiles, and no CoreCLR or hostfxr binary.

Reproduction commands (restore dependencies first if required):

```powershell
dotnet build BiliNCM.Connectors.slnx -c Release --no-restore
dotnet run --project tests/QQMusicWebProtocol.Tests -c Release --no-restore
dotnet run --project tests/QQMusicWebStatus.Tests -c Release --no-restore
dotnet run --project tests/QQMusicWindowTitleParser.Tests -c Release --no-restore
dotnet run --project tests/QQMusicWrongNextRecoveryPolicy.Tests -c Release --no-restore
dotnet run --project tests/QQMusicExternalProfile.Tests -c Release --no-restore
./tests/QQMusicWebBackendPolicy.Tests.ps1
./tests/QQMusicNativeWindowInspection.Tests.ps1
./tests/QQMusicPlaybackAnchorPolicy.Tests.ps1
./tests/QQMusicNativeNextTransportPolicy.Tests.ps1
node tests/catalog-v2-policy.test.mjs
```

## Earlier live evidence and limits

The accompanying core changes were tested with the E-drive 1.2.1 development
EXE. Normal requests inserted as native next. Forced selection and a reproduced
paused-state wrong-next case reached the exact pending song ID and consumed it
once; stopping autoplay confirmed cancellation while retaining pending requests.

The final latency candidate measured one normal insertion at 810 ms and one
forced-selection response at 1629 ms. The forced-selection response remained
indeterminate until a subsequent independent core observation confirmed the
requested ID. These small local samples do not establish audible onset timing,
sub-second forced playback, complete native queue enumeration or compatibility
with a future QQ build. Existing following songs were observed to survive;
full before/after native queue preservation was not enumerated. Preview-only
playback for a non-VIP account is not treated as full-track acceptance.

This record is preparation evidence. Publication still requires a successful
tag workflow, exactly three immutable signed assets, the catalog bot commit,
public catalog/Range/hash/signature checks and installation by the current core.
