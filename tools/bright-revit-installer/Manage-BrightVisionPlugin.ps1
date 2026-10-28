#requires -Version 5.1
<#
Installs the source-built Bright.Vision exporter for the current Windows user.
Runtime selection is based on the installed Revit configuration, not year alone.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Detect', 'Validate', 'Plan', 'Install', 'Uninstall')]
    [string]$Action = 'Plan',
    [string]$BundlePath = $PSScriptRoot,
    [string[]]$RevitVersions,
    [string]$AddinsRoot,
    [string]$MachineAddinsRoot,
    [bool]$ReplaceLegacy = $true,
    [hashtable]$RuntimeOverrides = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$packageId = 'BrightVision.RevitExporter'
$stateName = '.BrightVision.RevitExporter.install.json'
$vendorId = '6F12FA44-368B-4E47-9197-3DE316D5392A'
$coreNames = @('Bright.Vision.Revit.dll', 'Bright.Vision.addin')
$legalNames = @('Bright.Vision.LICENSE', 'Bright.Vision.NOTICE')
$allowedNames = @($coreNames) + @($legalNames)
$utf8 = New-Object System.Text.UTF8Encoding($false)
$useDefaultRoot = [string]::IsNullOrWhiteSpace($AddinsRoot)
. (Join-Path $PSScriptRoot 'RevitHosts.ps1')

