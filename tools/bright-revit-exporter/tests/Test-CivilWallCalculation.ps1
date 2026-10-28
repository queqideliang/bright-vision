#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\tests\civil-wall-domain-1.7'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory($output)
$framework=Join-Path (Split-Path $module -Parent) 'revit-reference-cache\framework\net45'
$dll=Join-Path $output 'CivilWallCalculationFixture.dll'
$argsList=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll')) { $argsList+='/reference:'+(Join-Path $framework $name) }
foreach($name in @('CivilQuantityModel.cs','CivilWallFacts.cs','CivilWallCalculation.cs')) { $argsList+=Join-Path $module $name }
$argsList+=Join-Path $PSScriptRoot 'CivilWallCalculationFixture.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @argsList
if($LASTEXITCODE -ne 0) { throw 'Wall domain fixture compile failed.' }
$assembly=[Reflection.Assembly]::LoadFile($dll)
$method=$assembly.GetType('CivilWallCalculationFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invokeArgs=New-Object object[] 1
$invokeArgs[0]=[string[]]@($output)
$exitCode=$method.Invoke($null,$invokeArgs)
if($exitCode -ne 0) { throw 'Wall domain fixture failed.' }
Get-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'report.json')
