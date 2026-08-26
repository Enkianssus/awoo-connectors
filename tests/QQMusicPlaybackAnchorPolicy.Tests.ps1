$ErrorActionPreference = 'Stop'

$adapterPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicPlayerAdapter.cs'
$policyPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicPlaybackAnchorPolicy.cs'
$transportPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicNativeNextTransport.cs'

$adapter = [IO.File]::ReadAllText(
    (Resolve-Path $adapterPath),
    [Text.Encoding]::UTF8)
$policy = [IO.File]::ReadAllText(
    (Resolve-Path $policyPath),
    [Text.Encoding]::UTF8)
$transport = [IO.File]::ReadAllText(
    (Resolve-Path $transportPath),
    [Text.Encoding]::UTF8)

if ($policy -notmatch 'qq-playback-anchor-missing') {
    throw 'QQ playback-anchor policy must expose the stable host failure code.'
}

$hasSessionEvidence = $policy -match 'sessionObservedPlaying'
$hasPlayingTimeline = $policy -match 'IsCrediblePlayingTimeline\(timeline\)'
if (-not $hasSessionEvidence -or -not $hasPlayingTimeline) {
    throw 'QQ playback-anchor policy must require a same-session Playing observation.'
}

if ($policy -match 'IsCrediblePausedTimeline\s*\(') {
    throw 'A paused timeline alone must not establish a fresh QQ playback anchor.'
}

if ($adapter -notmatch '_sessionObservedPlaying\s*=\s*false') {
    throw 'QQ playback anchor evidence must reset when the native process changes.'
}

$insertNext = [regex]::Match(
    $adapter,
    '(?s)private async Task<PlayerOperationResult> ExecuteInsertNextAsync\(.*?private static bool IsNativeInsertAccepted')
if (-not $insertNext.Success) {
    throw 'Could not isolate ExecuteInsertNextAsync for guard-order verification.'
}
$insertBody = $insertNext.Value
$guardFailure = $insertBody.IndexOf('if (!guardResult.IsSuccess)', [StringComparison]::Ordinal)
$nativeCall = $insertBody.IndexOf('EnsureNativeNextInsertedAsync(', [StringComparison]::Ordinal)
if ($guardFailure -lt 0 -or $nativeCall -lt 0 -or $guardFailure -gt $nativeCall) {
    throw 'ExecuteInsertNextAsync must stop before native insertion when ArmSoftwareNext fails.'
}

if ($transport -notmatch 'InsertAsync\(\s*\n?\s*QQMusicSongReference song,\s*\n?\s*int anchorProcessId') {
    throw 'Native QQ insertion must require an adapter-approved anchor process.'
}

$anchorChecks = [regex]::Match(
    $transport,
    '(?s)if \(anchorProcessId <= 0.*?\n            target = FindTarget\(\);')
if (-not $anchorChecks.Success) {
    throw 'Native QQ insertion must validate the anchor before FindTarget/AddSongs.'
}

Write-Output 'QQMusicPlaybackAnchorPolicy.Tests passed.'
