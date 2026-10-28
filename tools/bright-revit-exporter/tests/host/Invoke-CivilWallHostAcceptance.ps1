#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Launch','Cleanup')][string]$Action='Launch',
    [string]$RunDirectory,
    [string]$DriverPath=(Join-Path $PSScriptRoot 'artifacts\civil-wall-2020\Bright.Vision.CivilWallHostAcceptance.dll'),
    [string]$RevitPath=(Join-Path $env:ProgramFiles 'Autodesk\Revit 2020'),
    [string]$ProductionBuildManifestPath=(Join-Path $PSScriptRoot '..\..\artifacts\BV-1.7.0\build-manifest.json')
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$root=Join-Path $workspace 'test\revit-civil-acceptance\phase3-20261028'
$addinRoot=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2020'
$revit=[IO.Path]::GetFullPath($RevitPath)
$utf8=New-Object Text.UTF8Encoding($false)
function Child([string]$Parent,[string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if(!$full.StartsWith($Parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw ('Path escapes expected task directory: '+$full) }
    $cursor=$full
    while($cursor) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw ('Reparse path: '+$cursor) }
        $next=[IO.Path]::GetDirectoryName($cursor);if($next -eq $cursor) { break };$cursor=$next
    }
    return $full
}
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if($Action -eq 'Cleanup') {
    if(!$RunDirectory) { throw 'An exact phase3 run directory is required.' }
    $run=Child $root $RunDirectory
    $launch=Get-Content -LiteralPath (Join-Path $run 'launch.json') -Encoding UTF8 -Raw|ConvertFrom-Json
    $parsedNonce=[Guid]::Empty
    if(![Guid]::TryParseExact([string]$launch.runId,'N',[ref]$parsedNonce) -or $parsedNonce -eq [Guid]::Empty -or [IO.Path]::GetFileName($run) -cne ('run-'+$launch.runId)) { throw 'Run nonce or directory identity is invalid.' }
    $manifest=Child ([IO.Path]::GetFullPath($addinRoot)) $launch.manifestPath
    if([IO.Path]::GetFileName($manifest) -cne ('BV.CivilWallAcceptance.'+$launch.runId+'.addin')) { throw 'Manifest nonce does not match the owned run.' }
    if(Test-Path -LiteralPath $manifest) {
        if((Hash $manifest) -ne $launch.manifestSha256) { throw 'Temporary manifest changed; left intact.' }
        Remove-Item -LiteralPath $manifest
    }
    [IO.File]::WriteAllText((Join-Path $run 'cleanup.json'),(@{temporaryRegistrationRemoved=$true;revitClosed=$false;sourceSaved=$false}|ConvertTo-Json),$utf8)
    [pscustomobject]@{Result='Cleaned';RunDirectory=$run;RevitClosed=$false};return
}
if($RunDirectory) { throw 'Launch generates a fresh nonce and run directory; RunDirectory is only accepted for Cleanup.' }
if(@(Get-Process Revit -ErrorAction SilentlyContinue).Count) { throw 'Revit is already running; this test will not change an existing user session.' }
$driver=Child $workspace $DriverPath
if(!(Test-Path -LiteralPath $driver -PathType Leaf)) { throw 'Acceptance driver is missing; build it first.' }
$driverManifestPath=Join-Path ([IO.Path]::GetDirectoryName($driver)) 'build-manifest.json'
$driverBuild=Get-Content -LiteralPath $driverManifestPath -Encoding UTF8 -Raw|ConvertFrom-Json
$driverHash=Hash $driver
if(!$driverBuild.testOnly -or $driverBuild.expectedProductionVersion -ne '1.7.0.0' -or $driverBuild.fullClassName -ne 'BrightVision.CivilWallHostAcceptance.App' -or
    [IO.Path]::GetFullPath($driverBuild.assemblyPath) -ne $driver -or $driverBuild.assemblySha256 -ne $driverHash) { throw 'Driver build manifest does not match the exact compiled phase3 test driver.' }
