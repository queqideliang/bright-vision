#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ReferenceCache = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'revit-reference-cache'),
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$frameworkDirectory = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
if (!$OutputPath) { $OutputPath = Join-Path $moduleDirectory ('artifacts\tests\quantity-' + [guid]::NewGuid().ToString('N')) }
$testOutput = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory($testOutput)
$fixture = Join-Path $testOutput 'QuantitySnapshotFixture.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $name) }
foreach ($name in @('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','RevitQuantityReader.cs')) { $arguments += Join-Path $moduleDirectory $name }
$arguments += Join-Path $PSScriptRoot 'QuantityApiStubs.cs'
$arguments += Join-Path $PSScriptRoot 'QuantitySnapshotFixture.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Quantity snapshot fixture compilation failed.' }
& $fixture
if ($LASTEXITCODE -ne 0) { throw 'Quantity snapshot fixture failed.' }
[pscustomobject]@{ Result='PASS'; Fixture=$fixture; Scope='Pure SI/status logic and production instance-parameter reader with separate fake API types.' }
