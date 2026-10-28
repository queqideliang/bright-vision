#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$restore=Join-Path $root 'Restore-BosExport.ps1'
$module=Join-Path $root 'artifacts\Bright.Vision-1.2.0-final\module'
$manager=Join-Path $module 'Manage-BrightVisionPlugin.ps1'
$fixtureRoot=Join-Path $root ('.test-work\restore-bos-'+[Guid]::NewGuid().ToString('N'))
$utf8=New-Object Text.UTF8Encoding($false)
[void][IO.Directory]::CreateDirectory($fixtureRoot)
$script:checks=0
$global:BrightLegacyRestoreRunning=$false
function Get-Process { [CmdletBinding()]param([string]$Name) if($Name -ne 'Revit'){throw 'Unexpected process observation'}; if($global:BrightLegacyRestoreRunning){[pscustomobject]@{Id=12345;Name='Revit'}} }
function Assert([bool]$Value,[string]$Message){if(!$Value){throw "ASSERT: $Message"};$script:checks++}
function Write-Text([string]$Path,[string]$Value){[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Value,$utf8)}
function Snapshot([string]$Path){if(!(Test-Path -LiteralPath $Path)){return '<missing>'};return ((Get-ChildItem -LiteralPath $Path -Force -Recurse -File|Sort-Object FullName|ForEach-Object{$_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash}) -join "`n")}
function Fixture([string]$Name){
 $directory=Join-Path $fixtureRoot $Name
 $case=[pscustomobject]@{Target=(Join-Path $directory 'user');Machine=(Join-Path $directory 'machine');Report=(Join-Path $directory 'recovery.json')}
 foreach($year in @('2018','2020')){
  $yearRoot=Join-Path $case.Machine $year
  Write-Text (Join-Path $yearRoot 'BOSRevitExplore.addin') '<RevitAddIns><AddIn Type="Application"><Name>BOS3D Revit Explore</Name><Assembly>BOSRevitExploreLoader.dll</Assembly><ClientId>950ABA1D-EC23-479F-AA49-F8A84CEA55E6</ClientId><FullClassName>BOSRevitExploreLoader.App</FullClassName><VendorId>BOSWINNER</VendorId></AddIn></RevitAddIns>'
  Write-Text (Join-Path $yearRoot 'BOSRevitExploreLoader.dll') 'INERT ORIGINAL BOS LOADER'
  Write-Text (Join-Path $yearRoot 'BOSRevitExplore.dll') 'INERT ORIGINAL FAST ENGINE'
  Write-Text (Join-Path $yearRoot 'licence.dat') 'KEEP ORIGINAL LICENCE'
 }
 $null=& $manager -Action Install -BundlePath $module -AddinsRoot $case.Target -MachineAddinsRoot $case.Machine -RevitVersions 2018,2020,2026 -RuntimeOverrides @{'2026'='net10'} -Confirm:$false
 return $case
}
function Run-Restore($Case,[string]$Action='Plan',[string[]]$Years=@('2020','2018')){
 $text=& $restore -Action $Action -ModulePath $module -AddinsRoot $Case.Target -MachineAddinsRoot $Case.Machine -RevitVersions $Years -ReportPath $Case.Report -Confirm:$false
 return (($text -join "`n") | ConvertFrom-Json)
}

$case=Fixture 'selected-years'
$machineBefore=Snapshot $case.Machine
$userBefore=Snapshot $case.Target
$year2018=Snapshot (Join-Path $case.Target '2018')
$year2026=Snapshot (Join-Path $case.Target '2026')
$plan=Run-Restore $case
Assert ($plan.success -and $plan.status -eq 'Ready' -and $plan.targets.Count -eq 2) 'Read-only planning identifies exactly 2018 and 2020.'
Assert ((Snapshot $case.Target) -ceq $userBefore -and !(Test-Path -LiteralPath $case.Report)) 'Plan never writes registrations or recovery reports.'
$global:BrightLegacyRestoreRunning=$true
try{$result=Run-Restore $case 'Restore'}finally{$global:BrightLegacyRestoreRunning=$false}
Assert ($result.success -and $result.status -eq 'Deferred' -and $result.revitPids[0] -eq 12345) 'Running Revit defers restoration without closing the process.'
Assert ((Snapshot $case.Target) -ceq $userBefore -and !(Test-Path -LiteralPath $case.Report)) 'Running-host protection precedes all mutation.'
$rejected=$false
try{$null=Run-Restore $case 'Restore' @('2026')}catch{$rejected=$true}
Assert $rejected 'This recovery entry point cannot target a year without the known old engine.'
Assert ((Snapshot $case.Target) -ceq $userBefore) 'Rejected 2026 selection changes no registration.'

