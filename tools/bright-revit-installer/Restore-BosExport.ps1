#requires -Version 5.1
<#
Restore the original BOS export workflow for the explicitly supported legacy
hosts. Default Plan is read-only. Restore delegates ownership-aware removal to
the frozen 1.2 manager; it never writes ProgramData or changes another year.
#>
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param(
    [ValidateSet('Plan','Restore')][string]$Action='Plan',
    [ValidateSet('2018','2020')][string[]]$RevitVersions=@('2020','2018'),
    [string]$ModulePath=(Join-Path $PSScriptRoot 'artifacts\Bright.Vision-1.2.0-final\module'),
    [string]$AddinsRoot=(Join-Path $env:APPDATA 'Autodesk\Revit\Addins'),
    [string]$MachineAddinsRoot=(Join-Path $env:ProgramData 'Autodesk\Revit\Addins'),
    [string]$ReportPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$utf8=New-Object Text.UTF8Encoding($false)

function Resolve-RestorePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if (!$Path -or ![IO.Path]::IsPathRooted($Path) -or $full.Substring([IO.Path]::GetPathRoot($full).Length).Contains(':')) { throw 'An absolute path without alternate streams is required.' }
    for ($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse paths are not allowed: $cursor" }
    }
    return $full.TrimEnd('\')
}
function Read-RestoreJson([string]$Path) {
    $null=Resolve-RestorePath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path -Force).Length -gt 1048576) { throw "Missing or oversized metadata: $Path" }
    return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json)
}
function Read-RestoreXml([string]$Path) {
    $null=Resolve-RestorePath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path -Force).Length -gt 1048576) { throw "Missing or oversized registration: $Path" }
    $settings=New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null
    $reader=[Xml.XmlReader]::Create($Path,$settings)
    try { $xml=New-Object Xml.XmlDocument; $xml.XmlResolver=$null; $xml.Load($reader); return ,$xml }
    finally {$reader.Dispose()}
}
function Get-RestoreHash([string]$Path) { $null=Resolve-RestorePath $Path; return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-KnownBos([string]$Path) {
    $xml=Read-RestoreXml $Path
    $entries=@($xml.SelectNodes('/RevitAddIns/AddIn'))
    if ($entries.Count -ne 1 -or $entries[0].GetAttribute('Type') -cne 'Application') { throw "The legacy registration is not a single known BOS Application: $Path" }
    $entry=$entries[0]
    $ids=@($entry.SelectNodes('ClientId | AddInId'))
    if ($ids.Count -ne 1 -or $ids[0].InnerText.Trim() -ine '950ABA1D-EC23-479F-AA49-F8A84CEA55E6' -or
        [string]$entry.FullClassName -cne 'BOSRevitExploreLoader.App' -or [string]$entry.VendorId -cne 'BOSWINNER' -or
        [IO.Path]::GetFileName([string]$entry.Assembly) -ine 'BOSRevitExploreLoader.dll') { throw "The legacy registration does not match the inspected BOS identity: $Path" }
    $loader=[string]$entry.Assembly
    if (![IO.Path]::IsPathRooted($loader)) {$loader=Join-Path ([IO.Path]::GetDirectoryName($Path)) $loader}
    $loader=Resolve-RestorePath $loader
    if (!(Test-Path -LiteralPath $loader -PathType Leaf)) { throw "The original BOS loader is missing: $loader" }
    return [pscustomobject]@{Manifest=$Path;SHA256=(Get-RestoreHash $Path);Loader=$loader;LoaderSHA256=(Get-RestoreHash $loader)}
}
function Save-RestoreReport([string]$Path,$Value) {
    $full=Resolve-RestorePath $Path
    foreach ($root in @($AddinsRoot,$MachineAddinsRoot)) {
        if ($full -ieq $root -or $full.StartsWith($root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Recovery reports must be outside Revit addin roots.' }
    }
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
    $null=Resolve-RestorePath $full
    [IO.File]::WriteAllText($full,($Value | ConvertTo-Json -Depth 12),$utf8)
}

$reply=[ordered]@{success=$false;action=$Action;status='Blocked';years=@($RevitVersions);targets=@();revitPids=@();machineFilesChanged=$false;message='';rollback=$null;reportPath=$null}
try {
    if (!$RevitVersions.Count -or @($RevitVersions | Select-Object -Unique).Count -ne $RevitVersions.Count) {throw 'Select unique legacy years from 2018 and 2020.'}
    $ModulePath=Resolve-RestorePath $ModulePath
    $AddinsRoot=Resolve-RestorePath $AddinsRoot
    $MachineAddinsRoot=Resolve-RestorePath $MachineAddinsRoot
    if ($AddinsRoot -ieq $MachineAddinsRoot) {throw 'User and machine addin roots must be separate.'}
    foreach($name in @('Manage-BrightVisionPlugin.ps1','RevitHosts.ps1','LegacyMigration.ps1')) {
        $file=Resolve-RestorePath (Join-Path $ModulePath $name)
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) {throw "Missing frozen manager component: $name"}
    }
    $bundle=Read-RestoreJson (Join-Path $ModulePath 'bundle.json')
    if ($bundle.packageId -cne 'BrightVision.RevitExporter' -or $bundle.packageVersion -cne '1.2.0') {throw 'Restoration requires the retained source-built 1.2.0 module.'}
    $manager=Join-Path $ModulePath 'Manage-BrightVisionPlugin.ps1'
    $reply.rollback=[ordered]@{Manager=$manager;Action='Install';RevitVersions=@($RevitVersions);AddinsRoot=$AddinsRoot;MachineAddinsRoot=$MachineAddinsRoot;ReplaceLegacy=$true;RequiresRevitClosed=$true}
    $reply.revitPids=@(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    foreach($year in $RevitVersions) {
        $directory=Resolve-RestorePath (Join-Path $AddinsRoot $year)
        $machineLegacy=Join-Path (Join-Path $MachineAddinsRoot $year) 'BOSRevitExplore.addin'
        $legacy=Assert-KnownBos $machineLegacy
        $statePath=Join-Path $directory '.BrightVision.RevitExporter.install.json'
        $state=$null; $ownedFiles=@(); $masks=@()
        if (Test-Path -LiteralPath $statePath) {
            $state=Read-RestoreJson $statePath
            if ($state.schemaVersion -ne 1 -or $state.packageId -cne 'BrightVision.RevitExporter' -or $state.packageVersion -cne '1.2.0' -or
                [string]$state.year -cne $year -or (Resolve-RestorePath $state.targetDirectory) -ine $directory) {throw "Unexpected ownership state: $statePath"}
            foreach($record in @($state.files)) {
                if ($record.name -cnotin @('Bright.Vision.Revit.dll','Bright.Vision.addin') -or [string]$record.sha256 -notmatch '^[a-fA-F0-9]{64}$') {throw 'Unexpected owned filename or digest.'}
                $file=Join-Path $directory $record.name
                if ((Test-Path -LiteralPath $file) -and (Get-RestoreHash $file) -ine $record.sha256) {throw "Managed file changed; restoration stopped before any deletion: $file"}
                $ownedFiles += [pscustomobject]@{Name=$record.name;SHA256=$record.sha256}
            }
            if (@($ownedFiles | ForEach-Object {$_.Name} | Select-Object -Unique).Count -ne $ownedFiles.Count) {throw 'Duplicate owned file record.'}
            if ($state.PSObject.Properties.Name -contains 'legacyMasks') {
                foreach($record in @($state.legacyMasks)) {
                    if ($record.name -cne 'BOSRevitExplore.addin' -or [string]$record.sha256 -notmatch '^[a-fA-F0-9]{64}$') {throw 'This recovery tool only accepts the known BOSRevitExplore registration mask.'}
                    $file=Join-Path $directory $record.name
                    if ((Test-Path -LiteralPath $file) -and (Get-RestoreHash $file) -ine $record.sha256) {throw "The owned BOS mask was changed; no files were removed: $file"}
                    $masks += [pscustomobject]@{Name=$record.name;SHA256=$record.sha256}
                }
            }
        }
        foreach($name in @('Bright.Vision.Revit.dll','Bright.Vision.addin')) {
            if ((Test-Path -LiteralPath (Join-Path $directory $name)) -and $name -notin @($ownedFiles | ForEach-Object {$_.Name})) {throw "Unmanaged Bright registration or file will not be removed: $directory\$name"}
        }
        $userLegacy=Join-Path $directory 'BOSRevitExplore.addin'
        if ((Test-Path -LiteralPath $userLegacy) -and !$masks.Count) { $null=Assert-KnownBos $userLegacy }
        $reply.targets += [pscustomobject]@{Year=$year;Managed=($null -ne $state);StateSHA256=$(if($state){Get-RestoreHash $statePath}else{$null});OwnedFiles=@($ownedFiles);OwnedMasks=@($masks);OriginalBos=$legacy}
    }
    $reply.success=$true
    $reply.status=if($reply.revitPids.Count){'Deferred'}else{'Ready'}
    $reply.message=if($reply.revitPids.Count){'Save the model and close every Revit window before restoring the original workflow.'}else{'Only the selected 2018/2020 user registrations will be removed. The original BOS UI, options, paths, and engine will be used on the next Revit start.'}
    if ($Action -eq 'Restore' -and !$reply.revitPids.Count -and $PSCmdlet.ShouldProcess(($RevitVersions -join ', '),'Restore original BOS export workflow by removing only owned Bright 1.2 registrations')) {
        if (!$ReportPath) { $ReportPath=Join-Path $PSScriptRoot ('artifacts\legacy-recovery\'+[Guid]::NewGuid().ToString('N')+'.json') }
        $reply.reportPath=Resolve-RestorePath $ReportPath
        Save-RestoreReport $reply.reportPath ([pscustomobject]$reply)
        $rows=@(& $manager -Action Uninstall -BundlePath $ModulePath -RevitVersions $RevitVersions -AddinsRoot $AddinsRoot -MachineAddinsRoot $MachineAddinsRoot -Confirm:$false -WarningAction SilentlyContinue)
        if (@($rows | Where-Object {$_.Status -notin @('Uninstalled','NotManaged')}).Count) {throw 'Some modified managed files were preserved; inspect the ownership state before retrying.'}
        foreach($target in $reply.targets) {
            if ((Get-RestoreHash $target.OriginalBos.Manifest) -ine $target.OriginalBos.SHA256 -or (Get-RestoreHash $target.OriginalBos.Loader) -ine $target.OriginalBos.LoaderSHA256) {throw 'The original BOS files changed during recovery; review before restarting Revit.'}
            $userLegacy=Join-Path (Join-Path $AddinsRoot $target.Year) 'BOSRevitExplore.addin'
            if(Test-Path -LiteralPath $userLegacy){$null=Assert-KnownBos $userLegacy}
        }
        $reply.status='Restored'
        $reply.message='Original BOS export workflow restored for the selected years. Its UI temporarily retains BOS branding. The viewer, source exporter, and other Revit years are unchanged.'
        Save-RestoreReport $reply.reportPath ([pscustomobject]$reply)
    }
}
catch {
    $reply.success=$false; $reply.status='Blocked'; $reply.message=$_.Exception.Message
    if($reply.reportPath){try{Save-RestoreReport $reply.reportPath ([pscustomobject]$reply)}catch{}}
}
[pscustomobject]$reply | ConvertTo-Json -Depth 12
