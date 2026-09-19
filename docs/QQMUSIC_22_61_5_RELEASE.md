# QQ Music connector 22.61.5

This revision restores the native QQ connector implementation from
`qqmusic-v22.61.1` (`e05a5041593bd82bd769ccaabb56d3536d722062`).
The version increases normally; no historical tag or signed asset is replaced.

## Behavior and compatibility

- Uses the original exact-version/hash compatibility profiles and native
  insertion path. Unknown QQ updates are not automatically supported.
- Removes the Web backend and its isolated bridge from the current QQ package.
- Restores the original playback, observation, insertion and fallback behavior;
  no new insertion algorithm is introduced by this release.
- Keeps the original QQ protocol model in `LegacyPlayerContracts.cs`. Its
  contents match the old shared model exactly, allowing other connectors to
  retain their current shared contracts without a protocol regression.
- Tested profile branches: 22.22, 22.41, 22.51, 22.52, 22.60 and 22.61.
- Minimum core: 1.1.10, the framework-dependent v2 catalog floor. Historical
  Web revisions 22.61.2–22.61.4 retain their 1.2.1 behavior dependency.

## Pre-release validation

- Complete Release solution build: passed, zero warnings and errors. NetEase's
  existing native target used the workflow-pinned CEF headers and verified
  Chromium header; no NetEase source or dependency pins changed.
- Six native/runtime suites: playback-anchor policy, native-next transport
  policy, window-title metadata parser, wrong-next recovery, external profiles,
  and connector runtime shutdown all passed.
- Catalog v2 policy and generator syntax checks passed.
- Framework-dependent win-x86 publish and private .NET 8 ping/shutdown smoke
  passed and reported QQ connector 22.61.5.
- QQ production logic and profiles match 22.61.1. Project changes select version
  22.61.5 and the independent copy of the original QQ protocol model.

The release workflow publishes exactly one Awoo framework-dependent ZIP and
its signature and hash sidecars. It preserves v1 assets, catalogs, public key
identity and endpoints. Old R2 cache cleanup is skipped for this tag; uploading
this new release does not authorize deleting historical cache objects.

These checks do not claim a new live playback or foreground-window test.
