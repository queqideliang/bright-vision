#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-reader-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test output must remain in workspace.'}
[void][IO.Directory]::CreateDirectory($output)
$dll=Join-Path $output 'CivilWallReaderFixture.dll'
$arguments=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
$framework=Join-Path $workspace 'tools\revit-reference-cache\framework\net45'
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')){$arguments+='/reference:'+(Join-Path $framework $name)}
$sources=@('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilWallFacts.cs','RevitCivilWallReader.cs')|ForEach-Object{Join-Path $module $_}
$sources+=@('CivilWallApiStubs.cs','CivilWallReaderFixture.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
$arguments+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Wall adapter fixture failed to compile.'}
$assembly=[Reflection.Assembly]::LoadFile($dll)
$checks=$assembly.GetType('CivilWallReaderFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,@())
$report=[ordered]@{result='PASS';assertions=$checks;scope='Project-owned library with independent adapter API stand-ins and controlled Boolean results; no Autodesk assembly was loaded, no native CSG engine verified.';assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();sources=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText((Join-Path $output 'wall-reader-validation.json'),($report|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Assertions=$checks;Output=$output}
