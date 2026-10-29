#requires -Version 5.1
# Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory=(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'), [switch]$Legacy2015)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-join-reader-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must remain in workspace.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh join reader test directory.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse test output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$sourceNames=@('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallReadControl.cs','RevitWallCutoutReader.cs','RevitCivilWallReader.cs','CivilWallJoinCore.cs','CivilWallJoinPlan.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilWallJoinSession.cs','RevitWallJoinReader.cs')
$sources=@($sourceNames|ForEach-Object{Join-Path $module $_})+@(@('WallJoinApiStubs.cs','CivilWallJoinReaderFixture.cs')|ForEach-Object{Join-Path $PSScriptRoot $_})
$hashes=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$dll=Join-Path $output 'CivilWallJoinReaderFixture.dll';$compile=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
if($Legacy2015){$compile+='/define:REVIT_2015'}
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$reference=Join-Path $FrameworkDirectory $name;if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing Framework reference: '+$reference)};$compile+='/reference:'+$reference}
$compile+=$sources;& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compile
if($LASTEXITCODE-ne0){throw 'Wall join reader DLL fixture compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFile($dll);$invoke=New-Object object[] 1;$invoke[0]=[string[]]@($output)
try{$checks=$assembly.GetType('CivilWallJoinReaderFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$invoke)}catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};[ordered]@{result='FAIL';reason=$failure.Message;hostExecuted=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8;throw}
foreach($source in $hashes){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-cne$source.sha256){throw 'Join reader source changed during test; do not claim a consistent pass.'}}
$record=[ordered]@{result='PASS';assertions=$checks;scope='Own API stand-ins, exact axis-aligned box-set CSG oracle, real production reader/core. No Autodesk assembly or native kernel executed.';legacy2015=[bool]$Legacy2015;sources=$hashes;framework=$FrameworkDirectory;assembly=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();cases=[IO.File]::ReadAllLines((Join-Path $output 'cases.txt'));hostExecuted=$false;nativeDesktopInput=$false}
[IO.File]::WriteAllText((Join-Path $output 'wall-join-reader-validation.json'),($record|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Assertions=$checks;Output=$output}
