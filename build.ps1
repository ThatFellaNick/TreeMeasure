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
    /reference:System.Windows.Forms.dll `
    $src

# Publish a checksum alongside the executable so releases can be verified.
$hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  TreeMeasure.exe" | Set-Content -LiteralPath $checksum -Encoding ASCII

Write-Host "Built $out"
Write-Host "SHA256 $hash"
