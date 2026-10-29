#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manager = Join-Path $root 'Manage-BrightVisionPlugin.ps1'
. (Join-Path $root 'RevitHosts.ps1')
$testRoot = Join-Path $root ('.test-work\' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$script:count = 0
$global:BrightVisionFixtureRunning = $false
# Only fixture process observations are mocked. No actual host is closed.
function Get-Process { [CmdletBinding()]param([string]$Name) if ($Name -ne 'Revit') { throw 'Unexpected process query.' }; if ($global:BrightVisionFixtureRunning) { [pscustomobject]@{ Name='Revit' } } }
function Assert([bool]$Value,[string]$Message) { if (!$Value) { throw "ASSERT: $Message" }; $script:count++ }
function Rejects([scriptblock]$Action,[string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true; $global:BrightVisionFixtureLastRejectedError=$_.Exception.Message }; Assert $failed $Message }
function Write-Text([string]$Path,[string]$Text) { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)); [IO.File]::WriteAllText($Path,$Text,(New-Object Text.UTF8Encoding($false))) }
function Snapshot([string]$Directory) {
    if (!(Test-Path -LiteralPath $Directory)) { return '<missing>' }
    return ((Get-ChildItem -LiteralPath $Directory -Recurse -Force -File | Sort-Object FullName | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n")
}
function Fixture([string]$Name) {
    $directory=Join-Path $testRoot $Name
    $bundle=Join-Path $directory 'bundle'
    [void][IO.Directory]::CreateDirectory($bundle)
    $records=@()
    foreach ($year in 2015..2026) {
        $runtimes = if ($year -ge 2025) { @('net8','net10') } elseif ($year -ge 2021) { @('net48') } else { @('net45') }
        foreach ($runtime in $runtimes) {
            $id="$year-$runtime"
            $payload=Join-Path $bundle "payload\$id"
            Write-Text (Join-Path $payload 'Bright.Vision.Revit.dll') "INERT FIXTURE $id"
            Write-Text (Join-Path $payload 'Bright.Vision.addin') '<RevitAddIns><AddIn Type="Application"><Name>Bright.Vision</Name><Assembly>Bright.Vision.Revit.dll</Assembly><AddInId>6F12FA44-368B-4E47-9197-3DE316D5392A</AddInId><FullClassName>BrightVision.Revit.App</FullClassName><VendorId>BRVI</VendorId></AddIn></RevitAddIns>'
            $files=@('Bright.Vision.Revit.dll','Bright.Vision.addin') | ForEach-Object { $file=Join-Path $payload $_; [ordered]@{name=$_;size=(Get-Item -LiteralPath $file).Length;sha256=(Get-FileHash -LiteralPath $file).Hash} }
            $records += [ordered]@{id=$id;year=[string]$year;runtime=$runtime;files=@($files)}
        }
    }
    Write-Text (Join-Path $bundle 'bundle.json') ([ordered]@{schemaVersion=2;packageId='BrightVision.RevitExporter';packageVersion='1.2.0';variants=$records} | ConvertTo-Json -Depth 12)
    return [pscustomobject]@{ Bundle=$bundle; Target=(Join-Path $directory 'addins') }
}

$case=Fixture 'all-years'
$versions=@(2015..2026 | ForEach-Object { [string]$_ })
$overrides=@{'2025'='net8';'2026'='net10'}
$validated=& $manager -Action Validate -BundlePath $case.Bundle
Assert ($validated.RevitVersions.Count -eq 12 -and $validated.Variants.Count -eq 14) 'All twelve years and both modern runtimes must be inventoried.'
$sourceBefore=Snapshot $case.Bundle
$null=& $manager -Action Plan -BundlePath $case.Bundle -RevitVersions $versions -RuntimeOverrides $overrides -AddinsRoot $case.Target
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions $versions -RuntimeOverrides $overrides -AddinsRoot $case.Target -WhatIf
Assert (!(Test-Path -LiteralPath $case.Target)) 'Read-only planning must not create a target.'
Assert ((Snapshot $case.Bundle) -ceq $sourceBefore) 'Source payload must stay unchanged.'
$installed=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions $versions -RuntimeOverrides $overrides -AddinsRoot $case.Target -Confirm:$false)
Assert ($installed.Count -eq 12) 'Every requested year must be installed.'
foreach ($year in $versions) {
    $runtime=if ([int]$year -eq 2025) {'net8'} elseif ([int]$year -eq 2026) {'net10'} elseif ([int]$year -ge 2021) {'net48'} else {'net45'}
    $directory=Join-Path $case.Target $year
    Assert ([IO.File]::ReadAllText((Join-Path $directory 'Bright.Vision.Revit.dll')) -ceq "INERT FIXTURE $year-$runtime") "Correct runtime bytes for $year."
    [xml]$xml=[IO.File]::ReadAllText((Join-Path $directory 'Bright.Vision.addin'))
    Assert ($xml.RevitAddIns.AddIn.Name -ceq 'BV') "Registered brand $year."
    Assert ($xml.RevitAddIns.AddIn.AllowLoadIntoExistingSession -ceq 'false') "No hot load for $year."
    Assert ($xml.RevitAddIns.AddIn.AllowLoadingIntoExistingSession -ceq 'false') "Official parser no-hot-load field for $year."
    Assert ($xml.RevitAddIns.AddIn.Assembly -eq (Join-Path $directory 'Bright.Vision.Revit.dll')) "Absolute assembly path $year."
}
$before=Snapshot $case.Target
$again=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions $versions -RuntimeOverrides $overrides -AddinsRoot $case.Target -Confirm:$false)
Assert (@($again | Where-Object { $_.Status -ne 'AlreadyInstalled' }).Count -eq 0) 'Repeated install must be idempotent.'
Assert ((Snapshot $case.Target) -ceq $before) 'Idempotent install must not rewrite files.'
$unknown=Join-Path $case.Target '2026\user-log.txt'
Write-Text $unknown 'KEEP'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions $versions -AddinsRoot $case.Target -Confirm:$false
Assert ([IO.File]::ReadAllText($unknown) -ceq 'KEEP') 'Uninstall must preserve unrelated files.'
foreach ($year in $versions) { Assert (!(Test-Path -LiteralPath (Join-Path $case.Target "$year\Bright.Vision.addin"))) "Unregister $year." }

