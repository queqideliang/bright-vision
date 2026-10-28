# Exact legacy exporter identity, shared by the original BOS package and
# Bright.Vision 1.0. Never match a product by a filename or a partial "bos" name.
$legacyId = '950ABA1D-EC23-479F-AA49-F8A84CEA55E6'
function Get-LegacyMaskBytes([string]$Directory, [string]$Name) {
    Assert-LegacyName $Name
    $key = $utf8.GetBytes('BrightVision.RevitExporter.LegacyMask.v1|' + $Name.ToLowerInvariant())
    $digest = Get-BytesHash $key
    $id = ([Guid]$digest.Substring(0,32)).ToString('D')
    $assembly = [Security.SecurityElement]::Escape((Get-ChildPath $Directory 'Bright.Vision.Revit.dll'))
    # RevitAddInUtility rejects an empty RevitAddIns document. Register a
    # source-built no-op Application with a separate stable ID for each filename.
    return ,$utf8.GetBytes(('<?xml version="1.0" encoding="utf-8"?><!-- BrightVision.RevitExporter owned legacy mask v2: 950ABA1D-EC23-479F-AA49-F8A84CEA55E6 --><RevitAddIns><AddIn Type="Application"><Name>bright.vision legacy registration override</Name><Assembly>{0}</Assembly><AddInId>{1}</AddInId><FullClassName>BrightVision.Revit.LegacyMaskApp</FullClassName><VendorId>BRVI</VendorId><AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession><AllowLoadingIntoExistingSession>false</AllowLoadingIntoExistingSession></AddIn></RevitAddIns>' -f $assembly,$id))
}

function Get-LegacyMaskHash([string]$Directory, [string]$Name) { return Get-BytesHash (Get-LegacyMaskBytes $Directory $Name) }

function Test-LegacyManifest([string]$Path) {
    $xml = Read-XmlSafe $Path
    $entries = @($xml.SelectNodes('/RevitAddIns/AddIn'))
    if ($entries.Count -ne 1 -or $entries[0].GetAttribute('Type') -cne 'Application') { return $false }
    $entry = $entries[0]
    $ids = @($entry.SelectNodes('ClientId | AddInId'))
    if ($ids.Count -ne 1 -or $ids[0].InnerText.Trim() -ne $legacyId) { return $false }
    foreach ($pair in @(@('FullClassName','BOSRevitExploreLoader.App'), @('VendorId','BOSWINNER'))) {
        $nodes = @($entry.SelectNodes($pair[0]))
        if ($nodes.Count -ne 1 -or $nodes[0].InnerText.Trim() -cne $pair[1]) { return $false }
    }
    $assembly = @($entry.SelectNodes('Assembly'))
    return $assembly.Count -eq 1 -and [IO.Path]::GetFileName($assembly[0].InnerText.Trim()) -ieq 'BOSRevitExploreLoader.dll'
}

function Assert-LegacyName([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::GetFileName($Name) -cne $Name -or $Name -notmatch '^[^<>:"/\\|?*]+\.addin$') { throw 'Invalid legacy registration filename.' }
}

function Get-LegacyBackupPath([string]$Year, $Record) {
    Assert-LegacyName ([string]$Record.name)
    if ([string]$Record.backupId -notmatch '^[a-f0-9]{32}$' -or [string]$Record.sha256 -notmatch '^[a-f0-9]{64}$' -or
        [long]$Record.size -lt 1 -or [long]$Record.size -gt 1048576 -or [long]$Record.attributes -lt 0 -or
        ([long]$Record.attributes -band ([long][IO.FileAttributes]::ReparsePoint -bor [long][IO.FileAttributes]::Directory))) { throw 'Invalid legacy backup ownership record.' }
    return Get-ChildPath (Get-ChildPath (Get-ChildPath $legacyBackupRoot $Year) $Record.backupId) 'manifest.disabled'
}

