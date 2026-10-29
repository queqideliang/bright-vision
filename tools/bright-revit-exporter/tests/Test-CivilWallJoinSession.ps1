#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-join-session-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture output must remain in workspace.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh fixture directory; previous evidence is preserved.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse fixture output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$dll=Join-Path $output 'CivilWallJoinSessionFixture.dll'
$compilerArgs=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$file=Join-Path $framework $name;if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Missing existing Framework reference.'};$compilerArgs+='/reference:'+$file}
$sources=@('CivilQuantityModel.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallCalculation.cs','CivilWallBatchIntegrity.cs','CivilWallJoinCore.cs','CivilWallJoinPlan.cs','CivilWallJoinSession.cs')|ForEach-Object{Join-Path $module $_}
$sources+=Join-Path $PSScriptRoot 'CivilWallJoinSessionFixture.cs'
$before=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$compilerArgs+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if($LASTEXITCODE-ne0){throw 'Join-session fixture DLL compilation failed.'}
[ordered]@{schemaVersion=1;assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();frameworkDirectory=$framework;sources=$before;revitStarted=$false;nativeDesktopInput=$false;childFixtureExecutable=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
$assembly=[Reflection.Assembly]::LoadFile($dll);$method=$assembly.GetType('CivilWallJoinSessionFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invoke=New-Object object[] 1;$invoke[0]=[string[]]@($output)
try{if($method.Invoke($null,$invoke)-ne0){throw 'Join-session fixture failed.'}}
catch{$errorDetail=$_.Exception;while($errorDetail.InnerException){$errorDetail=$errorDetail.InnerException};[ordered]@{result='FAIL';assertionsPassed=$assembly.GetType('CivilWallJoinSessionFixture',$true).GetField('checks',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null);error=$errorDetail.Message;scope='Pure DLL join-session fixture; no Revit/UI';revitStarted=$false}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8;throw}
foreach($entry in $before){if((Get-FileHash -LiteralPath $entry.path).Hash.ToLowerInvariant()-cne$entry.sha256){throw 'Fixture source changed during validation.'}}
Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
