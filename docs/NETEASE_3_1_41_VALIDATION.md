# NetEase 3.1.41.205529 native bridge validation

Local adaptation, 2026-09-22. This document records a compatibility profile for
the installed Windows x64 player. It is not a release or publication record.
Playback and queue changes are left to the user's manual test.

## Measured player and CEF build

| Item | Observed value |
| --- | --- |
| Player | `3.1.41.205529`, Windows x64 |
| `cloudmusic.exe` SHA-256 | `16689D9731121F0F15116205D73A472748DE932B6A848E36B55B60501CBF0EDE` |
| `libcef.dll` SHA-256 | `A2A441490A9600F7E06B8C4DC609354FF7214ADD445A55C8FA699760D264E210` |
| `cef_version_info(0..7)` | `91, 2, 3, 2377, 91, 0, 4472, 169` |
| `cef_api_hash(0)` | `135912956b05b8d66775a091c5f7f17ae26eb09c` |
| `cef_api_hash(1)` | `0e4b5d3ff0027a5cf6e295b37d9b8b3dd2e80b7e` |
| `cef_api_hash(2)` | `326865300051196bd4a8e991bab2aa493c9c9a8f` |

The export implementations were read from the installed file without loading
it into another executable: `cef_version_info` at RVA `0x3c00` reads eight
integers from RVA `0x7a260dc`; `cef_api_hash` at RVA `0x3c20` reads the three
string pointers from RVA `0x7a26100`. The profile records raw export indices
instead of assigning potentially misleading universal/platform labels to
NetEase's custom build.

## Source and binary ABI evidence

