<#
    TreeMeasure build script.

    Compiles the portable Windows executable and optionally applies a trusted
    Authenticode signature before producing its SHA-256 checksum.
#>

param(
    [string]$CertificateThumbprint = $env:TREEMEASURE_SIGNING_THUMBPRINT,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [switch]$RequireSignature
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to this script so it can be run from any working directory.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\Program.cs"
$icon = Join-Path $root "assets\TreeMeasure.ico"
$manifest = Join-Path $root "app.manifest"
$dist = Join-Path $root "dist"
$out = Join-Path $dist "TreeMeasure.exe"
$checksum = Join-Path $dist "TreeMeasure.exe.sha256"

# TreeMeasure targets classic .NET Framework so it can compile on Windows
# machines that have the framework compiler but not the modern .NET SDK.
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path $compiler)) {
    # Fall back to the 32-bit framework compiler if the 64-bit compiler is not present.
    $compiler = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path $compiler)) {
    throw "Could not find the .NET Framework C# compiler."
}

if (-not (Test-Path $icon)) {
    throw "Could not find the application icon at $icon."
}

if (-not (Test-Path $manifest)) {
    throw "Could not find the Windows manifest at $manifest."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Build a GUI executable. References are listed explicitly so no project file is required.
& $compiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    /out:$out `
    /win32icon:$icon `
    /win32manifest:$manifest `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Runtime.Serialization.dll `
    /reference:System.Windows.Forms.dll `
    $src

# The framework compiler is a native executable, so explicitly enforce its exit code.
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE. Close any running copy of TreeMeasure and try again."
}

# Sign before hashing so the checksum always describes the final executable.
if ($CertificateThumbprint) {
    # Normalize copied thumbprints because certificate tools sometimes include spaces.
    $normalizedThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
    $certificate = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert |
        Where-Object { $_.Thumbprint -eq $normalizedThumbprint } |
        Select-Object -First 1

    if (-not $certificate) {
        throw "No code-signing certificate with thumbprint $normalizedThumbprint was found."
    }

    if (-not $certificate.HasPrivateKey) {
        throw "The selected code-signing certificate does not have an accessible private key."
    }

    if ($certificate.NotAfter -le (Get-Date)) {
        throw "The selected code-signing certificate expired on $($certificate.NotAfter)."
    }

    # RFC 3161 timestamping keeps the signature valid after the certificate expires.
    $signature = Set-AuthenticodeSignature `
        -FilePath $out `
        -Certificate $certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $TimestampServer

    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signing failed: $($signature.Status) - $($signature.StatusMessage)"
    }

    Write-Host "Signed $out"
    Write-Host "Signer $($certificate.Subject)"
}
elseif ($RequireSignature) {
    throw "A trusted signing certificate is required. Set TREEMEASURE_SIGNING_THUMBPRINT or pass -CertificateThumbprint."
}
else {
    Write-Warning "Built an unsigned development executable. Use -RequireSignature for releases."
}

# Publish a checksum alongside the executable so downloads can be verified.
$hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  TreeMeasure.exe" | Set-Content -LiteralPath $checksum -Encoding ASCII

Write-Host "Built $out"
Write-Host "SHA256 $hash"
