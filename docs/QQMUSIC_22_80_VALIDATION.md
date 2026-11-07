# QQ Music 22.80 profile-only update

Prepared 2026-11-07 for profile pack `1.5.0`, supporting installation
`QQMusic2280.02.25.56` (file version `22.80`, x86).

## Compatibility and distribution boundary

This update adds data for the existing `qqmusic-addsongs-cstring-v1` /
`type4-songid-v1` templates. No production connector source, connector version,
core version, VIP permission, user QQ setting or queue algorithm changes.
The published connector remains `22.76.1`; the minimum core remains `1.2.5`.
New unknown builds still require exact calibration; profiles cannot supply code,
new calling conventions or arbitrary virtual-method slots.

Exact fingerprints:

```text
QQMusic.dll       8F8671708430EA1C7B536008976894A1BC4A9633DDE44344ADEEE30100188072
QQMusicCommon.dll E7D0FBBE1CCC798D3AA6DFFD955ADA8AB140FA5673750140E9FF97BCA14196D2
QQMusicApi.dll    85B8AAA692C310E14F2F0235B45FFDBB2369B28213FDBC979FA084739CA39AAB
```

## Static evidence

Independent old-only code-window mapping and semantic playsong/menu-chain
discovery agree on the profile addresses. The old 22.76 DLLs were verified
against all three existing profile hashes before comparison.

The release gate is scoped review of the consumed ABI, not a claim that the
entire 22.80 program is identical to 22.76:

- Full AddSongs: 900 instructions / `0xAFC`; four reviewed changes are a log
  source line, an internal virtual slot `0x60 -> 0x70`, and two internal
  allocations `0xD78 -> 0xD88`. The fixed external calling convention remains.
- SongItem constructor/destructor (407/342 bytes), list root/category count,
  Common exports, CString manager (241 bytes) and nil getter (8 bytes) preserve
  the consumed layouts. Nil acquisition uses manager slot `0x0C`, then a
  16-byte CStringData header; AddSongs consumes that native reference.
- Actual IID/COM map and constructor-assigned IQMCatMgr vtable preserve object
  offset `0x14`, public GetSongInfo slot `0x34` (`ret 0x1C`), and Release slot
  `0x08` (`ret 4`). Internal downstream slot/member changes are not used as
  hardcoded offsets in the connector's fixed template.
- Full menu/configurable callers and switch tables retain the required call
  shape after explicitly reviewing internal changes. The 22 AddSongs unwind
  states remain; two sized-delete constants track the allocation size change.
- Type4: 42 exact API witnesses and 21 complete normalized windows pass,
  including serialization, decoding, sender and receiver evidence.
- Hidden global: all 84 references remain, but only 49 of the 84 forward
  128-byte windows normalize identically. The global is independently resolved
  by neighborhood evidence and the playsong/list-helper chain; this is not
  presented as 84 identical windows or whole-program equivalence.

The offline assertion script passed. Arbitrary transitive functions, every
relocated data object and every exception-helper closure are outside its scope.

The production read-only analyzer rejected 22.80 without the new profile; with
the new profile, all 26 required checks and the optional menu check passed.

## Automated checks

Full connector solution Release build passed with zero warnings/errors.
The unchanged search, title/artwork, playback-anchor, wrong-next, Type4 journal,
automation, native-support, performance and shutdown suites passed. New tests:

- External profile loading: real 22.80 fixture installed after first load,
  reload invalidation, exact version and all three DLL hashes, wrong/missing/
  mixed fingerprints rejected, earlier profiles retained; 78 malformed inputs.
- Native ABI: 2,105 managed emitted-x86 simulation checks including the real
  22.71, 22.76 and 22.80 profiles and an artificial future relocation case.
- Profile catalog: nine-profile retention, schema 2 / minimum connector
  22.76.1, 1.5.0 generation, immutable legacy entry and safe release workflow.

## Live acceptance

The already installed official 22.76.1 package was verified against its signed
Release ZIP; all four installed EXE/DLL files matched. A separate background
process used that unchanged executable plus the new external profile. Read-only
probe/search succeeded, reported QQ 22.80 and enabled native insertion support.

One user-authorized background `InsertNext` requested Shelter, song ID
`108031940`, through that unchanged signed executable. It returned `accepted`
in 4301 ms. The durable process-epoch receipt confirms:

- exactly one attempted/delivered send, stage 5, resolved target ID;
- successful CatMgr/GetSongInfo results and the connector's completion sentinel;
- original dispatch restored, current song and foreground unchanged;
- retained callback memory under the existing lifetime policy, no restart required;
- clean connector shutdown (`stopped=true`, `drained=true`).

No mouse/keyboard automation, Next, queue clear, QQ setting changes or change
to the bot's NetEase selection occurred. The bot remained connected to NetEase,
with empty request queue and unchanged autoplay/accepting values. The user
confirmed the next position was correct, the original queue was retained,
insertion stayed silent/no focus steal, and Shelter played normally without skipping.

One insertion is not exhaustive live coverage of automatic end-of-song guards,
all VIP tracks, repeat requests or every older player. The 4301 ms observation
includes command processing and state checks, not a universal latency guarantee.

## Publication and installation gates

Publish only `qqmusic-profiles-v1.5.0`: two alias ZIPs plus their signature and
hash sidecars (six assets). The v2 connector catalog and frozen v1 catalog must
remain unchanged. Only `profiles.qqmusicV2` advances; frozen `profiles.qqmusic`
1.3.0 remains untouched. All eight old profile documents retain their bytes.

Verify Action success, catalog bot commit, ordinary public catalog propagation,
both Ed25519-signed ZIPs, sidecars, hashes/sizes, full and Range downloads and
the current core's profile installation path. Old-cache cleanup is skipped for
this release. Existing installed connector binaries and previous profiles are
retained. No stronger future-update guarantee is claimed.
