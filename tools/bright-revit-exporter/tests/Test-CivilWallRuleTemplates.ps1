#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot ('..\artifacts\tests\civil-wall-rule-templates-' + [Guid]::NewGuid().ToString('N'))),
    [string]$FrameworkDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory; existing evidence is preserved.' }
[void][IO.Directory]::CreateDirectory($output)
# The fixture uses only our managed rule-template code and Windows/.NET; it
# needs no Revit API, reference cache, desktop, executable fixture or AV changes.
$framework = if ($FrameworkDirectory) { [IO.Path]::GetFullPath($FrameworkDirectory) } else { Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
$dll = Join-Path $output 'CivilWallRuleTemplateFixture.dll'
$compilerArgs = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $dll))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { $compilerArgs += '/reference:' + (Join-Path $framework $name) }
$sources = @((Join-Path $module 'CivilWallRuleTemplates.cs'), (Join-Path $PSScriptRoot 'CivilWallRuleTemplateFixture.cs'))
$compilerArgs += $sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Wall rule-template fixture DLL compilation failed.' }
$assembly = [Reflection.Assembly]::LoadFile($dll)
$method = $assembly.GetType('CivilWallRuleTemplateFixture', $true).GetMethod('Main', [Reflection.BindingFlags]'Static,NonPublic')
$invokeArgs = New-Object object[] 1
$invokeArgs[0] = [string[]]@($output)
if ($method.Invoke($null, $invokeArgs) -ne 0) { throw 'Wall rule-template fixture failed.' }
$evidence = [ordered]@{
    schemaVersion = 1
    fixtureKind = 'own-managed-rule-template-dll'
    outputPath = $output
    assemblySha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    frameworkDirectory = $framework
    sources = @($sources | ForEach-Object { [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
    revitStarted = $false
    nativeDesktopInput = $false
    childFixtureExecutable = $false
}
$evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