$case=Fixture 'runtime-conflict'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2026 -RuntimeOverrides @{'2026'='net8'} -AddinsRoot $case.Target -Confirm:$false
$before=Snapshot $case.Target
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2026 -RuntimeOverrides @{'2026'='net10'} -AddinsRoot $case.Target -Confirm:$false } 'Do not silently replace a different runtime build.'
Assert ((Snapshot $case.Target) -ceq $before) 'A runtime conflict must leave bytes intact.'
$dll=Join-Path $case.Target '2026\Bright.Vision.Revit.dll'
Write-Text $dll 'USER MODIFIED'
$removed=@(& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2026 -AddinsRoot $case.Target -Confirm:$false)
Assert ($removed[0].Status -eq 'PreservedModified') 'Preserve modified files and their ownership record.'
Assert ([IO.File]::ReadAllText($dll) -ceq 'USER MODIFIED') 'Do not delete modified DLLs.'

$case=Fixture 'missing-variant'
$jsonPath=Join-Path $case.Bundle 'bundle.json'
$json=[IO.File]::ReadAllText($jsonPath) | ConvertFrom-Json
$json.variants=@($json.variants | Where-Object { $_.id -ne '2026-net10' })
Write-Text $jsonPath ($json | ConvertTo-Json -Depth 12)
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017,2026 -RuntimeOverrides @{'2026'='net10'} -AddinsRoot $case.Target -Confirm:$false } 'Missing modern runtime must fail before installing any earlier year.'
Assert (!(Test-Path -LiteralPath $case.Target)) 'No partial install when one runtime variant is missing.'
Rejects { & $manager -Action Plan -BundlePath $case.Bundle -RevitVersions 2027 -AddinsRoot $case.Target } 'Unknown future years must be rejected.'

$case=Fixture 'running-host'
$global:BrightVisionFixtureRunning=$true
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017 -AddinsRoot $case.Target -Confirm:$false } 'Running host protection.' }
finally { $global:BrightVisionFixtureRunning=$false }
Assert (!(Test-Path -LiteralPath $case.Target)) 'Running host protection must precede writes.'

