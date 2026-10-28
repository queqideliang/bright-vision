#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$installerRoot=Split-Path $PSScriptRoot -Parent
$scriptPath=Join-Path $installerRoot 'Install-RevitIntegration.ps1'
$fixtureRoot=Join-Path (Split-Path $installerRoot -Parent) ('test-results\revit-integration-'+[Guid]::NewGuid().ToString('N'))
$module=Join-Path $fixtureRoot 'revit-plugin'
[void][IO.Directory]::CreateDirectory($module)
. $scriptPath -ModulePath $module
$script:assertions=0
function Assert([bool]$Value,[string]$Message) { if (!$Value) { throw "ASSERT: $Message" }; $script:assertions++ }
function New-Services([object[]]$Inventory=@(),[bool]$Running=$false) {
    $global:BrightRevitIntegrationFixture=@{Inventory=$Inventory;Running=$Running;Calls=(New-Object 'Collections.Generic.List[object]');Logs=(New-Object 'Collections.Generic.List[object]');InventoryReads=0;Sid='S-1-5-21-fixture';Failure=$null;LogFailure=$false}
    return @{
        GetUserSid={ $global:BrightRevitIntegrationFixture.Sid }
        GetInventory={ $global:BrightRevitIntegrationFixture.InventoryReads++; $global:BrightRevitIntegrationFixture.Inventory }
        GetRunning={ $global:BrightRevitIntegrationFixture.Running }
        RunManager={
            param($action,$years)
            $global:BrightRevitIntegrationFixture.Calls.Add([pscustomobject]@{Action=$action;Years=@($years)})
            if ($global:BrightRevitIntegrationFixture.Failure) { throw $global:BrightRevitIntegrationFixture.Failure }
            foreach ($year in $years) { [pscustomobject]@{RevitVersion=$year;Status=$(if ($action -eq 'Uninstall') {'Uninstalled'} elseif ($action -eq 'Plan') {'Ready'} else {'Installed'})} }
        }
        WriteLog={param($result) if ($global:BrightRevitIntegrationFixture.LogFailure) {throw 'fixture log lock'}; $global:BrightRevitIntegrationFixture.Logs.Add($result)}
    }
}
function HostRow([string]$Year='2020',[bool]$Managed=$false,[bool]$HasPayload=$true,[string]$Version='') {
    $row=[pscustomobject]@{RevitVersion=$Year;Detected=$true;Runtime='netframework';Managed=$Managed;HasPayload=$HasPayload}
    if ($Version) {$row | Add-Member -NotePropertyName ManagedVersion -NotePropertyValue $Version}
    return $row
}

$services=New-Services
$result=Invoke-RevitIntegrationCore -RequestedAction Status -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'not-needed' -and $result.code -eq 'no-revit') 'No Revit is a successful no-op.'
Assert ($global:BrightRevitIntegrationFixture.Logs.Count -eq 0 -and $global:BrightRevitIntegrationFixture.Calls.Count -eq 0) 'Status performs no mutations or default logging.'
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'not-needed') 'Installing a viewer without Revit succeeds.'
Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 0 -and $global:BrightRevitIntegrationFixture.Logs.Count -eq 1) 'No-Revit install logs its result without registering a plugin.'

$services=New-Services -Inventory @((HostRow)) -Running $true
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'deferred' -and $result.code -eq 'running-revit') 'A running Revit defers plugin registration without failing the viewer.'
Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 0) 'A running Revit never reaches Plan or Install.'
$result=Invoke-RevitIntegrationCore -RequestedAction Status -Context CurrentUser -Services $services
Assert ($result.status -eq 'deferred') 'Read-only status explains the running-host restriction.'

$services=New-Services -Inventory @((HostRow))
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context AllUsers -Services $services
Assert ($result.success -and $result.status -eq 'deferred' -and $result.code -eq 'current-user-required') 'All-users viewer setup requests a later logged-in-user repair.'
Assert ($global:BrightRevitIntegrationFixture.InventoryReads -eq 0 -and $global:BrightRevitIntegrationFixture.Calls.Count -eq 0) 'Elevated all-users setup does not inspect or modify an administrator plugin profile.'
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -ExpectedSid 'S-1-5-21-other' -Services $services
Assert ($result.status -eq 'deferred' -and $result.code -eq 'user-context-mismatch') 'An unexpected Windows SID cannot receive another user''s exporter registration.'
Assert ($global:BrightRevitIntegrationFixture.InventoryReads -eq 0) 'SID rejection happens before any registration inventory.'