function Assert-LegacyBackup([string]$Year, $Record) {
    $path = Get-LegacyBackupPath $Year $Record
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -Force).Length -ne [long]$Record.size -or (Get-FileDigest $path) -ne $Record.sha256) { throw "Legacy backup changed or is missing; nothing was restored: $path" }
    if (!(Test-LegacyManifest $path)) { throw "Legacy backup no longer identifies the known exporter: $path" }
    $journal = Read-Json (Get-ChildPath ([IO.Path]::GetDirectoryName($path)) 'recovery.json')
    $expected = Get-ChildPath (Get-ChildPath $AddinsRoot $Year) $Record.name
    if ($journal.schemaVersion -ne 1 -or $journal.packageId -cne $packageId -or [string]$journal.year -cne $Year -or
        (Get-FullPath $journal.originalPath) -ne $expected -or $journal.record.backupId -cne $Record.backupId -or
        $journal.record.sha256 -cne $Record.sha256 -or [long]$journal.record.size -ne [long]$Record.size -or [int]$journal.record.attributes -ne [int]$Record.attributes) { throw "Invalid legacy recovery journal: $path" }
}

function Get-StateLegacy($State, [string]$Property) {
    if ($State -and $State.PSObject.Properties.Name -contains $Property) { return @($State.$Property) }
    return @()
}

function Assert-LegacyState($State, [string]$Year) {
    $names = @{}
    foreach ($record in @(Get-StateLegacy $State 'legacyBackups')) {
        $null = Get-LegacyBackupPath $Year $record
        if ($names.ContainsKey([string]$record.name)) { throw 'Duplicate legacy backup ownership record.' }
        $names[[string]$record.name] = $true
        Assert-LegacyBackup $Year $record
    }
    $names = @{}
    foreach ($record in @(Get-StateLegacy $State 'legacyMasks')) {
        Assert-LegacyName ([string]$record.name)
        if ($record.name -ieq 'Bright.Vision.addin' -or [string]$record.sha256 -cne (Get-LegacyMaskHash $State.targetDirectory $record.name) -or $names.ContainsKey([string]$record.name)) { throw 'Invalid legacy mask ownership record.' }
        $names[[string]$record.name] = $true
    }
}

function Get-LegacyPlan([string]$Directory, [string]$Year, $State) {
    $user = @(); $machine = @(); $masks = @(); $warnings = @()
    $ownedMasks = @{}
    foreach ($record in @(Get-StateLegacy $State 'legacyMasks')) { $ownedMasks[[string]$record.name] = $record }
    foreach ($name in @($ownedMasks.Keys)) {
        $path = Get-ChildPath $Directory $name
        if (!(Test-Path -LiteralPath $path)) {
            $machinePath = if ($MachineAddinsRoot) { Get-ChildPath (Get-ChildPath $MachineAddinsRoot $Year) $name } else { $null }
            if (!$machinePath -or !(Test-Path -LiteralPath $machinePath -PathType Leaf) -or !(Test-LegacyManifest $machinePath)) { throw "Owned legacy mask is missing and its original machine registration cannot be verified: $path" }
            $masks += [pscustomobject]@{ name=$name; sha256=$ownedMasks[$name].sha256 }
        }
    }
    if (Test-Path -LiteralPath $Directory -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.addin' -File -Force) {
            Assert-NoReparsePath $file.FullName
            if ($ownedMasks.ContainsKey($file.Name)) {
                if ((Get-FileDigest $file.FullName) -ne $ownedMasks[$file.Name].sha256) { throw "Owned legacy mask was modified; it will not be overwritten: $($file.FullName)" }
                continue
            }
            if (Test-LegacyManifest $file.FullName) {
                $user += [pscustomobject]@{ name=$file.Name; path=$file.FullName; sha256=(Get-FileDigest $file.FullName); size=$file.Length; attributes=[int]$file.Attributes }
            }
        }
    }
    if ($MachineAddinsRoot) {
        $globalDirectory = Get-ChildPath $MachineAddinsRoot $Year
        if (Test-Path -LiteralPath $globalDirectory -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $globalDirectory -Filter '*.addin' -File -Force) {
                if (!(Test-LegacyManifest $file.FullName)) { continue }
                $machine += $file.FullName
                if ($file.Name -ieq 'Bright.Vision.addin' -or $ownedMasks.ContainsKey($file.Name)) { continue }
                $destination = Get-ChildPath $Directory $file.Name
                if ((Test-Path -LiteralPath $destination) -and $file.Name -notin @($user | ForEach-Object { $_.name })) {
                    $warnings += "A user manifest already overrides the known machine entry; it was preserved: $destination"
                    continue
                }
                $masks += [pscustomobject]@{ name=$file.Name; sha256=(Get-LegacyMaskHash $Directory $file.Name) }
            }
        }
    }
    return [pscustomobject]@{ User=@($user); Machine=@($machine); Masks=@($masks); Warnings=@($warnings) }
}

