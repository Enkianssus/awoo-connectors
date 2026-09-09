# Fixed Web status channel pure tests

Run from the connector repository:

```powershell
dotnet restore tests/QQMusicWebStatus.Tests/QQMusicWebStatus.Tests.csproj --configfile tests/QQMusicWebStatus.Tests/NuGet.Config -p:NuGetAudit=false
dotnet build tests/QQMusicWebStatus.Tests/QQMusicWebStatus.Tests.csproj -c Release --no-restore
dotnet tests/QQMusicWebStatus.Tests/bin/Release/net8.0/QQMusicWebStatus.Tests.dll
```

The executable tests pure XML/numeric parsing, unknown values, duplicate/namespace/DTD
rejection, read/write budget exclusion (including concurrent reservations), request
path validation, in-memory hash/PE checks, typed operation-bound responses and source
contracts. It does not compile or execute the native helper, query QQ, launch a player,
read process memory, or access the network.

`UseAppHost=false` makes the test a dotnet-hosted DLL. Assertion exceptions are
caught at the entry point and return exit code 1 instead of an unhandled CLR crash;
passing tests return exit code 0. No system Windows Error Reporting setting changes.

The production helper is separately compiled by the QQ connector project. Its fixed
1074 queries are `QueryPlayStatus` and `QueryPlayQueueSetting`; the latter's exact
case-sensitive `Setting` attribute is documented in
`docs/QQMUSIC_WEB_QUEUE_PROTOCOL_RESEARCH.md` (QQMusic.dll 0x4C6B29).
The helper is STA with a native message pump and a four-second watchdog; its parent
uses a five-second deadline and can terminate only the exact child it launched.
After successful cleanup and stdout flush, the helper terminates only itself via
`TerminateProcess(GetCurrentProcess(), exitCode)` to avoid QQ DLL process-detach
delays. The watchdog uses the same self-only boundary, with `Environment.Exit`
as a platform-failure fallback. Neither path opens or terminates the QQ process.
The API file remains read-locked during validation and calls. No raw response XML,
list names, account identifiers or native stderr is forwarded to the core.

`Succeeded` means both bounded status reads parsed successfully, not that music is
audible or a preceding write succeeded. Empty `listkey` is accepted and discarded;
no queue identity, revision, occurrence or atomic queue snapshot is provided. The
two native reads are sequential, not one transactional observation. Unknown
`songID=0` / `songtype=-1` are represented as null. PID/path/epoch/window checks
surround native calls, but are not an independent proof of QQ's internal IPC routing.
