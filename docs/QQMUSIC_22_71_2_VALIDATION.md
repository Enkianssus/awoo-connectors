# QQ Music 22.71.2 validation

The initial local draft was based on connector commit
`ddfd5f5754c014f090056d16bc1c9430cd6dfd81` (22.71.1), exported into an isolated
source tree on 2026-10-02. Its build and installation did not modify the dirty
connector checkout or publish a release. The 22.71.2 release preparation later
copied its reviewed changes into a separate clean checkout of the same commit.

## Behavior

- For the exact reviewed QQ 22.71 client/common/API hashes, the existing native
  AddSongs insertion uses the Type 4 WM_COPYDATA sender in place of launching
  the QQ executable. Positive UInt32 song IDs of song type 0 are supported.
  Nonzero types are rejected before mutation; they are never coerced to 0.
- A sender return is not an insertion acknowledgement. The bounded native
  observation continues after an uncertain send result, without retrying.
- Every trampoline exposed through a patch attempt remains allocated until QQ
  exits. Stage 5 precedes the final register/flag restoration and return.
- Durable pending/complete/rejected/blocked records, exact process epoch and
  executable identity, and an exclusive file lease prevent an uncertain request
  from being retried after the connector restarts.
- Original profiles, required/optional analysis checks, and diagnostic candidate
  output are preserved. Search caching is confined to a single PE image instance.
- The native protocol remains version 1 and the minimum core remains 1.1.10.

## Prior manual evidence and performance

The user verified the isolated Type 4 test window's insertion and repeated
operations. Before installation, the experimental journal contained five completed
operations for the observed QQ process; the latest helper took 570 ms and the
test window reported 687 ms total. The idle window was then closed. This supports
the tested Lemon/type-0 route; it is not an end-to-end test of the packaged adapter
or proof covering every song, account, or future QQ build.

Seven offline full-analysis comparisons measured 408–508 ms before and 23–34 ms
after, with all analysis/profile output equal except timestamps. An independent
comparison measured 468.24 ms versus 47.61 ms. Final compiled window-reader
comparisons measured 198–205 ms before and 4.35–4.64 ms after, selecting the same
HWND and title. These are local phase timings, not a guaranteed insertion SLA.

## Package validation record

The adjacent build logs and package manifest record full solution build, relevant
QQ harnesses/policies, new Type 4 lifecycle checks, performance equivalence tests,
framework-dependent publish, and ping/shutdown smoke results for the final bytes.
The package uses the existing private x86 .NET 8 runtime. Installation preserves
the previous version and backs up active.json before switching the running core.
No song is sent by build, smoke, installation, or connection verification.

## Release preparation validation (2026-10-03)

- Full clean `BiliNCM.Connectors.slnx` Release build: 0 warnings and 0 errors.
- QQ metadata/window-title, wrong-next recovery, external profiles and connector
  runtime shutdown harnesses passed.
- Type 4 lifecycle harness: 117 checks passed with zero process reads, sends or
  patch writes. Native analysis equivalence harness: 2,409 checks passed without
  process or UI actions. Both are included in the QQ release workflow.
- Playback-anchor, native-next/Type 4 lifecycle and v2 catalog policies passed.
- Framework-dependent win-x86 package version/profile checks and ping/shutdown
  smoke passed using the private .NET x86 8.0.29 runtime. Type 4 is compiled into
  the connector assembly; no separate transport helper is required.
- The release workflow retains exactly three signed-package assets and the
  minimum core remains 1.1.10. Frozen catalogs and previous QQ 22.71.1 assets were
  snapshotted before release for post-publication comparison.

These local checks do not claim full end-to-end coverage of the packaged adapter
against all songs or accounts. Every patch-exposed operation retains 4 KiB in QQ
until that process exits; the durable journal grows with operations. New or
unreviewed QQ builds do not inherit Type 4 authorization. Release completion
additionally requires successful GitHub Actions, catalog propagation, signature
and hash checks, public full/Range downloads and current-core installation.
