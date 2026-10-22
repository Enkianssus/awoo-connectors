$ErrorActionPreference = 'Stop'

$adapterPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouPlayerAdapter.cs'
$nativePath = Join-Path $PSScriptRoot '..\src\Kugou\KugouNativeController.cs'
$adapter = [IO.File]::ReadAllText((Resolve-Path $adapterPath), [Text.Encoding]::UTF8)
$native = [IO.File]::ReadAllText((Resolve-Path $nativePath), [Text.Encoding]::UTF8)

# Only a payload that KuGou accepted is remembered as the inserted song.
if ($adapter -notmatch 'if \(delivery\.Accepted\)\s*\{\s*RememberInsertedTrack\(track\);') {
    throw 'Inserted songs must be remembered only after KuGou accepts the IPC payload.'
}
foreach ($argument in @('track', 'target', 'displacedPending\.Target')) {
    if ($adapter -notmatch "SendInsertNextAsync\(\s*endpoint\.Value,\s*$argument,") {
        throw "SendInsertNextAsync must receive the inserted track ($argument)."
    }
}
if ($adapter -notmatch 'KugouNativeController\.RememberInsertedSong\(') {
    throw 'The inserted identity must also pin the native title lookup.'
}

$insertedIndex = $adapter.IndexOf('_insertedTracks.Find(', [StringComparison]::Ordinal)
$knownIndex = $adapter.IndexOf('FindKnownTrack(id, state.Hash)', [StringComparison]::Ordinal)
if ($insertedIndex -lt 0 -or $knownIndex -lt 0 -or $insertedIndex -gt $knownIndex) {
    throw 'The current track must prefer the inserted song over searched identities.'
}

$nativeInsertedIndex = $native.IndexOf('InsertedSongIdentities.Find(', [StringComparison]::Ordinal)
$nativeCacheIndex = $native.IndexOf('SongIdentityCache.TryGetValue(key, out var identity)', [StringComparison]::Ordinal)
if ($nativeInsertedIndex -lt 0 -or $nativeCacheIndex -lt 0 -or $nativeInsertedIndex -gt $nativeCacheIndex) {
    throw 'The native playback state must prefer the inserted identity over the title search cache.'
}

Write-Output 'KugouInsertedIdentityPolicy.Tests passed.'
