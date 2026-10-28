#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ReferenceCache = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'revit-reference-cache')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleDirectory = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$frameworkDirectory = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
$testOutput = Join-Path $moduleDirectory ('artifacts\tests\export-options-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testOutput)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path $testOutput 'ExportOptionsUiFixture.exe'
$arguments = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:' + $fixture))
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll','System.Windows.Forms.dll')) {
    $reference = Join-Path $frameworkDirectory $name
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing framework reference: $reference" }
    $arguments += '/reference:' + $reference
}
foreach ($name in @('BrandPixels.cs','Branding.cs','ExportOptions.cs','ExportOptionsDialog.cs')) { $arguments += Join-Path $moduleDirectory $name }
$arguments += Join-Path $PSScriptRoot 'RevitUiStubs.cs'
$arguments += Join-Path $PSScriptRoot 'ExportOptionsUiFixture.cs'
# The fixture is compiled against the adjacent fake API types. It does not
# reference, load, or execute Autodesk/vendor assemblies and never shows a
# window: RenderTargetBitmap renders the actual dialog content offscreen.
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Independent export options fixture compilation failed.' }
$preview = Join-Path $testOutput 'BV-export-options.png'
& $fixture $preview
if ($LASTEXITCODE -ne 0) { throw 'Independent export options fixture validation failed.' }
[pscustomobject]@{ Result='PASS'; Preview=$preview; Scope='Own dialog with fake Revit types; no shown window, host export, or desktop automation.' }
