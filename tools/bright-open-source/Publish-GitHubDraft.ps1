#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$PreparedDirectory,
    [Parameter(Mandatory=$true)][string]$Repository,
    [string]$ExpectedPublisher
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
# This script is prepared for an explicit future invocation. It is never called
# by the desktop updater or by the offline preparation script. It creates only
# a draft Release; it does not publish, edit an existing Release or create tags.
if(!$Repository -or $Repository -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$') { throw 'Use a plain OWNER/REPO identity.' }
$directory=[IO.Path]::GetFullPath($PreparedDirectory)
$generator=Join-Path $PSScriptRoot 'prepare-github-release.mjs'
$node=(Get-Command node.exe -ErrorAction Stop).Source
$gh=(Get-Command gh.exe -ErrorAction Stop).Source
$verifiedJson=& $node $generator --verify $directory
if($LASTEXITCODE -ne 0) { throw 'Prepared release verification failed; no GitHub action was attempted.' }
$verified=($verifiedJson -join "`n")|ConvertFrom-Json
$descriptor=$verified.descriptor
if($descriptor.repository -cne $Repository) { throw 'The destination differs from the prepared public repository identity.' }
$installer=Join-Path $directory $verified.manifest.installer.fileName
# Freeze asset handles against concurrent writers during the final validation
# and gh upload. No user credential or certificate-private-key store is read.
$locks=New-Object 'Collections.Generic.List[IO.FileStream]'
try {
    foreach($name in @($descriptor.assets)+@('release-descriptor.json')) {
        $locks.Add((New-Object IO.FileStream((Join-Path $directory $name),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)))
    }
    $null=& $node $generator --verify $directory
    if($LASTEXITCODE -ne 0) { throw 'Prepared release changed during preflight.' }
    # The same locked installer bytes are checked and uploaded. A successful
    # signature check made before acquiring these handles could become stale.
    $signature=Get-AuthenticodeSignature -LiteralPath $installer
    if($descriptor.channel -eq 'stable') {
        if([string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'Stable releases require the exact intended Authenticode certificate subject.' }
        if([string]$signature.Status -cne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $null -eq $signature.SignerCertificate -or
           ![string]::Equals([string]$signature.SignerCertificate.Subject,$ExpectedPublisher,[StringComparison]::Ordinal)) {
            throw 'Stable-release signing policy failed. Do not disable protection or publish changed bytes under a reviewed hash.'
        }
    }
    $tag=[string]$descriptor.tag
    $releaseArguments=@('release','create',$tag,'--repo',$Repository,'--verify-tag','--draft','--latest=false','--title',('崛亮视觉 · Bright Vision '+$descriptor.version),'--notes-file',(Join-Path $directory 'release-notes.md'))
    if($descriptor.channel -eq 'preview') { $releaseArguments+='--prerelease' }
    foreach($name in $descriptor.assets) { $releaseArguments+=Join-Path $directory $name }
    if($PSCmdlet.ShouldProcess(('https://github.com/'+$Repository+'/releases'),'Create one new draft with the verified files; do not publish')) {
        & $gh @releaseArguments
        if($LASTEXITCODE -ne 0) { throw 'Draft creation/upload did not complete; inspect GitHub draft state before retrying. No overwrite/clobber was requested.' }
    }
} finally { foreach($handle in $locks) { $handle.Dispose() } }
