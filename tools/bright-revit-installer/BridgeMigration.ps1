# The former display bridge is our own component, but calls the old engine.
# Move its complete, unchanged installation outside Revit's scanned directory.
# This migration never loads the bridge or needs an original exporter DLL.
$bridgeNames = @('bright.vision.LegacyBridge.addin','bright.vision.LegacyBridge.dll','.bright.vision.LegacyBridge.install.json')
$bridgeId = 'DE3F75E3-14D1-4E2C-A773-76C6D75DC263'

function Assert-BridgeManifest([string]$Path, [string]$Directory) {
    $xml = Read-XmlSafe $Path
    $entries = @($xml.SelectNodes('/RevitAddIns/AddIn'))
    if ($entries.Count -ne 1 -or $entries[0].GetAttribute('Type') -cne 'Application') { throw 'Unknown bridge registration.' }
    $entry = $entries[0]
    foreach ($pair in @(@('AddInId',$bridgeId),@('FullClassName','BrightVision.LegacyBridge.OwnApp'),@('VendorId','BRVI'))) {
        $values = @($entry.SelectNodes($pair[0]))
        if ($values.Count -ne 1 -or $values[0].InnerText.Trim() -ine $pair[1]) { throw 'Unknown bridge registration identity.' }
    }
    if (@($entry.SelectNodes('ClientId')).Count) { throw 'Ambiguous bridge registration identity.' }
    $assembly = @($entry.SelectNodes('Assembly'))
    if ($assembly.Count -ne 1) { throw 'Ambiguous bridge assembly path.' }
    $pathValue = $assembly[0].InnerText.Trim()
    if (![IO.Path]::IsPathRooted($pathValue)) { $pathValue = Get-ChildPath $Directory $pathValue }
    if ((Get-FullPath $pathValue) -ine (Get-ChildPath $Directory $bridgeNames[1])) { throw 'Bridge registration points to an unexpected assembly.' }
}

function Get-BridgePlan([string]$Directory, [string]$Year) {
    $present = @($bridgeNames | Where-Object { Test-Path -LiteralPath (Get-ChildPath $Directory $_) })
    if (!$present.Count) {
        # Preserve an unknown registration under another name instead of silently
        # leaving two products active or deleting a guessed file.
        if (Test-Path -LiteralPath $Directory -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.addin' -File -Force) {
                $xml = Read-XmlSafe $file.FullName
                if (@($xml.SelectNodes('//AddInId | //ClientId') | Where-Object { $_.InnerText.Trim() -ieq $bridgeId }).Count) { throw "Unmanaged bridge registration must be inspected before migration: $($file.FullName)" }
            }
        }
        return $null
    }
    if ($present.Count -ne 3) { throw 'The previous bridge installation is incomplete; preserve it for recovery before installing.' }
    $statePath = Get-ChildPath $Directory $bridgeNames[2]
    $state = Read-Json $statePath
    if ($state.schemaVersion -ne 1 -or $state.packageId -cne 'BrightVision.LegacyBridge' -or $state.version -cnotin @('1.0.0','1.1.0') -or
        [string]$state.year -cne $Year -or (Get-FullPath $state.targetDirectory) -ine $Directory -or $state.createdYearDirectory -isnot [bool] -or @($state.files).Count -ne 2) { throw 'Unknown bridge ownership state; no bridge files will be replaced.' }
    $records = @()
    foreach ($name in $bridgeNames) {
        $path = Get-ChildPath $Directory $name
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Bridge ownership expects an ordinary file.' }
        $info = Get-Item -LiteralPath $path -Force
        $hash = Get-FileDigest $path
        if ($info.Length -lt 1 -or $info.Length -gt 8388608) { throw 'Bridge file is unexpectedly large or empty.' }
        if ($name -ne $bridgeNames[2]) {
            $owned = @($state.files | Where-Object { $_.name -ceq $name })
            if ($owned.Count -ne 1 -or [string]$owned[0].sha256 -cne $hash) { throw "Previous bridge was modified; nothing will be replaced: $path" }
        }
        $records += [pscustomobject]@{ name=$name;sha256=$hash;size=$info.Length;attributes=[int]$info.Attributes }
    }
    Assert-BridgeManifest (Get-ChildPath $Directory $bridgeNames[0]) $Directory
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.addin' -File -Force) {
        if ($file.Name -ieq $bridgeNames[0]) { continue }
        $xml = Read-XmlSafe $file.FullName
        if (@($xml.SelectNodes('//AddInId | //ClientId') | Where-Object { $_.InnerText.Trim() -ieq $bridgeId }).Count) { throw 'Duplicate bridge registration must be inspected before migration.' }
    }
    return [pscustomobject]@{ version=$state.version;files=@($records) }
}

function Get-BridgeBackupDirectory([string]$Year, $Record) {
    if ([string]$Record.backupId -notmatch '^[a-f0-9]{32}$' -or [string]$Record.version -cnotin @('1.0.0','1.1.0') -or @($Record.files).Count -ne 3) { throw 'Invalid bridge recovery record.' }
    $seen = @{}
    foreach ($file in @($Record.files)) {
        if ($file.name -cnotin $bridgeNames -or $seen.ContainsKey([string]$file.name) -or [string]$file.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$file.size -lt 1 -or [long]$file.size -gt 8388608 -or [long]$file.attributes -lt 0 -or
            ([long]$file.attributes -band ([long][IO.FileAttributes]::ReparsePoint -bor [long][IO.FileAttributes]::Directory))) { throw 'Invalid bridge recovery file record.' }
        $seen[[string]$file.name] = $true
    }
    return Get-ChildPath (Get-ChildPath $bridgeBackupRoot $Year) $Record.backupId
}

