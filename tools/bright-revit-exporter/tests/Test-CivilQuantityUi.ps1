#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ReferenceCache = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'revit-reference-cache'),
    [string]$OutputPath = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$frameworkDirectory = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $moduleDirectory ('artifacts\tests\civil-ui-' + [guid]::NewGuid().ToString('N')) }
$testOutput = [IO.Path]::GetFullPath($OutputPath)
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $moduleDirectory '..\..'))
if (!$testOutput.StartsWith($workspaceRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'UI fixture output must stay within this workspace.' }
[void][IO.Directory]::CreateDirectory($testOutput)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path $testOutput 'CivilQuantityUiFixture.dll'
$arguments = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll')) {
    $reference = Join-Path $frameworkDirectory $name
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing framework reference: $reference" }
    $arguments += '/reference:' + $reference
}
$sourceNames = @('BrandPixels.cs','Branding.cs','CivilQuantityModel.cs','CivilProject.cs','CivilProjectWindow.cs','CivilQuantityWindow.cs')
$sourceHashes = @($sourceNames | ForEach-Object { [ordered]@{ name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $moduleDirectory $_)).Hash.ToLowerInvariant() } })
foreach ($name in $sourceNames) { $arguments += Join-Path $moduleDirectory $name }
$arguments += Join-Path $PSScriptRoot 'CivilQuantityUiFixture.cs'
# Compile production domain and WPF presentation directly. This isolated DLL
# has no Autodesk/vendor reference, never shows a Window or controls input.
& $compiler @arguments
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'Civil quantity UI fixture compilation failed.' }
$fixtureAssembly = [Reflection.Assembly]::LoadFile($fixture)
$fixtureMethod = $fixtureAssembly.GetType('CivilQuantityUiFixture', $true).GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$invokeArguments = New-Object object[] 1
$invokeArguments[0] = [string[]]@($testOutput)
try { $null = $fixtureMethod.Invoke($null, $invokeArguments) }
catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    throw ('Civil quantity UI fixture failed: ' + $failure.ToString())
}
foreach ($source in $sourceHashes) {
    if ((Get-FileHash -LiteralPath (Join-Path $moduleDirectory $source.name)).Hash.ToLowerInvariant() -cne $source.sha256) { throw ('Production source changed during UI fixture: ' + $source.name) }
}
$assertions = Get-Content -LiteralPath (Join-Path $testOutput 'civil-ui-assertions.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$scope = 'Production domain and actual WPF content rendered offscreen; no Revit API, shown window, or desktop automation.'
$report = [ordered]@{
    status='PASS'; assertions=$assertions.assertions; scope=$scope; capturedAtUtc=[DateTime]::UtcNow.ToString('o')
    productionSourceFiles=$sourceHashes
    fixtureSourceFiles=@(@('CivilQuantityUiFixture.cs','Test-CivilQuantityUi.ps1') | ForEach-Object { [ordered]@{ name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash.ToLowerInvariant() } })
    artifacts=@(Get-ChildItem -LiteralPath $testOutput -File | Sort-Object Name | ForEach-Object { [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } })
}
$reportPath = Join-Path $testOutput 'civil-ui-validation.json'
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ Result='PASS'; Assertions=$assertions.assertions; Output=$testOutput; ValidationReport=$reportPath; QuantityPreview=(Join-Path $testOutput 'BV-civil-quantities.png'); ProblemPreview=(Join-Path $testOutput 'BV-civil-problems.png'); ProjectQuantityPreview=(Join-Path $testOutput 'BV-civil-project-quantities.png'); ProjectSettingsPreview=(Join-Path $testOutput 'BV-civil-project-settings.png'); ProjectSegmentsPreview=(Join-Path $testOutput 'BV-civil-project-segments.png'); Scope=$scope }