function Get-FullPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'An empty filesystem path is not allowed.' }
    $full = [IO.Path]::GetFullPath($Path)
    $tail = $full.Substring([IO.Path]::GetPathRoot($full).Length)
    if ($tail.Contains(':')) { throw "Alternate data streams are not allowed: $Path" }
    if ($full.Length -eq [IO.Path]::GetPathRoot($full).Length) { return $full }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-NoReparsePath([string]$Path) {
    $full = Get-FullPath $Path
    $cursor = $full
    while (-not [string]::IsNullOrEmpty($cursor)) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points are not allowed in installation or payload paths: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Get-ChildPath([string]$Parent, [string]$Name) {
    $base = Get-FullPath $Parent
    $child = Get-FullPath ([IO.Path]::Combine($base, $Name))
    if (-not $child.StartsWith($base.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its intended directory: $Name"
    }
    Assert-NoReparsePath $child
    return $child
}

function Read-Json([string]$Path) {
    Assert-NoReparsePath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required JSON file not found: $Path" }
    if ((Get-Item -LiteralPath $Path -Force).Length -gt 1048576) { throw "JSON metadata exceeds the 1 MiB limit: $Path" }
    return (Get-Content -LiteralPath $Path -Encoding UTF8 -Raw | ConvertFrom-Json)
}

function Get-BytesHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-FileDigest([string]$Path) {
    Assert-NoReparsePath $Path
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

. (Join-Path $PSScriptRoot 'LegacyMigration.ps1')
. (Join-Path $PSScriptRoot 'BridgeMigration.ps1')

function Read-XmlSafe([string]$Path) {
    Assert-NoReparsePath $Path
    if ((Get-Item -LiteralPath $Path -Force).Length -gt 1048576) { throw "XML manifest is unexpectedly large: $Path" }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    }
    finally { $reader.Dispose() }
}

function Get-ManifestBytes([string]$Path, [string]$TargetDirectory) {
    $xml = Read-XmlSafe $Path
    $nodes = @($xml.SelectNodes('/RevitAddIns/AddIn'))
    if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('Type') -ne 'Application') { throw 'The addin manifest must contain exactly one Application.' }
    $addin = $nodes[0]
    $expected = @{ AddInId = $vendorId; FullClassName = 'BrightVision.Revit.App'; VendorId = 'BRVI'; Assembly = 'Bright.Vision.Revit.dll' }
    foreach ($name in $expected.Keys) {
        $values = @($addin.SelectNodes($name))
        if ($values.Count -ne 1 -or $values[0].InnerText.Trim() -ne $expected[$name]) { throw "Unexpected addin field: $name" }
    }
    $names = @($addin.SelectNodes('Name'))
    if ($names.Count -ne 1 -or $names[0].InnerText.Trim() -cnotin @('Bright.Vision','bright.vision','BV')) { throw 'Unexpected addin display name.' }
    $names[0].InnerText = 'BV'
    $load = @($addin.SelectNodes('AllowLoadIntoExistingSession'))
    if ($load.Count -gt 1) { throw 'Duplicate hot-load policy.' }
    if (!$load.Count) { $null = $addin.AppendChild($xml.CreateElement('AllowLoadIntoExistingSession')) }
    $addin.SelectSingleNode('AllowLoadIntoExistingSession').InnerText = 'false'
    # The 2020 official parser uses "Loading", while the developer guide also
    # documents "Load". Set both consistently; unrecognized fields are ignored.
    $loading = @($addin.SelectNodes('AllowLoadingIntoExistingSession'))
    if ($loading.Count -gt 1) { throw 'Duplicate hot-load policy.' }
    if (!$loading.Count) { $null = $addin.AppendChild($xml.CreateElement('AllowLoadingIntoExistingSession')) }
    $addin.SelectSingleNode('AllowLoadingIntoExistingSession').InnerText = 'false'
    $addin.SelectSingleNode('Assembly').InnerText = Get-ChildPath $TargetDirectory 'Bright.Vision.Revit.dll'
    $stream = New-Object IO.MemoryStream
    $settings = New-Object Xml.XmlWriterSettings
    $settings.Encoding = $utf8
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($stream, $settings)
    try { $xml.Save($writer); $writer.Flush(); return ,$stream.ToArray() }
    finally { $writer.Dispose(); $stream.Dispose() }
}

function Get-Bundle {
    $manifest = Read-Json (Get-ChildPath $BundlePath 'bundle.json')
    if ($manifest.schemaVersion -ne 2 -or $manifest.packageId -ne $packageId -or [string]::IsNullOrWhiteSpace($manifest.packageVersion)) { throw 'Unsupported bundle identity or schema.' }
    $bundleVersion = $null
    if (![version]::TryParse([string]$manifest.packageVersion, [ref]$bundleVersion)) { throw 'Unsupported bundle version.' }
    $requiredNames = if ($bundleVersion -ge [version]'1.7.0') { $allowedNames } else { $coreNames }
    $variants = @{}
    foreach ($variant in @($manifest.variants)) {
        $year = [string]$variant.year
        $key = [string]$variant.id
        $runtime = [string]$variant.runtime
        if ($year -notmatch '^(201[5-9]|202[0-6])$' -or $key -cne "$year-$runtime" -or $runtime -notin @('net45','net48','net8','net10') -or $variants.ContainsKey($key)) { throw "Invalid or duplicate payload variant: $key" }
        if (([int]$year -le 2024 -and $runtime -notin @('net45','net48')) -or ([int]$year -ge 2025 -and $runtime -notin @('net8','net10'))) { throw "Runtime does not match this Revit generation: $key" }
        $files = @($variant.files)
        if ($files.Count -ne $requiredNames.Count) { throw "Payload $key must contain exactly the approved core and version-specific license files." }
        $seen = @{}
        $directory = Get-ChildPath (Get-ChildPath $BundlePath 'payload') $key
        foreach ($file in $files) {
            $name = [string]$file.name
            if ($name -notin $requiredNames -or $seen.ContainsKey($name)) { throw "Unapproved or duplicate payload filename: $name" }
            $seen[$name] = $true
            if ([string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$file.size -lt 0) { throw "Invalid hash or size metadata: $name" }
            $filePath = Get-ChildPath $directory $name
            if (!(Test-Path -LiteralPath $filePath -PathType Leaf) -or (Get-Item -LiteralPath $filePath -Force).Length -ne [long]$file.size -or (Get-FileDigest $filePath) -ne [string]$file.sha256) { throw "Payload hash/size mismatch: $filePath" }
        }
        $null = Get-ManifestBytes (Get-ChildPath $directory 'Bright.Vision.addin') $directory
        $variants[$key] = $variant
    }
    if (!$variants.Count) { throw 'The bundle has no built variants.' }
    return [pscustomobject]@{ Manifest = $manifest; Variants = $variants }
}

function Get-Variant([string]$Year, $Bundle, [object[]]$Hosts) {
    $runtime = $null
    if ($RuntimeOverrides.ContainsKey($Year)) {
        if ($useDefaultRoot) { throw 'Runtime overrides require an explicit isolated/custom AddinsRoot.' }
        $runtime = [string]$RuntimeOverrides[$Year]
        if ($runtime -notin @('net45','net48','net8','net10')) { throw "Invalid runtime override: $runtime" }
    }
    elseif ([int]$Year -ge 2025) {
        $hostMatches = @($Hosts | Where-Object { $_.Year -eq $Year })
        if ($hostMatches.Count -ne 1 -or $hostMatches[0].Runtime -notin @('net8','net10')) { throw "Cannot determine the installed Revit $Year runtime; no plugin will be installed by guessing." }
        $runtime = $hostMatches[0].Runtime
    }
    $matches = @($Bundle.Variants.Values | Where-Object { $_.year -eq $Year -and (!$runtime -or $_.runtime -eq $runtime) })
    if ($matches.Count -ne 1) { throw "No unique built payload matches Revit $Year / $runtime." }
    return $matches[0]
}

function Read-State([string]$Directory, [string]$Year) {
    $path = Get-ChildPath $Directory $stateName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $state = Read-Json $path
    if ($state.schemaVersion -ne 1 -or $state.packageId -ne $packageId -or [string]$state.year -ne $Year -or (Get-FullPath $state.targetDirectory) -ne $Directory) { throw "Invalid ownership state: $path" }
    if ([string]::IsNullOrWhiteSpace($state.packageVersion) -or $state.createdYearDirectory -isnot [bool]) { throw "Invalid ownership metadata: $path" }
    $seen = @{}
    foreach ($file in @($state.files)) {
        if ([string]$file.name -notin $allowedNames -or $seen.ContainsKey([string]$file.name) -or [string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid file ownership record: $path" }
        $seen[[string]$file.name] = $true
    }
    Assert-LegacyState $state $Year
    Assert-BridgeBackups $state $Year
    return [pscustomobject]@{ Value = $state; Path = $path; Hash = (Get-FileDigest $path) }
}

function Assert-NoOtherRegistration([string]$Directory, [bool]$Owned) {
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return }
    Assert-NoReparsePath $Directory
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.addin' -File -Force) {
        if ($Owned -and $file.Name -eq 'Bright.Vision.addin') { continue }
        $xml = Read-XmlSafe $file.FullName
        foreach ($id in $xml.SelectNodes('//ClientId | //AddInId')) {
            if ($id.InnerText.Trim() -eq $vendorId) { throw "Another Bright.Vision registration already exists: $($file.FullName)" }
        }
    }
}

function Write-NewFile([string]$Path, [byte[]]$Bytes) {
    Assert-NoReparsePath $Path
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length) }
    catch { $stream.Dispose(); if (Test-Path -LiteralPath $Path -PathType Leaf) { Remove-Item -LiteralPath $Path -Force }; throw }
    finally { $stream.Dispose() }
}

