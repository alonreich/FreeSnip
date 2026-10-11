param(
    [string]$ExePath,
    [string]$Version,
    [string]$OriginalFilename,
    [string]$InternalName,
    [string]$FileDescription = "FreeSnip"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "Target executable not found: $ExePath"
}

# Parse version (yyyy.MM.dd.HHmm)
$vParts = $Version.Split('.')
$major = if ($vParts.Length -gt 0) { [int]$vParts[0] } else { 1 }
$minor = if ($vParts.Length -gt 1) { [int]$vParts[1] } else { 0 }
$build = if ($vParts.Length -gt 2) { [int]$vParts[2] } else { 0 }
$rev   = if ($vParts.Length -gt 3) { [int]$vParts[3] } else { 0 }

# Find rc.exe
$rcExe = (Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\rc.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName)
if (-not $rcExe) {
    $rcCmd = Get-Command rc.exe -ErrorAction SilentlyContinue
    if ($rcCmd) { $rcExe = $rcCmd.Source }
}
if (-not $rcExe) {
    throw "rc.exe (Resource Compiler) not found."
}

$tempBase = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "FS_Version_" + [System.Guid]::NewGuid().ToString("N"))
$rcPath = "$tempBase.rc"
$resPath = "$tempBase.res"

try {
    $rcContent = @"
1 VERSIONINFO
FILEVERSION $major,$minor,$build,$rev
PRODUCTVERSION $major,$minor,$build,$rev
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
    BLOCK "StringFileInfo"
    BEGIN
        BLOCK "040904b0"
        BEGIN
            VALUE "CompanyName", "FreeSnip"
            VALUE "FileDescription", "$FileDescription"
            VALUE "FileVersion", "$Version"
            VALUE "InternalName", "$InternalName"
            VALUE "LegalCopyright", "Copyright (c) FreeSnip 2004-2026"
            VALUE "OriginalFilename", "$OriginalFilename"
            VALUE "ProductName", "FreeSnip"
            VALUE "ProductVersion", "$Version"
        END
    END
    BLOCK "VarFileInfo"
    BEGIN
        VALUE "Translation", 0x409, 1200
    END
END
"@

    [System.IO.File]::WriteAllText($rcPath, $rcContent, [System.Text.Encoding]::ASCII)
    & $rcExe /r /fo $resPath $rcPath | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $resPath)) {
        throw "Resource compiler failed with exit code $LASTEXITCODE"
    }

    $resBytes = [System.IO.File]::ReadAllBytes($resPath)
    $offset = 32
    $dataSize = [BitConverter]::ToUInt32($resBytes, $offset)
    $headerSize = [BitConverter]::ToUInt32($resBytes, $offset + 4)
    $dataOffset = $offset + $headerSize
    $resourceData = New-Object byte[] $dataSize
    [Array]::Copy($resBytes, $dataOffset, $resourceData, 0, $dataSize)

    $def = @"
using System;
using System.Runtime.InteropServices;

public class Win32ResourceHelper
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cb);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
}
"@
    if (-not ([System.Management.Automation.PSTypeName]'Win32ResourceHelper').Type) {
        Add-Type -TypeDefinition $def
    }

    $hUpdate = [Win32ResourceHelper]::BeginUpdateResource($ExePath, $false)
    if ($hUpdate -eq [IntPtr]::Zero) {
        throw "BeginUpdateResource failed for $ExePath with error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }

    # Update both Neutral (0) and US-English (0x0409)
    [void][Win32ResourceHelper]::UpdateResource($hUpdate, [IntPtr]16, [IntPtr]1, 0, $resourceData, [uint32]$dataSize)
    [void][Win32ResourceHelper]::UpdateResource($hUpdate, [IntPtr]16, [IntPtr]1, 0x0409, $resourceData, [uint32]$dataSize)

    $ok = [Win32ResourceHelper]::EndUpdateResource($hUpdate, $false)
    if (-not $ok) {
        throw "EndUpdateResource failed for $ExePath with error $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }

    Write-Host "[VERSION] Successfully applied version resource to $([System.IO.Path]::GetFileName($ExePath)) (OriginalFilename: $OriginalFilename, Version: $Version)"
}
finally {
    if (Test-Path $rcPath) { Remove-Item $rcPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $resPath) { Remove-Item $resPath -Force -ErrorAction SilentlyContinue }
}
