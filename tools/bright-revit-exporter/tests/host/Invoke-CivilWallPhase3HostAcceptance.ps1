#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [ValidateSet('Verify','Launch','Cleanup')][string]$Action='Verify',
    [string]$RevitPath=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$boundary=Join-Path $workspace 'test\revit-civil-acceptance\phase3-1.9'
$utf8=New-Object Text.UTF8Encoding($false)
function Hash([string]$Path){return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Child([string]$Parent,[string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Acceptance path escapes its explicit boundary.'}
    for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse acceptance path refused.'}}
    return $full
}
$run=Child $boundary $RunDirectory
$prepared=Get-Content -LiteralPath (Join-Path $run 'prepared.json') -Raw -Encoding UTF8|ConvertFrom-Json
$nonce=[Guid]::Empty
if(![Guid]::TryParseExact([string]$prepared.runId,'N',[ref]$nonce)-or$nonce-eq[Guid]::Empty-or[IO.Path]::GetFileName($run)-cne('run-'+$prepared.runId)){throw 'Prepared acceptance nonce differs from its directory.'}
$addinRoot=[IO.Path]::GetFullPath((Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2020'))
$registration=Child $addinRoot (Join-Path $addinRoot ('BV.CivilWallPhase3Acceptance.'+$prepared.runId+'.addin'))
$launchFile=Join-Path $run 'launch.json'
if($Action-eq'Cleanup'){
    if(!(Test-Path -LiteralPath $launchFile -PathType Leaf)){throw 'No owned launch record exists; nothing is removed.'}
    $launch=Get-Content -LiteralPath $launchFile -Raw -Encoding UTF8|ConvertFrom-Json
    if($launch.runId-cne$prepared.runId-or[IO.Path]::GetFullPath($launch.manifestPath)-cne$registration){throw 'Launch registration does not match this exact run.'}
    if(Test-Path -LiteralPath $registration){if((Hash $registration)-cne$launch.manifestSha256){throw 'Registration changed; left intact.'};Remove-Item -LiteralPath $registration}
    [IO.File]::WriteAllText((Join-Path $run 'cleanup.json'),([ordered]@{temporaryRegistrationRemoved=$true;processStopped=$false;sourceSaved=$false;utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json),$utf8)
    [pscustomobject]@{Result='Cleaned';ProcessStopped=$false;Run=$run};return
}
if(!$prepared.preparedOnly-or$prepared.registered-or$prepared.revitStarted-or$prepared.productionInstalled-or$prepared.result-cne'PREPARED_NOT_RUN'){throw 'Expected a fresh, unstarted 1.9 prepared record.'}
if((Test-Path -LiteralPath $registration)-or(Test-Path -LiteralPath $launchFile)){throw 'This exact run already has launch state; it cannot be described as not run or launched again.'}
foreach($item in @(@($prepared.configPath,$prepared.configSha256),@($prepared.driverPath,$prepared.driverSha256),@($prepared.driverBuildManifestPath,$prepared.driverBuildManifestSha256),@($prepared.productionManifestPath,$prepared.productionManifestSha256),@($prepared.registrationCandidatePath,$prepared.registrationCandidateSha256))){if((Hash (Child $workspace $item[0]))-cne$item[1]){throw 'Prepared file changed.'}}
foreach($entry in @($prepared.sources)+@($prepared.payloads)){if((Hash (Child $workspace $entry.path))-cne$entry.sha256){throw 'Production source or payload changed since preparation.'}}
if((Hash $prepared.modelOriginal)-cne$prepared.modelSha256-or(Hash (Child $run $prepared.modelCopy))-cne$prepared.modelSha256){throw 'Prepared model or original sample changed.'}
$config=Get-Content -LiteralPath $prepared.configPath -Raw -Encoding UTF8|ConvertFrom-Json
if($config.runId-cne$prepared.runId-or[IO.Path]::GetFullPath($config.outputDirectory)-cne(Join-Path $run 'output')){throw 'Run configuration identity differs.'}
$target=@($prepared.payloads|Where-Object id -ceq '2020-net45')
if($target.Count-ne1-or[IO.Path]::GetFullPath($config.modelPath)-ine[IO.Path]::GetFullPath($prepared.modelCopy)-or$config.expectedModelSha256-cne$prepared.modelSha256-or
    [IO.Path]::GetFullPath($config.exporterPath)-ine[IO.Path]::GetFullPath($target[0].path)-or$config.expectedExporterSha256-cne$target[0].sha256-or
    $config.expectedDriverSha256-cne$prepared.driverSha256-or$config.productionManifestSha256-cne$prepared.productionManifestSha256-or$config.expectedPackageVersion-cne'1.9.0'-or
    $config.expectedAssemblyVersion-cne[Reflection.AssemblyName]::GetAssemblyName($target[0].path).Version.ToString()){throw 'Configuration is not bound to the exact prepared model, driver and 2020 production payload.'}
foreach($api in @(@('RevitAPI.dll',$config.expectedRevitApiPath,$config.expectedRevitApiSha256),@('RevitAPIUI.dll',$config.expectedRevitApiUiPath,$config.expectedRevitApiUiSha256))){
    $apiPath=[IO.Path]::GetFullPath((Join-Path $RevitPath $api[0]))
    if($apiPath-ine[IO.Path]::GetFullPath($api[1])-or(Hash $apiPath)-cne$api[2]){throw 'Explicit host API path or bytes differ from the compiled driver contract.'}
}
if($Action-eq'Verify'){[pscustomobject]@{Result='VerifiedNotRun';Run=$run;Sources=$prepared.productionSourceCount;Payloads=$prepared.payloadCount;Registered=$false;StartedRevit=$false};return}
if(@(Get-Process Revit -ErrorAction SilentlyContinue).Count){throw 'An existing Revit session is running; it will not be changed.'}
$executable=[IO.Path]::GetFullPath((Join-Path $RevitPath 'Revit.exe'))
if(!(Test-Path -LiteralPath $executable -PathType Leaf)){throw 'Explicit Revit executable does not exist.'}
Add-Type -Path (Join-Path $RevitPath 'RevitAddInUtility.dll')
$parsed=[Autodesk.RevitAddIns.AddInManifestUtility]::GetRevitAddInManifest($prepared.registrationCandidatePath)
if($parsed.AddInApplications.Count-ne1-or$parsed.AddInApplications[0].FullClassName-cne'BrightVision.CivilWallPhase3HostAcceptance.App'-or$parsed.AddInApplications[0].AllowLoadingIntoExistingSession-or
    [IO.Path]::GetFullPath($parsed.AddInApplications[0].Assembly)-ine[IO.Path]::GetFullPath($prepared.driverPath)){throw 'Official Revit parser rejected the prepared temporary registration.'}
[void][IO.Directory]::CreateDirectory($addinRoot)
$ownedRegistration=$false
$launch=[ordered]@{runId=$prepared.runId;manifestPath=$registration;manifestSha256=$prepared.registrationCandidateSha256;executable=$executable;modelPath=$prepared.modelCopy;driverPath=$prepared.driverPath;productionInstalled=$false;sourceSaved=$false;nativeDesktopInput=$false;windowStyle='Hidden';processId=$null;utc=[DateTime]::UtcNow.ToString('o')}
$previousConfig=[Environment]::GetEnvironmentVariable('BV_CIVIL_WALL_PHASE3_CONFIG','Process')
$previousNonce=[Environment]::GetEnvironmentVariable('BV_CIVIL_WALL_PHASE3_RUN_ID','Process')
try{
    $handle=[IO.File]::Open($registration,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$ownedRegistration=$true
    try{$bytes=[IO.File]::ReadAllBytes($prepared.registrationCandidatePath);$handle.Write($bytes,0,$bytes.Length)}finally{$handle.Dispose()}
    if((Hash $registration)-cne$prepared.registrationCandidateSha256){throw 'Written registration differs from its reviewed candidate.'}
    [IO.File]::WriteAllText($launchFile,($launch|ConvertTo-Json),$utf8)
    $env:BV_CIVIL_WALL_PHASE3_CONFIG=[string]$prepared.configPath;$env:BV_CIVIL_WALL_PHASE3_RUN_ID=[string]$prepared.runId
    $process=Start-Process -FilePath $executable -ArgumentList ('"'+[string]$prepared.modelCopy+'"') -WindowStyle Hidden -PassThru
    $launch.processId=$process.Id;[IO.File]::WriteAllText($launchFile,($launch|ConvertTo-Json),$utf8)
}catch{
    if($ownedRegistration-and(Test-Path -LiteralPath $registration)-and(Hash $registration)-ceq$launch.manifestSha256){Remove-Item -LiteralPath $registration};throw
}finally{
    [Environment]::SetEnvironmentVariable('BV_CIVIL_WALL_PHASE3_CONFIG',$previousConfig,'Process')
    [Environment]::SetEnvironmentVariable('BV_CIVIL_WALL_PHASE3_RUN_ID',$previousNonce,'Process')
}
[pscustomobject]@{Result='Launched';Run=$run;ProcessId=$launch.processId;NativeDesktopInput=$false;ProductionInstalled=$false}
