#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Status','Install','Uninstall')][string]$Action = 'Status',
    [Parameter(Mandatory=$true)][string]$ModulePath,
    [ValidateSet('CurrentUser','AllUsers')][string]$InstallContext = 'CurrentUser',
    [string]$ExpectedUserSid,
    [string]$LogPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-RevitIntegrationPath([string]$Path) {
    if (!$Path -or ![IO.Path]::IsPathRooted($Path)) { throw 'An absolute integration path is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Substring([IO.Path]::GetPathRoot($full).Length).Contains(':')) { throw 'Alternate data streams are not allowed.' }
    for ($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Integration paths cannot contain reparse points: $cursor" }
    }
    return $full
}

function Write-RevitIntegrationLog([string]$Path, $Result) {
    $full = Assert-RevitIntegrationPath $Path
    $directory = [IO.Path]::GetDirectoryName($full)
    $null = [IO.Directory]::CreateDirectory($directory)
    $null = Assert-RevitIntegrationPath $full
    $temporary = Join-Path $directory ('.revit-integration-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($Result | ConvertTo-Json -Depth 12 -Compress))
        $stream = New-Object IO.FileStream($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush() } finally { $stream.Dispose() }
        $null = Assert-RevitIntegrationPath $full
        if (Test-Path -LiteralPath $full -PathType Leaf) { [IO.File]::Replace($temporary,$full,$null) }
        else { [IO.File]::Move($temporary,$full) }
    }
    finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force } }
}

# Services are injected only by dot-sourced workspace tests. The CLI exposes no
# mock paths, runtime guesses, process bypasses, or target-user overrides.
function Invoke-RevitIntegrationCore {
    param([string]$RequestedAction,[string]$Context,[string]$ExpectedSid,[hashtable]$Services,[bool]$PersistStatus=$false)
    $result = [ordered]@{
        schemaVersion=1; success=$true; action=$RequestedAction; status='not-needed'; code='no-revit'
        exporterVersion='1.7.0'; hosts=@(); warnings=@(); message='No supported Revit installation was found.'
        checkedAt=[DateTime]::UtcNow.ToString('o'); installContext=$Context
    }
    try {
        do {
            if ($Context -ne 'CurrentUser') {
                $result.status='deferred'; $result.code='current-user-required'
                $result.message='The viewer is installed for all users. Open it as the intended Windows user to install that user''s Revit exporter.'
                break
            }
            $sid = & $Services.GetUserSid
            $result.userSid = [string]$sid
            if ($ExpectedSid -and $sid -cne $ExpectedSid) {
                $result.status='deferred'; $result.code='user-context-mismatch'
                $result.message='The active Windows identity differs from the requested user. No Revit registration was changed.'
                break
            }
            $rows = @(& $Services.GetInventory)
            $detected = @($rows | Where-Object { $_.Detected })
            $result.hosts = @($rows | ForEach-Object {
                $hostResult = [ordered]@{ year=[string]$_.RevitVersion; runtime=[string]$_.Runtime; managed=[bool]$_.Managed; status=$(if (!$_.HasPayload) {'unsupported'} elseif ($_.Managed) {'installed'} else {'ready'}) }
                if ($_.PSObject.Properties.Name -contains 'ManagedVersion' -and $_.ManagedVersion) { $hostResult.managedVersion=[string]$_.ManagedVersion }
                [pscustomobject]$hostResult
            })
            $managed = @($rows | Where-Object { $_.Managed })
            if ($RequestedAction -eq 'Uninstall') {
                if (!$managed.Count) { $result.status='ready'; $result.code='uninstalled'; $result.message='No exporter managed by this product remains for this user.'; break }
            }
            elseif (!$detected.Count) { break }
            $unsupported = @($detected | Where-Object { !$_.HasPayload })
            if ($RequestedAction -ne 'Uninstall' -and $unsupported.Count) {
                $result.status='failed'; $result.success=$false; $result.code='unsupported-runtime'
                $result.message='One or more detected Revit runtimes have no verified matching exporter. No partial installation was attempted.'
                break
            }
            if (& $Services.GetRunning) {
                $result.status='deferred'; $result.code='running-revit'
                $result.message='Save your work and close Revit, then retry from the viewer. The viewer installation is complete.'
                break
            }
            if ($RequestedAction -eq 'Status') {
                $needsInstall = @($detected | Where-Object { !$_.Managed -or ($_.PSObject.Properties.Name -contains 'ManagedVersion' -and $_.ManagedVersion -ne $result.exporterVersion) })
                $result.status = if ($needsInstall.Count) {'ready'} else {'installed'}
                $result.code = if ($needsInstall.Count) {'installation-required'} else {'already-installed'}
                $result.message = if ($needsInstall.Count) {'The Revit exporter can be installed or repaired for this user.'} else {'The detected Revit exporters are installed for this user.'}
                break
            }
            $years = @($(if ($RequestedAction -eq 'Uninstall') {$managed} else {$detected}) | ForEach-Object { [string]$_.RevitVersion })
            if ($RequestedAction -eq 'Install') { $null = & $Services.RunManager 'Plan' $years }
            $operations = @(& $Services.RunManager $RequestedAction $years)
            if ($operations.Count -ne $years.Count) { throw 'The exporter manager returned an incomplete result.' }
            if (@($operations | Where-Object { $_.Status -eq 'PreservedModified' }).Count) { throw 'Modified exporter files were preserved. Review the retained ownership state before retrying.' }
            $result.results = $operations
            $result.status = if ($RequestedAction -eq 'Install') {'installed'} else {'ready'}
            $result.code = if ($RequestedAction -eq 'Install') {'installed'} else {'uninstalled'}
            $result.message = if ($RequestedAction -eq 'Install') {'Revit exporter registration is complete. Restart Revit and open BV > bright vision.'} else {'The managed exporter was removed and its legacy registrations restored where applicable.'}
            foreach ($hostResult in $result.hosts) {
                if ($hostResult.year -notin $years) { continue }
                $hostResult.managed = $RequestedAction -eq 'Install'
                $hostResult.status = if ($hostResult.managed) {'installed'} else {'ready'}
                if ($hostResult.managed) { $hostResult | Add-Member -NotePropertyName managedVersion -NotePropertyValue $result.exporterVersion -Force }
                elseif ($hostResult.PSObject.Properties.Name -contains 'managedVersion') { $hostResult.PSObject.Properties.Remove('managedVersion') }
            }
        } while ($false)
    }
    catch {
        $result.message=$_.Exception.Message
        if ($result.message.StartsWith('Close all Revit processes',[StringComparison]::Ordinal)) { $result.status='deferred'; $result.code='running-revit' }
        else { $result.success=$false; $result.status='failed'; $result.code='operation-failed' }
    }
    if ($Services.ContainsKey('Warnings')) { $result.warnings=@($Services.Warnings | ForEach-Object {[string]$_}) }
    if ($RequestedAction -ne 'Status' -or $PersistStatus) {
        try { & $Services.WriteLog ([pscustomobject]$result) }
        catch { $result.warnings += 'The integration log could not be saved: ' + $_.Exception.Message }
    }
    return [pscustomobject]$result
}

