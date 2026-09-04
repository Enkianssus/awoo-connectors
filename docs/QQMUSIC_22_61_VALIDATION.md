# QQ Music 22.61 external profile validation

Pre-release validation performed locally on 2026-09-05 for profile pack 1.3.0
(`qqmusic-profiles-v1.3.0`). This record covers compatibility testing; publication
and post-release download/install checks are separate release gates.

## Compatibility boundary

- Player installation build: `QQMusic2261.01.18.01`, file version `22.61`, x86.
- Connector: unchanged published `qqmusic-v22.60.2` framework-dependent package.
- Profile: `profiles/qqmusic/22.61.json`, loaded through
  `BILINCM_QQMUSIC_PROFILE_DIR` before process startup.
- Both DLL SHA-256 values and the exact file version must match. Unknown DLLs
  remain rejected; this is not a wildcard or runtime pattern-scan bypass.
- The live test changed no runtime source, protocol, or core file. The subsequent
  connector 22.61.1 release updates connector metadata and bundles this same
  profile so the connector version follows the QQ 22.61 player branch.

The published 22.60.2 ZIP used by this live test had SHA-256
`fbea5a6cd20d3f466937f7494e5b2b3afae662a6cbfbf6204d255f5dd9102685`.
Its published checksum and Ed25519 signature were verified against the repository
public key before testing. The extracted package and its bundled profiles were
not modified. The small-package protocol smoke test passed with version 22.60.2.

## Static and analyzer checks

The new profile records both exact DLL hashes and every validated RVA. Two
independent static passes compared the old 22.60 and new 22.61 binaries:

- Complete `AddSongs`: `0x458F50` to `0x459280`, 900 instructions / `0xAFC`
  bytes, matching normalized instructions, layout and internal jump targets.
- Both direct callers retained their argument setup and stack cleanup.
- Complete SongItem constructor/destructor matched; the `0xA0` item size remained.
- Hidden-category global: `0xC5D1D0` to `0xC5D1C8`, all 84 reference windows
  matched; the new global is in writable, non-executable data.
- Common exports, list helpers, dispatch bytes and executable-section checks
  passed. Dispatch `0x4A7934` contains `E8 57 8D 16 00`, targeting `0x610690`.

The analyzer matched profile 22.61, passed all 22 required checks plus its optional
menu-anchor check, and returned `ExecutionAllowed=true`. A read-only analysis
after live testing passed again.

## Live test and limits

The user authorized one test-song insertion and brief track changes. A song
already in the QQ queue was started through QQ's own UI to establish a reliable
playback anchor; an idle player with no current song had correctly prevented
the earlier test attempts from inserting anything.

1. The unmodified release connected to QQ 22.61, searched successfully, and
   emitted `snapshot-events-v1` events through GSMTC and WinEventHook.
2. One `InsertNext` requested `Language - Porter Robinson`, song ID `1957690`.
   It returned `indeterminate` in 4696 ms, with the original current song still
   observed and the duplicate-prevention ledger armed. The test did not retry
   the insertion. It paused and gracefully shut down the connector.
3. QQ's own queue UI showed exactly one additional item: the requested track
   immediately after the original song. Surrounding visible items were retained.
4. A fresh unchanged connector process, without inserting again, sent `Next`.
   QQ reported the requested title and artist (1037 ms command response).
   `Previous` restored the original title and artist (1063 ms response).
5. The test sent `Pause` and received a successful command acknowledgement, then
   shut down with `drained=true`. The single test song remains in QQ's queue.

The initial response was not a clean native `verified` result. Existing 22.60.2
code only takes that `indeterminate` branch after AddSongs has returned with a
non-negative result for the requested song in the matching process session.
Additional title, foreground, stage or cleanup checks can still prevent strict
verification. That response does not expose the original diagnostic, so its
exact cause was not established. Queue inspection and subsequent Next confirmed
the insertion independently; no checks were weakened to force a passing result.

The restart deliberately discarded the pending logical guard. Consequently this
run does not claim live coverage of duplicate reuse, automatic end-of-track
guarding, wrong-next recovery or immediate `PlaySelected`. Their regression
tests passed, but they were not separately exercised live. Current-track native
metadata can come from catalog lookup, so title/artist verification is not
claimed as authoritative native song-ID readback. Timings above are individual
observations, not performance guarantees.

## Build and regression commands

Run from the connector repository; restore dependencies first if needed:

```powershell
dotnet build .\BiliNCM.Connectors.slnx -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\QQMusicPlaybackAnchorPolicy.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\QQMusicNativeNextTransportPolicy.Tests.ps1
dotnet run --project .\tests\QQMusicWindowTitleParser.Tests\QQMusicWindowTitleParser.Tests.csproj -c Release --no-restore
dotnet run --project .\tests\QQMusicWrongNextRecoveryPolicy.Tests\QQMusicWrongNextRecoveryPolicy.Tests.csproj -c Release --no-restore
dotnet run --project .\tests\QQMusicExternalProfile.Tests\QQMusicExternalProfile.Tests.csproj -c Release --no-restore
```

All passed; solution build had zero warnings and zero errors. The external-profile
test is standalone, not part of the solution build. It tests the real loader,
exact version and dual hashes, malformed documents and preserved built-in
profiles. The recovery source-contract test now normalizes line endings before
assertions so Windows CRLF does not cause a false failure; its semantic checks
are unchanged.

## Delivery and offline limitation

Profile pack 1.3.0 provides the separately versioned signed external delivery.
Do not replace its immutable assets or the existing signed 22.60.2 binary assets.
Connector 22.61.1 is a new release that follows the QQ 22.61 player branch and
bundles the same validated JSON. Its release must independently pass the v2
Catalog, signature, public download and current-core installation gates.

The current core requests the profile catalog when launching the QQ connector,
then passes the verified profile directory. The loader caches its first read,
so an already running connector must restart to see new profiles.

The current core's catalog-fetch failure path falls back to bundled connector
profiles instead of using its cached external profile directory. An offline
launch/reconnect with 22.60.2 may therefore lack 22.61 support even after a prior
successful profile download. Connector 22.61.1 embeds `22.61.json`, so once that
connector is installed it does not depend on an online profile fetch for this
exact build. The pre-existing cached-profile fallback issue remains relevant to
profile-only delivery.