function Remove-OwnedFile([string]$Directory, [string]$Name) {
    if ($Name -notin $allowedNames -and $Name -ne $stateName) { throw "Refusing to remove unapproved filename: $Name" }
    $path = Get-ChildPath $Directory $Name
    if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
}

function Write-ExistingState([string]$Path, [string]$ExpectedHash, [byte[]]$Bytes, [int]$MaximumBytes = 1048576) {
    Assert-NoReparsePath $Path
    # FileMode.Open preserves Hidden attributes; FileMode.Create cannot overwrite
    # a hidden file on Windows. Hold the file lock while checking and updating.
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $changed = $false
    try {
        if ($stream.Length -gt $MaximumBytes) { throw 'Owned file is unexpectedly large.' }
        $original = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $original.Length) {
            $read = $stream.Read($original, $offset, $original.Length - $offset)
            if ($read -eq 0) { throw 'Ownership state could not be fully read.' }
            $offset += $read
        }
        if ((Get-BytesHash $original) -ne $ExpectedHash) { throw "Ownership state changed during uninstall: $Path" }
        $stream.Position = 0
        $changed = $true
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.SetLength($Bytes.Length)
        $stream.Flush()
    }
    catch {
        if ($changed) {
            $stream.Position = 0
            $stream.Write($original, 0, $original.Length)
            $stream.SetLength($original.Length)
            $stream.Flush()
        }
        throw
    }
    finally { $stream.Dispose() }
}

