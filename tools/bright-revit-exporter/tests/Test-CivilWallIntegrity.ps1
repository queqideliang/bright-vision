#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot ('..\artifacts\tests\civil-wall-integrity-1.9-' + [Guid]::NewGuid().ToString('N'))),
    [string]$FrameworkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; previous evidence is preserved.' }
[void][IO.Directory]::CreateDirectory($output)
$framework = if ($FrameworkDirectory) { [IO.Path]::GetFullPath($FrameworkDirectory) } else { Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$dll = Join-Path $output 'CivilWallIntegrityFixture.dll'
$compilerArgs = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $dll))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { $compilerArgs += '/reference:' + (Join-Path $framework $name) }
$sources = @(@('CivilQuantityModel.cs','CivilProject.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilWallReport.cs') | ForEach-Object { Join-Path $module $_ })
$sources += Join-Path $PSScriptRoot 'CivilWallIntegrityFixture.cs'
$sourceHashes = @($sources | ForEach-Object { [ordered]@{ path=$_; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$compilerArgs += $sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Wall integrity fixture DLL compilation failed.' }
$assembly = [Reflection.Assembly]::LoadFile($dll)
$method = $assembly.GetType('CivilWallIntegrityFixture', $true).GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$invokeArgs = New-Object object[] 1
$invokeArgs[0] = [string[]]@($output)
[ordered]@{schemaVersion=1;fixtureKind='own-managed-wall-integrity-dll';outputPath=$output;frameworkDirectory=$framework;assemblySha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant();sources=$sourceHashes;revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
try { if ($method.Invoke($null, $invokeArgs) -ne 0) { throw 'Wall integrity fixture failed.' } }
catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    [ordered]@{result='FAIL';failure=$failure.Message;scope='Own managed wall integrity fixture; no Revit, shown window or input';assertionsPassedBeforeFailure=$assembly.GetType('CivilWallIntegrityFixture',$true).GetField('checks',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null);revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
    throw
}
foreach ($source in $sourceHashes) { if ((Get-FileHash -LiteralPath $source.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.sha256) { throw ('Source changed during integrity fixture: '+$source.path) } }
Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
