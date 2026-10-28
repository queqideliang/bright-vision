#requires -Version 5.1
[CmdletBinding()]
param([string]$UnsignedArtifactPath = (Join-Path $PSScriptRoot '..\release\civil-phase1\Bright.Vision-0.5.1-Setup.exe'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$scriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\scripts\verify-release-trust.ps1'))
. $scriptPath
$script:checks = 0
function Assert([bool]$Condition,[string]$Message) { $script:checks++; if (!$Condition) { throw "ASSERT: $Message" } }
function Fact([string]$Status='Valid',[bool]$Timestamp=$true,[string]$Publisher='CN=Bright Vision Test Publisher') {
    return [pscustomobject]@{SignatureStatus=$Status;HasTimestamp=$Timestamp;Publisher=$Publisher}
}
# Synthetic policy facts only. No test creates or trusts a certificate or signs
# a file; these cases do not establish that a real artifact is signed.
$expected = 'CN=Bright Vision Test Publisher'
$result = Test-ReleaseTrustPolicy -Record (Fact) -ExpectedPublisher $expected
Assert ($result.TrustedForRelease -and $result.PublisherMatches) 'Valid, timestamped, exact publisher facts pass policy.'
$result = Test-ReleaseTrustPolicy -Record (Fact 'NotSigned' $false '') -ExpectedPublisher $expected
Assert (!$result.TrustedForRelease -and $result.PolicyFailures.Count -ge 3) 'Unsigned facts cannot satisfy the formal gate.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact 'HashMismatch')).TrustedForRelease) 'Hash mismatch is not a trusted signature.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact 'UnknownError')).TrustedForRelease) 'Unknown verification state fails closed.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact 'Valid' $false)).TrustedForRelease) 'Valid without a timestamp is not a formal release.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact) -ExpectedPublisher 'CN=Other Publisher').TrustedForRelease) 'A different publisher cannot pass.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact) -ExpectedPublisher 'cn=Bright Vision Test Publisher').TrustedForRelease) 'Publisher comparison is exact and case-sensitive.'
Assert ((Test-ReleaseTrustPolicy -Record (Fact)).TrustedForRelease) 'ExpectedPublisher remains optional when a valid signer and timestamp exist.'
Assert (!(Test-ReleaseTrustPolicy -Record (Fact 'Valid' $true '')).TrustedForRelease) 'A missing signer subject fails closed.'

$artifact = [IO.Path]::GetFullPath($UnsignedArtifactPath)
if (!(Test-Path -LiteralPath $artifact -PathType Leaf)) { throw 'Provide an existing unsigned project artifact; this test never creates a new executable.' }
$beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
$beforeWrite = (Get-Item -LiteralPath $artifact).LastWriteTimeUtc
$audits = @(& $scriptPath -Path $artifact)
Assert ($audits.Count -eq 1 -and $audits[0].Action -eq 'Audit') 'Default invocation reports a read-only audit.'
Assert ($audits[0].SignatureStatus -eq 'NotSigned' -and !$audits[0].TrustedForRelease) 'The existing unsigned preview is reported honestly.'
Assert ($audits[0].Sha256 -eq $beforeHash.ToLowerInvariant()) 'The report binds the actual artifact SHA256.'
$gateRejected = $false
try { $null = & $scriptPath -Path $artifact -RequireTrustedSignature } catch { $gateRejected = $_.Exception.Message.Contains('release gate rejected') }
Assert $gateRejected 'The actual unsigned preview must fail the formal release gate.'
$adsRejected = $false
try { $null = Get-ReleaseTrustAudit -ArtifactPath ($artifact + ':untrusted-stream') } catch { $adsRejected = $_.Exception.Message.Contains('Alternate data streams') }
Assert $adsRejected 'Alternate data streams cannot be substituted for release files.'
Assert (((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -eq $beforeHash) -and ((Get-Item -LiteralPath $artifact).LastWriteTimeUtc -eq $beforeWrite)) 'Audit and refused gate do not modify the artifact.'
[pscustomobject]@{Result='PASS';Assertions=$script:checks;UnsignedArtifact=$artifact;UnsignedSignatureStatus=$audits[0].SignatureStatus;FormalGateRejected=$gateRejected;Scope='Synthetic policy facts and read-only Authenticode checks; no certificates, signing, trust changes, antivirus configuration, or sample upload.'}