$BundlePath = Get-FullPath $BundlePath
Assert-NoReparsePath $BundlePath
if ($Action -ne 'Uninstall') { $bundle = Get-Bundle }
if ($Action -eq 'Validate') {
    [pscustomobject]@{ Action = 'Validate'; Status = 'Valid'; PackageVersion = $bundle.Manifest.packageVersion; RevitVersions = @($bundle.Variants.Values.year | Sort-Object -Unique); Variants = @($bundle.Variants.Keys | Sort-Object) }
    return
}
if ($useDefaultRoot) {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) { throw 'APPDATA is unavailable; provide -AddinsRoot explicitly.' }
    $AddinsRoot = [IO.Path]::Combine($env:APPDATA, 'Autodesk\Revit\Addins')
}
$AddinsRoot = Get-FullPath $AddinsRoot
Assert-NoReparsePath $AddinsRoot
$legacyBackupRoot = Get-FullPath ([IO.Path]::Combine([IO.Path]::GetDirectoryName($AddinsRoot), '.BrightVision.LegacyBackup'))
Assert-NoReparsePath $legacyBackupRoot
$bridgeBackupRoot = Get-FullPath ([IO.Path]::Combine([IO.Path]::GetDirectoryName($AddinsRoot), '.BrightVision.BridgeBackup'))
Assert-NoReparsePath $bridgeBackupRoot
if (!$MachineAddinsRoot -and $useDefaultRoot -and $env:ProgramData) { $MachineAddinsRoot = [IO.Path]::Combine($env:ProgramData, 'Autodesk\Revit\Addins') }
if ($MachineAddinsRoot) {
    $MachineAddinsRoot = Get-FullPath $MachineAddinsRoot
    Assert-NoReparsePath $MachineAddinsRoot
    if ($MachineAddinsRoot -eq $AddinsRoot) { throw 'User and machine addin roots must be separate.' }
}
$hosts = @(Get-DetectedRevitHosts)
if ($Action -eq 'Detect') {
    $detected = @($hosts | ForEach-Object { $_.Year })
    $owned = @(2015..2026 | ForEach-Object { [string]$_ } | Where-Object { Test-Path -LiteralPath ([IO.Path]::Combine($AddinsRoot, $_, $stateName)) -PathType Leaf })
    foreach ($year in (@($detected) + @($owned) | Sort-Object -Unique)) {
        $candidateHosts = @($hosts | Where-Object { $_.Year -eq $year })
        $hostRuntime = if ($candidateHosts.Count) { $candidateHosts[0].Runtime } else { 'unknown' }
        $variant = $null
        try { $variant = Get-Variant $year $bundle $hosts } catch { }
        $directory = Get-ChildPath $AddinsRoot $year
        $state = Read-State $directory $year
        $legacy = Get-LegacyPlan $directory $year $(if ($state) { $state.Value } else { $null })
        $bridge = Get-BridgePlan $directory $year
        [pscustomobject]@{ RevitVersion = $year; Detected = ($year -in $detected); Managed = ($year -in $owned); HasPayload = ($null -ne $variant); Runtime = $hostRuntime; Variant = $(if ($variant) { $variant.id } else { $null }); HostVerified = $false; LegacyUserCount=$legacy.User.Count; LegacyMachineCount=$legacy.Machine.Count; BridgeMigrationRequired=($null -ne $bridge); RequiresAdmin=$false }
    }
    return
}
if (!$RevitVersions -or !$RevitVersions.Count) {
    if ($Action -eq 'Uninstall') {
        $RevitVersions = @(2015..2026 | ForEach-Object { [string]$_ } | Where-Object { Test-Path -LiteralPath ([IO.Path]::Combine($AddinsRoot, $_, $stateName)) })
    }
    else { $RevitVersions = @($hosts | ForEach-Object { $_.Year }) }
}
if (!$RevitVersions -or !$RevitVersions.Count) { throw 'No supported Revit installation/owned state was found.' }
$seenYears = @{}
$selectedVariants = @{}
foreach ($year in $RevitVersions) {
    if ($year -notmatch '^(201[5-9]|202[0-6])$' -or $seenYears.ContainsKey($year)) { throw "Unsupported or duplicate Revit version: $year" }
    if ($Action -ne 'Uninstall') { $selectedVariants[$year] = Get-Variant $year $bundle $hosts }
    $seenYears[$year] = $true
}
if ($Action -in @('Install', 'Uninstall') -and @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close all Revit processes before installing or uninstalling the exporter.' }

# Preflight every selected version before any mutation.
$plans = @()
foreach ($year in $RevitVersions) {
    $directory = Get-ChildPath $AddinsRoot $year
    $state = Read-State $directory $year
    if ($Action -eq 'Uninstall') {
        if ($state) {
            foreach ($file in @($state.Value.files)) { $null = Get-ChildPath $directory $file.name }
            foreach ($record in @(Get-StateLegacy $state.Value 'legacyMasks')) {
                $mask = Get-ChildPath $directory $record.name
                if ((Test-Path -LiteralPath $mask) -and (!(Test-Path -LiteralPath $mask -PathType Leaf) -or (Get-FileDigest $mask) -ne $record.sha256)) { throw "Owned legacy mask was modified; uninstall will preserve this installation: $mask" }
            }
            foreach ($record in @(Get-StateLegacy $state.Value 'legacyBackups')) {
                $destination = Get-ChildPath $directory $record.name
                if (Test-Path -LiteralPath $destination) {
                    $owned = @($state.Value.files | Where-Object { $_.name -eq $record.name }) + @(Get-StateLegacy $state.Value 'legacyMasks' | Where-Object { $_.name -eq $record.name })
                    if ($owned.Count -ne 1 -or (Get-FileDigest $destination) -ne $owned[0].sha256) { throw "Legacy restore would overwrite an existing registration; nothing changed: $destination" }
                }
            }
            foreach ($record in @(Get-StateLegacy $state.Value 'bridgeBackups')) {
                foreach ($file in @($record.files)) {
                    if (Test-Path -LiteralPath (Get-ChildPath $directory $file.name)) { throw 'Previous bridge restore would overwrite an existing file; nothing changed.' }
                }
            }
        }
        $plans += [pscustomobject]@{ Year = $year; Directory = $directory; State = $state }
        continue
    }
    Assert-NoOtherRegistration $directory ($null -ne $state)
    if ($MachineAddinsRoot) {
        $globalDirectory = Get-ChildPath $MachineAddinsRoot $year
        if ((Get-FullPath $globalDirectory) -ne $directory) { Assert-NoOtherRegistration $globalDirectory $false }
    }
    $legacy = Get-LegacyPlan $directory $year $(if ($state) { $state.Value } else { $null })
    $bridge = Get-BridgePlan $directory $year
    if ($bridge -and @(Get-StateLegacy $(if ($state) {$state.Value} else {$null}) 'bridgeBackups').Count) { throw 'A previous bridge is already backed up; inspect the newly active bridge before installation.' }
    $upgrade = $false
    if ($state -and $state.Value.packageVersion -ne $bundle.Manifest.packageVersion) {
        if ([version]$state.Value.packageVersion -ge [version]$bundle.Manifest.packageVersion -or $state.Value.variantId -ne $selectedVariants[$year].id) { throw "Owned installation is newer or uses a different runtime: $directory" }
        $upgrade = $true
    }
    $files = @()
    foreach ($file in @($selectedVariants[$year].files)) {
        $source = Get-ChildPath (Get-ChildPath (Get-ChildPath $BundlePath 'payload') $selectedVariants[$year].id) $file.name
        $destination = Get-ChildPath $directory $file.name
        if ($file.name -eq 'Bright.Vision.addin') { $bytes = Get-ManifestBytes $source $directory }
        else {
            $bytes = [IO.File]::ReadAllBytes($source)
            if ((Get-BytesHash $bytes) -ne [string]$file.sha256 -or $bytes.Length -ne [long]$file.size) { throw "Payload changed after validation: $source" }
        }
        $hash = Get-BytesHash $bytes
        if ($null -eq $state) {
            if ((Test-Path -LiteralPath $destination) -and !($ReplaceLegacy -and $file.name -in @($legacy.User | ForEach-Object { $_.name }))) { throw "Unmanaged file conflict; nothing will be overwritten: $destination" }
        }
        else {
            $record = @($state.Value.files | Where-Object { $_.name -eq $file.name })
            if ($record.Count -eq 0 -and $upgrade -and $file.name -in $legalNames) {
                # Only the two new license files may extend a complete old
                # two-file installation. Never adopt an unmanaged existing file.
                if (Test-Path -LiteralPath $destination) { throw "Unmanaged file conflict; nothing will be overwritten: $destination" }
            }
            else {
                if ($record.Count -ne 1 -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw "Owned installation differs or is incomplete; inspect/uninstall it before reinstalling: $directory" }
                $currentHash = Get-FileDigest $destination
                if ($currentHash -ne $record[0].sha256 -or (!$upgrade -and $currentHash -ne $hash)) { throw "Installed file has changed; refusing to overwrite: $destination" }
            }
        }
        $files += [pscustomobject]@{ Name = [string]$file.name; Bytes = $bytes; Hash = $hash }
    }
    $plans += [pscustomobject]@{ Year = $year; Directory = $directory; State = $state; Files = $files; Legacy=$legacy; Bridge=$bridge; Upgrade=$upgrade }
}

if ($Action -eq 'Plan') {
    foreach ($plan in $plans) { [pscustomobject]@{ Action = 'Plan'; RevitVersion = $plan.Year; Destination = $plan.Directory; Status = $(if ($plan.Upgrade) {'UpgradeReady'} elseif ($plan.State) { 'AlreadyInstalled' } else { 'Ready' }); FileCount = $plan.Files.Count; LegacyUserCount=$plan.Legacy.User.Count; LegacyMachineCount=$plan.Legacy.Machine.Count; BridgeMigrationRequired=($null -ne $plan.Bridge); ReplaceLegacy=$ReplaceLegacy; RequiresAdmin=$false } }
    return
}

if ($Action -eq 'Install') {
    if (-not $PSCmdlet.ShouldProcess(($plans.Directory -join ', '), 'Install Bright.Vision Revit exporter')) {
        foreach ($plan in $plans) { [pscustomobject]@{ Action = 'Install'; RevitVersion = $plan.Year; Destination = $plan.Directory; Status = 'Skipped' } }
        return
    }
    $createdFiles = New-Object 'System.Collections.Generic.List[object]'
    $createdDirectories = New-Object 'System.Collections.Generic.List[string]'
    $replacedFiles = New-Object 'System.Collections.Generic.List[object]'
    $disabledLegacy = New-Object 'System.Collections.Generic.List[object]'
    $disabledBridges = New-Object 'System.Collections.Generic.List[object]'
    try {
        foreach ($plan in $plans) {
            $migrate = $ReplaceLegacy -and ($plan.Legacy.User.Count -gt 0 -or $plan.Legacy.Masks.Count -gt 0)
            if ($plan.State -and !$plan.Upgrade -and !$migrate -and !$plan.Bridge) { continue }
            Assert-NoReparsePath $plan.Directory
            $missing = New-Object 'System.Collections.Generic.List[string]'
            $cursor = $plan.Directory
            while (-not (Test-Path -LiteralPath $cursor)) { $missing.Add($cursor); $cursor = [IO.Path]::GetDirectoryName($cursor) }
            for ($i = $missing.Count - 1; $i -ge 0; $i--) {
                Assert-NoReparsePath $missing[$i]
                $null = [IO.Directory]::CreateDirectory($missing[$i])
                $createdDirectories.Add($missing[$i])
            }
            $backups = @(Get-StateLegacy $(if ($plan.State) { $plan.State.Value } else { $null }) 'legacyBackups')
            $masks = @(Get-StateLegacy $(if ($plan.State) { $plan.State.Value } else { $null }) 'legacyMasks')
            $bridgeBackups = @(Get-StateLegacy $(if ($plan.State) { $plan.State.Value } else { $null }) 'bridgeBackups')
            if ($plan.Bridge) {
                $record = Disable-Bridge $plan.Directory $plan.Year $plan.Bridge
                $bridgeBackups += $record
                $disabledBridges.Add([pscustomobject]@{ Directory=$plan.Directory;Year=$plan.Year;Record=$record })
            }
            if ($ReplaceLegacy) {
                foreach ($entry in $plan.Legacy.User) {
                    $record = Disable-LegacyManifest $plan.Year $entry
                    $backups += $record
                    $disabledLegacy.Add([pscustomobject]@{ Directory=$plan.Directory; Year=$plan.Year; Record=$record })
                }
                foreach ($mask in $plan.Legacy.Masks) {
                    $path = Get-ChildPath $plan.Directory $mask.name
                    $maskBytes = Get-LegacyMaskBytes $plan.Directory $mask.name
                    if ((Get-BytesHash $maskBytes) -ne $mask.sha256) { throw 'Legacy mask plan changed.' }
                    Write-NewFile $path $maskBytes
                    $createdFiles.Add([pscustomobject]@{ Directory=$plan.Directory; Name=$mask.name; Hash=$mask.sha256; IsMask=$true })
                    if ($mask.name -notin @($masks | ForEach-Object { $_.name })) { $masks += $mask }
                }
            }
            foreach ($file in $plan.Files) {
                $path = Get-ChildPath $plan.Directory $file.Name
                if ($plan.State) {
                    if (!$plan.Upgrade) { continue }
                    $old = @($plan.State.Value.files | Where-Object { $_.name -eq $file.Name })
                    if ($old.Count -eq 0 -and $file.Name -in $legalNames) {
                        Write-NewFile $path $file.Bytes
                        $createdFiles.Add([pscustomobject]@{ Directory=$plan.Directory; Name=$file.Name; Hash=$file.Hash; IsMask=$false })
                    }
                    else {
                        if ($old.Count -ne 1) { throw "Owned installation changed during upgrade: $path" }
                        $original = [IO.File]::ReadAllBytes($path)
                        if ((Get-BytesHash $original) -ne $old[0].sha256) { throw "Owned file changed during upgrade: $path" }
                        Write-ExistingState $path $old[0].sha256 $file.Bytes 134217728
                        $replacedFiles.Add([pscustomobject]@{ Path=$path; Hash=$file.Hash; Bytes=$original })
                    }
                }
                else {
                    Write-NewFile $path $file.Bytes
                    $createdFiles.Add([pscustomobject]@{ Directory=$plan.Directory; Name=$file.Name; Hash=$file.Hash; IsMask=$false })
                }
            }
            $newState = [ordered]@{
                schemaVersion = 1; packageId = $packageId; packageVersion = [string]$bundle.Manifest.packageVersion
                year = $plan.Year; targetDirectory = $plan.Directory; variantId = [string]$selectedVariants[$plan.Year].id
                createdYearDirectory = $(if ($plan.State) { $plan.State.Value.createdYearDirectory } else { $createdDirectories.Contains($plan.Directory) })
                files = @($plan.Files | ForEach-Object { [ordered]@{ name = $_.Name; sha256 = $_.Hash } })
                legacyBackups = @($backups); legacyMasks = @($masks); bridgeBackups = @($bridgeBackups)
            }
            $stateBytes = $utf8.GetBytes(($newState | ConvertTo-Json -Depth 8))
            $statePath = Get-ChildPath $plan.Directory $stateName
            if ($plan.State) {
                $original = [IO.File]::ReadAllBytes($statePath)
                Write-ExistingState $statePath $plan.State.Hash $stateBytes
                $replacedFiles.Add([pscustomobject]@{ Path=$statePath; Hash=(Get-BytesHash $stateBytes); Bytes=$original })
            }
            else {
                Write-NewFile $statePath $stateBytes
                $createdFiles.Add([pscustomobject]@{ Directory=$plan.Directory; Name=$stateName; Hash=(Get-BytesHash $stateBytes); IsMask=$false })
            }
            [IO.File]::SetAttributes($statePath, ([IO.File]::GetAttributes($statePath) -bor [IO.FileAttributes]::Hidden))
        }
    }
    catch {
        $originalError = $_
        for ($i = $createdFiles.Count - 1; $i -ge 0; $i--) {
            $record = $createdFiles[$i]
            try {
                $path = Get-ChildPath $record.Directory $record.Name
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-FileDigest $path) -eq $record.Hash) {
                    if ($record.IsMask) { Assert-LegacyName $record.Name; Remove-Item -LiteralPath $path -Force }
                    else { Remove-OwnedFile $record.Directory $record.Name }
                }
            }
            catch { Write-Warning "Rollback could not remove a newly created file: $($_.Exception.Message)" }
        }
        for ($i = $replacedFiles.Count - 1; $i -ge 0; $i--) {
            $record = $replacedFiles[$i]
            try { Write-ExistingState $record.Path $record.Hash $record.Bytes 134217728 }
            catch { Write-Warning "Rollback could not restore an upgraded file: $($_.Exception.Message)" }
        }
        for ($i = $disabledLegacy.Count - 1; $i -ge 0; $i--) {
            $record = $disabledLegacy[$i]
            try { Restore-LegacyManifest $record.Directory $record.Year $record.Record }
            catch { Write-Warning "Rollback retained a legacy backup for recovery: $($_.Exception.Message)" }
        }
        for ($i = $disabledBridges.Count - 1; $i -ge 0; $i--) {
            $record = $disabledBridges[$i]
            try { Restore-Bridge $record.Directory $record.Year $record.Record; Remove-BridgeJournal $record.Year $record.Record }
            catch { Write-Warning "Rollback retained a bridge backup for recovery: $($_.Exception.Message)" }
        }
        for ($i = $createdDirectories.Count - 1; $i -ge 0; $i--) {
            try {
                $path = Get-FullPath $createdDirectories[$i]
                Assert-NoReparsePath $path
                if ((Test-Path -LiteralPath $path -PathType Container) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path -Force }
            }
            catch { Write-Warning "Rollback left an empty directory: $($_.Exception.Message)" }
        }
        throw $originalError
    }
    foreach ($plan in $plans) {
        foreach ($warning in $plan.Legacy.Warnings) { Write-Warning $warning }
        [pscustomobject]@{ Action='Install'; RevitVersion=$plan.Year; Destination=$plan.Directory; Status=$(if ($plan.Upgrade) {'Upgraded'} elseif ($plan.State) {'AlreadyInstalled'} else {'Installed'}); LegacyUserBackedUp=$(if ($ReplaceLegacy) {$plan.Legacy.User.Count} else {0}); LegacyMachineMasked=$(if ($ReplaceLegacy) {$plan.Legacy.Machine.Count} else {0}); BridgeBackedUp=($null -ne $plan.Bridge); LegacyReplacementEnabled=$ReplaceLegacy; MachineFilesChanged=$false }
    }
    return
}

