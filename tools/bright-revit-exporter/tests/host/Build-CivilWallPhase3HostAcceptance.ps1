#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProductionBuildManifestPath,
    [string]$OutputPath='',
    [string]$RevitPath=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020'),
    [string]$FrameworkDirectory=(Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
if(!$OutputPath){$OutputPath=Join-Path $PSScriptRoot ('artifacts\civil-wall-phase3-1.9-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'New SDK driver output must remain in this workspace.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse SDK output refused.'}}
if(Test-Path -LiteralPath $output){throw 'SDK output already exists; preserve prior evidence and choose a fresh directory.'}
$manifestPath=[IO.Path]::GetFullPath($ProductionBuildManifestPath)
$production=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
if($production.packageId-ne'BrightVision.RevitExporter'-or$production.packageVersion-ne'1.9.0'-or@($production.sourceFiles).Count-eq0){throw 'Expected a new BV 1.9 build manifest with an explicit source inventory.'}
if(@($production.variants|Where-Object{$_.id-eq'2020-net45'}).Count-ne1){throw 'New production manifest does not have one Revit 2020 payload.'}
[void][IO.Directory]::CreateDirectory($output)
$source=Join-Path $PSScriptRoot 'CivilWallPhase3HostAcceptanceApp.cs'
$support=Join-Path $PSScriptRoot 'CivilSnapshotComparison.cs'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
$supportHash=(Get-FileHash -LiteralPath $support).Hash.ToLowerInvariant()
$sourceSnapshot=Join-Path $output 'CivilWallPhase3HostAcceptanceApp.cs'
$supportSnapshot=Join-Path $output 'CivilSnapshotComparison.cs'
Copy-Item -LiteralPath $source -Destination $sourceSnapshot
Copy-Item -LiteralPath $support -Destination $supportSnapshot
$dll=Join-Path $output 'Bright.Vision.CivilWallPhase3HostAcceptance.dll'
$framework=[IO.Path]::GetFullPath($FrameworkDirectory)
$references=@((Join-Path $RevitPath 'RevitAPI.dll'),(Join-Path $RevitPath 'RevitAPIUI.dll'))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll')){$references+=Join-Path $framework $name}
foreach($reference in $references){if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing existing local API or Framework reference: '+$reference)}}
$arguments=@('/nologo','/noconfig','/target:library','/platform:x64','/langversion:5','/optimize+','/codepage:65001','/nostdlib+',('/out:'+$dll))
$arguments+=@($references|ForEach-Object{'/reference:'+$_});$arguments+=$sourceSnapshot;$arguments+=$supportSnapshot
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE-ne0){throw 'Independent 1.9 SDK driver compilation failed.'}
if((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()-cne$sourceHash-or(Get-FileHash -LiteralPath $support).Hash.ToLowerInvariant()-cne$supportHash){throw 'SDK driver input source changed during compilation.'}
$record=[ordered]@{
    schemaVersion=1;testOnly=$true;preparedOnly=$true;registered=$false;startedRevit=$false;assemblyType='DLL';targetHost='Revit 2020';expectedPackageVersion=$production.packageVersion
    productionManifestPath=$manifestPath;productionManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant();productionSourceCount=@($production.sourceFiles).Count
    sourcePath=$sourceSnapshot;editableSourcePath=$source;sourceSha256=$sourceHash;supportSources=@([ordered]@{path=$supportSnapshot;sha256=$supportHash})
    assemblyPath=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();fullClassName='BrightVision.CivilWallPhase3HostAcceptance.App'
    configurationEnvironment='BV_CIVIL_WALL_PHASE3_CONFIG';nonceEnvironment='BV_CIVIL_WALL_PHASE3_RUN_ID'
    api=@($references[0..1]|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
    references=@($references|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
    limitation='Compilation is not execution. Real Door/Window family availability and official native cutout behavior are pending host checks; blocked cases remain explicit.'
}
[IO.File]::WriteAllText((Join-Path $output 'build-manifest.json'),($record|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='CompiledOnly';Driver=$dll;Manifest=(Join-Path $output 'build-manifest.json');ProductionSources=$record.productionSourceCount;Registered=$false;StartedRevit=$false}