function Assert-BridgeBackup([string]$Year, $Record) {
    $backup = Get-BridgeBackupDirectory $Year $Record
    foreach ($file in @($Record.files)) {
        $path = Get-ChildPath $backup $file.name
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path -Force).Length -ne [long]$file.size -or (Get-FileDigest $path) -cne $file.sha256) { throw "Bridge backup is missing or changed; it will be preserved: $path" }
    }
    $journal = Read-Json (Get-ChildPath $backup 'recovery.json')
    if ($journal.schemaVersion -ne 1 -or $journal.packageId -cne $packageId -or [string]$journal.year -cne $Year -or (Get-FullPath $journal.originalDirectory) -ine (Get-ChildPath $AddinsRoot $Year) -or
        ($journal.record | ConvertTo-Json -Depth 8 -Compress) -cne ($Record | ConvertTo-Json -Depth 8 -Compress)) { throw 'Invalid bridge recovery journal.' }
    Assert-BridgeManifest (Get-ChildPath $backup $bridgeNames[0]) (Get-ChildPath $AddinsRoot $Year)
}

function Assert-BridgeBackups($State, [string]$Year) {
    $records = @(Get-StateLegacy $State 'bridgeBackups')
    if ($records.Count -gt 1) { throw 'Multiple previous bridge installations cannot be restored safely.' }
    foreach ($record in $records) { Assert-BridgeBackup $Year $record }
}

function Disable-Bridge([string]$Directory, [string]$Year, $Plan) {
    $current = Get-BridgePlan $Directory $Year
    if (!$current -or ($current | ConvertTo-Json -Depth 8 -Compress) -cne ($Plan | ConvertTo-Json -Depth 8 -Compress)) { throw 'Previous bridge changed during installation.' }
    $record = [pscustomobject]@{ backupId=[Guid]::NewGuid().ToString('N');version=$Plan.version;files=@($Plan.files) }
    $backup = Get-BridgeBackupDirectory $Year $record
    [void][IO.Directory]::CreateDirectory($backup)
    $moved = @()
    try {
        $journal = [ordered]@{ schemaVersion=1;packageId=$packageId;year=$Year;originalDirectory=$Directory;record=$record }
        Write-NewFile (Get-ChildPath $backup 'recovery.json') $utf8.GetBytes(($journal | ConvertTo-Json -Depth 10))
        foreach ($file in @($record.files)) {
            $source = Get-ChildPath $Directory $file.name
            if ((Get-FileDigest $source) -cne $file.sha256) { throw 'Bridge file changed before backup.' }
            [IO.File]::Move($source,(Get-ChildPath $backup $file.name))
            $moved += $file
        }
        Assert-BridgeBackup $Year $record
        return $record
    }
    catch {
        for ($index=$moved.Count-1;$index -ge 0;$index--) {
            $file=$moved[$index];$source=Get-ChildPath $backup $file.name;$destination=Get-ChildPath $Directory $file.name
            if (!(Test-Path -LiteralPath $destination) -and (Get-FileDigest $source) -ceq $file.sha256) { [IO.File]::Move($source,$destination) }
        }
        Remove-BridgeJournal $Year $record
        throw
    }
}

function Restore-Bridge([string]$Directory, [string]$Year, $Record) {
    Assert-BridgeBackup $Year $Record
    $backup = Get-BridgeBackupDirectory $Year $Record
    foreach ($file in @($Record.files)) { if (Test-Path -LiteralPath (Get-ChildPath $Directory $file.name)) { throw 'Restoring the previous bridge would overwrite a file; backup retained.' } }
    $moved = @()
    try {
        # Register last, so a partial restore never activates a missing engine.
        foreach ($file in @($Record.files | Sort-Object @{ Expression={if ($_.name -like '*.addin') {1} else {0}} })) {
            $destination = Get-ChildPath $Directory $file.name
            [IO.File]::Move((Get-ChildPath $backup $file.name),$destination)
            $moved += $file
            [IO.File]::SetAttributes($destination,[IO.FileAttributes][int]$file.attributes)
        }
    }
    catch {
        for ($index=$moved.Count-1;$index -ge 0;$index--) { $file=$moved[$index];[IO.File]::Move((Get-ChildPath $Directory $file.name),(Get-ChildPath $backup $file.name)) }
        throw
    }
}

function Undo-BridgeRestore([string]$Directory, [string]$Year, $Record) {
    $backup = Get-BridgeBackupDirectory $Year $Record
    foreach ($file in @($Record.files)) { if ((Get-FileDigest (Get-ChildPath $Directory $file.name)) -cne $file.sha256) { throw 'Restored bridge changed before committing ownership.' } }
    foreach ($file in @($Record.files)) { [IO.File]::Move((Get-ChildPath $Directory $file.name),(Get-ChildPath $backup $file.name)) }
    Assert-BridgeBackup $Year $Record
}

function Remove-BridgeJournal([string]$Year, $Record) {
    $backup = Get-BridgeBackupDirectory $Year $Record
    if (!(Test-Path -LiteralPath $backup -PathType Container)) { return }
    # Retain the journal if any backup file remains after an interrupted move.
    if (@($Record.files | Where-Object { Test-Path -LiteralPath (Get-ChildPath $backup $_.name) }).Count) { return }
    $journal = Get-ChildPath $backup 'recovery.json'
    if (Test-Path -LiteralPath $journal -PathType Leaf) { Remove-Item -LiteralPath $journal -Force }
    if (@(Get-ChildItem -LiteralPath $backup -Force).Count -eq 0) { Remove-Item -LiteralPath $backup -Force }
}