$driverSource=Child $workspace $driverBuild.sourcePath
if((Hash $driverSource) -ne $driverBuild.sourceSha256) { throw 'Acceptance driver source changed since its recorded build.' }
if($driverBuild.PSObject.Properties.Name -contains 'supportSources') {
    foreach($support in $driverBuild.supportSources) { $supportPath=Child $workspace $support.path; if((Hash $supportPath) -ne $support.sha256) { throw 'Acceptance driver support source changed since its recorded build.' } }
}
foreach($api in $driverBuild.api) { if((Hash $api.path) -ne $api.sha256) { throw 'Acceptance reference API changed since compilation.' } }
$frozenManifestPath=Child $workspace $ProductionBuildManifestPath
$frozenBuild=Get-Content -LiteralPath $frozenManifestPath -Encoding UTF8 -Raw|ConvertFrom-Json
if($frozenBuild.packageId -ne 'BrightVision.RevitExporter' -or $frozenBuild.packageVersion -ne '1.7.0' -or @($frozenBuild.variants).Count -ne 14 -or @($frozenBuild.sourceFiles).Count -ne 24) { throw 'Expected the frozen BV 1.7.0 / 24-source / 14-target production build.' }
$moduleRoot=Join-Path $workspace 'tools\bright-revit-exporter'
foreach($file in $frozenBuild.sourceFiles) {
    if([IO.Path]::GetFileName([string]$file.name) -cne [string]$file.name) { throw 'Frozen source name is not a plain filename.' }
    $sourceFile=Child $moduleRoot (Join-Path $moduleRoot $file.name)
    if((Hash $sourceFile) -ne $file.sha256) { throw ('Frozen source changed: '+$file.name) }
}
$expectedIds=@(foreach($year in 2015..2026) { foreach($runtime in $(if($year -ge 2025){@('net8','net10')}elseif($year -ge 2021){@('net48')}else{@('net45')})) { [string]$year+'-'+$runtime } })
if(@(Compare-Object ($expectedIds|Sort-Object) (@($frozenBuild.variants.id)|Sort-Object)).Count) { throw 'Frozen target matrix is not the expected 14-target set.' }
foreach($payload in $frozenBuild.variants) {
    if($payload.relativePath -cne ('payload/'+$payload.id+'/Bright.Vision.Revit.dll') -or @($payload.files).Count -ne 1 -or $payload.files[0].name -cne 'Bright.Vision.Revit.dll') { throw 'Unexpected frozen payload shape.' }
    $payloadPath=Child ([IO.Path]::GetDirectoryName($frozenManifestPath)) (Join-Path ([IO.Path]::GetDirectoryName($frozenManifestPath)) $payload.relativePath)
    if((Hash $payloadPath) -ne $payload.files[0].sha256 -or (Get-Item -LiteralPath $payloadPath).Length -ne $payload.files[0].size) { throw ('Frozen payload changed: '+$payload.id) }
}
$variant=@($frozenBuild.variants|Where-Object id -eq '2020-net45')
if($variant.Count -ne 1) { throw 'Frozen production build has no unique Revit 2020 target.' }
$frozenDll=Child ([IO.Path]::GetDirectoryName($frozenManifestPath)) (Join-Path ([IO.Path]::GetDirectoryName($frozenManifestPath)) $variant[0].relativePath)
$frozenDllEntry=@($variant[0].files|Where-Object name -eq 'Bright.Vision.Revit.dll')
if($frozenDllEntry.Count -ne 1 -or (Hash $frozenDll) -ne $frozenDllEntry[0].sha256) { throw 'Frozen Revit 2020 production DLL does not match its build manifest.' }
$exporter=Join-Path $addinRoot 'Bright.Vision.Revit.dll'
$statePath=Join-Path $addinRoot '.BrightVision.RevitExporter.install.json'
$state=Get-Content -LiteralPath $statePath -Encoding UTF8 -Raw|ConvertFrom-Json
if($state.packageVersion -ne '1.7.0' -or $state.variantId -ne '2020-net45') { throw 'Install verified BV 1.7.0 / 2020-net45 before running this test.' }
$exporterHash=Hash $exporter
$ownedDll=@($state.files|Where-Object name -eq 'Bright.Vision.Revit.dll')
if($ownedDll.Count -ne 1 -or $ownedDll[0].sha256 -ne $exporterHash -or $exporterHash -ne $frozenDllEntry[0].sha256) { throw 'Installed production DLL differs from its ownership state or frozen build.' }
$original=Join-Path $revit 'Samples\rst_basic_sample_project.rvt'
$originalHash=Hash $original
$source=Child $root (Join-Path $root 'source\rst_basic_sample_project.rvt')
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($source))
if(Test-Path -LiteralPath $source) {
    if((Hash $source) -ne $originalHash) { throw 'Existing phase3 sample copy changed; preserved without replacement.' }
} else { Copy-Item -LiteralPath $original -Destination $source }
$runId=[Guid]::NewGuid().ToString('N')
$run=Child $root (Join-Path $root ('run-'+$runId))
$output=Child $root (Join-Path $run 'output')
[void][IO.Directory]::CreateDirectory($output)
$configPath=Join-Path $run 'config.json'
$config=[ordered]@{runId=$runId;modelPath=$source;outputDirectory=$output;exporterPath=$exporter;expectedExporterSha256=$exporterHash;expectedModelSha256=$originalHash;expectedDriverSha256=$driverHash}
[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $run 'prepared-model.json'),([ordered]@{original=$original;copy=$source;sha256=$originalHash;bytes=(Get-Item -LiteralPath $source).Length;sourceSaved=$false}|ConvertTo-Json),$utf8)
$manifest=Child ([IO.Path]::GetFullPath($addinRoot)) (Join-Path $addinRoot ('BV.CivilWallAcceptance.'+$runId+'.addin'))
$xml='<?xml version="1.0" encoding="utf-8"?><RevitAddIns><AddIn Type="Application"><Name>BV Civil wall SDK acceptance (temporary)</Name><Assembly>'+[Security.SecurityElement]::Escape($driver)+'</Assembly><AddInId>B17355F0-4D59-41AE-B82E-E6AE453B4BA4</AddInId><FullClassName>BrightVision.CivilWallHostAcceptance.App</FullClassName><VendorId>BRVI</VendorId><VendorDescription>One-run developer SDK civil wall acceptance</VendorDescription><AllowLoadingIntoExistingSession>False</AllowLoadingIntoExistingSession></AddIn></RevitAddIns>'
$handle=[IO.File]::Open($manifest,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $bytes=$utf8.GetBytes($xml);$handle.Write($bytes,0,$bytes.Length) } finally { $handle.Dispose() }
$launch=[ordered]@{runId=$runId;configPath=$configPath;configSha256=(Hash $configPath);driver=$driver;driverSha256=$driverHash;driverBuildManifest=$driverManifestPath;driverBuildManifestSha256=(Hash $driverManifestPath);productionBuildManifest=$frozenManifestPath;productionBuildManifestSha256=(Hash $frozenManifestPath);installedStatePath=$statePath;installedStateSha256=(Hash $statePath);exporterPath=$exporter;exporterSha256=$exporterHash;modelPath=$source;modelSha256=$originalHash;manifestPath=$manifest;manifestSha256=(Hash $manifest);processId=$null;startedAtUtc=[DateTime]::UtcNow.ToString('o');sourceSaved=$false;transactionsAllowed=$true;fixtureRollbackRequired=$true;activeViewWritesAllowed=$false}
[IO.File]::WriteAllText((Join-Path $run 'launch.json'),($launch|ConvertTo-Json),$utf8)
$oldConfig=[Environment]::GetEnvironmentVariable('BV_CIVIL_WALL_ACCEPTANCE_CONFIG','Process')
$oldRun=[Environment]::GetEnvironmentVariable('BV_CIVIL_WALL_ACCEPTANCE_RUN_ID','Process')
try {
    Add-Type -Path (Join-Path $revit 'RevitAddInUtility.dll')
    $parsed=[Autodesk.RevitAddIns.AddInManifestUtility]::GetRevitAddInManifest($manifest)
    if($parsed.AddInApplications.Count -ne 1 -or $parsed.AddInApplications[0].FullClassName -ne 'BrightVision.CivilWallHostAcceptance.App' -or $parsed.AddInApplications[0].AllowLoadingIntoExistingSession) { throw 'Official parser rejected the temporary phase3 registration identity.' }
    $env:BV_CIVIL_WALL_ACCEPTANCE_CONFIG=$configPath;$env:BV_CIVIL_WALL_ACCEPTANCE_RUN_ID=$runId
    $process=Start-Process -FilePath (Join-Path $revit 'Revit.exe') -ArgumentList ('"'+$source+'"') -WindowStyle Normal -PassThru
    $launch.processId=$process.Id
    [IO.File]::WriteAllText((Join-Path $run 'launch.json'),($launch|ConvertTo-Json),$utf8)
} catch {
    if((Test-Path -LiteralPath $manifest) -and (Hash $manifest) -eq $launch.manifestSha256) { Remove-Item -LiteralPath $manifest };throw
} finally {
    [Environment]::SetEnvironmentVariable('BV_CIVIL_WALL_ACCEPTANCE_CONFIG',$oldConfig,'Process')
    [Environment]::SetEnvironmentVariable('BV_CIVIL_WALL_ACCEPTANCE_RUN_ID',$oldRun,'Process')
}
[pscustomobject]@{Result='Launched';RunDirectory=$run;ProcessId=$launch.processId;Model=$source;OutputDirectory=$output;ExpectedProductionVersion='1.7.0'}
