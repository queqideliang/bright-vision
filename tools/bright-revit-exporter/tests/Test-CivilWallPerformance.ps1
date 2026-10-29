#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot ('..\artifacts\tests\civil-wall-performance-1.9-' + [Guid]::NewGuid().ToString('N'))),
    [ValidateSet(100,1000,10000)][int[]]$Sizes = @(100,1000,10000),
    [ValidateRange(3,9)][int]$Iterations = 3,
    [ValidateRange(1,3)][int]$Warmup = 1,
    [string]$FrameworkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; previous evidence is preserved.' }
[void][IO.Directory]::CreateDirectory($output)
$framework = if ($FrameworkDirectory) { [IO.Path]::GetFullPath($FrameworkDirectory) } else { Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$dll = Join-Path $output 'CivilWallPerformanceFixture.dll'
$compilerArgs = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $dll))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { $compilerArgs += '/reference:' + (Join-Path $framework $name) }
$sources = @(@('CivilQuantityModel.cs','CivilProject.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilWallReport.cs') | ForEach-Object { Join-Path $module $_ })
$sources += Join-Path $PSScriptRoot 'CivilWallPerformanceFixture.cs'
$sourceHashes = @($sources | ForEach-Object { [ordered]@{ path=$_; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$compilerArgs += $sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Wall performance diagnostic DLL compilation failed.' }
$assembly = [Reflection.Assembly]::LoadFile($dll)
$method = $assembly.GetType('CivilWallPerformanceFixture', $true).GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$invokeArgs = New-Object object[] 1
$invokeArgs[0] = [string[]]@($output,($Sizes -join ','),$Iterations.ToString(),$Warmup.ToString())
[ordered]@{schemaVersion=1;fixtureKind='own-managed-wall-performance-diagnostic-dll';outputPath=$output;frameworkDirectory=$framework;assemblySha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant();sources=$sourceHashes;revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
try { if ($method.Invoke($null, $invokeArgs) -ne 0) { throw 'Wall performance diagnostic failed.' } }
catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    [ordered]@{result='FAIL';failure=$failure.Message;scope='Own managed performance diagnostic; not Revit/export/UI performance';revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8
    throw
}
foreach ($source in $sourceHashes) { if ((Get-FileHash -LiteralPath $source.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.sha256) { throw ('Source changed during performance diagnostic: '+$source.path) } }
$result = Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$result.Summaries | Select-Object Size,Stage,MedianMilliseconds,MinMilliseconds,MaxMilliseconds,MedianManagedHeapDeltaBytes,MedianPrivateMemoryDeltaBytes,MedianGen2Collections | Format-Table -AutoSize
[pscustomobject]@{Result=$result.Result;Scope=$result.Scope;Report=(Join-Path $output 'report.json');BuildValidation=(Join-Path $output 'build-validation.json')}
