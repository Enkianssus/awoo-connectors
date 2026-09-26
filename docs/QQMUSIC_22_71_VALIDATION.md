# QQ Music 22.71 native compatibility validation

Prepared locally on 2026-09-27 for connector `22.71.1`. Publication and live
validation are separate gates; neither is implied by the read-only analyzer.

## Scope

- QQ installation build: `QQMusic2271.00.25.13`, file version `22.71`, x86.
- Baseline: released native `22.61.5`, which restored the `22.61.1` behavior.
- Production changes: one exact compatibility profile and version/support
  metadata. No changes to the native transport, queue insertion algorithm,
  forced-play/guard algorithm, native contract or QQ settings.
- Preserve all prior profiles and unknown-build refusal. No Web helper,
  wildcard hash, runtime discovery bypass, queue rebuild or privilege override.
- V2 package: Awoo framework-dependent x86 only, core floor `1.1.10`.
  Historical Web revisions `22.61.2`–`22.61.4` retain core floor `1.2.1`.

Exact new DLL SHA-256 values:

```text
QQMusic.dll
0108E68BEDA8B0AF61A71911519FA4FEB5DF02417D32F4A0993868E5157624BE
QQMusicCommon.dll
4267E1C27F7251A31460613A97F5787FD7D9D0BE3664E5E615251ABA2780471F
```

The residual 22.61 updater DLL pair was verified against both hashes in the
existing `22.61.json` before comparison. No QQ binary or user data is published.

## Static validation

Two independent offline mappings (semantic call-chain discovery and byte-window
comparison against 22.61) agree on every profile field. The semantic discovery
tool remains research-only and never grants execution permission.

- Complete AddSongs: 900 instructions / `0xAFC` bytes, matching normalized
  instruction offsets, sizes and internal branches.
- SongItem constructor: 104 instructions / `0x197` bytes; destructor: 119 /
  `0x156`; matching layout, including the `0xA0` object size.
- List root: 76 instructions / `0x11F`; category count: 123 / `0x166`.
- List helper code before its switch table: 1,943 instructions / `0x18EC`.
- All 34 helper switch targets retain their relative offsets. The hidden
  category global remains writable/non-executable, with all 84 reference
  windows matching between builds.
- Both direct AddSongs callers remain. The menu caller preserves ECX manager,
  EDX vector, a non-null empty UTF-16 context, mode zero and 8-byte caller cleanup.
  Their decoded code ranges and explicit switch tables match. AddSongs' 22
  exception-unwind states and cleanup action entry blocks also match.
- The CatMgr IID/COM map still selects the interface at object offset `0x14`.
  Its actual vtable slot `0x34` implementation matches (120 instructions / 342
  bytes, five `ret 0x1C` exits). Slot `0x08` release thunk and 40-instruction /
  114-byte release body also match, including `ret 4` cleanup.
- Common exports match the mapped addresses. GetQQUinEx retains the EDX:EAX
  return; observed immediate changes in its comparison are diagnostic line
  numbers, not account/rights values.
- Dispatch at `0x004B0CE4` is `E8 C7 91 16 00`, targeting `0x00619EB0`.
- Native analyzer: all 22 required checks and the optional menu check pass.
  Without the new profile it correctly rejected the exact same new DLLs.

Static equivalence is not a proof of future compatibility or of runtime
behavior for a different DLL pair. Exploratory decoding initially crossed
switch tables as if they were code; only corrected code ranges and separately
checked table targets form the final evidence. This is not a whole-program
control-flow or exception-unwind proof.

## Automated verification

Full `BiliNCM.Connectors.slnx` Release build: zero warnings, zero errors.
The following six suites passed:

1. `QQMusicPlaybackAnchorPolicy.Tests.ps1`
2. `QQMusicNativeNextTransportPolicy.Tests.ps1`
3. `QQMusicWindowTitleParser.Tests`
4. `QQMusicWrongNextRecoveryPolicy.Tests`
5. `QQMusicExternalProfile.Tests`
6. `ConnectorRuntimeShutdown.Tests`

External-profile tests retain the 22.61 fixture and add 22.71 exact-field,
version, dual-hash, mixed-build and malformed-document checks. Catalog v2
policy tests and `node --check scripts/update-catalog-v2.mjs` pass.

The Awoo framework-dependent package passes `ping`/`shutdown` smoke testing
as version `22.71.1`, includes the exact 22.71 profile, and contains no Web
helper or self-contained runtime. The read-only live probe connects to QQ
22.71, receives GSMTC/WinEventHook snapshots and searches Shelter successfully.

## Live validation and publication

One user-authorized insertion of `Shelter - Porter Robinson / Madeon`, song
ID `108031940`, ran through the built connector's normal `InsertNext` command.
The user first started an existing song to establish a reliable playback anchor.

- Returned `accepted`, not `indeterminate`, in 2797 ms. The current song remained
  `Lemon`; the native acceptance path requires completed stage 5, matching song
  ID, successful GetCatMgr/GetSongInfo/AddSongs results, restored dispatch code,
  released temporary memory and unchanged foreground window.
- One insertion attempt, zero retries. The harness sent no Next, PlaySelected,
  clear, delete or setting command. It shut down with `stopped=true, drained=true`.
- The user independently confirmed the item was next, the old queue remained,
  there was no focus steal and the inserted song played normally without skipping.

This single live run does not separately cover duplicate requests, every older
player profile, automatic end-of-track recovery, force play or all VIP tracks.
Those unchanged policies have regression coverage; they are not claimed as
additional 22.71 live tests. The observed latency is not a performance guarantee.

Release requires a successful workflow, exactly three signed v2 assets,
catalog bot commit, ordinary public catalog, full/Range download and current
core installation verification. This tag skips destructive old R2 cleanup;
no historical Release, signed asset, v1 catalog or profile pack is replaced.
