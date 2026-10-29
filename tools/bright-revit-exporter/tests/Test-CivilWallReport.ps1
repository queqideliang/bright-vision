#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot ('..\artifacts\tests\civil-wall-report-1.8-' + [Guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath (Join-Path $output 'report.json')) { throw 'Use a fresh report directory; previous evidence is preserved.' }
[void][IO.Directory]::CreateDirectory($output)
$framework=Join-Path (Split-Path $module -Parent) 'revit-reference-cache\framework\net45'
if(!(Test-Path -LiteralPath (Join-Path $framework 'mscorlib.dll'))) { $framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$dll=Join-Path $output 'CivilWallReportFixture.dll'
$compilerArgs=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { $compilerArgs+='/reference:'+(Join-Path $framework $name) }
foreach($name in @('CivilQuantityModel.cs','CivilProject.cs','CivilWallFacts.cs','CivilWallCalculation.cs','CivilWallReport.cs')) { $compilerArgs+=Join-Path $module $name }
$compilerArgs+=Join-Path $PSScriptRoot 'CivilWallReportFixture.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if($LASTEXITCODE -ne 0) { throw 'Wall report fixture DLL compilation failed.' }
$assembly=[Reflection.Assembly]::LoadFile($dll)
$method=$assembly.GetType('CivilWallReportFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invokeArgs=New-Object object[] 1
$invokeArgs[0]=[string[]]@($output)
if($method.Invoke($null,$invokeArgs) -ne 0) { throw 'Wall report fixture failed.' }
Get-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'report.json')
