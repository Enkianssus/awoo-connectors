$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $testRoot '..\..\src\QQMusic'))
$outputRoot = Join-Path $testRoot 'bin\offline'
$generatedRoot = Join-Path $testRoot 'obj\offline'
$compilerPath = 'C:\Program Files\dotnet\sdk\8.0.416\Roslyn\bincore\csc.dll'
$referenceRoot = 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.22\ref\net8.0'
New-Item -ItemType Directory -Force -Path $outputRoot, $generatedRoot | Out-Null
$globalUsings = Join-Path $generatedRoot 'GlobalUsings.g.cs'
@'
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
'@ | Set-Content -LiteralPath $globalUsings -Encoding utf8
$sources = @('QQMusicNativeNextAnalyzer.cs', 'QQMusicNativeNextProfile.cs', 'QQMusicNativeController.cs', 'QQMusicWindowTitleParser.cs')
$sourceArgs = @('"' + $globalUsings + '"', '"' + (Join-Path $testRoot 'Program.cs') + '"')
$sourceArgs += $sources | ForEach-Object { '"' + (Join-Path $sourceRoot $_) + '"' }
$referenceArgs = Get-ChildItem -LiteralPath $referenceRoot -Filter '*.dll' | ForEach-Object { '/reference:"' + $_.FullName + '"' }
$assembly = Join-Path $outputRoot 'QQMusic.NativePerformance.Tests.dll'
$compilerArgs = @('/nostdlib+', '/target:exe', '/platform:anycpu', '/nullable:enable', '/langversion:12', '/deterministic+', '/optimize+', '/warnaserror+', ('/out:"' + $assembly + '"')) + $referenceArgs + $sourceArgs
$response = Join-Path $generatedRoot 'compiler.rsp'
$compilerArgs | Set-Content -LiteralPath $response -Encoding utf8
& 'C:\Program Files\dotnet\dotnet.exe' exec $compilerPath /noconfig ('@' + $response)
if ($LASTEXITCODE -ne 0) { throw "Offline test compile failed: $LASTEXITCODE" }
@'
{"runtimeOptions":{"tfm":"net8.0","framework":{"name":"Microsoft.NETCore.App","version":"8.0.0"}}}
'@ | Set-Content -LiteralPath (Join-Path $outputRoot 'QQMusic.NativePerformance.Tests.runtimeconfig.json') -Encoding utf8
& 'C:\Program Files\dotnet\dotnet.exe' $assembly
if ($LASTEXITCODE -ne 0) { throw "Offline test failed: $LASTEXITCODE" }