$services=New-Services -Inventory @((HostRow '2020'),(HostRow '2026'))
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -ExpectedSid 'S-1-5-21-fixture' -Services $services
Assert ($result.success -and $result.status -eq 'installed' -and $result.exporterVersion -eq '1.7.0') 'Supported detected hosts install from the bundled exporter.'
Assert (($global:BrightRevitIntegrationFixture.Calls.Action -join ',') -eq 'Plan,Install') 'Every installation performs complete planning before installation.'
Assert (($global:BrightRevitIntegrationFixture.Calls[0].Years -join ',') -eq '2020,2026') 'The whole detected host set is planned together.'
Assert (@($result.hosts | Where-Object {!$_.managed -or $_.status -ne 'installed'}).Count -eq 0) 'Successful response includes managed host state.'
Assert ($global:BrightRevitIntegrationFixture.Logs.Count -eq 1) 'Install records a recoverable result.'

$services=New-Services -Inventory @((HostRow '2020' $true $true '1.7.0'))
$result=Invoke-RevitIntegrationCore -RequestedAction Status -Context CurrentUser -Services $services
Assert ($result.status -eq 'installed' -and $result.hosts[0].managedVersion -eq '1.7.0') 'Existing managed installation is reported without reinstalling.'
Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 0 -and $global:BrightRevitIntegrationFixture.Logs.Count -eq 0) 'Already-installed Status remains read-only.'
foreach ($previousVersion in @('1.3.1','1.4.0','1.5.0','1.6.0')) {
    $services=New-Services -Inventory @((HostRow '2020' $true $true $previousVersion))
    $result=Invoke-RevitIntegrationCore -RequestedAction Status -Context CurrentUser -Services $services
    Assert ($result.status -eq 'ready' -and $result.exporterVersion -eq '1.7.0' -and $result.hosts[0].managedVersion -eq $previousVersion) "A managed $previousVersion exporter reports the available 1.7.0 upgrade."
    Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 0 -and $global:BrightRevitIntegrationFixture.Logs.Count -eq 0) 'Upgrade Status remains read-only.'
}
$result=Invoke-RevitIntegrationCore -RequestedAction Uninstall -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'ready' -and $result.code -eq 'uninstalled' -and !$result.hosts[0].managed) 'Explicit removal returns ready without removing the viewer.'

$services=New-Services -Inventory @((HostRow '2020'),(HostRow '2026' $false $false))
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert (!$result.success -and $result.status -eq 'failed' -and $result.code -eq 'unsupported-runtime') 'Unknown runtime is actionable and cannot trigger a partial installation.'
Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 0) 'Unsupported hosts block the complete host set before writes.'

$services=New-Services -Inventory @((HostRow))
$global:BrightRevitIntegrationFixture.Failure='Unmanaged file conflict; nothing will be overwritten: fixture'
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert (!$result.success -and $result.status -eq 'failed' -and $result.message.Contains('Unmanaged file conflict')) 'Ownership conflicts are returned as failed integration, not a failed viewer setup.'
Assert ($global:BrightRevitIntegrationFixture.Calls.Count -eq 1 -and $global:BrightRevitIntegrationFixture.Calls[0].Action -eq 'Plan') 'A planning conflict never reaches Install.'
Assert ($global:BrightRevitIntegrationFixture.Logs[0].status -eq 'failed') 'Conflicts are logged for application repair.'
$global:BrightRevitIntegrationFixture.Failure='Close all Revit processes before installing or uninstalling the exporter.'
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'deferred') 'A host starting after the initial observation is still a safe deferral.'

$services=New-Services -Inventory @((HostRow))
$global:BrightRevitIntegrationFixture.LogFailure=$true
$result=Invoke-RevitIntegrationCore -RequestedAction Install -Context CurrentUser -Services $services
Assert ($result.success -and $result.status -eq 'installed' -and $result.warnings.Count -eq 1) 'A log lock cannot falsely undo or fail a completed plugin installation.'