if ($MyInvocation.InvocationName -eq '.') { return }
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$ProgressPreference='SilentlyContinue'
$explicitLog = $PSBoundParameters.ContainsKey('LogPath')
try {
    $module = Assert-RevitIntegrationPath $ModulePath
    $manager = Join-Path $module 'Manage-BrightVisionPlugin.ps1'
    foreach ($name in @('Manage-BrightVisionPlugin.ps1','RevitHosts.ps1','LegacyMigration.ps1','BridgeMigration.ps1','bundle.json')) {
        $file = Assert-RevitIntegrationPath (Join-Path $module $name)
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "The bundled Revit integration component is missing: $name" }
    }
    if (!$LogPath) {
        if ($InstallContext -eq 'AllUsers') { $LogPath=Join-Path ([IO.Path]::GetDirectoryName($module)) 'revit-integration\machine-setup.json' }
        else { $LogPath=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Bright.Vision\RevitIntegration\last-result.json' }
    }
    $services=@{ Warnings=(New-Object 'Collections.Generic.List[string]') }
    $services.GetUserSid={ [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    $services.GetRunning={ @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0 }
    $services.GetInventory={
        if ([Environment]::Is64BitOperatingSystem -and ![Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell to inspect 64-bit Revit registrations.' }
        $inventory=@(& $manager -Action Detect -BundlePath $module -WarningAction SilentlyContinue)
        foreach ($row in $inventory) {
            if ($row.Managed) {
                $state=Assert-RevitIntegrationPath (Join-Path $env:APPDATA ('Autodesk\Revit\Addins\'+$row.RevitVersion+'\.BrightVision.RevitExporter.install.json'))
                if ((Get-Item -LiteralPath $state -Force).Length -gt 1048576) { throw 'Exporter ownership metadata is unexpectedly large.' }
                $value=[IO.File]::ReadAllText($state) | ConvertFrom-Json
                $row | Add-Member -NotePropertyName ManagedVersion -NotePropertyValue ([string]$value.packageVersion)
            }
            $row
        }
    }
    $services.RunManager={
        param($managerAction,$years)
        $managerWarnings=@()
        $values=@(& $manager -Action $managerAction -BundlePath $module -RevitVersions $years -Confirm:$false -WarningAction SilentlyContinue -WarningVariable managerWarnings)
        foreach ($warning in $managerWarnings) { $services.Warnings.Add([string]$warning) }
        $values
    }
    $services.WriteLog={param($value) Write-RevitIntegrationLog $LogPath $value}
    $reply=Invoke-RevitIntegrationCore -RequestedAction $Action -Context $InstallContext -ExpectedSid $ExpectedUserSid -Services $services -PersistStatus $explicitLog
}
catch {
    $reply=[pscustomobject]@{schemaVersion=1;success=$false;action=$Action;status='failed';code='bundle-unavailable';exporterVersion='1.7.0';hosts=@();warnings=@();message=$_.Exception.Message;checkedAt=[DateTime]::UtcNow.ToString('o')}
    if ($LogPath -and ($Action -ne 'Status' -or $explicitLog)) { try { Write-RevitIntegrationLog $LogPath $reply } catch { } }
}
$reply | ConvertTo-Json -Depth 12 -Compress
# Viewer installation must survive unavailable/blocked Revit integration. Its
# caller consumes success/status from JSON, never an NSIS install failure code.
exit 0
