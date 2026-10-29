#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProductionBuildManifestPath,
    [Parameter(Mandatory=$true)][string]$DriverBuildManifestPath,
    [string]$ModelPath=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020\Samples\rac_basic_sample_project.rvt')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$boundary=Join-Path $workspace 'test\revit-civil-acceptance\phase3-1.9'
$utf8=New-Object Text.UTF8Encoding($false)
function Hash([string]$File){return(Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()}
function Child([string]$Parent,[string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw('Prepared path escapes its explicit boundary: '+$full)}
    for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw('Reparse prepared path refused: '+$cursor)}}
    return $full
}
$productionManifest=Child $workspace $ProductionBuildManifestPath
$driverManifest=Child $workspace $DriverBuildManifestPath
$production=Get-Content -LiteralPath $productionManifest -Raw -Encoding UTF8|ConvertFrom-Json
$driver=Get-Content -LiteralPath $driverManifest -Raw -Encoding UTF8|ConvertFrom-Json
if($production.packageId-ne'BrightVision.RevitExporter'-or$production.packageVersion-ne'1.9.0'-or@($production.sourceFiles).Count-eq0){throw 'Only an explicit new BV 1.9 production manifest is accepted.'}
if(!$driver.testOnly-or!$driver.preparedOnly-or$driver.registered-or$driver.startedRevit-or$driver.fullClassName-ne'BrightVision.CivilWallPhase3HostAcceptance.App'-or$driver.expectedPackageVersion-ne$production.packageVersion){throw 'Expected the independent unregistered, unstarted 1.9 SDK DLL driver.'}
if($driver.productionManifestSha256-cne(Hash $productionManifest)-or$driver.productionSourceCount-ne@($production.sourceFiles).Count){throw 'Driver build references a different production manifest; rebuild the new driver without altering older evidence.'}
$sources=@();$seenSources=@{}
foreach($entry in $production.sourceFiles){
    if(!$entry.name-or$seenSources.ContainsKey($entry.name)){throw 'Missing or duplicate production source identity.'};$seenSources[$entry.name]=$true
    $file=Child $module (Join-Path $module $entry.name)
    if((Hash $file)-cne$entry.sha256){throw('Current production source differs from the exact recorded development build: '+$entry.name)}
    $sources+=[ordered]@{name=$entry.name;path=$file;sha256=$entry.sha256}
}
$payloads=@();$seenPayloads=@{};$buildRoot=Split-Path $productionManifest -Parent
foreach($variant in $production.variants){
    if(!$variant.id-or$seenPayloads.ContainsKey($variant.id)){throw 'Missing or duplicate payload identity.'};$seenPayloads[$variant.id]=$true
    if($variant.relativePath-cne('payload/'+$variant.id+'/Bright.Vision.Revit.dll')-or@($variant.files).Count-ne1-or$variant.files[0].name-cne'Bright.Vision.Revit.dll'){throw 'Unexpected production payload shape.'}
    $payload=Child $buildRoot (Join-Path $buildRoot $variant.relativePath)
    if((Hash $payload)-cne$variant.files[0].sha256-or(Get-Item -LiteralPath $payload).Length-ne$variant.files[0].size){throw('Production payload identity differs: '+$variant.id)}
    foreach($api in $variant.sourceApi){if((Hash $api.path)-cne$api.sha256){throw('Recorded legal API reference changed: '+$api.path)}}
    $payloads+=[ordered]@{id=$variant.id;path=$payload;sha256=$variant.files[0].sha256;bytes=$variant.files[0].size}
}
$target=@($payloads|Where-Object{$_.id-eq'2020-net45'});if($target.Count-ne1){throw 'No unique 2020-net45 development payload.'}
$dll=Child $workspace $driver.assemblyPath
if((Hash $dll)-cne$driver.assemblySha256-or[IO.Path]::GetExtension($dll)-cne'.dll'){throw 'Driver DLL identity differs.'}
foreach($source in @([pscustomobject]@{path=$driver.sourcePath;sha256=$driver.sourceSha256})+@($driver.supportSources)){if((Hash (Child $workspace $source.path))-cne$source.sha256){throw 'Compiled driver source snapshot changed.'}}
foreach($api in $driver.api){if((Hash $api.path)-cne$api.sha256){throw 'Driver official API reference changed.'}}
$driverApi=@($driver.api|Where-Object{[IO.Path]::GetFileName($_.path)-ceq'RevitAPI.dll'});$driverUiApi=@($driver.api|Where-Object{[IO.Path]::GetFileName($_.path)-ceq'RevitAPIUI.dll'})
if($driverApi.Count-ne1-or$driverUiApi.Count-ne1){throw 'Expected exact RevitAPI and RevitAPIUI driver reference identities.'}
$original=[IO.Path]::GetFullPath($ModelPath)
if(!(Test-Path -LiteralPath $original -PathType Leaf)-or[IO.Path]::GetExtension($original)-ine'.rvt'){throw 'Provide an existing explicit local sample RVT to copy.'}
$nonce=[guid]::NewGuid().ToString('N');$run=Child $boundary (Join-Path $boundary ('run-'+$nonce))
if(Test-Path -LiteralPath $run){throw 'Refusing to overwrite an existing acceptance run.'}
$inputFolder=Join-Path $run 'input';$output=Join-Path $run 'output';[void][IO.Directory]::CreateDirectory($inputFolder);[void][IO.Directory]::CreateDirectory($output)
$model=Join-Path $inputFolder 'acceptance-model.rvt';$originalHash=Hash $original;Copy-Item -LiteralPath $original -Destination $model
if((Hash $model)-cne$originalHash-or(Hash $original)-cne$originalHash){throw 'Official sample copy identity failed.'}
$config=[ordered]@{
    runId=$nonce;modelPath=$model;outputDirectory=$output;originalModelPath=$original;expectedOriginalModelSha256=$originalHash
    exporterPath=$target[0].path;expectedExporterSha256=$target[0].sha256;expectedAssemblyVersion=[Reflection.AssemblyName]::GetAssemblyName($target[0].path).Version.ToString()
    expectedPackageVersion=$production.packageVersion;productionSourceCount=@($sources).Count;productionManifestPath=$productionManifest;productionManifestSha256=(Hash $productionManifest)
    expectedModelSha256=$originalHash;expectedDriverSha256=$driver.assemblySha256
    expectedRevitApiPath=[IO.Path]::GetFullPath($driverApi[0].path);expectedRevitApiSha256=$driverApi[0].sha256
    expectedRevitApiUiPath=[IO.Path]::GetFullPath($driverUiApi[0].path);expectedRevitApiUiSha256=$driverUiApi[0].sha256
}
$configuration=Join-Path $run 'config.json';[IO.File]::WriteAllText($configuration,($config|ConvertTo-Json -Depth 5),$utf8)
$registration=Join-Path $run 'registration-candidate.addin'
$xml='<?xml version="1.0" encoding="utf-8"?><RevitAddIns><AddIn Type="Application"><Name>BV 1.9 SDK acceptance (prepared only)</Name><Assembly>'+[Security.SecurityElement]::Escape($dll)+'</Assembly><AddInId>D49816C3-EBA4-4CD8-A5E1-9D519A0E6191</AddInId><FullClassName>BrightVision.CivilWallPhase3HostAcceptance.App</FullClassName><VendorId>BRVI</VendorId><VendorDescription>One-run developer fixture, new 1.9 evidence only</VendorDescription><AllowLoadingIntoExistingSession>False</AllowLoadingIntoExistingSession></AddIn></RevitAddIns>'
[IO.File]::WriteAllText($registration,$xml,$utf8)
$preparation=[ordered]@{
    result='PREPARED_NOT_RUN';runId=$nonce;preparedOnly=$true;registered=$false;revitStarted=$false;productionInstalled=$false;sourceSaved=$false;nativeDesktopInput=$false
    configPath=$configuration;configSha256=(Hash $configuration);productionManifestPath=$productionManifest;productionManifestSha256=(Hash $productionManifest)
    driverBuildManifestPath=$driverManifest;driverBuildManifestSha256=(Hash $driverManifest);driverPath=$dll;driverSha256=$driver.assemblySha256
    productionSourceCount=@($sources).Count;payloadCount=@($payloads).Count;sources=$sources;payloads=$payloads
    modelOriginal=$original;modelCopy=$model;modelSha256=$originalHash;modelBytes=(Get-Item -LiteralPath $model).Length
    registrationCandidatePath=$registration;registrationCandidateSha256=(Hash $registration);registrationInstalled=$false
    environmentForLaterExplicitLaunch=[ordered]@{BV_CIVIL_WALL_PHASE3_CONFIG=$configuration;BV_CIVIL_WALL_PHASE3_RUN_ID=$nonce}
    requiredCases=@('default-no-opening','legacy-no-opening','default-native-opening','legacy-native-opening','default-actual-door','legacy-refuses-actual-door','default-actual-window','legacy-refuses-actual-window')
    pendingCases=@([ordered]@{case='actual-door/window-cutout';status='NOT_RUN';reason='Sample was copied, not opened. Only actually loaded hosted symbols will be tried in a later authorized SDK run; unavailable/nonrectangular cases remain blocked, never replaced by nominal dimensions.'})
    limits=@('Prepared/compiled only; no claim of live Revit success.','No automatic installation or addin registration.','No installed 1.8 assembly or historical host evidence reused as 1.9 success.','Actual run must roll back all temporary walls/openings/family instances/views/types and restore selection without saving RVT.')
}
$record=Join-Path $run 'prepared.json';[IO.File]::WriteAllText($record,($preparation|ConvertTo-Json -Depth 10),$utf8)
[pscustomobject]@{Result='PREPARED_NOT_RUN';Run=$run;Config=$configuration;Record=$record;ProductionSources=@($sources).Count;Payloads=@($payloads).Count;Registered=$false;StartedRevit=$false;Installed=$false}
