<#
    TreeMeasure release script.

    Produces a signed portable release archive. The script deliberately fails
    when no trusted Authenticode certificate is configured, preventing an
    unsigned executable from being mistaken for a public release.
#>

param(
    [string]$Version = "1.1.0",
    [string]$CertificateThumbprint = $env:TREEMEASURE_SIGNING_THUMBPRINT,
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

# Resolve every path from the repository root so the command works anywhere.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildScript = Join-Path $root "build.ps1"
$dist = Join-Path $root "dist"
$executable = Join-Path $dist "TreeMeasure.exe"
$executableChecksum = Join-Path $dist "TreeMeasure.exe.sha256"
$archive = Join-Path $dist "TreeMeasure-v$Version-win-portable.zip"
$archiveChecksum = "$archive.sha256"

if (-not $CertificateThumbprint) {
    throw "Set TREEMEASURE_SIGNING_THUMBPRINT to the trusted certificate thumbprint before creating a release."
}

# Build, sign, timestamp, and verify the executable before packaging it.
& $buildScript `
    -CertificateThumbprint $CertificateThumbprint `
    -TimestampServer $TimestampServer `
    -RequireSignature

if ($LASTEXITCODE -ne 0) {
    throw "The signed build failed."
}

$verifiedSignature = Get-AuthenticodeSignature -FilePath $executable
if ($verifiedSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Release signature verification failed: $($verifiedSignature.StatusMessage)"
}

# Refuse to package an archive whose label disagrees with the embedded version.
$embeddedVersion = [Version](Get-Item -LiteralPath $executable).VersionInfo.FileVersion
$requestedVersion = [Version]$Version
if ($embeddedVersion.Major -ne $requestedVersion.Major -or
    $embeddedVersion.Minor -ne $requestedVersion.Minor -or
    $embeddedVersion.Build -ne $requestedVersion.Build) {
    throw "Release version $Version does not match embedded version $embeddedVersion."
}

# Recreate the versioned archive so stale files cannot leak into a release.
Remove-Item -LiteralPath $archive, $archiveChecksum -Force -ErrorAction SilentlyContinue
Compress-Archive -LiteralPath $executable, $executableChecksum -DestinationPath $archive

# Hash the archive separately so GitHub release downloads can be verified too.
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $(Split-Path -Leaf $archive)" | Set-Content -LiteralPath $archiveChecksum -Encoding ASCII

Write-Host "Created signed release $archive"
Write-Host "SHA256 $archiveHash"
