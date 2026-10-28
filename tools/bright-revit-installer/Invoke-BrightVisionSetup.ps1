#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Detect', 'Validate', 'Plan', 'Install', 'Uninstall')]
    [string]$Action = 'Detect',
    [string[]]$RevitVersions,
    [string]$AddinsRoot,
    [string]$MachineAddinsRoot,
    [bool]$ReplaceLegacy = $true,
    [ValidateSet('net8','net10')][string]$Runtime
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$arguments = @{ BundlePath = $PSScriptRoot; ErrorAction = 'Stop'; WarningAction = 'SilentlyContinue'; WarningVariable = 'setupWarnings' }
if ($RevitVersions) { $arguments.RevitVersions = $RevitVersions }
if ($AddinsRoot) { $arguments.AddinsRoot = $AddinsRoot }
if ($MachineAddinsRoot) { $arguments.MachineAddinsRoot = $MachineAddinsRoot }
$arguments.ReplaceLegacy = $ReplaceLegacy
if ($Runtime) {
    if (!$AddinsRoot -or !$RevitVersions) { throw 'Runtime overrides require explicit versions and a custom AddinsRoot.' }
    $arguments.RuntimeOverrides = @{}
    foreach ($year in $RevitVersions) { if ([int]$year -ge 2025) { $arguments.RuntimeOverrides[[string]$year] = $Runtime } }
}
$manager = Join-Path $PSScriptRoot 'Manage-BrightVisionPlugin.ps1'
$setupWarnings = @()
try {
    if ($Action -eq 'Install') {
        $null = & $manager @arguments -Action Plan
        $results = @(& $manager @arguments -Action Install -Confirm:$false)
    }
    elseif ($Action -eq 'Uninstall') { $results = @(& $manager @arguments -Action Uninstall -Confirm:$false) }
    else { $results = @(& $manager @arguments -Action $Action) }
    [ordered]@{ success = $true; product = 'Bright.Vision'; action = $Action; results = $results; warnings = @($setupWarnings | ForEach-Object { [string]$_ }) } | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
catch {
    [ordered]@{ success = $false; product = 'Bright.Vision'; action = $Action; message = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
}
