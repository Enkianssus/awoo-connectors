$ErrorActionPreference = 'Stop'

$adapterPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicPlayerAdapter.cs'
$policyPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicPlaybackAnchorPolicy.cs'
$audioScopePath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicAudioMuteScope.cs'
$sharedContractsPath = Join-Path $PSScriptRoot '..\src\Shared\PlayerContracts.cs'
$transportPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicNativeNextTransport.cs'

$adapter = [IO.File]::ReadAllText(
    (Resolve-Path $adapterPath),
    [Text.Encoding]::UTF8)
$policy = [IO.File]::ReadAllText(
    (Resolve-Path $policyPath),
    [Text.Encoding]::UTF8)
$audioScope = [IO.File]::ReadAllText(
    (Resolve-Path $audioScopePath),
    [Text.Encoding]::UTF8)
$sharedContracts = [IO.File]::ReadAllText(
    (Resolve-Path $sharedContractsPath),
    [Text.Encoding]::UTF8)
$transport = [IO.File]::ReadAllText(
    (Resolve-Path $transportPath),
    [Text.Encoding]::UTF8)

if ($policy -notmatch 'qq-playback-anchor-missing') {
    throw 'QQ playback-anchor policy must expose the stable host failure code.'
}

$hasSessionEvidence = $policy -match 'sessionObservedPlaying'
$hasAudioEvidence = $policy -match 'hasActiveAudioSession\s*&&\s*IsCrediblePlayingTimeline'
$hasPlayingTimeline = $policy -match 'IsCrediblePlayingTimeline\(timeline\)'
if (-not $hasSessionEvidence -or -not $hasAudioEvidence -or -not $hasPlayingTimeline) {
    throw 'QQ playback-anchor policy must require active QQ audio and a credible Playing observation.'
}

if ($policy -match 'IsCrediblePausedTimeline\s*\(') {
    throw 'A paused timeline alone must not establish a fresh QQ playback anchor.'
}

$sessionReset = [regex]::Match(
    $adapter,
    '(?s)private void ObserveNativeSession\(.*?private static string\? FindExecutablePath')
if ((-not $sessionReset.Success) -or
    ($sessionReset.Value -notmatch '_nativeSessionProcessId\s*==\s*processId') -or
    ($sessionReset.Value -notmatch '_sessionObservedPlaying\s*=\s*false')) {
    throw 'QQ playback anchor evidence must reset when the native process changes.'
}

if (($audioScope -notmatch 'int\?\s+expectedProcessId') -or
    ($audioScope -notmatch 'control2\.GetState\(out var sessionState\)') -or
    ($audioScope -notmatch 'IsActiveAudioSessionState\(sessionState\)') -or
    ($audioScope -notmatch 'HasActiveAudioSession')) {
    throw 'QQ audio-session capture must read IAudioSessionControl2 state for the current process.'
}

if (($adapter -notmatch 'QQMusicAudioMuteScope\.Capture\(processId\.Value\)') -or
    ($adapter -notmatch 'audio\.HasActiveAudioSession')) {
    throw 'QQ anchor evaluation must use the current process active-audio evidence.'
}

if (($adapter -notmatch 'PlaybackAnchorReady:\s*anchor\.IsReliable') -or
    ($adapter -notmatch 'snapshot\.PlaybackAnchorReady\.ToString\(\)')) {
    throw 'QQ ProbeAsync and snapshot event fingerprints must expose PlaybackAnchorReady.'
}

if ($sharedContracts -notmatch 'bool\s+PlaybackAnchorReady\s*=\s*false') {
    throw 'PlayerSnapshot must add a backwards-compatible default PlaybackAnchorReady field.'
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
