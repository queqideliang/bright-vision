#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$FrameworkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workspace = [IO.Path]::GetFullPath((Join-Path $module '..\..'))
if (!$OutputPath) { $OutputPath = Join-Path $module ('artifacts\tests\civil-wall-rule-ui-1.9-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (!$output.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Rule UI test output must stay in the workspace.' }
for ($cursor = $output; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse rule UI test output refused.' }
}
if (Test-Path -LiteralPath $output) { throw 'Rule UI test output exists; choose a new directory to preserve earlier evidence.' }
[void][IO.Directory]::CreateDirectory($output)
$framework = if ($FrameworkDirectory) { [IO.Path]::GetFullPath($FrameworkDirectory) } else { Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dll = Join-Path $output 'CivilWallRuleUiFixture.dll'
$compile = @('/nologo', '/noconfig', '/target:library', '/platform:anycpu', '/optimize+', '/codepage:65001', '/langversion:5', '/nostdlib+', ('/out:' + $dll))
$references = @()
foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'System.Xaml.dll')) {
    $reference = Join-Path $framework $name
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { $reference = Join-Path (Join-Path $framework 'WPF') $name }
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { throw ('Missing pure Framework/WPF reference: ' + $name + ' under ' + $framework) }
    $references += $reference
    $compile += '/reference:' + $reference
}
$sources = @('BrandPixels.cs', 'Branding.cs', 'CivilWallRuleTemplates.cs', 'CivilWallRuleWindow.cs') | ForEach-Object { Join-Path $module $_ }
$sources += Join-Path $PSScriptRoot 'CivilWallRuleUiFixture.cs'
$before = @($sources | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() } })
$runnerHash = (Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
$compile += $sources
& $compiler @compile
if ($LASTEXITCODE -ne 0) { throw 'Rule template WPF fixture compilation failed.' }
$assembly = [Reflection.Assembly]::LoadFile($dll)
$method = $assembly.GetType('CivilWallRuleUiFixture', $true).GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$invoke = New-Object object[] 1
$invoke[0] = [string[]]@($output)
try {
    if ($method.Invoke($null, $invoke) -ne 0) { throw 'Rule template WPF fixture returned a failure.' }
}
catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    throw ('Rule template WPF fixture failed: ' + $failure.ToString())
}
foreach ($source in $before) {
    if ((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant() -cne $source.sha256) { throw 'Rule UI source changed during validation; rerun against completed source.' }
}
if ((Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant() -cne $runnerHash) { throw 'Rule UI runner changed during validation.' }
$report = Get-Content -LiteralPath (Join-Path $output 'report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$build = [ordered]@{
    result = 'PASS'; assertions = $report.assertions
    scope = 'Project-owned Framework DLL and actual offscreen WPF controls; no displayed window, native input, Autodesk assembly, commercial template or Revit.'
    capturedAtUtc = [DateTime]::UtcNow.ToString('o'); framework = $framework
    assembly = $dll; assemblySha256 = (Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant(); sources = $before
    runner = [ordered]@{ path = $PSCommandPath; sha256 = $runnerHash }
    references = @($references | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() } })
    artifacts = @(Get-ChildItem -LiteralPath $output -File | Sort-Object Name | ForEach-Object { [ordered]@{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } })
}
$validation = Join-Path $output 'ui-build-validation.json'
[IO.File]::WriteAllText($validation, ($build | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ Result = 'PASS'; Assertions = $report.assertions; Output = $output; Validation = $validation }
