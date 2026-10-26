# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
param([switch]$Test, [string]$PayloadZip, [switch]$Package)
$ErrorActionPreference='Stop'
if($Package -and $PayloadZip){throw 'Public tools packaging cannot be combined with a third-party payload.'}
if($Package -and -not $Test){throw 'Use -Test -Package so download packages pass regression tests first.'}
$repo=Split-Path $PSScriptRoot -Parent
$out=Join-Path $repo 'artifacts'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if(-not (Test-Path -LiteralPath $compiler)){throw 'Windows x64 with .NET Framework 4.8 is required.'}
New-Item -ItemType Directory -Path $out,(Join-Path $out 'compiler-temp') -Force|Out-Null
foreach($notice in @('LICENSE','NOTICE.md','THIRD_PARTY.md')){
 Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination (Join-Path $out $notice)
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $repo 'src'),(Join-Path $repo 'tests') -Filter '*.cs' -File|Sort-Object FullName|ForEach-Object FullName)
$refs=@('System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.ServiceProcess.dll','Microsoft.CSharp.dll','System.IO.Compression.dll')|ForEach-Object {'/reference:'+$_}
$oldTemp=$env:TEMP
$oldTmp=$env:TMP
try {
 $env:TEMP=Join-Path $out 'compiler-temp'
 $env:TMP=$env:TEMP
 function Compile-Entry([string]$entry,[string]$name,[string[]]$inputs,[string[]]$extra){
  $destination=Join-Path $out $name
  & $compiler /nologo /utf8output /target:winexe /platform:x64 /optimize+ ('/main:'+$entry) ('/win32manifest:'+(Join-Path $repo 'src\launcher.manifest')) ('/out:'+$destination) $refs $inputs $extra
  if($LASTEXITCODE -ne 0){throw "Compilation failed: $entry"}
  Copy-Item -LiteralPath (Join-Path $repo 'src\app.config') -Destination ($destination+'.config')
 }
 Compile-Entry 'GHubSetup.Setup' 'Setup.Development.exe' $files @()
 Compile-Entry 'GHubLocal.Program' 'G HUB Offline.exe' $files @()
 Compile-Entry 'GHubLocal.NetworkEntry' '网络设置.exe' $files @()
 Compile-Entry 'GHubSetup.Removal' '卸载 G HUB 本地版.exe' $files @()
 if($Test){
  # Compile the isolated smoke harness for CI, but never run it on the host.
  Compile-Entry 'SandboxSmoke' 'SandboxSmoke.Development.exe' ($files+@(Join-Path $repo 'tests\sandbox\SandboxSmoke.cs')) @()
  $report=Join-Path $out 'installer-tests.json'
  $p=Start-Process -FilePath (Join-Path $out 'Setup.Development.exe') -ArgumentList @('--self-test',('"'+$report+'"')) -WindowStyle Hidden -Wait -PassThru
  if($p.ExitCode -ne 0){if(Test-Path -LiteralPath $report){Get-Content -LiteralPath $report -Raw|Write-Output};throw "Installer tests failed. See $report"}
  $p=Start-Process -FilePath (Join-Path $out 'G HUB Offline.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
  if($p.ExitCode -ne 0){throw 'Launcher tests failed.'}
  Get-Content -LiteralPath $report -Raw|ConvertFrom-Json|Select-Object success,tests
 }
 if($PayloadZip){
  # Only for a separately authorized, privacy-reviewed resource package.
  $payload=(Resolve-Path -LiteralPath $PayloadZip).Path
  $hashReport=Join-Path $out 'payload-validation.json'
  python (Join-Path $PSScriptRoot 'validate_payload.py') $payload $hashReport
  if($LASTEXITCODE -ne 0){throw 'Payload rejected. No complete installer was built.'}
  $manifestHash=(Get-Content -LiteralPath $hashReport -Raw|ConvertFrom-Json).manifestSha256
  $setup=Join-Path $repo 'src\Setup.cs'
  $generated=Join-Path $out 'Setup.WithPayload.cs'
  $text=[IO.File]::ReadAllText($setup).Replace(('public const string ManifestHash="'+('0'*64)+'";'),('public const string ManifestHash="'+$manifestHash+'";'))
  [IO.File]::WriteAllText($generated,$text,[Text.UTF8Encoding]::new($true))
  $inputs=@($files|Where-Object {$_ -ne $setup})+$generated
  Compile-Entry 'GHubSetup.Setup' 'GHub-Local-Setup.exe' $inputs @(('/resource:'+$payload+',Payload.zip'))
  $p=Start-Process -FilePath (Join-Path $out 'GHub-Local-Setup.exe') -ArgumentList @('--verify-package',('"'+(Join-Path $out 'embedded-check.json')+'"')) -WindowStyle Hidden -Wait -PassThru
  if($p.ExitCode -ne 0){throw 'Embedded payload verification failed.'}
 }
 if($Package){
  node (Join-Path $repo 'frontend\test-offline.cjs')
  if($LASTEXITCODE -ne 0){throw 'Frontend policy tests failed.'}
  python -m unittest discover -s (Join-Path $repo 'tests') -p 'test_*.py'
  if($LASTEXITCODE -ne 0){throw 'Resource, privacy or package tests failed.'}
  python (Join-Path $PSScriptRoot 'package_tools.py')
  if($LASTEXITCODE -ne 0){throw 'Public tools package validation failed.'}
 }
} finally {$env:TEMP=$oldTemp;$env:TMP=$oldTmp}