The upstream GitHub commits API did not resolve `3268653` (HTTP 422). It must
not be described as an audited upstream commit or an upstream header update.
The current build continues using the existing, pinned upstream CEF headers:
[`04c8d5653ca5b3e36b693d871cce1ef09617bfc9`](https://github.com/chromiumembedded/cef/tree/04c8d5653ca5b3e36b693d871cce1ef09617bfc9).

The local `cef_browser.h`, `cef_devtools_message_observer.h`, and
`cef_registration.h` were compared byte-for-byte with that upstream commit
and matched. Their SHA-256 hashes are respectively:

- `DFB6E9875C071603E75FC6A7C1809896791668082087C2AB46D45AB33C1DAAB5`
- `659A91E0DE0BD391C9F3CCF57E40994BB9D849846F3035A3D74FC2B042C9FDC5`
- `029D03577D097990E4C97CA59CE3C891F34E7EF1468F4E3793CDD5348605D39F`

Relevant upstream declarations and implementations:

- [`browser_platform_delegate.h`](https://github.com/chromiumembedded/cef/blob/04c8d5653ca5b3e36b693d871cce1ef09617bfc9/libcef/browser/browser_platform_delegate.h)
  stores `web_contents_` before `browser_`, following the delegate vptr.
- [`cef_browser.h`](https://github.com/chromiumembedded/cef/blob/04c8d5653ca5b3e36b693d871cce1ef09617bfc9/include/cef_browser.h)
  declares host methods `SendDevToolsMessage`, `ExecuteDevToolsMethod`, and
  `AddDevToolsMessageObserver` consecutively at slots 21, 22, and 23.
- [`browser_host_base.cc`](https://github.com/chromiumembedded/cef/blob/04c8d5653ca5b3e36b693d871cce1ef09617bfc9/libcef/browser/browser_host_base.cc)
  implements those methods and the DevTools manager dispatch.
- [`cef_devtools_message_observer.h`](https://github.com/chromiumembedded/cef/blob/04c8d5653ca5b3e36b693d871cce1ef09617bfc9/include/cef_devtools_message_observer.h)
  declares the five observer callbacks; returning true from the raw message
  callback suppresses ABI-sensitive typed result/event dispatch.

Read-only inspection of the running player's `CefBrowserWindow` found its
`GWLP_USERDATA` delegate and the host pointer at delegate offset `0x10`.
The host vtable and called functions were located inside the loaded libcef:

| Object or method | Module-relative RVA |
| --- | --- |
| Host vtable | `0x8322880` |
| Host slot 21: `SendDevToolsMessage` | `0x34b5b90` |
| Host slot 22: `ExecuteDevToolsMethod` | `0x34b5dc0` |
| Host slot 23: `AddDevToolsMessageObserver` | `0x34b5f30` |

MSVC `dumpbin /disasm` verified the call ABI in this exact DLL:

- Slot 21 checks the message in `rdx` and size in `r8`, retains the host in
  `rcx`, and forwards those values to its DevTools manager. The function
  refers to its `SendDevToolsMessage` diagnostic string at RVA `0x832407f`
  and `../../cef/libcef/browser/browser_host_base.cc` at RVA `0x8323fe0`.
- Slot 23 saves `rdx` as hidden return storage, accesses the observer directly
  through `r8` (including its virtual-base refcount dispatch), and writes the
  registration pointer to the saved return storage. This confirms the
  existing `AddDevToolsMessageObserverAbi` thunk remains necessary; passing
  the standalone MSVC `CefRefPtr` by value would introduce an extra pointer
  level.

This is evidence for the bridge's used ABI subset, not a claim that every
CEF API is unchanged. The new profile therefore accepts only the measured
eight-part CEF version, both hash strings, full commit string, and the exact
host vtable plus two called method RVAs. All pointers must also pass the
existing readable-memory and libcef executable-page checks. A different
build requires review and a separate profile.

## Runtime readiness and regression coverage

The new profile does not report `READY` merely because `cef_post_task` or
`SendDevToolsMessage` accepted a request. It first installs the observation
binding and requires a real `Runtime.bindingCalled` message through the
native observer with the matching binding name and a nonempty event payload.
The bounded initial wait leaves the bridge waiting when a callback is late;
the watcher can subsequently finish the proof. `HELLO` reports the selected
profile and `validation=profile+devtools-callback`.

The original 91.2.2 profile and its same-API-hash patch support remain. Its
patch path now also needs callback proof. Unknown hash pairs are rejected;
the 3.1.41 profile additionally rejects every changed version component,
missing/changed commit, or changed host/call-target RVA. Commands cannot
clear a terminal `REFUSED` state.

Local validation completed:

- `native/Netease/test-compatibility.ps1`: 25 C++ assertions passed, including
  known legacy/new profiles, unknown or reversed hashes, all eight version
  components, commit checks, and each pinned host/call-target RVA.
- `native/Netease/build-native.ps1`: Release x64 build passed with `/W4 /WX`.
- Native source `git diff --check`: passed.

## Local integration result

Completed on 2026-09-22 against the existing Awoo MusicBot `1.2.3` session:

- Full `BiliNCM.Connectors.slnx` Release build passed with zero warnings/errors.
- NetEase track/search harness passed 13 assertions; catalog-v2 policy passed.
- Framework-dependent package `3.1.41.205529.1` passed ping/shutdown using the
  application's existing private x64 .NET `8.0.29` runtime.
- The package was installed in a new local version directory. The old package
  and activation descriptor were backed up, and the existing core reconnected
  without restarting or switching its selected player.
- CloudMusic was restarted to unload the old bridge. The loaded new bridge's
  SHA-256 matched the published local build output:
  `6C1F5EAB5A06B980227197EEDBC5B673AE801F3D1FACD4815BFFA07B31B57677`.
- The live handshake returned `OK READY cef=91.2.3+4472.169
  profile=netease-3.1.41.205529 validation=profile+devtools-callback
  route=internal-devtools events=ready`. Native diagnostics reported exception
  code/address zero and real Redux track/heartbeat events.
- The core automatically dispatched its pre-existing queued request after
  reconnecting. Its log confirmed `PlaySelected` reached the requested track,
  and the queued item became the current requested song. No test playback
  command or queue edit was sent by the integration scripts.

This is a local test installation, not a signed/public release. Manual request
and next-song insertion acceptance remains with the user. Runtime evidence and
activation backups are retained in the parent workspace's
`.tmp/netease-3.1.41-local/` directory; no personal runtime data is committed.