$hostRoot=Join-Path $testRoot 'host-config'
Write-Text (Join-Path $hostRoot 'RevitAPI.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net8.0"}}'
Assert ((Get-RevitHostRuntime $hostRoot '2026') -eq 'net8') 'Read .NET8 from host config.'
Write-Text (Join-Path $hostRoot 'RevitAPI.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0"}}'
Assert ((Get-RevitHostRuntime $hostRoot '2026') -eq 'net10') 'Read .NET10 for the same Revit year.'
Write-Text (Join-Path $hostRoot 'RevitAPI.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net11.0"}}'
Assert ((Get-RevitHostRuntime $hostRoot '2026') -eq 'unknown') 'Unknown runtimes must not fall back to .NET8.'
Assert ((Get-RevitHostRuntime $hostRoot '2024') -eq 'netframework') 'Legacy Revit generation uses Framework.'

function Legacy([string]$Directory,[string]$Name='BOSRevitExplore.addin') {
    $path=Join-Path $Directory $Name
    Write-Text $path '<RevitAddIns><AddIn Type="Application"><Name>Bright.Vision</Name><Assembly>BOSRevitExploreLoader.dll</Assembly><ClientId>950ABA1D-EC23-479F-AA49-F8A84CEA55E6</ClientId><FullClassName>BOSRevitExploreLoader.App</FullClassName><VendorId>BOSWINNER</VendorId></AddIn></RevitAddIns>'
    return $path
}
function Set-BundleVersion($Case,[string]$Version) {
    $manifestPath=Join-Path $Case.Bundle 'bundle.json'
    $manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $manifest.packageVersion=$Version
    foreach ($variant in $manifest.variants) {
        $dll=Join-Path $Case.Bundle ('payload\'+$variant.id+'\Bright.Vision.Revit.dll')
        Write-Text $dll ("INERT FIXTURE " + $variant.id + ' version ' + $Version)
        $record=@($variant.files | Where-Object {$_.name -eq 'Bright.Vision.Revit.dll'})[0]
        $record.sha256=(Get-FileHash -LiteralPath $dll).Hash
        $record.size=(Get-Item -LiteralPath $dll).Length
        if ([version]$Version -ge [version]'1.7.0') {
            foreach ($name in @('Bright.Vision.LICENSE','Bright.Vision.NOTICE')) {
                $path=Join-Path ([IO.Path]::GetDirectoryName($dll)) $name
                Write-Text $path ("INERT LEGAL FIXTURE " + $name + ' version ' + $Version)
                $legal=[pscustomobject]@{name=$name;size=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}
                $variant.files=@($variant.files | Where-Object { $_.name -ne $name }) + @($legal)
            }
        }
    }
    Write-Text $manifestPath ($manifest | ConvertTo-Json -Depth 12)
}

$case=Fixture 'legacy-user-and-machine'
$machine=Join-Path (Split-Path $case.Target -Parent) 'machine'
$userLegacy=Legacy (Join-Path $case.Target '2020')
$machineLegacy=Legacy (Join-Path $machine '2020')
Write-Text (Join-Path $case.Target '2020\BOSRevitExploreLoader.dll') 'LEGACY DLL MUST STAY'
Write-Text (Join-Path $case.Target '2020\licence.dat') 'LEGACY LICENCE MUST STAY'
Write-Text (Join-Path $case.Target '2020\unrelated-bos.addin') '<RevitAddIns><AddIn Type="Application"><Name>Other BOS tool</Name><Assembly>Other.dll</Assembly><AddInId>00000000-0000-0000-0000-000000000001</AddInId><FullClassName>Other.Tool</FullClassName></AddIn></RevitAddIns>'
$originalUser=Snapshot $case.Target
$originalMachine=Snapshot $machine
$legacyHash=(Get-FileHash -LiteralPath $userLegacy).Hash
$backupRoot=Join-Path (Split-Path $case.Target -Parent) '.BrightVision.LegacyBackup'
$null=& $manager -Action Plan -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine
Assert (!(Test-Path -LiteralPath $backupRoot)) 'Planning never creates a legacy backup.'
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false)[0]
Assert ($result.LegacyUserBackedUp -eq 1 -and $result.LegacyMachineMasked -eq 1) 'Both known legacy registrations must be replaced for this user.'
[xml]$mask=[IO.File]::ReadAllText($userLegacy)
Assert (@($mask.SelectNodes('/RevitAddIns/AddIn')).Count -eq 1 -and $mask.RevitAddIns.AddIn.FullClassName -ceq 'BrightVision.Revit.LegacyMaskApp') 'A no-op user Application overrides the identically named machine manifest.'
Assert ($mask.RevitAddIns.AddIn.AddInId -ne '6F12FA44-368B-4E47-9197-3DE316D5392A' -and $mask.RevitAddIns.AddIn.AddInId -ne '950ABA1D-EC23-479F-AA49-F8A84CEA55E6') 'The mask uses its own ID and never re-registers either exporter.'
$ownedMask=[IO.File]::ReadAllText($userLegacy)
Assert ((Snapshot $machine) -ceq $originalMachine) 'Machine manifests and DLLs must never be changed.'
Assert ([IO.File]::ReadAllText((Join-Path $case.Target '2020\BOSRevitExploreLoader.dll')) -ceq 'LEGACY DLL MUST STAY') 'Original DLL retained.'
Assert ([IO.File]::ReadAllText((Join-Path $case.Target '2020\licence.dat')) -ceq 'LEGACY LICENCE MUST STAY') 'Original licence retained.'
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
$backup=Join-Path $backupRoot ('2020\'+$state.legacyBackups[0].backupId+'\manifest.disabled')
Assert ((Get-FileHash -LiteralPath $backup).Hash -eq $legacyHash) 'Legacy manifest backup is byte exact and outside Revit scanning.'
$installed=Snapshot $case.Target
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $installed) 'Legacy replacement is idempotent.'
Remove-Item -LiteralPath $userLegacy -Force
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $installed) 'An owned missing mask is repaired only after verifying the machine legacy identity.'
Write-Text $userLegacy '<RevitAddIns><!-- USER MODIFIED --></RevitAddIns>'
$modified=Snapshot $case.Target
Rejects { & $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false } 'A modified owned mask blocks uninstall before removing the new exporter.'
Assert ((Snapshot $case.Target) -ceq $modified) 'Modified masks are not overwritten or deleted.'
# Restore only this test's exact owned bytes, then verify reversible uninstall.
Write-Text $userLegacy $ownedMask
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $originalUser) 'Uninstall restores the original user manifest and preserves unrelated tools.'
Assert ((Snapshot $machine) -ceq $originalMachine) 'Uninstall leaves the machine registration available and byte exact.'

$case=Fixture 'legacy-restore-retry'
$null=Legacy (Join-Path $case.Target '2020') 'A.addin'
$null=Legacy (Join-Path $case.Target '2020') 'B.addin'
$original=Snapshot $case.Target
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
$locked=Join-Path (Split-Path $case.Target -Parent) ('.BrightVision.LegacyBackup\2020\'+$state.legacyBackups[1].backupId+'\manifest.disabled')
$lock=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Rejects { & $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'A locked second backup reports the incomplete restoration.' }
finally { $lock.Dispose() }
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
Assert (@($state.legacyBackups).Count -eq 1) ("The first successful restoration is committed before trying the second. Error: " + $global:BrightVisionFixtureLastRejectedError)
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $original) 'Uninstall can be retried after releasing a backup lock.'

# Fault injection only intercepts creation of one fixture file. No production
# testing hooks are added to the installer and no Revit process is launched.
$global:BrightVisionFixtureFailCreation=$null
$global:BrightVisionFixtureInjected=$false
function New-Object {
    [CmdletBinding()]param([Parameter(Position=0,Mandatory=$true)][string]$TypeName,[Parameter(Position=1)][object[]]$ArgumentList)
    if ($global:BrightVisionFixtureFailCreation -and $TypeName -eq 'IO.FileStream' -and [string]$ArgumentList[0] -eq $global:BrightVisionFixtureFailCreation) {
        $global:BrightVisionFixtureInjected=$true
        throw 'Injected fixture write failure.'
    }
    Microsoft.PowerShell.Utility\New-Object @PSBoundParameters
}
$case=Fixture 'legacy-cross-year-rollback'
$null=Legacy (Join-Path $case.Target '2017')
$null=Legacy (Join-Path $case.Target '2020')
$original=Snapshot $case.Target
$global:BrightVisionFixtureFailCreation=Join-Path $case.Target '2020\Bright.Vision.addin'
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017,2020 -AddinsRoot $case.Target -Confirm:$false } 'An in-transaction failure rolls back every selected year.' }
finally { $global:BrightVisionFixtureFailCreation=$null }
Assert $global:BrightVisionFixtureInjected 'The failure happened during the transaction, after migration started.'
Assert ((Snapshot $case.Target) -ceq $original) 'Rollback restores every disabled legacy manifest and removes new exporter files.'

