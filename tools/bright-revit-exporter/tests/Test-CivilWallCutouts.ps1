#requires -Version 5.1
# Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-cutouts-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must remain inside this workspace.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh fixture directory; previous evidence is preserved.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse fixture output is refused.'}
}
[void][IO.Directory]::CreateDirectory($output)
$foreign=Join-Path $output 'foreign'
[void][IO.Directory]::CreateDirectory($foreign)
$marker=Join-Path $foreign 'FixtureMarker.cs'
[IO.File]::WriteAllText($marker,'// Project-owned location-check marker; no Autodesk implementation. public marker follows.'+[Environment]::NewLine+'public sealed class FixtureIfcLocationMarker {}',(New-Object Text.UTF8Encoding($false)))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler '/nologo' '/noconfig' '/target:library' ('/out:'+(Join-Path $foreign 'RevitAPIIFC.dll')) $marker
if($LASTEXITCODE -ne 0){throw 'Location-check fixture marker failed to compile.'}
$dll=Join-Path $output 'CivilWallCutoutFixture.dll'
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $workspace 'tools\revit-reference-cache\framework\net45'}
if(!$FrameworkDirectory-and!(Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))){$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$arguments=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$reference=Join-Path $framework $name;if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw('Missing existing Framework reference: '+$reference)};$arguments+='/reference:'+$reference}
$sources=@('CivilQuantityModel.cs','CivilWallFacts.cs','CivilWallRuleTemplates.cs','RevitWallCutoutReader.cs')|ForEach-Object{Join-Path $module $_}
$sources+=@('WallCutoutApiStubs.cs','CivilWallCutoutFixture.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
$sourceHashes=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$arguments+=$sources
& $compiler @arguments
if($LASTEXITCODE -ne 0){throw 'Wall actual-cutout adapter fixture failed to compile.'}
$assembly=[Reflection.Assembly]::LoadFile($dll)
$invokeArgs=New-Object object[] 1
$invokeArgs[0]=[string[]]@($output)
$checks=$assembly.GetType('CivilWallCutoutFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$invokeArgs)
foreach($source in $sourceHashes){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-ne$source.sha256){throw 'Fixture sources changed during test.'}}
$report=[ordered]@{
    result='PASS';assertions=$checks;frameworkDirectory=$framework
    scope='Project-owned API stand-ins and exact reflection signature/path contracts only. No Autodesk assembly loaded; no Revit host, actual IFC API execution, whole-wall solid proof or native CSG validation.'
    assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();sources=$sourceHashes
    locationCheckMarker=[ordered]@{source=$marker;sourceSha256=(Get-FileHash -LiteralPath $marker).Hash.ToLowerInvariant();assembly=(Join-Path $foreign 'RevitAPIIFC.dll');assemblySha256=(Get-FileHash -LiteralPath (Join-Path $foreign 'RevitAPIIFC.dll')).Hash.ToLowerInvariant();projectOwned=$true}
    cases=[IO.File]::ReadAllLines((Join-Path $output 'cases.txt'))
    hostExecuted=$false;nativeGeometryVerified=$false;nominalDimensionsUsed=$false
}
[IO.File]::WriteAllText((Join-Path $output 'wall-cutout-validation.json'),($report|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Assertions=$checks;Output=$output;HostExecuted=$false}
