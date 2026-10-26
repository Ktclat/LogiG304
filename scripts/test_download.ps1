# SPDX-FileCopyrightText: 2026 Ktclat
# SPDX-License-Identifier: AGPL-3.0-only
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference='Stop'
$folder=(Resolve-Path -LiteralPath $PackageDirectory).Path
$smoke=Join-Path $folder ('smoke-'+[Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $folder 'LogiG304-Tools-Windows-x64.zip'))
try {
 foreach($entry in $archive.Entries){
  $path=[IO.Path]::GetFullPath((Join-Path $smoke $entry.FullName))
  if(-not $path.StartsWith($smoke+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe package entry.'}
 }
} finally {$archive.Dispose()}
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $folder 'LogiG304-Tools-Windows-x64.zip'),$smoke)
$before=@(Get-ChildItem -LiteralPath $smoke -Recurse -File|ForEach-Object {$_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash})
$checks=@(
 @('开始使用.exe',('--check-package "'+$smoke+'"'),0),
 @('tools\G HUB Offline.exe','--audit',2),
 @('tools\网络设置.exe','--audit',2),
 @('tools\卸载 G HUB 本地版.exe','--probe-download',2)
)
foreach($check in $checks){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=Join-Path $smoke $check[0]
 $info.Arguments=$check[1]
 $info.UseShellExecute=$false
 $info.CreateNoWindow=$true
 $proc=[Diagnostics.Process]::Start($info)
 try {
  if(-not $proc.WaitForExit(30000)){$proc.Kill();throw 'Download smoke check timed out.'}
  if($proc.ExitCode -ne $check[2]){throw ('Unexpected result from '+$check[0]+': '+$proc.ExitCode)}
 } finally {$proc.Dispose()}
}
$after=@(Get-ChildItem -LiteralPath $smoke -Recurse -File|ForEach-Object {$_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash})
if(Compare-Object $before $after){throw 'Read-only checks modified the extracted files.'}
$directories=@(Get-ChildItem -LiteralPath $smoke -Recurse -Directory)
if($directories.Count -ne 1 -or $directories[0].FullName -ne (Join-Path $smoke 'tools')){throw 'A loose tool created unexpected data or logs.'}
# Remove only the successfully verified fixture's regular files and empty folders.
# On failure keep the fixture for inspection; never clean arbitrary output folders.
foreach($file in Get-ChildItem -LiteralPath $smoke -Recurse -File){
 if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected fixture link.'}
 Remove-Item -LiteralPath $file.FullName -Force
}
Remove-Item -LiteralPath (Join-Path $smoke 'tools') -Force
Remove-Item -LiteralPath $smoke -Force
Write-Output 'PASS: actual download integrity, all three loose-tool guards and no writes to the extracted package.'