# CLI checks execute only an inert workspace manager returning no hosts. Neither
# real Revit processes nor real AppData/ProgramData registration paths are used.
function Invoke-FixtureCli([string]$RequestedAction,[string]$Module,[string]$Log) {
    # Exercise the public script entry in child script scope. Its exit returns
    # to this harness; no nested PowerShell process or real integration is used.
    $output=& $scriptPath -Action $RequestedAction -ModulePath $Module -LogPath $Log
    return [pscustomobject]@{ExitCode=$LASTEXITCODE;Text=($output -join "`n")}
}
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $module 'Manage-BrightVisionPlugin.ps1'),'param($Action,$BundlePath) if ($Action -ne "Detect") { throw "Unexpected fixture mutation" }',$utf8)
foreach ($name in @('RevitHosts.ps1','LegacyMigration.ps1','BridgeMigration.ps1')) {[IO.File]::WriteAllText((Join-Path $module $name),'# inert fixture',$utf8)}
[IO.File]::WriteAllText((Join-Path $module 'bundle.json'),'{}',$utf8)
$log=Join-Path $fixtureRoot 'explicit-status.json'
$cli=Invoke-FixtureCli 'Status' $module $log
Assert ($cli.ExitCode -eq 0) 'The wrapper returns exit zero independently of business state.'
$reply=$cli.Text | ConvertFrom-Json
Assert ($reply.status -eq 'not-needed' -and (Test-Path -LiteralPath $log -PathType Leaf)) 'Explicit Status logging writes only the requested fixture file.'
$saved=[IO.File]::ReadAllText($log) | ConvertFrom-Json
Assert ($saved.schemaVersion -eq 1 -and $saved.code -eq 'no-revit') 'The persisted integration log is valid JSON.'
Remove-Item -LiteralPath (Join-Path $module 'BridgeMigration.ps1')
$cli=Invoke-FixtureCli 'Status' $module (Join-Path $fixtureRoot 'missing-bridge.json')
$reply=$cli.Text | ConvertFrom-Json
Assert (!$reply.success -and $reply.code -eq 'bundle-unavailable' -and $reply.exporterVersion -eq '1.7.0') 'A missing bridge migration helper is rejected with current bundle metadata.'
Assert ($reply.message.Contains('BridgeMigration.ps1')) 'The missing helper is identified before invoking the manager.'
$failureLog=Join-Path $fixtureRoot 'failed.json'
$cli=Invoke-FixtureCli 'Install' (Join-Path $fixtureRoot 'missing-module') $failureLog
$reply=$cli.Text | ConvertFrom-Json
Assert ($cli.ExitCode -eq 0 -and !$reply.success -and $reply.status -eq 'failed') 'A missing bundle cannot fail NSIS viewer installation.'
Assert (([IO.File]::ReadAllText($failureLog) | ConvertFrom-Json).code -eq 'bundle-unavailable') 'A component failure is preserved for diagnosis.'

$include=[IO.File]::ReadAllText((Join-Path $installerRoot 'revit-integration.nsh'))
Assert ($include.Contains('$installMode == "CurrentUser"') -and $include.Contains('-InstallContext AllUsers')) 'NSIS keeps user and machine setup contexts distinct.'
Assert ($include.Contains('Sysnative') -and $include.Contains('-NonInteractive') -and $include.Contains('-WindowStyle Hidden')) 'NSIS invokes hidden 64-bit Windows PowerShell without an extra UI.'
$uninstallMacro=$include.Substring($include.IndexOf('!macro customUnInstall'))
Assert (!$uninstallMacro.Contains('-Action Uninstall') -and !$uninstallMacro.Contains('Delete ') -and !$uninstallMacro.Contains('RMDir')) 'Viewer uninstall and update never silently remove Revit integration.'

[pscustomobject]@{Result='PASS';Assertions=$script:assertions;FixtureRoot=$fixtureRoot;Scope='Inert workspace fixtures; no Revit close or registration.'}
