#requires -Version 5.1
# Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-reader-1.9-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must remain in workspace.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh 1.9 fixture directory; previous validation evidence is preserved.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse reader fixture output is refused.'}
}
[void][IO.Directory]::CreateDirectory($output)
$dll=Join-Path $output 'CivilWallReaderFixture.dll'
$arguments=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $workspace 'tools\revit-reference-cache\framework\net45'}
if(!$FrameworkDirectory-and!(Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))){$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$reference=Join-Path $framework $name;if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw('Missing existing Framework reference: '+$reference)};$arguments+='/reference:'+$reference}
$sources=@('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallReadControl.cs','RevitWallCutoutReader.cs','RevitCivilWallReader.cs')|ForEach-Object{Join-Path $module $_}
$sources+=@('CivilWallApiStubs.cs','CivilWallReaderFixture.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
$sourceHashes=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$arguments+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Wall adapter fixture failed to compile.'}
$assembly=[Reflection.Assembly]::LoadFile($dll)
$checks=$assembly.GetType('CivilWallReaderFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@())
foreach($source in $sourceHashes){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-ne$source.sha256){throw 'Wall adapter source changed during compilation/test; validation is not committed.'}}
$report=[ordered]@{result='PASS';assertions=$checks;legacyAssertions=79;scope='Project-owned library with independent adapter API stand-ins and controlled Boolean results; no Autodesk assembly was loaded, no native IFC API executed, no native CSG engine verified. Includes rule freezing, conservative policies/tolerances, cancellation atomic return and revision checks.';assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();sources=$sourceHashes;hostExecuted=$false;nativeGeometryVerified=$false}
[IO.File]::WriteAllText((Join-Path $output 'wall-reader-validation.json'),($report|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Assertions=$checks;Output=$output}
