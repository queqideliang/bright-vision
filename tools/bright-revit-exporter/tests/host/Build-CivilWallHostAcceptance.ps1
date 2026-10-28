#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath=(Join-Path $PSScriptRoot 'artifacts\civil-wall-2020'),
    [string]$RevitPath=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020'),
    [string]$FrameworkDirectory=(Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'SDK test build output must remain inside workspace.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse SDK output refused.'}}
$source=Join-Path $PSScriptRoot 'CivilWallHostAcceptanceApp.cs'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
$comparisonSource=Join-Path $PSScriptRoot 'CivilSnapshotComparison.cs'
$comparisonHash=(Get-FileHash -LiteralPath $comparisonSource).Hash.ToLowerInvariant()
[void][IO.Directory]::CreateDirectory($output)
$snapshot=Join-Path $output 'CivilWallHostAcceptanceApp.cs'
Copy-Item -LiteralPath $source -Destination $snapshot
$comparisonSnapshot=Join-Path $output 'CivilSnapshotComparison.cs'
Copy-Item -LiteralPath $comparisonSource -Destination $comparisonSnapshot
$assembly=Join-Path $output 'Bright.Vision.CivilWallHostAcceptance.dll'
$framework=[IO.Path]::GetFullPath($FrameworkDirectory)
$refs=@((Join-Path $RevitPath 'RevitAPI.dll'),(Join-Path $RevitPath 'RevitAPIUI.dll'))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll')){$refs+=Join-Path $framework $name}
foreach($reference in $refs){if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing legal local reference; specify RevitPath / FrameworkDirectory: '+$reference)}}
$arguments=@('/nologo','/noconfig','/target:library','/platform:x64','/langversion:5','/optimize+','/codepage:65001','/nostdlib+',('/out:'+$assembly))
$arguments+=@($refs|ForEach-Object{'/reference:'+$_});$arguments+=$snapshot;$arguments+=$comparisonSnapshot
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Wall SDK test driver compilation failed.'}
if((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $sourceHash){throw 'Wall SDK test source changed during build.'}
if((Get-FileHash -LiteralPath $comparisonSource).Hash.ToLowerInvariant() -ne $comparisonHash){throw 'Wall snapshot comparison source changed during build.'}
$manifest=[ordered]@{testOnly=$true;registered=$false;startedRevit=$false;expectedProductionVersion='1.7.0.0';sourcePath=$snapshot;sourceSha256=$sourceHash;supportSources=@([ordered]@{path=$comparisonSnapshot;sha256=$comparisonHash});assemblyPath=$assembly;assemblySha256=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant();fullClassName='BrightVision.CivilWallHostAcceptance.App';configurationEnvironment='BV_CIVIL_WALL_ACCEPTANCE_CONFIG';nonceEnvironment='BV_CIVIL_WALL_ACCEPTANCE_RUN_ID';api=@($refs[0..1]|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText((Join-Path $output 'build-manifest.json'),($manifest|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='Built';Driver=$assembly;SHA256=$manifest.assemblySha256;Registered=$false;StartedRevit=$false}
