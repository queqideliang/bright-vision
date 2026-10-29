#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BaselineDllPath,
    [Parameter(Mandatory=$true)][string]$CurrentDllPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot ('..\artifacts\tests\civil-wall-fingerprint-compatibility-1.9-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$boundary = [IO.Path]::GetFullPath((Join-Path $module 'artifacts\tests')).TrimEnd('\') + '\'
$output = [IO.Path]::GetFullPath($OutputPath)
if (!$output.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Comparison output must be a fresh module test directory.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; previous evidence is preserved.' }
[void][IO.Directory]::CreateDirectory($output)
function Read-OwnedFixture([string]$Path) {
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (!$pathFull.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($pathFull) -cne 'CivilWallPerformanceFixture.dll') { throw 'Only our workspace performance diagnostic DLL is accepted.' }
    $manifestPath = Join-Path ([IO.Path]::GetDirectoryName($pathFull)) 'build-validation.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $sha256 = (Get-FileHash -LiteralPath $pathFull -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.fixtureKind -cne 'own-managed-wall-performance-diagnostic-dll' -or $manifest.assemblySha256 -cne $sha256) { throw 'Own fixture manifest or DLL SHA mismatch.' }
    $factory = @($manifest.sources | Where-Object { [IO.Path]::GetFileName($_.path) -ceq 'CivilWallPerformanceFixture.cs' })
    if ($factory.Count -ne 1) { throw 'Missing unique fixture factory source identity.' }
    $loadedAssembly = [Reflection.Assembly]::LoadFile($pathFull)
    if (![String]::Equals([IO.Path]::GetFullPath($loadedAssembly.Location),$pathFull,[StringComparison]::OrdinalIgnoreCase)) { throw 'The CLR did not load the exact requested fixture location.' }
    [pscustomobject]@{path=$pathFull;sha256=$sha256;moduleVersionId=$loadedAssembly.ManifestModule.ModuleVersionId.ToString();manifestPath=$manifestPath;manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();factorySha256=$factory[0].sha256;assembly=$loadedAssembly}
}
function Get-PropertyValue($Value,[string]$Name) { $Value.GetType().GetProperty($Name,[Reflection.BindingFlags]'Public,Instance').GetValue($Value,$null) }
function New-CaseBatch($Fixture,[int]$Size,[bool]$UnicodeProbe) {
    $inputArgs = New-Object object[] 1
    $inputArgs[0] = $Size
    $caseInput = $Fixture.assembly.GetType('CivilWallPerformanceFixture',$true).GetMethod('Input',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$inputArgs)
    if ($UnicodeProbe) {
        # Exercise valid UTF16/UTF8 and writer buffer boundaries without using
        # source model files or claiming these artificial names came from Revit.
        $probe = (New-Object string ([char]'x',65535)) + [string][char]0x5899 + [string][char]0xD83D + [string][char]0xDE00 + "`r`nquoted"
        $native = Get-PropertyValue $caseInput 'NativeSnapshot'
        $native.GetType().GetProperty('ModelTitle').SetValue($native,$probe,$null)
        $walls = Get-PropertyValue $caseInput 'Walls'
        $walls[0].GetType().GetProperty('Name').SetValue($walls[0],$probe,$null)
        $elements = Get-PropertyValue $native 'Elements'
        $elements[0].GetType().GetProperty('Name').SetValue($elements[0],$probe,$null)
    }
    $calculateArgs = New-Object object[] 1
    $calculateArgs[0] = $caseInput
    $batch = $Fixture.assembly.GetType('BrightVision.Civil.CivilWallCalculator',$true).GetMethod('Calculate',[Reflection.BindingFlags]'Public,Static').Invoke($null,$calculateArgs)
    $reportArgs = New-Object object[] 2
    $reportArgs[0] = $batch
    $reportArgs[1] = $null
    $report = $Fixture.assembly.GetType('BrightVision.Civil.CivilWallReport',$true).GetMethod('Create',[Reflection.BindingFlags]'Public,Static').Invoke($null,$reportArgs)
    [pscustomobject]@{
        inputFingerprint=(Get-PropertyValue $batch 'InputFingerprint')
        resultFingerprint=(Get-PropertyValue $batch 'ResultFingerprint')
        ruleFingerprint=(Get-PropertyValue $batch 'RuleFingerprint')
        snapshotFingerprint=$report.GetType().GetField('SnapshotFingerprint',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($report)
    }
}
$previousCulture = [Threading.Thread]::CurrentThread.CurrentCulture
$previousUICulture = [Threading.Thread]::CurrentThread.CurrentUICulture
try {
    $baseline = Read-OwnedFixture $BaselineDllPath
    $current = Read-OwnedFixture $CurrentDllPath
    if ($baseline.factorySha256 -cne $current.factorySha256) { throw 'Comparison requires byte-identical synthetic input factory source.' }
    $cases = @()
    foreach ($size in @(100,1000,10000)) { $cases += [pscustomobject]@{size=$size;culture='en-US';unicodeProbe=$false} }
    foreach ($culture in @('zh-CN','fr-FR','ar-SA')) { $cases += [pscustomobject]@{size=100;culture=$culture;unicodeProbe=$false} }
    foreach ($culture in @('en-US','zh-CN','fr-FR','ar-SA')) { $cases += [pscustomobject]@{size=100;culture=$culture;unicodeProbe=$true} }
    $comparisons = @()
    foreach ($case in $cases) {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($case.culture)
        [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo($case.culture)
        $oldValues = New-CaseBatch $baseline $case.size $case.unicodeProbe
        $newValues = New-CaseBatch $current $case.size $case.unicodeProbe
        $matches = @('inputFingerprint','resultFingerprint','ruleFingerprint','snapshotFingerprint') | ForEach-Object { $oldValues.$_ -ceq $newValues.$_ }
        $comparisons += [ordered]@{size=$case.size;culture=$case.culture;unicodeBufferProbe=$case.unicodeProbe;equal=($matches -notcontains $false);baseline=$oldValues;current=$newValues}
        if ($matches -contains $false) { throw ('Canonical hash mismatch for '+$case.size+'/'+$case.culture+'/unicode='+$case.unicodeProbe) }
        Write-Output ('MATCH size='+$case.size+' culture='+$case.culture+' unicode='+$case.unicodeProbe)
    }
    [ordered]@{result='PASS';scope='Old frozen StringBuilder diagnostic DLL vs current streaming diagnostic DLL, identical synthetic input factory; direct Input/Result/Rule/AppliedSnapshot canonical SHA comparison only';comparisons=$comparisons;baseline=[ordered]@{path=$baseline.path;sha256=$baseline.sha256;moduleVersionId=$baseline.moduleVersionId;manifestSha256=$baseline.manifestSha256};current=[ordered]@{path=$current.path;sha256=$current.sha256;moduleVersionId=$current.moduleVersionId;manifestSha256=$current.manifestSha256};factorySourceSha256=$baseline.factorySha256;runtime=[Environment]::Version.ToString();revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
    [pscustomobject]@{Result='PASS';Cases=$comparisons.Count;HashComparisons=($comparisons.Count*4);Report=(Join-Path $output 'report.json')}
}
catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    [ordered]@{result='FAIL';failure=$failure.Message;scope='Own frozen/current diagnostic DLL canonical comparison';revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8
    throw
}
finally { [Threading.Thread]::CurrentThread.CurrentCulture=$previousCulture; [Threading.Thread]::CurrentThread.CurrentUICulture=$previousUICulture }