foreach ($plan in $plans) {
    if (-not $plan.State) {
        [pscustomobject]@{ Action = 'Uninstall'; RevitVersion = $plan.Year; Destination = $plan.Directory; Status = 'NotManaged'; PreservedFiles = @() }
        continue
    }
    if (-not $PSCmdlet.ShouldProcess($plan.Directory, 'Remove only unchanged files owned by this installer')) {
        [pscustomobject]@{ Action = 'Uninstall'; RevitVersion = $plan.Year; Destination = $plan.Directory; Status = 'Skipped'; PreservedFiles = @() }
        continue
    }
    if ((Get-FileDigest $plan.State.Path) -ne $plan.State.Hash) { throw "Ownership state changed during uninstall: $($plan.State.Path)" }
    $remaining = @()
    foreach ($file in @($plan.State.Value.files)) {
        $path = Get-ChildPath $plan.Directory $file.name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileDigest $path) -ne $file.sha256) {
            $remaining += $file
            Write-Warning "Preserving modified file: $path"
            continue
        }
        Remove-OwnedFile $plan.Directory $file.name
    }
    foreach ($record in @(Get-StateLegacy $plan.State.Value 'legacyMasks')) {
        $path = Get-ChildPath $plan.Directory $record.name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            if ((Get-FileDigest $path) -ne $record.sha256) { throw "Owned legacy mask changed during uninstall: $path" }
            Remove-Item -LiteralPath $path -Force
        }
    }
    # Commit progress before restoring backups. A locked second backup must not
    # make an already-restored first backup appear missing on the next attempt.
    $plan.State.Value.files = @($remaining)
    $plan.State.Value | Add-Member -NotePropertyName legacyMasks -NotePropertyValue @() -Force
    $progress = $utf8.GetBytes(($plan.State.Value | ConvertTo-Json -Depth 8))
    Write-ExistingState $plan.State.Path $plan.State.Hash $progress
    $plan.State.Hash = Get-BytesHash $progress
    foreach ($record in @(Get-StateLegacy $plan.State.Value 'legacyBackups')) {
        $previous = @(Get-StateLegacy $plan.State.Value 'legacyBackups')
        Restore-LegacyManifest $plan.Directory $plan.Year $record
        $plan.State.Value.legacyBackups = @($previous | Where-Object { $_.backupId -ne $record.backupId })
        $progress = $utf8.GetBytes(($plan.State.Value | ConvertTo-Json -Depth 8))
        try { Write-ExistingState $plan.State.Path $plan.State.Hash $progress }
        catch {
            $plan.State.Value.legacyBackups = $previous
            try { Undo-LegacyRestore $plan.Directory $plan.Year $record }
            catch { Write-Warning "Ownership could not be committed; inspect the retained recovery journal: $($_.Exception.Message)" }
            throw
        }
        $plan.State.Hash = Get-BytesHash $progress
    }
    foreach ($record in @(Get-StateLegacy $plan.State.Value 'bridgeBackups')) {
        $previous = @(Get-StateLegacy $plan.State.Value 'bridgeBackups')
        Restore-Bridge $plan.Directory $plan.Year $record
        $plan.State.Value.bridgeBackups = @($previous | Where-Object { $_.backupId -ne $record.backupId })
        $progress = $utf8.GetBytes(($plan.State.Value | ConvertTo-Json -Depth 10))
        try { Write-ExistingState $plan.State.Path $plan.State.Hash $progress }
        catch {
            $plan.State.Value.bridgeBackups = $previous
            try { Undo-BridgeRestore $plan.Directory $plan.Year $record }
            catch { Write-Warning "Bridge ownership could not be committed; inspect the retained recovery journal: $($_.Exception.Message)" }
            throw
        }
        $plan.State.Hash = Get-BytesHash $progress
        Remove-BridgeJournal $plan.Year $record
    }
    if ($remaining.Count -eq 0) {
        Remove-OwnedFile $plan.Directory $stateName
        if ($plan.State.Value.createdYearDirectory -and @(Get-ChildItem -LiteralPath $plan.Directory -Force).Count -eq 0) {
            $path = Get-ChildPath $AddinsRoot $plan.Year
            Remove-Item -LiteralPath $path -Force
        }
    }
    [pscustomobject]@{ Action = 'Uninstall'; RevitVersion = $plan.Year; Destination = $plan.Directory; Status = $(if ($remaining.Count) { 'PreservedModified' } else { 'Uninstalled' }); PreservedFiles = @($remaining | ForEach-Object { $_.name }) }
}
