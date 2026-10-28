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
if (!$OutputPath) { $OutputPath = Join-Path $moduleDirectory ('artifacts\tests\civil-' + [guid]::NewGuid().ToString('N')) }
$testOutput = [IO.Path]::GetFullPath($OutputPath)
[void][IO.Directory]::CreateDirectory($testOutput)
$fixture = Join-Path $testOutput 'CivilQuantityFixture.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments = @('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll')) { $arguments += '/reference:' + (Join-Path $frameworkDirectory $name) }
$arguments += Join-Path $moduleDirectory 'CivilQuantityModel.cs'
$arguments += Join-Path $PSScriptRoot 'CivilQuantityFixture.cs'
$stdout = Join-Path $testOutput 'compile.stdout.txt'
$stderr = Join-Path $testOutput 'compile.stderr.txt'
$process = Start-Process -FilePath $compiler -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $fixture -PathType Leaf)) {
    Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue
    Get-Content -LiteralPath $stderr -ErrorAction SilentlyContinue
    throw 'Civil quantity fixture compilation failed.'
}
# This is our own pure test assembly. Run its checks in the existing CLR rather
# than spawning an EXE; no Revit, Windows UI or target/vendor code is loaded.
$assembly = [Reflection.Assembly]::LoadFile($fixture)
$type = $assembly.GetType('CivilQuantityFixture', $true)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$result = [int]$type.GetMethod('Main', $flags).Invoke($null, @())
$assertions = [int]$type.GetField('checks', $flags).GetValue($null)
$report = [pscustomobject]@{ Result=$(if ($result -eq 0) {'PASS'} else {'FAIL'}); Assertions=$assertions; Fixture=$fixture; Scope='Pure filtering, grouping, metric coverage and traceable CSV; no Revit API reference or host.' }
[IO.File]::WriteAllText((Join-Path $testOutput 'report.json'), ($report | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
if ($result -ne 0) { throw 'Civil quantity fixture failed.' }
$report
