#requires -Version 5.1
# Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-rule-calculation-1.9-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must remain in workspace.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh 1.9 rule calculation fixture directory; previous evidence is preserved.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse rule calculation output is refused.'}
}
[void][IO.Directory]::CreateDirectory($output)
$dll=Join-Path $output 'CivilWallRuleCalculationFixture.dll'
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $workspace 'tools\revit-reference-cache\framework\net45'}
if(!$FrameworkDirectory-and!(Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))){$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$arguments=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$reference=Join-Path $framework $name;if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw('Missing existing Framework reference: '+$reference)};$arguments+='/reference:'+$reference}
$sources=@('CivilQuantityModel.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs')|ForEach-Object{Join-Path $module $_}
$sources+=Join-Path $PSScriptRoot 'CivilWallRuleCalculationFixture.cs'
$sourceHashes=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$ruleIndexPath=Join-Path $module 'rule-templates\index.json'
$ruleIndex=Get-Content -Raw -Encoding UTF8 -LiteralPath $ruleIndexPath|ConvertFrom-Json
if($ruleIndex.schemaVersion-ne1-or$ruleIndex.templates.Count-ne2){throw 'Unexpected shipped BV rule index format.'}
$ruleArtifacts=@([ordered]@{path=$ruleIndexPath;sha256=(Get-FileHash -LiteralPath $ruleIndexPath).Hash.ToLowerInvariant()})
foreach($entry in $ruleIndex.templates){
    if([IO.Path]::GetFileName([string]$entry.file)-ne[string]$entry.file){throw 'Rule index must reference simple local filenames.'}
    $file=Join-Path (Split-Path $ruleIndexPath -Parent) $entry.file
    $hash=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    if($hash-ne$entry.sha256){throw 'Shipped rule JSON file differs from its index SHA256.'}
    $ruleArtifacts+=[ordered]@{path=$file;sha256=$hash;templateId=$entry.templateId;fingerprint=$entry.fingerprint}
}
$arguments+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Selected-rule calculation fixture failed to compile.'}
$assembly=[Reflection.Assembly]::LoadFile($dll)
$invokeArgs=New-Object object[] 1
$invokeArgs[0]=[string[]]@($output,$module)
$checks=$assembly.GetType('CivilWallRuleCalculationFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$invokeArgs)
foreach($source in $sourceHashes){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-ne$source.sha256){throw 'Rule calculation sources changed during test; no result committed.'}}
foreach($artifact in $ruleArtifacts){if((Get-FileHash -LiteralPath $artifact.path).Hash.ToLowerInvariant()-ne$artifact.sha256){throw 'Shipped BV rule artifacts changed during test.'}}
$semanticImports=[IO.File]::ReadAllLines((Join-Path $output 'imported-template-fingerprints.tsv'))
if($semanticImports.Length-ne$ruleIndex.templates.Count){throw 'Every indexed BV rule must be semantically imported.'}
foreach($entry in $ruleIndex.templates){if($semanticImports-notcontains($entry.file+"`t"+$entry.fingerprint)){throw 'Loaded BV rule fingerprint differs from its index.'}}
$report=[ordered]@{result='PASS';assertions=$checks;scope='Pure project-owned domain DLL. Explicit controlled SI proof facts and independent area/volume expectations; selected policy and tolerance affect calculation acceptance, including shipped BV JSON Load/catalog/index fingerprint checks. No Autodesk assembly loaded, no native IFC API/CSG or host verification.';assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();sources=$sourceHashes;shippedRuleArtifacts=$ruleArtifacts;semanticImports=$semanticImports;cases=[IO.File]::ReadAllLines((Join-Path $output 'cases.txt'));hostExecuted=$false;nativeGeometryVerified=$false}
[IO.File]::WriteAllText((Join-Path $output 'wall-rule-calculation-validation.json'),($report|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Assertions=$checks;Output=$output;HostExecuted=$false}