$case=Fixture 'safe-upgrade-and-rollback'
Set-BundleVersion $case '1.1.0'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017 -AddinsRoot $case.Target -Confirm:$false
$original=Snapshot $case.Target
Set-BundleVersion $case '1.2.0'
$global:BrightVisionFixtureInjected=$false
$global:BrightVisionFixtureFailCreation=Join-Path $case.Target '2020\Bright.Vision.addin'
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017,2020 -AddinsRoot $case.Target -Confirm:$false } 'A later-year failure rolls back an earlier-year upgrade.' }
finally { $global:BrightVisionFixtureFailCreation=$null }
Assert $global:BrightVisionFixtureInjected 'Upgrade rollback fault was reached.'
Assert ((Snapshot $case.Target) -ceq $original) 'Upgrade rollback restores the previous DLL, manifest, and hidden ownership state.'
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017 -AddinsRoot $case.Target -Confirm:$false)[0]
Assert ($result.Status -eq 'Upgraded') 'An unchanged 1.1 installation upgrades directly to 1.2.'
$state=[IO.File]::ReadAllText((Join-Path $case.Target '2017\.BrightVision.RevitExporter.install.json')) | ConvertFrom-Json
Assert ($state.packageVersion -ceq '1.2.0') 'Upgraded ownership records the new package version.'
Assert ([IO.File]::ReadAllText((Join-Path $case.Target '2017\Bright.Vision.Revit.dll')).Contains('version 1.2.0')) 'Upgrade deploys verified new bytes.'
function Bridge([string]$Directory,[string]$Year,[string]$Version='1.1.0') {
    $path=Join-Path $Directory $Year
    Write-Text (Join-Path $path 'bright.vision.LegacyBridge.dll') 'INERT OWNED BRIDGE DLL'
    $assembly=[Security.SecurityElement]::Escape((Join-Path $path 'bright.vision.LegacyBridge.dll'))
    Write-Text (Join-Path $path 'bright.vision.LegacyBridge.addin') ('<RevitAddIns><AddIn Type="Application"><Name>bright.vision</Name><Assembly>'+ $assembly +'</Assembly><AddInId>DE3F75E3-14D1-4E2C-A773-76C6D75DC263</AddInId><FullClassName>BrightVision.LegacyBridge.OwnApp</FullClassName><VendorId>BRVI</VendorId></AddIn></RevitAddIns>')
    $files=@('bright.vision.LegacyBridge.dll','bright.vision.LegacyBridge.addin') | ForEach-Object { [ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $path $_)).Hash.ToLowerInvariant()} }
    $state=[ordered]@{schemaVersion=1;packageId='BrightVision.LegacyBridge';version=$Version;year=$Year;targetDirectory=$path;createdYearDirectory=$false;files=@($files)}
    Write-Text (Join-Path $path '.bright.vision.LegacyBridge.install.json') ($state | ConvertTo-Json -Depth 6)
}

