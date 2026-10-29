#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts'),
    [string]$ReferenceCache = (Join-Path (Split-Path $PSScriptRoot -Parent) 'revit-reference-cache'),
    [string]$HostConfigPath,
    [string[]]$TargetIds = @(),
    [switch]$SkipFixture
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputPath)
$cache = [IO.Path]::GetFullPath($ReferenceCache)
[void][IO.Directory]::CreateDirectory($output)
$hostConfig = if ($HostConfigPath) { Get-Content -LiteralPath $HostConfigPath -Encoding UTF8 -Raw | ConvertFrom-Json } else { $null }
function ConfigValue([string]$Group,[string]$Key) {
    if ($null -eq $hostConfig) { return $null }
    $groupProperty = $hostConfig.PSObject.Properties[$Group]
    if ($null -eq $groupProperty -or $null -eq $groupProperty.Value) { return $null }
    $property = $groupProperty.Value.PSObject.Properties[$Key]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) { return $null }
    return [IO.Path]::GetFullPath([string]$property.Value)
}
$framework45 = ConfigValue 'frameworkDirectories' 'net45'
if (!$framework45) { $framework45 = Join-Path $cache 'framework\net45' }
$framework48 = ConfigValue 'frameworkDirectories' 'net48'
if (!$framework48) { $framework48 = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8' }
$dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
$roslyn = $null
if ($dotnet) {
    $sdk = (& $dotnet --list-sdks | Select-Object -Last 1) -replace ' .*$', ''
    $roslyn = Join-Path ([IO.Path]::GetDirectoryName($dotnet)) "sdk\$sdk\Roslyn\bincore\csc.dll"
}
$legacyCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
function RefPack([int]$Major,[bool]$Desktop) {
    $key = if ($Desktop) { "net$Major-desktop" } else { "net$Major-core" }
    $override = ConfigValue 'frameworkDirectories' $key
    if ($override) { return $override }
    $cached = Join-Path $cache ("framework\$key")
    if (Test-Path -LiteralPath $cached -PathType Container) { return $cached }
    if (!$dotnet) { throw "Install the .NET SDK or configure frameworkDirectories.$key before building modern Revit targets." }
    $packName = if ($Desktop) { 'Microsoft.WindowsDesktop.App.Ref' } else { 'Microsoft.NETCore.App.Ref' }
    $packRoot = Join-Path ([IO.Path]::GetDirectoryName($dotnet)) "packs\$packName"
    $pack = Get-ChildItem -LiteralPath $packRoot -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and ([version]$_.Name).Major -eq $Major } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if (!$pack) { throw "Missing $packName for .NET $Major; configure a licensed local reference directory." }
    return Join-Path $pack.FullName "ref\net$Major.0"
}
$source = @('Json.cs','PackageWriter.cs','ExportStatistics.cs','QuantitySnapshot.cs','RevitQuantityReader.cs','CivilQuantityModel.cs','CivilProject.cs','CivilLevelResolver.cs','RevitCivilQuantityReader.cs','CivilProjectWindow.cs','CivilQuantityWindow.cs','CivilQuantityCommand.cs','CivilWallRuleTemplates.cs','CivilWallReadControl.cs','CivilWallFacts.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilWallReport.cs','RevitWallCutoutReader.cs','RevitCivilWallReader.cs','CivilWallRuleWindow.cs','CivilWallWindow.cs','CivilWallCommand.cs','CivilWallJoinCore.cs','CivilWallJoinPlan.cs','CivilWallJoinPlanWindow.cs','CivilWallJoinSession.cs','RevitWallJoinReader.cs','CivilWallJoinWindow.cs','CivilWallJoinCommand.cs','BrandPixels.cs','Branding.cs','ExportOptions.cs','ExportOptionsDialog.cs','ExportAttachments.cs','SelectedViewResolver.cs','PluginUpdateCore.cs','PluginUpdateClient.cs','PluginUpdateWindow.cs','PluginHelpWindow.cs','PluginSupportCommands.cs','RevitApp.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$sourceHashes = @($source | ForEach-Object { [ordered]@{ name=[IO.Path]::GetFileName($_);sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() } })
$variants = @()
foreach ($year in 2015..2026) {
    $runtimes = if ($year -ge 2025) { @('net8','net10') } elseif ($year -ge 2021) { @('net48') } else { @('net45') }
    foreach ($runtime in $runtimes) {
        $id = "$year-$runtime"
        if ($TargetIds.Count -and $id -notin $TargetIds) { continue }
        $directory = Join-Path $output "payload\$id"
        [void][IO.Directory]::CreateDirectory($directory)
        $api = ConfigValue 'apiDirectories' $id
        if (!$api) { $api = ConfigValue 'apiDirectories' ([string]$year) }
        if (!$api) {
            $api = Join-Path $cache "refs\$id"
            if (!(Test-Path -LiteralPath $api -PathType Container)) { $api = Join-Path $cache "refs\$year" }
        }
        $references = @((Join-Path $api 'RevitAPI.dll'), (Join-Path $api 'RevitAPIUI.dll'))
        foreach ($path in $references) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing real Revit API reference: $path" } }
        $assembly = Join-Path $directory 'Bright.Vision.Revit.dll'
        $defines = @('BRIGHT_' + $runtime.ToUpperInvariant())
        if ($year -eq 2015) { $defines += 'REVIT_2015' }
        if ($year -eq 2016) { $defines += 'REVIT_2016' }
        if ($year -le 2016) { $defines += 'LEGACY_DIALOG' }
        if ($year -ge 2024) { $defines += 'REVIT_2024_OR_GREATER' }
        if ($year -ge 2020) { $defines += 'REVIT_2020_OR_GREATER' }
        $arguments = @('/nologo','/noconfig','/target:library','/platform:x64','/optimize+','/codepage:65001','/langversion:5',"/out:$assembly")
        if ($defines.Count) { $arguments += ('/define:' + ($defines -join ';')) }
        if ($runtime -in @('net45','net48')) {
            $framework = if ($runtime -eq 'net45') { $framework45 } else { $framework48 }
            $arguments += '/nostdlib+'
            foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Net.Http.dll','System.IO.Compression.dll','System.Runtime.Serialization.dll','System.Xml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Drawing.dll')) { $references += Join-Path $framework $name }
            $arguments += @($references | ForEach-Object { "/reference:$_" })
            $arguments += $source
            & $legacyCompiler @arguments
        }
        else {
            if (!$dotnet -or !$roslyn) { throw 'The .NET SDK is required for net8/net10 targets.' }
            $major = if ($runtime -eq 'net8') { 8 } else { 10 }
            $framework = RefPack $major $false
            $references += @(Get-ChildItem -LiteralPath $framework -Filter '*.dll' -File | ForEach-Object { $_.FullName })
            $desktop = RefPack $major $true
            foreach ($name in @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll','System.Windows.Forms.dll','System.Windows.Forms.Primitives.dll','System.Drawing.Common.dll')) {
                $desktopReference = Join-Path $desktop $name
                if (!(Test-Path -LiteralPath $desktopReference -PathType Leaf)) { throw "Missing WPF reference: $desktopReference" }
                # WindowsDesktop supplies the real WindowsBase; the core pack's
                # facade has the same name and must not shadow or duplicate it.
                $references = @($references | Where-Object { [IO.Path]::GetFileName($_) -ne $name })
                $references += $desktopReference
            }
            $arguments += @('/nostdlib+', '/nullable:disable')
            $arguments += @($references | ForEach-Object { "/reference:$_" })
            $arguments += $source
            & $dotnet $roslyn @arguments
        }
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Export plugin compilation failed: $id" }
        $notes = @('Compiled against actual API metadata; not verified inside a running Revit host.')
        if ($year -eq 2025 -and $runtime -eq 'net10') { $notes += 'Built for .NET 10 against the 2025 .NET 8 API contract; 2025.5 host verification is pending.' }
        $variants += [ordered]@{
            id = $id; year = [string]$year; runtime = $runtime; relativePath = "payload/$id/Bright.Vision.Revit.dll"
            files = @([ordered]@{ name = 'Bright.Vision.Revit.dll'; size = (Get-Item -LiteralPath $assembly).Length; sha256 = (Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant() })
            sourceApi = @($references[0..1] | ForEach-Object { [ordered]@{ path = $_; version = (Get-Item -LiteralPath $_).VersionInfo.FileVersion; sha256 = (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() } })
            buildStatus = 'built'; hostVerification = $false; notes = $notes
        }
        Write-Host "Built $id"
    }
}
if (!$SkipFixture) {
    $fixtureExe = Join-Path $output 'Bright.Vision.ExportFixture.exe'
    $arguments = @('/nologo','/noconfig','/target:exe','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',"/out:$fixtureExe")
    foreach ($name in @('mscorlib.dll','System.dll','System.Core.dll','System.IO.Compression.dll')) { $arguments += "/reference:$(Join-Path $framework45 $name)" }
    $arguments += @((Join-Path $PSScriptRoot 'Json.cs'), (Join-Path $PSScriptRoot 'PackageWriter.cs'), (Join-Path $PSScriptRoot 'ExportStatistics.cs'), (Join-Path $PSScriptRoot 'QuantitySnapshot.cs'), (Join-Path $PSScriptRoot 'BrandPixels.cs'), (Join-Path $PSScriptRoot 'ExportFixture.cs'))
    & $legacyCompiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compiler failed.' }
    & $fixtureExe (Join-Path $output 'export-fixture.zip')
    if ($LASTEXITCODE -ne 0) { throw 'Pure export fixture validation failed.' }
    & (Join-Path $PSScriptRoot 'tests\Test-QuantitySnapshot.ps1') -ReferenceCache $cache -OutputPath (Join-Path $output 'quantity-reader-tests')
}
foreach ($record in $sourceHashes) { if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $record.name)).Hash.ToLowerInvariant() -cne $record.sha256) { throw 'Exporter source changed during compilation; rebuild a consistent bundle.' } }
if (!$variants.Count) { throw 'No valid build targets were selected.' }
$manifest = [ordered]@{ schemaVersion = 1; packageId = 'BrightVision.RevitExporter'; packageVersion = '1.11.0'; productName = 'BV - 崛亮视觉 · Bright Vision'; addinId = '6F12FA44-368B-4E47-9197-3DE316D5392A'; vendorId = 'BRVI'; fullClassName = 'BrightVision.Revit.App'; sourceFiles=$sourceHashes; variants = $variants }
[IO.File]::WriteAllText((Join-Path $output 'build-manifest.json'),($manifest | ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]@{ Result = 'Built'; Variants = $variants.Count; Manifest = (Join-Path $output 'build-manifest.json'); HostVerification = $false }
