#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1,20000)][int]$Triangles = 5000,
    [ValidateRange(2,2000)][int]$Instances = 30,
    [ValidateRange(1,10)][int]$Trials = 3,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts\benchmarks'),
    [string]$ReferenceCache = (Join-Path (Split-Path $PSScriptRoot -Parent) 'revit-reference-cache')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputPath)
$framework = Join-Path ([IO.Path]::GetFullPath($ReferenceCache)) 'framework\net45'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$null = [IO.Directory]::CreateDirectory($output)
$executable = Join-Path $output 'Bright.Vision.SyntheticBenchmark.exe'
$arguments = @('/nologo','/noconfig','/target:exe','/platform:x64','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',"/out:$executable")
foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')) { $arguments += "/reference:$(Join-Path $framework $name)" }
$arguments += @('Json.cs','PackageWriter.cs','BenchmarkFixture.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Synthetic benchmark compilation failed.' }
$results = New-Object 'Collections.Generic.List[object]'
for ($trial = 1; $trial -le $Trials; $trial++) {
    $zip = Join-Path $output "synthetic-trial-$trial.zip"
    $json = & $executable $zip $Triangles $Instances
    if ($LASTEXITCODE -ne 0) { throw "Synthetic benchmark failed in trial $trial" }
    $value = $json | ConvertFrom-Json
    if ($value.schema -ne 'Bright.Vision.SyntheticBenchmark/1' -or $value.uniqueGeometries -ne 1) { throw 'Invalid synthetic benchmark result.' }
    $results.Add($value)
}
$report = Join-Path $output 'benchmark-results.json'
[IO.File]::WriteAllText($report, ($results.ToArray() | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ Result = 'PASS'; Trials = $Trials; TrianglesPerMesh = $Triangles; Instances = $Instances; Report = $report; RevitLoaded = $false }