$case=Fixture 'independent-bridge-migration'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2018' '1.0.0'
Bridge $case.Target '2020'
$original=Snapshot $case.Target
$bridgeRoot=Join-Path (Split-Path $case.Target -Parent) '.BrightVision.BridgeBackup'
$plan=@(& $manager -Action Plan -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target)
Assert (@($plan | Where-Object {$_.BridgeMigrationRequired}).Count -eq 2) 'Plan identifies both managed bridge versions without needing BOS files.'
Assert ((Snapshot $case.Target) -ceq $original -and !(Test-Path -LiteralPath $bridgeRoot)) 'Bridge planning is read-only.'
$installed=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target -Confirm:$false)
Assert (@($installed | Where-Object {$_.BridgeBackedUp}).Count -eq 2) 'Both bridge versions are migrated.'
foreach ($year in @('2018','2020')) {
    $path=Join-Path $case.Target $year
    Assert (!(Test-Path -LiteralPath (Join-Path $path 'bright.vision.LegacyBridge.addin'))) "No active bridge registration in $year."
    Assert (!(Test-Path -LiteralPath (Join-Path $path 'bright.vision.LegacyBridge.dll'))) "No bridge runtime DLL in $year."
    $state=[IO.File]::ReadAllText((Join-Path $path '.BrightVision.RevitExporter.install.json')) | ConvertFrom-Json
    Assert ($state.packageVersion -ceq '1.3.0' -and @($state.bridgeBackups).Count -eq 1) "Independent version and recovery ownership in $year."
    $backup=Join-Path $bridgeRoot ($year+'\'+$state.bridgeBackups[0].backupId)
    Assert (@(Get-ChildItem -LiteralPath $backup -File -Force).Count -eq 4) "All three bridge files and a recovery journal retained outside scanning for $year."
}
$before=Snapshot $case.Target
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $before) 'Independent installation with bridge backup is idempotent.'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $original) 'Uninstall restores all original bridge bytes without requiring the vendor engine.'
Assert (@(Get-ChildItem -LiteralPath $bridgeRoot -Recurse -File -Force).Count -eq 0) 'Successful restoration removes completed bridge recovery journals.'

$case=Fixture 'bridge-modified-preflight'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2018'
Bridge $case.Target '2020'
Write-Text (Join-Path $case.Target '2020\bright.vision.LegacyBridge.dll') 'USER MODIFIED'
$before=Snapshot $case.Target
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target -Confirm:$false } 'Modified bridge blocks all selected years before mutation.'
Assert ((Snapshot $case.Target) -ceq $before) 'Modified bridge and earlier selected year preserved.'

