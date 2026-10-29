#requires -Version 5.1
# Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param(
    [string]$OutputPath='',
    [string]$Revit2015ApiDirectory=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2015'),
    [string]$Revit2020ApiDirectory=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020'),
    [string]$FrameworkDirectory=(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-join-api-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Metadata test output must remain in the workspace.'}
if(Test-Path -LiteralPath $output){throw 'Choose a fresh metadata test directory; previous evidence is preserved.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse metadata output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$sourceNames=@('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilLevelResolver.cs','RevitCivilQuantityReader.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallReadControl.cs','RevitWallCutoutReader.cs','RevitCivilWallReader.cs','CivilWallJoinCore.cs','CivilWallJoinPlan.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilWallJoinSession.cs','RevitWallJoinReader.cs')
$sources=@($sourceNames|ForEach-Object{Join-Path $module $_})
$hashes=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$builds=@()
foreach($year in @('2015','2020')){
    $api=[IO.Path]::GetFullPath($(if($year-eq'2015'){$Revit2015ApiDirectory}else{$Revit2020ApiDirectory}))
    $references=@();foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$references+=Join-Path $FrameworkDirectory $name}
    foreach($name in @('RevitAPI.dll','RevitAPIUI.dll')){$references+=Join-Path $api $name}
    foreach($reference in $references){if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing existing legal API or Framework metadata: '+$reference)}}
    $dll=Join-Path $output ('WallJoinMetadata-'+$year+'.dll')
    $compile=@('/nologo','/noconfig','/target:library','/platform:x64','/optimize+','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
    if($year-eq'2015'){$compile+='/define:REVIT_2015'}
    $compile+=@($references|ForEach-Object{'/reference:'+$_});$compile+=$sources
    & $compiler @compile
    if($LASTEXITCODE-ne0){throw ('Wall join actual API metadata compilation failed: '+$year)}
    $builds+=[ordered]@{year=$year;path=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();api=@($references|Select-Object -Last 2|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}});compiled=$true;hostExecuted=$false}
}
foreach($entry in $hashes){if((Get-FileHash -LiteralPath $entry.path).Hash.ToLowerInvariant()-cne$entry.sha256){throw 'Source changed during metadata compilation.'}}
$record=[ordered]@{result='PASS';scope='Real Autodesk metadata compilation only; no Autodesk assembly loaded, no native geometry or Revit process executed.';sources=$hashes;builds=$builds}
[IO.File]::WriteAllText((Join-Path $output 'wall-join-api-validation.json'),($record|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Output=$output;Targets='2015,2020';HostExecuted=$false}
