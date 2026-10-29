#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [string]$FrameworkDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$module=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$workspace=[IO.Path]::GetFullPath((Join-Path $module '..\..'))
if(!$OutputPath){$OutputPath=Join-Path $module ('artifacts\tests\civil-wall-ui-1.9-'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputPath)
if(!$output.StartsWith($workspace.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'UI test output must stay in this workspace.'}
for($cursor=$output;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse UI test output refused.'}
}
if(Test-Path -LiteralPath $output){throw 'UI test output already exists; use a new directory to preserve previous evidence.'}
[void][IO.Directory]::CreateDirectory($output)
$framework=if($FrameworkDirectory){[IO.Path]::GetFullPath($FrameworkDirectory)}else{Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'}
$dll=Join-Path $output 'CivilWallUiFixture.dll'
$compile=@('/nologo','/noconfig','/target:library','/platform:anycpu','/optimize+','/codepage:65001','/langversion:5','/nostdlib+',('/out:'+$dll))
$references=@()
foreach($name in @('mscorlib.dll','System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Xml.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll')) {
    $reference=Join-Path $framework $name
    if(!(Test-Path -LiteralPath $reference -PathType Leaf)){$reference=Join-Path (Join-Path $framework 'WPF') $name}
    if(!(Test-Path -LiteralPath $reference -PathType Leaf)){throw ('Missing pure Framework/WPF reference: '+$name+' under '+$framework)}
    $references+=$reference;$compile+='/reference:'+$reference
}
$sources=@('CivilQuantityModel.cs','CivilWallReadControl.cs','CivilWallRuleTemplates.cs','CivilWallFacts.cs','CivilWallBatchIntegrity.cs','CivilWallCalculation.cs','CivilProject.cs','CivilProjectWindow.cs','CivilWallReport.cs','CivilWallRuleWindow.cs','CivilWallWindow.cs','BrandPixels.cs','Branding.cs')|ForEach-Object{Join-Path $module $_}
$sources+=Join-Path $PSScriptRoot 'CivilWallUiFixture.cs'
$before=@($sources|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$compile+=$sources
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') @compile
if($LASTEXITCODE -ne 0){throw 'Wall WPF fixture compilation failed.'}
$assembly=[Reflection.Assembly]::LoadFile($dll);$method=$assembly.GetType('CivilWallUiFixture',$true).GetMethod('Main',[Reflection.BindingFlags]'Static,NonPublic')
$invoke=New-Object object[] 1;$invoke[0]=[string[]]@($output)
try { if($method.Invoke($null,$invoke)-ne 0){throw 'Wall WPF fixture failed.'} }
catch { $failure=$_.Exception;while($null-ne$failure.InnerException){$failure=$failure.InnerException};throw ('Wall WPF fixture failed: '+$failure.ToString()) }
foreach($source in $before){if((Get-FileHash -LiteralPath $source.path).Hash.ToLowerInvariant() -cne $source.sha256){throw 'UI source changed during validation; rerun against the completed source.'}}
$build=[ordered]@{result='PASS';scope='Project-owned pure Framework DLL and offscreen RenderTargetBitmap only; no shown window, native input, Autodesk assembly or Revit.';framework=$framework;assembly=$dll;sha256=(Get-FileHash -LiteralPath $dll).Hash.ToLowerInvariant();sources=$before;references=@($references|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})}
[IO.File]::WriteAllText((Join-Path $output 'ui-build-validation.json'),($build|ConvertTo-Json -Depth 7),(New-Object Text.UTF8Encoding($false)))
Get-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'report.json')