$case=Fixture 'bridge-cross-year-rollback'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2018'
Bridge $case.Target '2020'
$original=Snapshot $case.Target
$global:BrightVisionFixtureInjected=$false
$global:BrightVisionFixtureFailCreation=Join-Path $case.Target '2020\Bright.Vision.addin'
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2018,2020 -AddinsRoot $case.Target -Confirm:$false } 'Later-year write failure restores all migrated bridge installations.' }
finally { $global:BrightVisionFixtureFailCreation=$null }
Assert $global:BrightVisionFixtureInjected 'Bridge rollback fault reached after migration.'
Assert ((Snapshot $case.Target) -ceq $original) 'All bridge bytes and states return after failed install.'

$case=Fixture 'bridge-restore-locked-file'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2020'
$original=Snapshot $case.Target
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
$locked=Join-Path (Split-Path $case.Target -Parent) ('.BrightVision.BridgeBackup\2020\'+$state.bridgeBackups[0].backupId+'\bright.vision.LegacyBridge.addin')
$lock=[IO.File]::Open($locked,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Rejects { & $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'A locked bridge registration defers full restoration.' }
finally { $lock.Dispose() }
Assert (!(Test-Path -LiteralPath (Join-Path $case.Target '2020\bright.vision.LegacyBridge.dll'))) 'A partial restore is rolled back as a complete three-file unit.'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $original) 'Bridge restoration can be retried after unlocking.'

$case=Fixture 'bridge-backup-tamper'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2020'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
$backup=Join-Path (Split-Path $case.Target -Parent) ('.BrightVision.BridgeBackup\2020\'+$state.bridgeBackups[0].backupId+'\bright.vision.LegacyBridge.dll')
Write-Text $backup 'CHANGED BACKUP'
$before=Snapshot $case.Target
Rejects { & $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'Tampered bridge backup refuses uninstall before removing independent exporter.'
Assert ((Snapshot $case.Target) -ceq $before) 'Independent installation retained when bridge backup cannot be restored.'

$case=Fixture 'own-1.2-to-1.3'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017 -AddinsRoot $case.Target -Confirm:$false
Set-BundleVersion $case '1.3.0'
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2017 -AddinsRoot $case.Target -Confirm:$false)[0]
Assert ($result.Status -ceq 'Upgraded' -and !$result.BridgeBackedUp) 'Existing self-built 1.2 upgrades without bridge or vendor files.'

$case=Fixture 'bridge-with-original-machine-entry'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2020'
$machine=Join-Path (Split-Path $case.Target -Parent) 'machine'
$legacy=Legacy (Join-Path $machine '2020')
Write-Text (Join-Path $machine '2020\BOSRevitExploreLoader.dll') 'UNUSED ORIGINAL LOADER'
Write-Text (Join-Path $machine '2020\BOSRevitExplore.dll') 'UNUSED ORIGINAL CORE'
$original=Snapshot $case.Target
$machineBefore=Snapshot $machine
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false)[0]
Assert ($result.BridgeBackedUp -and $result.LegacyMachineMasked -eq 1) 'Migrate bridge and suppress machine original entry in the same transaction.'
[xml]$mask=[IO.File]::ReadAllText((Join-Path $case.Target '2020\BOSRevitExplore.addin'))
Assert ($mask.RevitAddIns.AddIn.FullClassName -ceq 'BrightVision.Revit.LegacyMaskApp' -and $mask.RevitAddIns.AddIn.Assembly -eq (Join-Path $case.Target '2020\Bright.Vision.Revit.dll')) 'Original filename overrides only load the independent no-op class.'
Assert ((Snapshot $machine) -ceq $machineBefore) 'Vendor bytes are neither copied nor changed during complete migration.'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $original -and (Snapshot $machine) -ceq $machineBefore) 'Complete uninstall restores old bridge and re-exposes original machine registration without modifying it.'

$case=Fixture 'bridge-migration-with-legacy-opt-out'
Set-BundleVersion $case '1.3.0'
Bridge $case.Target '2020'
$machine=Join-Path (Split-Path $case.Target -Parent) 'machine'
$null=Legacy (Join-Path $machine '2020')
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -ReplaceLegacy:$false -Confirm:$false)[0]
Assert ($result.BridgeBackedUp -and !$result.LegacyReplacementEnabled -and !(Test-Path -LiteralPath (Join-Path $case.Target '2020\BOSRevitExplore.addin'))) 'Legacy opt-out preserves original product but still removes this product bridge dependency.'

