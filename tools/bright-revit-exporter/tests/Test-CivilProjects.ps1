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
if (!(Test-Path -LiteralPath (Join-Path $frameworkDirectory 'mscorlib.dll'))) { $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319' }
if (!$OutputPath) { $OutputPath = Join-Path $moduleDirectory ('artifacts\tests\civil-project-' + [guid]::NewGuid().ToString('N')) }
$testOutput = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory($testOutput)
$fixture = Join-Path $testOutput 'CivilProjectFixture.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $name) }
$arguments += Join-Path $moduleDirectory 'CivilQuantityModel.cs'
$arguments += Join-Path $moduleDirectory 'CivilProject.cs'
$arguments += Join-Path $moduleDirectory 'CivilWallRuleTemplates.cs'
$arguments += Join-Path $PSScriptRoot 'CivilQuantityFixture.cs'
$arguments += Join-Path $PSScriptRoot 'CivilProjectFixture.cs'
$stdout = Join-Path $testOutput 'compile.stdout.txt'
$stderr = Join-Path $testOutput 'compile.stderr.txt'
$process = Start-Process -FilePath $compiler -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $fixture -PathType Leaf)) {
    Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
    Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
    throw 'Civil project fixture DLL compilation failed.'
}
# This explicitly authorized test compiles only our managed library. It never
# creates, executes, restores or renames the previously blocked fixture EXE.
$assembly = [Reflection.Assembly]::LoadFile($fixture)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$results = @()
foreach ($typeName in @('CivilQuantityFixture','CivilProjectFixture')) {
    $type = $assembly.GetType($typeName, $true)
    if ($typeName -eq 'CivilProjectFixture') { $methodArguments = @($testOutput) } else { $methodArguments = @() }
    $result = [int]$type.GetMethod('Main', $flags).Invoke($null, $methodArguments)
    $assertions = [int]$type.GetField('checks', $flags).GetValue($null)
    $results += [pscustomobject]@{ Type=$typeName; Result=$(if ($result -eq 0) {'PASS'} else {'FAIL'}); Assertions=$assertions }
    if ($result -ne 0) { throw ($typeName + ' failed.') }
}
$report = [pscustomobject]@{ Result='PASS'; Assertions=($results | Measure-Object -Property Assertions -Sum).Sum; Cases=$results; Fixture=$fixture; Scope='Own managed domain DLL: project identity, source binding, mapping, single segment membership, grouping/CSV and bounded atomic JSON; no Revit/UI/child executable.' }
[IO.File]::WriteAllText((Join-Path $testOutput 'report.json'), ($report | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
$report
