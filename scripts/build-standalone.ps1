param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'artifacts\standalone'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $repo 'standalone'
$files = @(Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$refs = @('/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
$resources = @(('/resource:' + (Join-Path $repo 'LICENSE') + ',LICENSE.txt'), ('/resource:' + (Join-Path $source 'THIRD-PARTY.txt') + ',THIRD-PARTY.txt'), ('/resource:' + (Join-Path $source '使用说明.txt') + ',使用说明.txt'))
$exe = Join-Path $out 'LogiG304.exe'
& $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ /main:LogiG304.Standalone.Program ('/win32manifest:' + (Join-Path $repo 'src\launcher.manifest')) ('/out:' + $exe) $refs $resources $files
if ($LASTEXITCODE -ne 0) { throw 'Standalone compilation failed.' }
if ($Test) {
    $testExe = Join-Path $out 'Standalone.Tests.exe'
    & $compiler /nologo /utf8output /target:exe /platform:x64 /main:LogiG304.Standalone.ControllerTests ('/out:' + $testExe) $refs $resources $files (Join-Path $repo 'tests\ControllerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Standalone tests failed.' }
}
if ($Package) {
    $release = Join-Path $out 'release'
    if (Test-Path -LiteralPath $release) { throw 'Release directory already exists. Use a fresh build checkout or review it before packaging again.' }
    New-Item -ItemType Directory -Path $release | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $release 'LogiG304.exe')
    Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $release 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $source 'THIRD-PARTY.txt'),(Join-Path $source '使用说明.txt') -Destination $release
    Copy-Item -LiteralPath $exe -Destination (Join-Path $out 'LogiG304-Setup.exe')
    $zip = Join-Path $out 'LogiG304-0.1.0-preview.1-win-x64.zip'
    Compress-Archive -Path (Join-Path $release '*') -DestinationPath $zip
    $sums = @((Join-Path $out 'LogiG304-Setup.exe'),$zip) | ForEach-Object {
        (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $_ -Leaf)
    }
    [IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), $sums, [Text.Encoding]::ASCII)
}
