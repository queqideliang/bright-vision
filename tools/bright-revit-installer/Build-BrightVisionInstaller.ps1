#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$BuildManifestPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts\BV-1.11.0'),
    [string]$LegalDirectory,
    [switch]$AllowPartial
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $BuildManifestPath).ProviderPath
$sourceRoot = [IO.Path]::GetDirectoryName($source)
$output = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\')
if ($output.Equals($sourceRoot,[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith("$sourceRoot\",[StringComparison]::OrdinalIgnoreCase) -or $sourceRoot.StartsWith("$output\",[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be separate from exporter build artifacts.' }
if (Test-Path -LiteralPath $output) { throw "Output exists; choose a new directory: $output" }
for ($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse output path: $cursor" }
}
$manifest = [IO.File]::ReadAllText($source) | ConvertFrom-Json
if ($manifest.packageId -ne 'BrightVision.RevitExporter' -or $manifest.addinId -ne '6F12FA44-368B-4E47-9197-3DE316D5392A' -or $manifest.fullClassName -ne 'BrightVision.Revit.App') { throw 'Unexpected source-built exporter identity.' }
if ($manifest.packageVersion -cne '1.11.0') { throw 'This installer source requires the 1.11.0 exporter build manifest.' }
if ([string]::IsNullOrWhiteSpace($LegalDirectory)) {
    $legalCandidates = @((Join-Path (Split-Path $PSScriptRoot -Parent) 'bright-open-source\legal'), (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
    $LegalDirectory = @($legalCandidates | Where-Object { (Test-Path -LiteralPath (Join-Path $_ 'LICENSE') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $_ 'NOTICE') -PathType Leaf) } | Select-Object -First 1)
    if (!$LegalDirectory) { throw 'Supply a reviewed LICENSE/NOTICE directory with -LegalDirectory.' }
}
$legalRoot = [IO.Path]::GetFullPath($LegalDirectory)
for ($cursor=$legalRoot;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'License inputs cannot use links or junctions.' }
}
$legalBytes = @{}
foreach ($name in @('LICENSE','NOTICE')) {
    $path = Join-Path $legalRoot $name
    $entry = Get-Item -LiteralPath $path -Force
    if ($entry.PSIsContainer -or $entry.Length -lt 1 -or $entry.Length -gt 131072 -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid license input: $name" }
    $legalBytes[$name] = [IO.File]::ReadAllBytes($path)
}
$legalSha=[Security.Cryptography.SHA256]::Create()
try { $licenseHash=([BitConverter]::ToString($legalSha.ComputeHash($legalBytes['LICENSE']))).Replace('-','').ToLowerInvariant() }
finally { $legalSha.Dispose() }
if ($licenseHash -cne 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30') { throw 'LICENSE must match the reviewed complete Apache-2.0 canonical text.' }
if ([Text.Encoding]::UTF8.GetString($legalBytes['NOTICE']) -notmatch 'Copyright 2026 崛亮视觉 \(Bright Vision\)') { throw 'NOTICE must retain the company copyright.' }
$built = @($manifest.variants | Where-Object { $_.buildStatus -eq 'built' })
if (!$built.Count) { throw 'No successfully built variants are available.' }
$years = @($built.year | Sort-Object -Unique)
if (!$AllowPartial) {
    foreach ($year in 2015..2026) { if ([string]$year -notin $years) { throw "Missing a built Revit $year payload; refusing a full-range package." } }
    foreach ($key in @('2025-net8','2025-net10','2026-net8','2026-net10')) { if ($key -notin @($built.id)) { throw "Missing runtime variant: $key" } }
}
if (!$PSCmdlet.ShouldProcess($output,'Build the source-based Bright.Vision Revit installer (no installation)')) { return }
[void][IO.Directory]::CreateDirectory($output)
$module = Join-Path $output 'module'
[void][IO.Directory]::CreateDirectory($module)
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($name in @('LICENSE','NOTICE')) { [IO.File]::WriteAllBytes((Join-Path $module $name), $legalBytes[$name]) }
$variants = @()
foreach ($variant in $built) {
    $id = [string]$variant.id
    if ($id -notmatch '^(201[5-9]|202[0-6])-(net45|net48|net8|net10)$' -or $id -cne "$($variant.year)-$($variant.runtime)") { throw "Invalid variant: $id" }
    $dll = [IO.Path]::GetFullPath([IO.Path]::Combine($sourceRoot,[string]$variant.relativePath))
    if (!$dll.StartsWith($sourceRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Exporter path escapes its build directory.' }
    $record = @($variant.files | Where-Object { $_.name -eq 'Bright.Vision.Revit.dll' })
    if ($record.Count -ne 1 -or !(Test-Path -LiteralPath $dll -PathType Leaf) -or (Get-FileHash -LiteralPath $dll).Hash -ne $record[0].sha256 -or (Get-Item -LiteralPath $dll).Length -ne $record[0].size) { throw "Invalid/stale exporter bytes: $id" }
    $directory = Join-Path $module "payload\$id"
    [void][IO.Directory]::CreateDirectory($directory)
    Copy-Item -LiteralPath $dll -Destination (Join-Path $directory 'Bright.Vision.Revit.dll')
    $xml = '<?xml version="1.0" encoding="utf-8"?><RevitAddIns><AddIn Type="Application"><Name>BV</Name><Assembly>Bright.Vision.Revit.dll</Assembly><AddInId>6F12FA44-368B-4E47-9197-3DE316D5392A</AddInId><FullClassName>BrightVision.Revit.App</FullClassName><VendorId>BRVI</VendorId><VendorDescription>崛亮视觉 · Bright Vision</VendorDescription><AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession><AllowLoadingIntoExistingSession>false</AllowLoadingIntoExistingSession></AddIn></RevitAddIns>'
    [IO.File]::WriteAllText((Join-Path $directory 'Bright.Vision.addin'),$xml,$utf8)
    foreach ($name in @('LICENSE','NOTICE')) { [IO.File]::WriteAllBytes((Join-Path $directory ('Bright.Vision.'+$name)), $legalBytes[$name]) }
    $files = @('Bright.Vision.Revit.dll','Bright.Vision.addin','Bright.Vision.LICENSE','Bright.Vision.NOTICE') | ForEach-Object {
        $file=Join-Path $directory $_
        [ordered]@{ name=$_;size=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() }
    }
    $variants += [ordered]@{ id=$id;year=[string]$variant.year;runtime=[string]$variant.runtime;files=@($files);hostVerification=$false;notes=@($variant.notes) }
}
$legalRecords = @('LICENSE','NOTICE') | ForEach-Object { $path=Join-Path $module $_; [ordered]@{name=$_;size=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()} }
$bundle = [ordered]@{ schemaVersion=2;packageId='BrightVision.RevitExporter';packageVersion=[string]$manifest.packageVersion;productName='BV / 崛亮视觉 · Bright Vision';license='Apache-2.0';copyrightOwner='崛亮视觉';legalNotices=@($legalRecords);variants=$variants }
[IO.File]::WriteAllText((Join-Path $module 'bundle.json'),($bundle | ConvertTo-Json -Depth 12),$utf8)
foreach ($name in @('Manage-BrightVisionPlugin.ps1','LegacyMigration.ps1','BridgeMigration.ps1','RevitHosts.ps1','Invoke-BrightVisionSetup.ps1','README.md')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $module $name) }
Copy-Item -LiteralPath $source -Destination (Join-Path $output 'exporter-build-manifest.json')
$zip = Join-Path $output 'Bright.Vision.module.zip'
$entries = @('payload','bundle.json','LICENSE','NOTICE','Manage-BrightVisionPlugin.ps1','LegacyMigration.ps1','BridgeMigration.ps1','RevitHosts.ps1','Invoke-BrightVisionSetup.ps1','README.md') | ForEach-Object { Join-Path $module $_ }
Compress-Archive -LiteralPath $entries -DestinationPath $zip -CompressionLevel Optimal
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $output 'Bright.Vision-Setup.exe'
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/codepage:65001',
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.IO.Compression.dll','/reference:System.Web.Extensions.dll',
    "/win32manifest:$(Join-Path $PSScriptRoot 'BrightVisionSetup.manifest')", "/resource:$zip,BrightVision.Module.zip", "/out:$exe",(Join-Path $PSScriptRoot 'BrightVisionSetup.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $exe)) { throw 'Installer compilation failed.' }
[pscustomobject]@{ Installer=$exe;SHA256=(Get-FileHash -LiteralPath $exe).Hash;SupportedYears=$years;Variants=@($built.id);Installed=$false }