$result=Run-Restore $case 'Restore' @('2020')
Assert ($result.success -and $result.status -eq 'Restored') 'Explicit recovery removes the 2020 source exporter.'
foreach($name in @('Bright.Vision.addin','Bright.Vision.Revit.dll','.BrightVision.RevitExporter.install.json','BOSRevitExplore.addin')){
 Assert (!(Test-Path -LiteralPath (Join-Path $case.Target ('2020\'+$name)))) ('Recovery removes only owned 2020 file: '+$name)
}
Assert ((Snapshot (Join-Path $case.Target '2018')) -ceq $year2018) 'A 2020-only recovery does not alter 2018.'
Assert ((Snapshot (Join-Path $case.Target '2026')) -ceq $year2026) 'The 2026 source exporter is preserved.'
Assert ((Snapshot $case.Machine) -ceq $machineBefore) 'BOS manifests, original engine, loader, and licences remain byte exact.'
$report=[IO.File]::ReadAllText($case.Report)|ConvertFrom-Json
Assert ($report.status -eq 'Restored' -and $report.rollback.RevitVersions[0] -eq '2020') 'Recovery report retains exact selected-year rollback parameters.'
$again=Run-Restore $case 'Restore' @('2020')
Assert ($again.success -and $again.status -eq 'Restored') 'Recovery is safe to repeat after the source exporter is gone.'
Assert ((Snapshot $case.Machine) -ceq $machineBefore) 'Repeated recovery still never writes the machine installation.'

# Roll back only this fixture to the retained source-built package. No API DLL is
# loaded and no real Windows user registration directory is passed to the manager.
$null=& $manager -Action Install -BundlePath $module -AddinsRoot $case.Target -MachineAddinsRoot $case.Machine -RevitVersions 2020 -Confirm:$false
Assert ((Snapshot $case.Target) -ceq $userBefore) 'The preserved 1.2 module can restore the exact original fixture registration state.'
$result=Run-Restore $case 'Restore' @('2018')
Assert ($result.success -and $result.status -eq 'Restored') 'The second legacy year can be restored independently.'
Assert ((Snapshot (Join-Path $case.Target '2026')) -ceq $year2026) 'Independent 2018 recovery also leaves 2026 untouched.'

$case=Fixture 'modified-file'
$mask=Join-Path $case.Target '2020\BOSRevitExplore.addin'
$originalMask=[IO.File]::ReadAllBytes($mask)
Write-Text $mask '<RevitAddIns><!-- modified by user --></RevitAddIns>'
$before=Snapshot $case.Target
$result=Run-Restore $case 'Restore'
Assert (!$result.success -and $result.status -eq 'Blocked') 'A changed ownership mask blocks restoration.'
Assert ((Snapshot $case.Target) -ceq $before -and !(Test-Path -LiteralPath $case.Report)) 'A conflict in the second year prevents removal from either year.'
[IO.File]::WriteAllBytes($mask,$originalMask)
Write-Text (Join-Path $case.Target '2020\Bright.Vision.Revit.dll') 'USER CHANGED EXPORTER'
$before=Snapshot $case.Target
$result=Run-Restore $case 'Restore'
Assert (!$result.success -and (Snapshot $case.Target) -ceq $before) 'Modified source DLLs are not deleted or silently overwritten.'

$case=Fixture 'missing-original-engine'
Remove-Item -LiteralPath (Join-Path $case.Machine '2020\BOSRevitExploreLoader.dll') -Force
$before=Snapshot $case.Target
$result=Run-Restore $case 'Restore'
Assert (!$result.success -and $result.message.Contains('loader is missing')) 'Recovery refuses to disable the only available exporter.'
Assert ((Snapshot $case.Target) -ceq $before -and !(Test-Path -LiteralPath $case.Report)) 'Missing legacy engine blocks all selected years before mutation.'

[pscustomobject]@{Result='PASS';Assertions=$script:checks;FixtureRoot=$fixtureRoot;Scope='Workspace-only restoration/rollback fixtures; no Revit closed or real addins modified.'}
