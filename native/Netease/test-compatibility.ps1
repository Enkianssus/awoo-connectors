$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($visualStudio)) {
    throw 'Visual C++ x64 build tools were not found.'
}
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$outputDirectory = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$source = Join-Path $PSScriptRoot 'CefCompatibilityProfiles.Tests.cpp'
$executable = Join-Path $outputDirectory 'CefCompatibilityProfiles.Tests.exe'
$object = Join-Path $outputDirectory 'CefCompatibilityProfiles.Tests.obj'
$compile = @(
    "`"$vcvars`"", '&&', 'cl.exe', '/nologo', '/std:c++20', '/EHsc', '/W4', '/WX',
    "/Fo`"$object`"", "/Fe:`"$executable`"", "`"$source`""
) -join ' '
& $env:ComSpec /d /s /c $compile
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $executable
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
