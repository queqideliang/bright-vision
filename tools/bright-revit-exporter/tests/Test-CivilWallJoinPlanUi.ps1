#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='',[string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-join-plan-ui-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Offscreen editor fixtures remain within this workspace.'}
if(Test-Path -LiteralPath $output){throw 'Preserve previous evidence; use a fresh fixture directory.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse UI fixture output refused.'}}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$dll=Join-Path $output 'CivilWallJoinPlanUiFixture.dll';$sources=@('BrandPixels.cs','Branding.cs','CivilWallJoinPlan.cs','CivilWallJoinPlanWindow.cs')|ForEach-Object{Join-Path $module $_};$sources+=Join-Path $PSScriptRoot 'CivilWallJoinPlanUiFixture.cs'
$records=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$compile=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/optimize+','/codepage:65001','/nostdlib+',('/out:'+$dll));$references=@()
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll')){$reference=Join-Path $framework $name;if(!(Test-Path -LiteralPath $reference -PathType Leaf)){$reference=Join-Path (Join-Path $framework 'WPF') $name};if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing Framework/WPF reference: '+$name)};$references+=$reference;$compile+='/reference:'+$reference}
$compile+=$sources;& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compile
if($LASTEXITCODE-ne0){throw 'Join plan WPF compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFile($dll);$arguments=New-Object object[] 1;$arguments[0]=[string[]]@($output)
try{if($assembly.GetType('CivilWallJoinPlanUiFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$arguments)-ne0){throw 'Join plan UI fixture failed.'}}
catch{$failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException};[ordered]@{result='FAIL';error=$failure.ToString();uiShown=$false;nativeDesktopInput=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8;throw}
foreach($source in $records){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant()-cne$source.sha256){throw 'Join plan UI source changed during validation.'}}
$report=Get-Content -LiteralPath (Join-Path $output 'report.json') -Raw -Encoding UTF8|ConvertFrom-Json
[ordered]@{result='PASS';assertions=$report.assertions;sources=$records;references=@($references|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}});assembly=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();scope='Own WPF editor offscreen; no Revit, Autodesk geometry or native input';uiShown=$false;artifacts=@(Get-ChildItem -LiteralPath $output -File|ForEach-Object{[ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length}})}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'ui-build-validation.json') -Encoding UTF8
[pscustomobject]@{Result='PASS';Assertions=$report.assertions;Output=$output;UiShown=$false}
