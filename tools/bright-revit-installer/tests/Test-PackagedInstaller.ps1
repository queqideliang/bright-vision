#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallerPath,
    [Parameter(Mandatory=$true)][string]$AddInUtilityPath,
    [string]$ExpectedVersion='1.8.0',
    [string]$LegalDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$moduleRoot=Split-Path $PSScriptRoot -Parent
$testRoot=Join-Path $moduleRoot ('.test-work\packaged-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
$installer=(Resolve-Path -LiteralPath $InstallerPath).ProviderPath
$utility=(Resolve-Path -LiteralPath $AddInUtilityPath).ProviderPath
$target=Join-Path $testRoot 'addins'
$cache=Join-Path $testRoot 'cache'
$script:count=0
function Assert([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message }; $script:count++ }
if ([string]::IsNullOrWhiteSpace($LegalDirectory)) {
    $legalCandidates=@((Join-Path (Split-Path $moduleRoot -Parent) 'bright-open-source\legal'), (Split-Path (Split-Path $moduleRoot -Parent) -Parent))
    $LegalDirectory=@($legalCandidates | Where-Object { (Test-Path -LiteralPath (Join-Path $_ 'LICENSE') -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $_ 'NOTICE') -PathType Leaf) } | Select-Object -First 1)
    if (!$LegalDirectory) { throw 'Supply reviewed LICENSE/NOTICE source inputs for packaged verification.' }
}
$expectedLegal=@{}
foreach ($name in @('LICENSE','NOTICE')) { $expectedLegal[$name]=(Get-FileHash -LiteralPath (Join-Path $LegalDirectory $name)).Hash.ToLowerInvariant() }
Assert ($expectedLegal['LICENSE'] -ceq 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30') 'Expected legal source is the complete Apache-2.0 canonical text.'
Assert ([Reflection.AssemblyName]::GetAssemblyName($installer).Version.ToString() -ceq ($ExpectedVersion+'.0')) 'Installer assembly version must match its bundled exporter version.'
Assert ([Diagnostics.FileVersionInfo]::GetVersionInfo($installer).FileVersion -ceq ($ExpectedVersion+'.0')) 'Installer file version must match its bundled exporter version.'
Assert ([Diagnostics.FileVersionInfo]::GetVersionInfo($installer).CompanyName -ceq '崛亮视觉') 'Installer metadata must identify the company.'
Assert ([Diagnostics.FileVersionInfo]::GetVersionInfo($installer).LegalCopyright -ceq 'Copyright 2026 崛亮视觉 (Bright Vision)') 'Installer metadata must preserve the company copyright.'
function Invoke-Package([string]$Action,[string]$Years='2015,2016,2017,2018,2019,2020,2021,2022,2023,2024,2025,2026',[string]$Runtime='net10',[string]$Destination=$target) {
    $id=$Action+'-'+[Guid]::NewGuid().ToString('N')
    $stdout=Join-Path $testRoot ($id+'.json')
    $stderr=Join-Path $testRoot ($id+'.stderr.txt')
    $arguments='--action '+$Action+' --versions '+$Years+' --runtime '+$Runtime+' --addins-root "'+$Destination+'" --cache-root "'+$cache+'"'
    $process=Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $text=[IO.File]::ReadAllText($stdout)
    if ($process.ExitCode -ne 0) { throw ($text+[IO.File]::ReadAllText($stderr)) }
    $reply=$text | ConvertFrom-Json
    Assert ($reply.success -eq $true) "Packaged $Action must report success."
    return $reply
}
$validated=Invoke-Package 'Validate'
Assert ($validated.results[0].PackageVersion -ceq $ExpectedVersion -and @($validated.results[0].Variants).Count -eq 14) 'Embedded bundle must match the expected independent build version and all variants.'
$null=Invoke-Package 'Plan'
Assert (!(Test-Path -LiteralPath $target)) 'Packaged planning must not create plugin files.'
$installed=Invoke-Package 'Install'
Assert (@($installed.results).Count -eq 12) 'Install all twelve explicit fixture years.'
$cacheDirectory=@(Get-ChildItem -LiteralPath $cache -Directory | Sort-Object LastWriteTime)[-1].FullName
$embedded=[IO.File]::ReadAllText((Join-Path $cacheDirectory 'bundle.json')) | ConvertFrom-Json
Assert ($embedded.license -ceq 'Apache-2.0' -and $embedded.copyrightOwner -ceq '崛亮视觉') 'Embedded metadata identifies the community license and copyright owner.'
foreach ($name in @('LICENSE','NOTICE')) {
    $record=@($embedded.legalNotices | Where-Object name -eq $name)
    Assert ($record.Count -eq 1 -and $record[0].sha256 -ceq $expectedLegal[$name] -and (Get-FileHash -LiteralPath (Join-Path $cacheDirectory $name)).Hash -ieq $expectedLegal[$name]) 'Actual module includes the reviewed readable root legal text.'
}
Assert (@(Get-ChildItem -LiteralPath $cacheDirectory -Recurse -File).Count -eq 65) 'Full package contains 56 per-variant payload files and 9 metadata/helper/legal files.'
Assert ((Get-ChildItem -LiteralPath (Join-Path $cacheDirectory 'payload') -Recurse -Filter '*.dll' -File).Count -eq 14) 'Embedded payload has exactly one owned DLL per variant.'
Assert (@(Get-ChildItem -LiteralPath $cacheDirectory -Recurse -File | Where-Object {$_.Name -like 'BOS*.dll' -or $_.Name -like '*LegacyBridge.dll' -or $_.Name -like 'RevitAPI*.dll'}).Count -eq 0) 'No vendor, bridge, or Autodesk API DLL is embedded.'
[void][Reflection.Assembly]::LoadFrom($utility)
foreach ($year in 2015..2026) {
    $runtime=if ($year -le 2020) {'net45'} elseif ($year -le 2024) {'net48'} else {'net10'}
    $variant=@($embedded.variants | Where-Object {$_.id -ceq "$year-$runtime"})[0]
    $expected=@($variant.files | Where-Object {$_.name -ceq 'Bright.Vision.Revit.dll'})[0]
    $directory=Join-Path $target ([string]$year)
    Assert ((Get-FileHash -LiteralPath (Join-Path $directory 'Bright.Vision.Revit.dll')).Hash -ieq $expected.sha256) "Installed $year DLL matches embedded $runtime payload."
    $state=[IO.File]::ReadAllText((Join-Path $directory '.BrightVision.RevitExporter.install.json')) | ConvertFrom-Json
    Assert (@($variant.files).Count -eq 4 -and @($state.files).Count -eq 4) "Installed $year owns the complete four-file payload."
    foreach ($name in @('LICENSE','NOTICE')) {
        $filename='Bright.Vision.'+$name
        $source=@($variant.files | Where-Object name -eq $filename)
        $owned=@($state.files | Where-Object name -eq $filename)
        Assert ($source.Count -eq 1 -and $owned.Count -eq 1 -and $source[0].sha256 -ceq $expectedLegal[$name] -and $owned[0].sha256 -ceq $expectedLegal[$name] -and (Get-FileHash -LiteralPath (Join-Path $directory $filename)).Hash -ieq $expectedLegal[$name]) "Installed $year legal text preserves reviewed bytes and ownership."
    }
    $manifest=[Autodesk.RevitAddIns.AddInManifestUtility]::GetRevitAddInManifest((Join-Path $directory 'Bright.Vision.addin'))
    Assert ($manifest.AddInApplications.Count -eq 1) "Official utility accepts the $year manifest."
    $app=$manifest.AddInApplications[0]
    Assert ($app.Name -ceq 'BV' -and $app.FullClassName -ceq 'BrightVision.Revit.App' -and !$app.AllowLoadingIntoExistingSession) "Official manifest $year has owned class, BV brand, and hot-load disabled."
    [xml]$registered=[IO.File]::ReadAllText((Join-Path $directory 'Bright.Vision.addin'))
    Assert ($registered.RevitAddIns.AddIn.VendorDescription -ceq '崛亮视觉 · Bright Vision') "Installed $year vendor description identifies the company."
}
$null=Invoke-Package 'Uninstall'
Assert (!(Test-Path -LiteralPath $target) -or @(Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count -eq 0) 'Packaged uninstall leaves no owned fixture files.'
$net8Target=Join-Path $testRoot 'net8-addins'
$null=Invoke-Package 'Install' '2025,2026' 'net8' $net8Target
foreach ($year in @('2025','2026')) {
    $variant=@($embedded.variants | Where-Object {$_.id -ceq "$year-net8"})[0]
    $expected=@($variant.files | Where-Object {$_.name -ceq 'Bright.Vision.Revit.dll'})[0]
    Assert ((Get-FileHash -LiteralPath (Join-Path $net8Target "$year\Bright.Vision.Revit.dll")).Hash -ieq $expected.sha256) "Packaged .NET 8 selection for $year."
}
$null=Invoke-Package 'Uninstall' '2025,2026' 'net8' $net8Target
$report=[ordered]@{Result='PASS';Assertions=$script:count;Installer=$installer;Size=(Get-Item -LiteralPath $installer).Length;SHA256=(Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant();FixtureRoot=$testRoot;Scope='Own CLI EXE, isolated deployment, no original exporter present, official manifest parser only. No Revit or plugin code execution.'}
[IO.File]::WriteAllText((Join-Path $testRoot 'report.json'),($report | ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[pscustomobject]$report
