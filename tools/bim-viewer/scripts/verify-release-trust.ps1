#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0)][string[]]$Path = @(),
    [ValidateSet('Audit')][string]$Action = 'Audit',
    [switch]$RequireTrustedSignature,
    [string]$ExpectedPublisher
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ReleaseTrustPath([string]$ArtifactPath) {
    if ([string]::IsNullOrWhiteSpace($ArtifactPath) -or ![IO.Path]::IsPathRooted($ArtifactPath)) { throw 'An absolute artifact path is required.' }
    if ($ArtifactPath.StartsWith('\\?\') -or $ArtifactPath.StartsWith('\\.\')) { throw 'Device artifact paths are not supported.' }
    $withoutDrive = if ($ArtifactPath -match '^[A-Za-z]:') { $ArtifactPath.Substring(2) } else { $ArtifactPath }
    if ($withoutDrive.Contains(':')) { throw 'Alternate data streams are not release artifacts.' }
    $full = [IO.Path]::GetFullPath($ArtifactPath)
    if ($full.Substring([IO.Path]::GetPathRoot($full).Length).Contains(':')) { throw 'Alternate data streams are not release artifacts.' }
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Release artifact paths cannot contain reparse points: $cursor"
        }
    }
    if (!(Test-Path -LiteralPath $full -PathType Leaf)) { throw "Release artifact does not exist: $full" }
    return $full
}

# Pure policy evaluation is also used by isolated tests. It does not sign,
# install, search certificates, or modify any operating-system trust setting.
function Test-ReleaseTrustPolicy {
    param([Parameter(Mandatory=$true)]$Record, [string]$ExpectedPublisher)
    $failures = New-Object 'Collections.Generic.List[string]'
    if ([string]$Record.SignatureStatus -cne 'Valid') { $failures.Add("Authenticode status is $($Record.SignatureStatus), not Valid.") }
    if (!$Record.HasTimestamp) { $failures.Add('A timestamp certificate was not found in the signature.') }
    if ([string]::IsNullOrWhiteSpace([string]$Record.Publisher)) { $failures.Add('No signer publisher subject is available.') }
    $publisherRequired = ![string]::IsNullOrEmpty($ExpectedPublisher)
    $publisherMatches = !$publisherRequired -or [string]::Equals([string]$Record.Publisher, $ExpectedPublisher, [StringComparison]::Ordinal)
    if (!$publisherMatches) { $failures.Add('Signer publisher subject does not exactly match ExpectedPublisher.') }
    return [pscustomobject]@{
        TrustedForRelease = ($failures.Count -eq 0)
        PublisherCheckRequired = $publisherRequired
        PublisherMatches = $publisherMatches
        PolicyFailures = @($failures.ToArray())
    }
}

function Get-ReleaseTrustAudit {
    param([Parameter(Mandatory=$true)][string]$ArtifactPath, [string]$ExpectedPublisher)
    $full = Assert-ReleaseTrustPath $ArtifactPath
    $before = Get-Item -LiteralPath $full
    # Keep the audited bytes read-locked while hashing and asking Windows to
    # verify Authenticode. This deliberately fails if a writer already holds it.
    $stream = New-Object IO.FileStream($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        $sha256 = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant()
        $signature = Get-AuthenticodeSignature -LiteralPath $full
        $null = Assert-ReleaseTrustPath $full
        $after = Get-Item -LiteralPath $full
        if ($before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc) { throw 'Artifact changed during its trust audit.' }
        $publisher = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Subject } else { '' }
        $signerThumbprint = if ($null -ne $signature.SignerCertificate) { [string]$signature.SignerCertificate.Thumbprint } else { '' }
        $timestampPublisher = if ($null -ne $signature.TimeStamperCertificate) { [string]$signature.TimeStamperCertificate.Subject } else { '' }
        $record = [pscustomobject][ordered]@{
            SchemaVersion = 1
            Action = 'Audit'
            Path = $full
            Bytes = [long]$after.Length
            Sha256 = $sha256
            SignatureStatus = [string]$signature.Status
            StatusMessage = [string]$signature.StatusMessage
            Publisher = $publisher
            SignerThumbprint = $signerThumbprint
            HasTimestamp = ($null -ne $signature.TimeStamperCertificate)
            TimestampPublisher = $timestampPublisher
            ExpectedPublisher = $ExpectedPublisher
            TrustedForRelease = $false
            PublisherCheckRequired = $false
            PublisherMatches = $false
            PolicyFailures = @()
            AuditNote = 'Authenticode and SHA256 describe publisher trust and byte integrity; this audit does not establish the cause of an antivirus detection or prove absence of malware.'
        }
        $policy = Test-ReleaseTrustPolicy -Record $record -ExpectedPublisher $ExpectedPublisher
        $record.TrustedForRelease = $policy.TrustedForRelease
        $record.PublisherCheckRequired = $policy.PublisherCheckRequired
        $record.PublisherMatches = $policy.PublisherMatches
        $record.PolicyFailures = @($policy.PolicyFailures)
        return $record
    }
    finally { $hash.Dispose(); $stream.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ($Path.Count -eq 0) { throw 'Specify one or more absolute artifact paths. Audit is read-only; -RequireTrustedSignature turns the result into a release gate.' }
    if ($PSBoundParameters.ContainsKey('ExpectedPublisher') -and [string]::IsNullOrWhiteSpace($ExpectedPublisher)) { throw 'ExpectedPublisher must be the exact nonempty certificate subject, or be omitted.' }
    $reports = @($Path | ForEach-Object { Get-ReleaseTrustAudit -ArtifactPath $_ -ExpectedPublisher $ExpectedPublisher })
    $reports
    if ($RequireTrustedSignature) {
        $rejected = @($reports | Where-Object { !$_.TrustedForRelease })
        if ($rejected.Count -gt 0) { throw "Trusted-signature release gate rejected $($rejected.Count) artifact(s). Valid Authenticode, a timestamp, and any exact ExpectedPublisher match are required." }
    }
}
