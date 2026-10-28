#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$Revit2015ApiDirectory=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2015'),
    [string]$Revit2020ApiDirectory=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020'),
    [string]$FrameworkDirectory=(Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$module = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace = [IO.Path]::GetFullPath((Join-Path $module '..\..'))
if (!$OutputPath) { $OutputPath = Join-Path $module ('artifacts\tests\civil-wall-api-' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputPath)
if (!$output.StartsWith($workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay in this workspace.' }
for ($cursor=$output; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse API compile output refused.' }
}
$framework = [IO.Path]::GetFullPath($FrameworkDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @('Json.cs','PackageWriter.cs','QuantitySnapshot.cs','CivilQuantityModel.cs','CivilLevelResolver.cs','RevitCivilQuantityReader.cs','CivilWallFacts.cs','RevitCivilWallReader.cs') | ForEach-Object { Join-Path $module $_ }
$sourceHashes = @($sources | ForEach-Object { [ordered]@{ path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() } })
[void][IO.Directory]::CreateDirectory($output)
$builds = @()
foreach ($year in @('2015','2020')) {
    $api = [IO.Path]::GetFullPath($(if ($year -eq '2015') { $Revit2015ApiDirectory } else { $Revit2020ApiDirectory }))
    foreach ($name in @('RevitAPI.dll','RevitAPIUI.dll')) { if (!(Test-Path -LiteralPath (Join-Path $api $name) -PathType Leaf)) { throw ('Missing legal local Revit API reference: ' + (Join-Path $api $name)) } }
    foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')) { if (!(Test-Path -LiteralPath (Join-Path $framework $name) -PathType Leaf)) { throw ('Missing legal local framework reference: ' + (Join-Path $framework $name)) } }
    $dll = Join-Path $output ('CivilWallApi-' + $year + '.dll')
    $arguments = @('/nologo','/noconfig','/target:library','/platform:x64','/optimize+','/langversion:5','/codepage:65001','/nostdlib+',('/out:' + $dll))
    foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')) { $arguments += '/reference:' + (Join-Path $framework $name) }
    foreach ($name in @('RevitAPI.dll','RevitAPIUI.dll')) { $arguments += '/reference:' + (Join-Path $api $name) }
    if ($year -eq '2015') { $arguments += '/define:REVIT_2015' }
    $arguments += $sources
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw ('Wall reader actual API compilation failed: ' + $year) }
    $builds += [ordered]@{ year=$year;path=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();api=@('RevitAPI.dll','RevitAPIUI.dll' | ForEach-Object { $file=Join-Path $api $_;[ordered]@{path=$file;sha256=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()} });compiled=$true;hostExecuted=$false }
}
foreach ($source in $sourceHashes) { if ((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant() -ne $source.sha256) { throw 'Wall source changed during API compilation.' } }
$report = [ordered]@{ result='PASS';scope='Actual Autodesk metadata compilation only; no Revit/vendor API assembly was loaded or executed.';sources=$sourceHashes;builds=$builds }
[IO.File]::WriteAllText((Join-Path $output 'wall-api-build-validation.json'),($report | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{Result='PASS';Output=$output;Years='2015,2020';HostExecuted=$false}
