$ErrorActionPreference = 'Stop'

# Source-contract checks only: never enumerate real processes/windows or call QQ.
$sourcePath = Join-Path $PSScriptRoot '../src/QQMusic/QQMusicNativeController.cs'
$source = [IO.File]::ReadAllText((Resolve-Path $sourcePath), [Text.Encoding]::UTF8)
$inspectStart = $source.IndexOf('public static IReadOnlyList<QQMusicWindowInfo> InspectWindows()')
$inspectEnd = $source.IndexOf('private static bool IsProcessInspectionFailure', $inspectStart)
$inspect = $source.Substring($inspectStart, $inspectEnd - $inspectStart)
$callbackStart = $inspect.IndexOf('(handle, _) =>')
$callbackEnd = $inspect.IndexOf('windows.Clear();', $callbackStart)
$callback = $inspect.Substring($callbackStart, $callbackEnd - $callbackStart)
$checks = 0

function Check([bool] $condition, [string] $description) {
    if (!$condition) { throw $description }
    $script:checks++
}

Check ([regex]::Matches($inspect, 'Process\.GetProcessesByName\("QQMusic"\)').Count -eq 1) `
    'Each inspection must take exactly one fresh QQ process-name snapshot.'
Check ($inspect.IndexOf('Process.GetProcessesByName') -lt $inspect.IndexOf('EnumWindows(')) `
    'Candidate processes must be collected before desktop windows are enumerated.'
Check ($inspect -notmatch 'GetProcessById|static\s+(?:readonly\s+)?(?:Dictionary|HashSet|Process\[\])') `
    'Inspection must not query each desktop process or use a cross-call process cache.'
Check ($inspect -match 'var candidates = new Dictionary<int, \(Process Process, string Name\)>\(\)') `
    'Candidate ownership must remain local to the current inspection.'
Check ($inspect -match '(?s)name\.Equals\("QQMusic", StringComparison\.OrdinalIgnoreCase\).*?!process\.HasExited.*?candidates\.TryAdd') `
    'Only named, live QQ candidates may enter the PID map.'
Check ($callback -notmatch 'ProcessName|GetProcessesByName|GetProcessById') `
    'The desktop callback must not perform process-name queries.'
Check ($callback -match '(?s)processId == 0 \|\| processId > int\.MaxValue.*?!candidates\.TryGetValue.*?return true;.*?ReadClassName\(handle\).*?ReadWindowText\(handle\).*?IsWindowVisible\(handle\)') `
    'PID filtering must precede fresh QQ window metadata reads.'
Check ([regex]::Matches($callback, 'candidate\.Process\.HasExited').Count -eq 2) `
    'Candidate liveness must be checked before and after reading a window.'
Check ($callback -match '(?s)GetWindowThreadProcessId\(handle, out var currentProcessId\);.*?currentProcessId == processId && !candidate\.Process\.HasExited.*?windows\.Add\(window\)') `
    'Reused/changed window ownership must not publish a QQ window.'
Check ($inspect -match '(?s)if \(!EnumWindows\(.*?windows\.Clear\(\)') `
    'An incomplete native enumeration must fail closed.'
Check ($inspect -match '(?s)finally\s*\{\s*foreach \(var process in processes\)\s*\{\s*process\.Dispose\(\)') `
    'Every process snapshot wrapper must be disposed, including early returns.'
Check ([regex]::Matches($inspect, 'catch \(Exception error\) when \(IsProcessInspectionFailure\(error\)\)').Count -eq 3 -and
    $source -match '(?s)private static bool IsProcessInspectionFailure.*?ArgumentException.*?InvalidOperationException.*?Win32Exception.*?UnauthorizedAccessException.*?NotSupportedException;') `
    'Candidate discovery and inspection failures must not publish uncertain windows.'
Check ($source -match '(?s)return InspectWindows\(\)\s*\.Where\(window => window\.IsVisible\)\s*\.OrderByDescending\(window =>\s*QQMusicWindowTitleParser\.Parse\(window\.Title\) is not null\)\s*\.ThenByDescending\(window =>\s*window\.Title\.Equals\(\s*"QQ音乐",\s*StringComparison\.OrdinalIgnoreCase\)\)\s*\.ThenByDescending\(window => window\.Title\.Length\)\s*\.FirstOrDefault\(\);') `
    'Main-window visibility and title preference ordering must remain unchanged.'

Write-Output "PASS $checks native window inspection source-contract checks (no live calls)."
