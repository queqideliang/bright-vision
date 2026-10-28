#requires -Version 5.1
# Inert publisher test: gh.exe resolves ONLY to this test's recording script.
# No GitHub/network action, installer launch, credential read or .git write.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
$fixtureParent=Join-Path $project 'test-results'
[void][IO.Directory]::CreateDirectory($fixtureParent)
$fixture=Join-Path $fixtureParent ('draft-publisher-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$publisher=Join-Path $project 'Publish-GitHubDraft.ps1'
$generator=Join-Path $project 'prepare-github-release.mjs'
$global:BrightDraftPublisherDouble=@{RealNode=(Microsoft.PowerShell.Core\Get-Command node.exe -ErrorAction Stop).Source;RecordingGh=(Join-Path $fixture 'recording-gh.ps1')}
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($global:BrightDraftPublisherDouble.RecordingGh, '$global:BrightDraftPublisherCalls.Add(@($args)); $global:LASTEXITCODE=0', $utf8)
$global:BrightDraftPublisherCalls=New-Object 'Collections.Generic.List[object]'
$assertions=0
function Assert([bool]$Value,[string]$Message) { if(!$Value){throw "ASSERT: $Message"}; $script:assertions++ }
# Scope-local command resolution is a test double, not an executable shim or a
# PATH change. The production publisher does not expose injection parameters.
function Get-Command {
    param([string]$Name,[object]$ErrorAction)
    if($Name -eq 'gh.exe'){return [pscustomobject]@{Source=$global:BrightDraftPublisherDouble.RecordingGh}}
    if($Name -eq 'node.exe'){return [pscustomobject]@{Source=$global:BrightDraftPublisherDouble.RealNode}}
    throw 'Unexpected tool requested by publisher test.'
}
function Prepare([string]$Channel,[string]$Name) {
    $output=Join-Path $fixture $Name
    $installer=Join-Path $fixture 'Bright.Vision-0.6.0-Setup.exe'
    [IO.File]::WriteAllBytes($installer,[Text.Encoding]::ASCII.GetBytes('MZ inert release fixture; never executable or launched'))
    $null=& $global:BrightDraftPublisherDouble.RealNode $generator --repository BrightVision/bright-vision --product viewer --channel $Channel --version 0.6.0 --installer $installer --output $output --revit-version 1.7.0
    if($LASTEXITCODE -ne 0){throw 'Offline fixture preparation failed.'}
    return $output
}
$preview=Prepare preview 'preview'
& $publisher -PreparedDirectory $preview -Repository BrightVision/bright-vision -WhatIf
Assert ($global:BrightDraftPublisherCalls.Count -eq 0) 'WhatIf performs no publish invocation.'
& $publisher -PreparedDirectory $preview -Repository BrightVision/bright-vision -Confirm:$false
Assert ($global:BrightDraftPublisherCalls.Count -eq 1) 'Preview passes preflight and invokes only the local recording tool.'
$recorded=@($global:BrightDraftPublisherCalls[0])
foreach($flag in @('--verify-tag','--draft','--latest=false','--prerelease')) {Assert ($recorded -contains $flag) "The invocation preserves $flag."}
Assert (($recorded[0..2] -join ',') -eq 'release,create,viewer-v0.6.0') 'Actual prepared arguments reach the tool instead of PowerShell automatic args.'
Assert ($recorded -contains 'BrightVision/bright-vision') 'The explicit repository reaches the invocation.'
foreach($asset in @('Bright.Vision-0.6.0-Setup.exe','update.json','SHA256SUMS.txt','release-notes.md')) {Assert ($recorded -contains (Join-Path $preview $asset)) "Only reviewed fixed assets are uploaded: $asset"}
Assert (!($recorded -contains (Join-Path $preview 'release-descriptor.json'))) 'Local descriptor is not uploaded.'
$global:BrightDraftPublisherCalls.Clear()
try {& $publisher -PreparedDirectory $preview -Repository SomeoneElse/other -Confirm:$false;throw 'Expected repository rejection'} catch {Assert ($_.Exception.Message -like '*destination differs*') 'Changing the destination is rejected before any invocation.'}
Assert ($global:BrightDraftPublisherCalls.Count -eq 0) 'Repository rejection has no GitHub action.'
$stable=Prepare stable 'stable'
try {& $publisher -PreparedDirectory $stable -Repository BrightVision/bright-vision -ExpectedPublisher 'CN=Inert test subject' -Confirm:$false;throw 'Expected signature rejection'} catch {Assert ($_.Exception.Message -like '*signing policy failed*') 'An unsigned inert fixture cannot enter the stable channel.'}
Assert ($global:BrightDraftPublisherCalls.Count -eq 0) 'Stable signature rejection has no GitHub action.'
$report=[pscustomobject]@{Result='PASS';Assertions=$assertions;GitHubTool='Scope-local recording PowerShell script';NetworkActions=0;InstallerExecuted=$false;CredentialsRead=$false;Fixture=$fixture}
[IO.File]::WriteAllText((Join-Path $fixture 'report.json'),($report|ConvertTo-Json -Depth 5),$utf8)
$report|ConvertTo-Json -Compress
