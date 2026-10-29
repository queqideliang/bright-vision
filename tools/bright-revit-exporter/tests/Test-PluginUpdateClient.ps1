#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\plugin-update-client-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture output must remain in the workspace.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse fixture output refused.'}}
if(Test-Path -LiteralPath $output){throw 'Use a new directory; previous evidence is preserved.'}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $workspace 'tools\revit-reference-cache\framework\net45'}
$dll=Join-Path $output 'PluginUpdateClientFixture.dll'
$compilerArgs=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Net.Http.dll')){$file=Join-Path $framework $name;if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Missing existing Framework reference.'};$compilerArgs+='/reference:'+$file}
$sources=@((Join-Path $module 'PluginUpdateCore.cs'),(Join-Path $module 'PluginUpdateClient.cs'),(Join-Path $module 'Json.cs'),(Join-Path $PSScriptRoot 'PluginUpdateClientFixture.cs'))
$before=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$compilerArgs+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if($LASTEXITCODE-ne0){throw 'Plugin update client fixture DLL compilation failed.'}
$junctionTarget=Join-Path $output 'junction-target'
$junction=Join-Path $output 'junction-cache'
[void][IO.Directory]::CreateDirectory($junctionTarget)
[void](New-Item -ItemType Junction -Path $junction -Target $junctionTarget)
if(!((Get-Item -LiteralPath $junction -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Fixture junction was not created as a reparse point.'}
[ordered]@{schemaVersion=1;assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();frameworkDirectory=$framework;sources=$before;reparseFixture=$junction;revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false;networkUsed=$false;installerStarted=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
$assembly=[Reflection.Assembly]::LoadFile($dll)
$method=$assembly.GetType('PluginUpdateClientFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invoke=New-Object object[] 1;$invoke[0]=[string[]]@($output,$junction)
try{if($method.Invoke($null,$invoke)-ne0){throw 'Plugin update client fixture failed.'}}
catch{$errorDetail=$_.Exception;while($errorDetail.InnerException){$errorDetail=$errorDetail.InnerException};[ordered]@{result='FAIL';assertionsPassed=$assembly.GetType('PluginUpdateClientFixture',$true).GetField('checks',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null);error=$errorDetail.Message;scope='Fake HTTP transport/cache DLL; no network, Revit, UI or installation';revitStarted=$false}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8;throw}
foreach($entry in $before){if((Get-FileHash -LiteralPath $entry.path).Hash.ToLowerInvariant()-cne$entry.sha256){throw 'Fixture source changed during validation.'}}
Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
