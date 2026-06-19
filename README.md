# TreeMeasure

TreeMeasure is a lightweight Windows disk-usage viewer.

It scans a selected drive or folder, shows where space is being used in an
expandable tree, and includes a right-click menu for common file-location
actions.

Completed scans can be exported as JSON for analysis. The export contains the
full file/folder hierarchy with paths, object types, byte sizes, file counts,
folder counts, skipped counts, and child objects.

The tree starts filling while the scan is still running. TreeMeasure keeps the
view folder-first for performance on large drives while counting all files,
folders, sizes, and skipped protected items.

## Build

TreeMeasure targets .NET Framework and can be built with the compiler included
with Windows:

```powershell
.\build.ps1
```

The executable is written to `dist\TreeMeasure.exe`.
The build also writes `dist\TreeMeasure.exe.sha256` so the executable can be
verified after download or transfer.

Development builds are unsigned by default and show a warning. Public releases
must be signed with a trusted code-signing certificate installed in the Windows
certificate store. Set its thumbprint without placing private key material in
the repository:

```powershell
$env:TREEMEASURE_SIGNING_THUMBPRINT = "YOUR_CERTIFICATE_THUMBPRINT"
.\release.ps1
```

The release command builds version 1.1.2, signs it with SHA-256, adds a trusted
timestamp, verifies the resulting Authenticode signature, and creates a
versioned portable ZIP plus checksum in `dist`. It stops rather than producing
an unsigned release when the certificate is unavailable or invalid.

## Security and verification

TreeMeasure is built directly from the source in this repository without a
packer or obfuscator. The executable includes product/version metadata and an
explicit Windows manifest. Release builds should be Authenticode-signed with a
trusted code-signing certificate when one is available; unsigned new utilities
can receive reputation-based false positives from some security products.

Only a certificate issued by a publicly trusted code-signing provider (or an
equivalent managed signing service) improves public trust. A locally generated
self-signed certificate is suitable for testing but is not used for releases.

To verify a build in PowerShell:

```powershell
Get-FileHash .\dist\TreeMeasure.exe -Algorithm SHA256
Get-AuthenticodeSignature .\dist\TreeMeasure.exe
```

## Portable and Backstage use

TreeMeasure is a single portable `.exe`; it does not need an installer, Docker,
Python, or the modern .NET SDK.

You can launch it directly against a drive or folder:

```powershell
.\dist\TreeMeasure.exe C:\
.\dist\TreeMeasure.exe /path C:\Users
.\dist\TreeMeasure.exe /path \\server\share
```

UNC shares and mapped network drives are supported. TreeMeasure uses the
Windows credentials of the account that launches it and does not store network
credentials. Mapped drive letters are session-specific, so a Backstage/SYSTEM
session may not see drives mapped by the desktop user; enter the direct UNC path
instead and grant that account access to the share.

This is intended to make it friendlier in restricted remote-control sessions
such as ScreenConnect Backstage. The right-click menu is app-owned and limited
to explicit file viewing, navigation, rescanning, and export actions; it does
not load or invoke third-party Explorer shell extensions.

When launched in ScreenConnect Backstage, TreeMeasure should normally run as
SYSTEM. To scan protected locations from a normal desktop session, use
Windows' standard `Run as administrator` option when starting the executable.
TreeMeasure does not elevate or relaunch itself.