function Disable-LegacyManifest([string]$Year, $Entry) {
    Assert-NoReparsePath $Entry.path
    if ((Get-FileDigest $Entry.path) -ne $Entry.sha256 -or !(Test-LegacyManifest $Entry.path)) { throw "Legacy registration changed during installation: $($Entry.path)" }
    $record = [pscustomobject]@{ name=$Entry.name; backupId=[Guid]::NewGuid().ToString('N'); sha256=$Entry.sha256; size=$Entry.size; attributes=$Entry.attributes }
    $backup = Get-LegacyBackupPath $Year $record
    $directory = [IO.Path]::GetDirectoryName($backup)
    Assert-NoReparsePath $directory
    $null = [IO.Directory]::CreateDirectory($directory)
    try {
        # Keep a recovery journal beside the opaque backup, outside Revit's
        # scanned Addins directories. DLLs, configuration, and licences stay put.
        $journal = [ordered]@{ schemaVersion=1; packageId=$packageId; year=$Year; originalPath=$Entry.path; record=$record }
        Write-NewFile (Get-ChildPath $directory 'recovery.json') $utf8.GetBytes(($journal | ConvertTo-Json -Depth 8))
        Assert-NoReparsePath $Entry.path
        if ((Get-FileDigest $Entry.path) -ne $Entry.sha256) { throw 'Legacy registration changed before backup.' }
        [IO.File]::Move($Entry.path, $backup)
        Assert-LegacyBackup $Year $record
        return $record
    }
    catch {
        if ((Test-Path -LiteralPath $backup -PathType Leaf) -and !(Test-Path -LiteralPath $Entry.path) -and (Get-FileDigest $backup) -eq $record.sha256) { [IO.File]::Move($backup, $Entry.path) }
        if (!(Test-Path -LiteralPath $backup)) { Remove-LegacyJournal $Year $record }
        throw
    }
}

function Remove-LegacyJournal([string]$Year, $Record) {
    $backup = Get-LegacyBackupPath $Year $Record
    $directory = [IO.Path]::GetDirectoryName($backup)
    Assert-NoReparsePath $directory
    $journal = Get-ChildPath $directory 'recovery.json'
    if (Test-Path -LiteralPath $journal -PathType Leaf) { Remove-Item -LiteralPath $journal -Force }
    if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory -Force }
}

function Restore-LegacyManifest([string]$Directory, [string]$Year, $Record) {
    Assert-LegacyBackup $Year $Record
    $backup = Get-LegacyBackupPath $Year $Record
    $destination = Get-ChildPath $Directory $Record.name
    if (Test-Path -LiteralPath $destination) { throw "Legacy restore would overwrite an existing registration; backup retained: $destination" }
    [IO.File]::Move($backup, $destination)
    [IO.File]::SetAttributes($destination, [IO.FileAttributes][int]$Record.attributes)
    Remove-LegacyJournal $Year $Record
}

function Undo-LegacyRestore([string]$Directory, [string]$Year, $Record) {
    $original = Get-ChildPath $Directory $Record.name
    if ((Get-FileDigest $original) -ne $Record.sha256 -or !(Test-LegacyManifest $original)) { throw 'Restored legacy manifest changed before ownership could be committed.' }
    $backup = Get-LegacyBackupPath $Year $Record
    $parent = [IO.Path]::GetDirectoryName($backup)
    Assert-NoReparsePath $parent
    $null = [IO.Directory]::CreateDirectory($parent)
    $journal = [ordered]@{ schemaVersion=1; packageId=$packageId; year=$Year; originalPath=$original; record=$Record }
    Write-NewFile (Get-ChildPath $parent 'recovery.json') $utf8.GetBytes(($journal | ConvertTo-Json -Depth 8))
    [IO.File]::Move($original, $backup)
    Assert-LegacyBackup $Year $Record
}
