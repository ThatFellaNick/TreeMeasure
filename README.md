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

## Security and verification

TreeMeasure is built directly from the source in this repository without a
packer or obfuscator. The executable includes product/version metadata and an
explicit Windows manifest. Release builds should be Authenticode-signed with a
trusted code-signing certificate when one is available; unsigned new utilities
can receive reputation-based false positives from some security products.

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
```

This is intended to make it friendlier in restricted remote-control sessions
such as ScreenConnect Backstage, where normal Explorer shell behavior may be
limited. The right-click menu includes both Explorer and command-prompt actions.

When launched in ScreenConnect Backstage, TreeMeasure should normally run as
SYSTEM. When launched from a normal desktop session, the toolbar includes
`Restart as Admin` so protected locations can be scanned with elevated access
without changing the portable executable.