foreach ($upgrade in @(@{From='1.3.0';To='1.3.1'},@{From='1.3.1';To='1.4.0'},@{From='1.4.0';To='1.5.0'},@{From='1.5.0';To='1.6.0'},@{From='1.6.0';To='1.7.0'},@{From='1.7.0';To='1.8.0'})) {
$case=Fixture ('upgrade-preserves-original-recovery-'+$upgrade.From+'-'+$upgrade.To)
Set-BundleVersion $case $upgrade.From
Bridge $case.Target '2020'
$machine=Join-Path (Split-Path $case.Target -Parent) 'machine'
$null=Legacy (Join-Path $machine '2020')
$original=Snapshot $case.Target
$machineBefore=Snapshot $machine
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
$before=Snapshot $case.Target
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$stateBefore=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
$bridgeBefore=$stateBefore.bridgeBackups | ConvertTo-Json -Depth 10 -Compress
$masksBefore=$stateBefore.legacyMasks | ConvertTo-Json -Depth 10 -Compress
$backupRoot=Join-Path (Split-Path $case.Target -Parent) '.BrightVision.BridgeBackup'
$backupBefore=Snapshot $backupRoot
Set-BundleVersion $case $upgrade.To
$global:BrightVisionFixtureInjected=$false
$global:BrightVisionFixtureFailCreation=Join-Path $case.Target '2018\Bright.Vision.addin'
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020,2018 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false } "Upgrade $($upgrade.From) -> $($upgrade.To) rolls back if a later selected year fails." }
finally { $global:BrightVisionFixtureFailCreation=$null }
Assert $global:BrightVisionFixtureInjected 'Cross-year failure happened after the upgrade.'
Assert ((Snapshot $case.Target) -ceq $before -and (Snapshot $backupRoot) -ceq $backupBefore -and (Snapshot $machine) -ceq $machineBefore) "Rollback preserves $($upgrade.From) bytes, bridge backup, and machine registration."
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false)[0]
$stateAfter=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
Assert ($result.Status -ceq 'Upgraded' -and $stateAfter.packageVersion -ceq $upgrade.To) "Complete $($upgrade.From) upgrades to $($upgrade.To)."
Assert (($stateAfter.bridgeBackups | ConvertTo-Json -Depth 10 -Compress) -ceq $bridgeBefore -and (Snapshot $backupRoot) -ceq $backupBefore) 'Upgrade keeps the original bridge recovery record and bytes.'
Assert (($stateAfter.legacyMasks | ConvertTo-Json -Depth 10 -Compress) -ceq $masksBefore -and (Snapshot $machine) -ceq $machineBefore) 'Upgrade keeps machine mask ownership and leaves original machine files untouched.'
if ([version]$upgrade.To -ge [version]'1.7.0') {
    if ([version]$upgrade.From -lt [version]'1.7.0') {
        Assert (@($stateBefore.files).Count -eq 2 -and @($stateAfter.files).Count -eq 4) 'The 1.6 two-file owner state upgrades to four owned files.'
    } else {
        Assert (@($stateBefore.files).Count -eq 4 -and @($stateAfter.files).Count -eq 4) 'A legal-aware owner upgrades with all four managed files preserved.'
    }
    foreach ($name in @('Bright.Vision.LICENSE','Bright.Vision.NOTICE')) {
        $path=Join-Path $case.Target ('2020\'+$name)
        $record=@($stateAfter.files | Where-Object name -eq $name)
        Assert ($record.Count -eq 1 -and (Get-FileHash -LiteralPath $path).Hash -ieq $record[0].sha256) 'New legal file is installed with exact ownership hash.'
    }
}
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -MachineAddinsRoot $machine -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $original -and (Snapshot $machine) -ceq $machineBefore) 'Uninstall after upgrade still restores the pre-1.3 bridge and original registration.'
}
$case=Fixture 'legal-fresh-install-and-uninstall'
Set-BundleVersion $case '1.7.0'
$plan=@(& $manager -Action Plan -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target)[0]
Assert ($plan.FileCount -eq 4 -and !(Test-Path -LiteralPath $case.Target)) 'A new legal-aware plan contains four files and remains read-only.'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
Assert (@($state.files).Count -eq 4) 'Fresh installation owns the DLL, addin and both legal files.'
foreach ($name in @('Bright.Vision.LICENSE','Bright.Vision.NOTICE')) {
    Assert ([IO.File]::ReadAllText((Join-Path $case.Target ('2020\'+$name))) -ceq ('INERT LEGAL FIXTURE '+$name+' version 1.7.0')) 'Installed legal bytes match the selected payload.'
}
$before=Snapshot $case.Target
$result=@(& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false)[0]
Assert ($result.Status -ceq 'AlreadyInstalled' -and (Snapshot $case.Target) -ceq $before) 'Repeated legal-aware installation does not rewrite files.'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
Assert (!(Test-Path -LiteralPath $statePath) -and !(Test-Path -LiteralPath (Join-Path $case.Target '2020\Bright.Vision.LICENSE')) -and !(Test-Path -LiteralPath (Join-Path $case.Target '2020\Bright.Vision.NOTICE'))) 'Uninstall removes unchanged legal files and ownership state.'

$case=Fixture 'legal-required-for-new-version'
Set-BundleVersion $case '1.7.0'
$manifestPath=Join-Path $case.Bundle 'bundle.json'
$manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
foreach ($variant in $manifest.variants) { $variant.files=@($variant.files | Where-Object { $_.name -in @('Bright.Vision.Revit.dll','Bright.Vision.addin') }) }
Write-Text $manifestPath ($manifest | ConvertTo-Json -Depth 12)
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'A 1.7 bundle cannot silently omit both legal files.'
Assert (!(Test-Path -LiteralPath $case.Target)) 'Missing legal payload fails before any installation.'

$case=Fixture 'legal-unmanaged-upgrade-conflict'
Set-BundleVersion $case '1.6.0'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
Write-Text (Join-Path $case.Target '2020\Bright.Vision.LICENSE') 'UNMANAGED LEGAL FILE KEEP'
$before=Snapshot $case.Target
Set-BundleVersion $case '1.7.0'
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'Adding new legal files must not adopt or overwrite an unmanaged existing file.'
Assert ((Snapshot $case.Target) -ceq $before) 'The complete old installation and unmanaged legal file survive the conflict.'

$case=Fixture 'legal-partial-upgrade-rollback-and-tamper-preservation'
Set-BundleVersion $case '1.6.0'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$before=Snapshot $case.Target
Set-BundleVersion $case '1.7.0'
$global:BrightVisionFixtureInjected=$false
$global:BrightVisionFixtureFailCreation=Join-Path $case.Target '2020\Bright.Vision.NOTICE'
try { Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'A failure creating NOTICE after LICENSE rolls back the old two-file installation.' }
finally { $global:BrightVisionFixtureFailCreation=$null }
Assert $global:BrightVisionFixtureInjected 'The legal rollback fault occurred during the actual new file write.'
Assert ((Snapshot $case.Target) -ceq $before) 'Rollback removes the newly added LICENSE and restores old bytes and owner state.'
$null=& $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
$changedLegal=Join-Path $case.Target '2020\Bright.Vision.LICENSE'
Write-Text $changedLegal 'USER MODIFIED LEGAL FILE KEEP'
Write-Text (Join-Path $case.Target '2020\OtherTool.LICENSE') 'UNRELATED KEEP'
$result=@(& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false)[0]
Assert ($result.Status -ceq 'PreservedModified' -and @($result.PreservedFiles).Count -eq 1 -and $result.PreservedFiles[0] -ceq 'Bright.Vision.LICENSE') 'Modified legal material and its ownership record survive uninstall.'
Assert ([IO.File]::ReadAllText($changedLegal) -ceq 'USER MODIFIED LEGAL FILE KEEP') 'Uninstall does not delete a user-edited license.'
$statePath=Join-Path $case.Target '2020\.BrightVision.RevitExporter.install.json'
$state=[IO.File]::ReadAllText($statePath) | ConvertFrom-Json
Assert (@($state.files).Count -eq 1 -and $state.files[0].name -ceq 'Bright.Vision.LICENSE') 'Partial uninstall retains only the modified owned file in state.'
Write-Text $changedLegal 'INERT LEGAL FIXTURE Bright.Vision.LICENSE version 1.7.0'
$null=& $manager -Action Uninstall -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false
Assert (!(Test-Path -LiteralPath $statePath) -and !(Test-Path -LiteralPath $changedLegal)) 'Uninstall can finish after the original legal bytes are restored.'
Assert ([IO.File]::ReadAllText((Join-Path $case.Target '2020\OtherTool.LICENSE')) -ceq 'UNRELATED KEEP') 'Another product legal file remains unchanged.'

$case=Fixture 'legal-payload-tamper'
Set-BundleVersion $case '1.7.0'
Write-Text (Join-Path $case.Bundle 'payload\2020-net45\Bright.Vision.NOTICE') 'PAYLOAD TAMPER'
Rejects { & $manager -Action Install -BundlePath $case.Bundle -RevitVersions 2020 -AddinsRoot $case.Target -Confirm:$false } 'Legal payload hash mismatch is rejected before installation.'
Assert (!(Test-Path -LiteralPath $case.Target)) 'Legal hash failure leaves no target files.'
Remove-Item -LiteralPath Function:New-Object

[pscustomobject]@{Result='PASS';Assertions=$script:count;FixtureRoot=$testRoot;Scope='Workspace fixtures only; no Revit process or API execution.'}
