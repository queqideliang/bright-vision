# Copyright 2026 崛亮视觉 (Bright Vision).
# SPDX-License-Identifier: Apache-2.0

#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\artifacts\tests\civil-snapshot-comparison'),
    [string]$FrameworkDirectory = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory(),
    [string]$ReferenceCache
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this .NET Framework DLL-only test with Windows PowerShell 5.1.' }
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$testOutput = [IO.Path]::GetFullPath($OutputPath)
if (!$testOutput.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture output must remain inside workspace.' }
for ($cursor = $testOutput; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse fixture output refused.' }
}
if ($PSBoundParameters.ContainsKey('ReferenceCache')) {
    if ($PSBoundParameters.ContainsKey('FrameworkDirectory')) { throw 'Specify either FrameworkDirectory or ReferenceCache, not both.' }
    if ([string]::IsNullOrWhiteSpace($ReferenceCache)) { throw 'An explicit ReferenceCache must not be empty.' }
    $testFrameworkDirectory = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
} else {
    $testFrameworkDirectory = [IO.Path]::GetFullPath($FrameworkDirectory)
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @((Join-Path $PSScriptRoot 'CivilSnapshotComparison.cs'), (Join-Path $PSScriptRoot 'CivilSnapshotComparisonFixture.cs'))
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll') | ForEach-Object { Join-Path $testFrameworkDirectory $_ }
foreach ($required in @($compiler) + $sources + $references) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw ('Missing legal local fixture input: ' + $required) }
}
[void][IO.Directory]::CreateDirectory($testOutput)
$fixture = Join-Path $testOutput 'CivilSnapshotComparisonFixture.dll'
$arguments = @('/nologo', '/noconfig', '/target:library', '/platform:anycpu', '/optimize+', '/codepage:65001', '/langversion:5', '/nostdlib+', ('/out:' + $fixture))
foreach ($reference in $references) { $arguments += '/reference:' + $reference }
$arguments += $sources
$sourceHashes = @($sources | ForEach-Object { [ordered]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
& $compiler @arguments
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Snapshot comparison fixture DLL compilation failed.' }
# Load only the new, isolated managed fixture DLL. No fixture EXE, production
# assembly, Revit API, Revit process, registration or application-data writes.
$assembly = [Reflection.Assembly]::LoadFrom($fixture)
$type = $assembly.GetType('BrightVision.CivilWallHostAcceptance.CivilSnapshotComparisonFixture', $true)
$method = $type.GetMethod('Run', [Reflection.BindingFlags]'NonPublic,Static')
try { $fixtureReport = ($method.Invoke($null, [object[]]@()) | ConvertFrom-Json) }
catch {
    if ($_.Exception.InnerException) { throw $_.Exception.InnerException }
    throw
}
foreach ($entry in $sourceHashes) {
    if ((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.SHA256) { throw 'Comparison fixture source changed during verification.' }
}
$report = [ordered]@{
    Result = $fixtureReport.Result
    Assertions = $fixtureReport.Assertions
    EqualityCases = $fixtureReport.EqualityCases
    DifferenceCases = $fixtureReport.DifferenceCases
    Fixture = $fixtureReport.Fixture
    NativeVolume = $fixtureReport.NativeVolume
    AssemblyPath = $fixture
    AssemblySHA256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant()
    Sources = $sourceHashes
    References = @($references | ForEach-Object { [ordered]@{ Path = $_; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
    LoadedProductionAssembly = $false
    LoadedRevitApi = $false
    StartedRevit = $false
    Cases = $fixtureReport.Cases
}
$reportPath = Join-Path $testOutput 'report.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
if ($report.Result -ne 'PASS') { throw 'Snapshot comparison fixture failed.' }
[pscustomobject]@{ Result = $report.Result; Assertions = $report.Assertions; EqualityCases = $report.EqualityCases; DifferenceCases = $report.DifferenceCases; Assembly = $fixture; SHA256 = $report.AssemblySHA256; Report = $reportPath }
