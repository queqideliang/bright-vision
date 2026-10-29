#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputPath='', [string]$FrameworkDirectory='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\plugin-support-ui-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Support UI fixtures must remain within the workspace.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor)-and((Get-Item -LiteralPath $cursor -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Reparse support UI output refused.'}
}
if(Test-Path -LiteralPath $output){throw 'Use a fresh directory; previous evidence is preserved.'}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$dll=Join-Path $output 'PluginSupportUiFixture.dll'
$names=@('BrandPixels.cs','Branding.cs','Json.cs','PluginHelpWindow.cs','PluginUpdateCore.cs','PluginUpdateClient.cs','PluginUpdateWindow.cs')
$sources=@($names|ForEach-Object{Join-Path $module $_})+@((Join-Path $PSScriptRoot 'PluginSupportUiFixture.cs'))
$guides=@((Join-Path $module 'USER_GUIDE.md'))
$workspaceTutorial=Join-Path $workspace 'test\插件功能入门.md'
if(Test-Path -LiteralPath $workspaceTutorial -PathType Leaf){$guides+=$workspaceTutorial}
$records=@(($sources+$guides+@($PSCommandPath))|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$compile=@('/nologo','/noconfig','/target:library','/platform:anycpu','/langversion:5','/optimize+','/codepage:65001','/nostdlib+',('/out:'+$dll))
$references=@()
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Net.Http.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll')){
    $reference=Join-Path $framework $name
    if(!(Test-Path -LiteralPath $reference -PathType Leaf)){$reference=Join-Path (Join-Path $framework 'WPF') $name}
    if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw('Missing existing Framework reference: '+$name)}
    $references+=$reference;$compile+='/reference:'+$reference
}
$compile+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compile
if($LASTEXITCODE-ne0){throw 'Support UI fixture C# 5 compilation failed.'}
[ordered]@{schemaVersion=1;scope='Production help/update WPF; asynchronous injected byte transport; no host or desktop';sources=$records;framework=$framework;assembly=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();nativeDesktopInput=$false;childFixtureExecutable=$false;networkUsed=$false;installerStarted=$false}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'build-validation.json') -Encoding UTF8
$assembly=[Reflection.Assembly]::LoadFile($dll)
$arguments=New-Object object[] 1;$arguments[0]=[string[]]@($output,$module)
try{
    if($assembly.GetType('PluginSupportUiFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,$arguments)-ne0){throw 'Support UI fixture failed.'}
}
catch{
    $failure=$_.Exception;while($failure.InnerException){$failure=$failure.InnerException}
    [ordered]@{result='FAIL';error=$failure.ToString();assertionsBeforeFailure=$assembly.GetType('PluginSupportUiFixture',$true).GetField('checks',[Reflection.BindingFlags]'Static,NonPublic').GetValue($null);uiShown=$false;nativeDesktopInput=$false;networkUsed=$false;installerStarted=$false}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $output 'failure.json') -Encoding UTF8
    throw
}
foreach($record in $records){if((Get-FileHash -LiteralPath $record.path).Hash.ToLowerInvariant()-cne$record.sha256){throw 'Support UI source changed during validation; fresh rerun required.'}}
$report=Get-Content -LiteralPath (Join-Path $output 'report.json') -Raw -Encoding UTF8|ConvertFrom-Json
[ordered]@{result='PASS';assertions=$report.assertions;sources=$records;references=@($references|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}});assembly=$dll;assemblySha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();scope=$report.scope;uiShown=$false;nativeDesktopInput=$false;networkUsed=$false;installerStarted=$false;artifacts=@(Get-ChildItem -LiteralPath $output -File|ForEach-Object{[ordered]@{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'ui-build-validation.json') -Encoding UTF8
[pscustomobject]@{Result='PASS';Assertions=$report.assertions;Layouts=$report.layouts.Count;Output=$output;UiShown=$false;NetworkUsed=$false;InstallerStarted=$false}
