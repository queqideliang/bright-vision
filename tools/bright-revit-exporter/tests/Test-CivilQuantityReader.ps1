#requires -Version 5.1
[CmdletBinding()]
param([string]$ReferenceCache=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'revit-reference-cache'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$framework=Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
$output=Join-Path $module ('artifacts\tests\civil-reader-fixture-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($output)
$fixture=Join-Path $output 'CivilReaderFixture.dll'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:'+$fixture))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')){$arguments+='/reference:'+(Join-Path $framework $name)}
foreach($name in @('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilLevelResolver.cs','RevitCivilQuantityReader.cs')){$arguments+=Join-Path $module $name}
$arguments+=Join-Path $PSScriptRoot 'CivilReaderApiStubs.cs'
$arguments+=Join-Path $PSScriptRoot 'CivilReaderFixture.cs'
& $compiler @arguments
if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $fixture -PathType Leaf)){throw 'Civil reader fixture compilation failed.'}
# Load only the newly compiled, project-owned isolated fixture. Its Autodesk
# namespace is a separate fake API; no Revit or vendor DLL is loaded here.
$fixtureAssembly=[Reflection.Assembly]::LoadFile($fixture)
$fixtureMethod=$fixtureAssembly.GetType('CivilReaderFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$fixtureResult=$fixtureMethod.Invoke($null,@())
if($fixtureResult -ne 0){throw 'Civil reader fixture failed.'}
[pscustomobject]@{Result='PASS';Fixture=$fixture;Scope='Production reader with independent API stubs; does not load or run Revit.'}
