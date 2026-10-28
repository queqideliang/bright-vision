#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath=(Join-Path $PSScriptRoot '..\artifacts\tests\civil-wall-ui-1.7'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory($output)
$framework=Join-Path (Split-Path $module -Parent) 'revit-reference-cache\framework\net45'
$dll=Join-Path $output 'CivilWallUiFixture.dll'
$compile=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll')){$compile+='/reference:'+(Join-Path $framework $name)}
foreach($name in @('CivilQuantityModel.cs','CivilWallFacts.cs','CivilWallCalculation.cs','CivilWallWindow.cs','BrandPixels.cs','Branding.cs')){$compile+=Join-Path $module $name}
$compile+=Join-Path $PSScriptRoot 'CivilWallUiFixture.cs'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compile
if($LASTEXITCODE -ne 0){throw 'Wall WPF fixture compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFile($dll);$method=$assembly.GetType('CivilWallUiFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invoke=New-Object object[] 1;$invoke[0]=[string[]]@($output)
if($method.Invoke($null,$invoke)-ne 0){throw 'Wall WPF fixture failed.'}
Get-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'report.json')
