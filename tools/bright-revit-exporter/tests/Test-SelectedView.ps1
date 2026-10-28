#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ReferenceCache = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'revit-reference-cache'),
    [switch]$VerifyMutation
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$frameworkDirectory = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
$testOutput = Join-Path $moduleDirectory ('artifacts\tests\selected-view-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testOutput)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path $testOutput 'SelectedViewFixture.exe'
$arguments = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll')) {
    $reference = Join-Path $frameworkDirectory $name
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing framework reference: $reference" }
    $arguments += '/reference:' + $reference
}
$resolver = Join-Path $moduleDirectory 'SelectedViewResolver.cs'
$arguments += $resolver
$arguments += Join-Path $PSScriptRoot 'SelectedViewApiStubs.cs'
$arguments += Join-Path $PSScriptRoot 'SelectedViewFixture.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Selected-view identity fixture compilation failed.' }
& $fixture
if ($LASTEXITCODE -ne 0) { throw 'Selected-view identity regression failed.' }
$mutationRejected = $null
if ($VerifyMutation) {
    $source = [IO.File]::ReadAllText($resolver)
    $nativeGuard = '!source.Equals(document)'
    if ([regex]::Matches($source,[regex]::Escape($nativeGuard)).Count -ne 1) { throw 'Cannot uniquely identify the native identity guard for the optional mutation proof.' }
    $mutantSource = Join-Path $testOutput 'SelectedViewResolver.reference-comparison.cs'
    [IO.File]::WriteAllText($mutantSource,$source.Replace($nativeGuard,'!Object.ReferenceEquals(source, document)'),(New-Object Text.UTF8Encoding($false)))
    $mutantFixture = Join-Path $testOutput 'SelectedViewReferenceComparisonFixture.exe'
    $mutantArguments = @($arguments | ForEach-Object {
        if ($_ -ceq $resolver) { $mutantSource }
        elseif ($_ -ceq ('/out:' + $fixture)) { '/out:' + $mutantFixture }
        else { $_ }
    })
    & $compiler @mutantArguments
    if ($LASTEXITCODE -ne 0) { throw 'Mutation proof compilation failed.' }
    $mutantOutput = & $mutantFixture
    if ($LASTEXITCODE -eq 0) { throw 'The fixture failed to catch the managed-reference comparison regression.' }
    if (($mutantOutput -join "`n") -notmatch 'FAIL: InvalidDataException:') { throw 'The mutant failed for an unexpected reason.' }
    $mutationRejected = $true
    Write-Output 'Mutation proof PASS: replacing native identity equality with managed reference comparison fails the same-model wrapper case.'
}
[pscustomobject]@{ Result='PASS'; Fixture=$fixture; MutationRejected=$mutationRejected; Scope='Production resolver compiled against separate fake API types; no Revit host or vendor assembly.' }
