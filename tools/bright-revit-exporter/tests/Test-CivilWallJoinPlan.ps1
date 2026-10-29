#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='',[string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-join-plan-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Join plan fixture must remain inside this workspace.'}
if(Test-Path -LiteralPath $output){throw 'Preserve previous evidence; use a fresh fixture directory.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse fixture output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$dll=Join-Path $output 'CivilWallJoinPlanFixture.dll'
$sources=@((Join-Path $module 'CivilWallJoinPlan.cs'),(Join-Path $PSScriptRoot 'CivilWallJoinPlanFixture.cs'))
$records=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$compilerArgs=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/codepage:65001','/nostdlib+',('/out:'+$dll))
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll')){$file=Join-Path $framework $name;if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw ('Missing existing Framework reference: '+$file)};$compilerArgs+='/reference:'+$file}
$compilerArgs+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compilerArgs
if($LASTEXITCODE-ne0){throw 'Join plan fixture compilation failed.'}
$build=[ordered]@{schemaVersion=1;scope='Own managed plan storage DLL; no Autodesk or desktop input';frameworkDirectory=$framework;sources=$records;assembly=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();hostExecuted=$false}
$build|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
$assembly=[Reflection.Assembly]::LoadFile($dll);$arguments=New-Object object[] 1;$arguments[0]=[string[]]@($output)
try{if($assembly.GetType('CivilWallJoinPlanFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$arguments)-ne0){throw 'Join plan fixture failed.'}}
catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};[ordered]@{result='FAIL';failure=$failure.ToString();hostExecuted=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8;throw}
foreach($source in $records){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-cne$source.sha256){throw 'Source changed during plan validation.'}}
Get-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
