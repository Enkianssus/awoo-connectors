$ErrorActionPreference = 'Stop'

$inspectorPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouProcessImage.cs'
$resetPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouAnchorHistoryReset.cs'
$adapterPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouPlayerAdapter.cs'
$policyPath = Join-Path $PSScriptRoot '..\src\Kugou\KugouAnchorResetProfilePolicy.cs'
$inspector = [IO.File]::ReadAllText((Resolve-Path $inspectorPath), [Text.Encoding]::UTF8)
$reset = [IO.File]::ReadAllText((Resolve-Path $resetPath), [Text.Encoding]::UTF8)
$adapter = [IO.File]::ReadAllText((Resolve-Path $adapterPath), [Text.Encoding]::UTF8)
$policy = [IO.File]::ReadAllText((Resolve-Path $policyPath), [Text.Encoding]::UTF8)

function Remove-CommentLines([string] $source) {
    ($source -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
}

function Test-ReadsProcessModules([string] $source) {
    $code = Remove-CommentLines $source
    $code -match '\.MainModule\b' -or $code -match '\.Modules\b'
}

# The x86 connector cannot read the module list of 64-bit KuGou.
if (Test-ReadsProcessModules $adapter) {
    throw 'The KuGou adapter must not read process modules to find the player version.'
}
if ($adapter -notmatch 'KugouProcessInspector\.TryInspect\(') {
    throw 'The KuGou adapter must read the player version through KugouProcessInspector.'
}

$hasBitnessQueries = $inspector -match 'QueryFullProcessImageNameW' -and
    $inspector -match 'IsWow64Process2\(' -and
    $inspector -match 'IsWow64Process\(' -and
    $inspector -match 'EntryPointNotFoundException' -and
    $inspector -match 'ProcessQueryLimitedInformation = 0x1000'
if (-not $hasBitnessQueries) {
    throw 'The process inspector must use QueryFullProcessImageName and IsWow64Process2 with the IsWow64Process fallback.'
}
if (Test-ReadsProcessModules $inspector) {
    throw 'The process inspector must not enumerate process modules.'
}
if (-not (Test-ReadsProcessModules $reset)) {
    throw 'The verified x86 anchor-reset path must keep its module lookup.'
}
if ($inspector -notmatch 'GetDirectoryName\(ExecutablePath\)' -or $inspector -notmatch '"kugou\.dll"') {
    throw 'kugou.dll must be located next to the KuGou executable.'
}

$inspectIndex = $reset.IndexOf('KugouProcessInspector.TryInspect(', [StringComparison]::Ordinal)
$moduleIndex = $reset.IndexOf('FindKugouModule(target)', [StringComparison]::Ordinal)
if ($inspectIndex -lt 0 -or $moduleIndex -lt 0 -or $inspectIndex -gt $moduleIndex) {
    throw 'The anchor reset must stop for 64-bit KuGou before enumerating modules.'
}
if ($reset -notmatch 'BuildUnsupported64BitPrompt\(' -or $adapter -notmatch 'BuildUnsupported64BitPrompt\(') {
    throw 'Both the anchor reset and the probe status must report 64-bit KuGou.'
}

$prompt = '\u68C0\u6D4B\u5230 64 \u4F4D\u9177\u72D7\uFF08\u7248\u672C \{displayVersion\}\uFF09\uFF0C\u65E0\u5207\u6B4C\u63D2\u5165\u6682\u4E0D\u652F\u6301\uFF0C\u5DF2\u4F7F\u7528\u517C\u5BB9\u63D2\u5165'
if ($policy -notmatch $prompt) {
    throw 'The 64-bit KuGou status text is missing or changed.'
}

Write-Output 'KugouProcessBitnessPolicy.Tests passed.'
