$ErrorActionPreference = 'Stop'

$transportPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicNativeNextTransport.cs'
$projectPath = Join-Path $PSScriptRoot '..\src\QQMusic\BiliNCM.Connector.QQMusic.csproj'
$adapterPath = Join-Path $PSScriptRoot '..\src\QQMusic\QQMusicPlayerAdapter.cs'
$catalogScriptPath = Join-Path $PSScriptRoot '..\scripts\update-catalog-v2.mjs'

$transport = [IO.File]::ReadAllText(
    (Resolve-Path $transportPath),
    [Text.Encoding]::UTF8)
$project = [xml](Get-Content -Raw -Encoding UTF8 $projectPath)
$adapter = Get-Content -Raw -Encoding UTF8 $adapterPath
$catalogScript = Get-Content -Raw -Encoding UTF8 $catalogScriptPath

$officialCallShape = '(?s)emitter\.Bytes\(0x8B, 0xCE, 0x8D, 0x97\);.*?' +
    'emitter\.Byte\(0x68\);\s*' +
    'emitter\.UInt32\(checked\(data \+ EmptyWideStringOffset\)\);\s*' +
    'emitter\.Bytes\(0x6A, 0x00, 0xB8\);.*?' +
    'emitter\.Bytes\(0xFF, 0xD0, 0x83, 0xC4, 0x08\);'

if ($transport -notmatch 'EmptyWideStringOffset = 0xD4' -or
    $transport -notmatch $officialCallShape) {
    throw 'QQ Music AddSongs must receive the non-null empty UTF-16 context and clean both stack arguments.'
}

$expectedProfiles = @(
    @{
        Version = '22.51'
        Fields = @{
            fileVersion = '22.51'
            clientSha256 = 'A7C9F69824793B7661FBB5CEB41A9F68904F6D59EBB18D02E8265D9D5D98C16A'
            commonSha256 = 'D351295E436FFBBD8C1C2AEA1566F227271DF8390F01CBB72F06CD6362419C4D'
            singleSongPlayDispatchRva = '0x0049BDD4'
            expectedPlayDispatchBytes = 'E8 67 69 16 00'
            addSongsRva = '0x0044D570'
            songItemSize = '0xA0'
        }
    },
    @{
        Version = '22.52'
        Fields = @{
            fileVersion = '22.52'
            clientSha256 = 'A06046FD1D36BCEA03CE1A014209F143537B37471CF53CB010E087D080C14DDD'
            commonSha256 = 'F57AB179585F455C031DE9891E2A79131BFC965DD5D64BA94143DD90894ABD7D'
            singleSongPlayDispatchRva = '0x0049C6B4'
            expectedPlayDispatchBytes = 'E8 77 66 16 00'
            getCatManagerRva = '0x0000F0ED'
            getQqUinExRva = '0x0002E089'
            songItemConstructorRva = '0x0004B8D0'
            songItemDestructorRva = '0x0004B410'
            addSongsRva = '0x0044E220'
            hiddenCategoryIdRva = '0x00C48340'
            getListRootRva = '0x006259C0'
            getListHelperRva = '0x00625B20'
            getCategoryCountRva = '0x004FE0F0'
            songItemSize = '0xA0'
        }
    },
    @{
        Version = '22.60'
        Fields = @{
            fileVersion = '22.60'
            clientSha256 = 'DDF538362972357E7637019192D593A136EE0B7D9330EBCE583289BDAA58B2A6'
            commonSha256 = 'F134E98C4698864B5400D4F247C43299E79E9616A7131042378E78AA5850EBA4'
            singleSongPlayDispatchRva = '0x004A7494'
            expectedPlayDispatchBytes = 'E8 E7 8C 16 00'
            getCatManagerRva = '0x0000F0ED'
            getQqUinExRva = '0x0002E1E5'
            songItemConstructorRva = '0x0004B8D0'
            songItemDestructorRva = '0x0004B410'
            addSongsRva = '0x00458F50'
            hiddenCategoryIdRva = '0x00C5D1D0'
            getListRootRva = '0x00632DD0'
            getListHelperRva = '0x00632F30'
            getCategoryCountRva = '0x00509A60'
            songItemSize = '0xA0'
        }
    },
    @{
        Version = '22.61'
        Fields = @{
            fileVersion = '22.61'
            clientSha256 = 'D42A800E2110B27C2D94DBB1D78AB1A9DDDA2BBDA3E623C5EEBB980AF92F9B29'
            commonSha256 = '15190F1D87B5B3853EF47F943F333FAD9E8D51277ADFD56AC332EABBDF8FC14D'
            singleSongPlayDispatchRva = '0x004A7934'
            expectedPlayDispatchBytes = 'E8 57 8D 16 00'
            getCatManagerRva = '0x0000F0ED'
            getQqUinExRva = '0x0002E1E5'
            songItemConstructorRva = '0x0004B8D0'
            songItemDestructorRva = '0x0004B410'
            addSongsRva = '0x00459280'
            hiddenCategoryIdRva = '0x00C5D1C8'
            getListRootRva = '0x006332F0'
            getListHelperRva = '0x00633450'
            getCategoryCountRva = '0x00509FB0'
            songItemSize = '0xA0'
        }
    }
)
foreach ($expectedProfile in $expectedProfiles) {
    $profilePath = Join-Path $PSScriptRoot "..\profiles\qqmusic\$($expectedProfile.Version).json"
    $profile = Get-Content -Raw -Encoding UTF8 $profilePath | ConvertFrom-Json
    foreach ($entry in $expectedProfile.Fields.GetEnumerator()) {
        if ([string]$profile.($entry.Key) -cne $entry.Value) {
            throw "QQ Music $($expectedProfile.Version) profile field '$($entry.Key)' does not match the validated image."
        }
    }
}

if ([string]$project.Project.PropertyGroup.Version -ne '22.61.1') {
    throw 'QQ Music connector version must follow the tested QQ Music 22.61 branch.'
}
if ($adapter -notmatch '22\.22 / 22\.41 / 22\.51 / 22\.52 / 22\.60 / 22\.61') {
    throw 'QQ Music adapter must advertise the complete tested player list through 22.61.'
}
if ($catalogScript -notmatch "testedPlayerVersion: '22\.22 / 22\.41 / 22\.51 / 22\.52 / 22\.60 / 22\.61'") {
    throw 'QQ Music v2 catalog metadata must advertise the complete tested player list through 22.61.'
}

Write-Output 'QQMusicNativeNextTransportPolicy.Tests passed.'
