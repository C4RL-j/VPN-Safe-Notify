[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PortableDirectory,
    [string]$ZipPath,
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$resolvedPortableDirectory = [IO.Path]::GetFullPath($PortableDirectory)
if ($resolvedPortableDirectory.Length -gt [IO.Path]::GetPathRoot($resolvedPortableDirectory).Length) {
    $resolvedPortableDirectory = $resolvedPortableDirectory.TrimEnd([char]'\', [char]'/')
}
$report = [ordered]@{
    Success = $false
    VerifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    PortableDirectory = $resolvedPortableDirectory
    RuntimeExecutionAttempted = $false
    Files = @()
    Failures = @()
}

function Test-Requirement([bool]$Condition, [string]$Message) {
    if (-not $Condition) { $failures.Add($Message) }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($Stream)).Replace('-', '')
    }
    finally { $algorithm.Dispose() }
}

function Get-PortableRelativePath([string]$Root, [string]$FullPath) {
    $prefix = $Root.TrimEnd([char]'\', [char]'/') + [IO.Path]::DirectorySeparatorChar
    if (-not $FullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "File is outside the portable directory: $FullPath"
    }
    return $FullPath.Substring($prefix.Length).Replace('\', '/')
}

try {
    if (-not [IO.Directory]::Exists($report.PortableDirectory)) {
        throw "Portable directory does not exist: $($report.PortableDirectory)"
    }
    $exePath = Join-Path $report.PortableDirectory 'VPN Safe Notify.exe'
    if (-not [IO.File]::Exists($exePath)) {
        throw "The portable executable is missing: $exePath"
    }

    $allItems = @(Get-ChildItem -LiteralPath $report.PortableDirectory -Recurse -Force)
    if (@($allItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
        throw 'Portable distributions must not contain links or reparse points to external files.'
    }
    $files = @($allItems | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
    $inventory = @()
    foreach ($file in $files) {
        $name = Get-PortableRelativePath $report.PortableDirectory $file.FullName
        $inventory += [pscustomobject]@{
            Path = $name
            Bytes = $file.Length
            Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
        Test-Requirement ($name -notmatch '(?i)(^|/)(?:.*Checks(?:\.|/)|bin/|obj/)|\.(?:pdb|cs|csproj|sln|cache)$') "Unexpected development artifact in portable output: $name"
        Test-Requirement (-not ($name -match '(?i)\.exe$' -and $name -ne 'VPN Safe Notify.exe')) "Unexpected additional executable in portable output: $name"
        Test-Requirement ($name -notmatch '(?i)\.(?:ps1|bat|cmd|reg)$') "Unexpected development or installation script in portable output: $name"
    }
    $report.Files = $inventory
    $readmes = @($files | Where-Object { $_.DirectoryName -eq $report.PortableDirectory -and $_.Name -match '^README\.(md|txt)$' })
    Test-Requirement ($readmes.Count -eq 1) 'Portable output must contain one root README.md or README.txt.'
    $sidecars = @($inventory | Where-Object { $_.Path -ne 'VPN Safe Notify.exe' -and $_.Path -notmatch '^README\.(md|txt)$' })
    $runtimeSidecars = @($sidecars | Where-Object { $_.Path -notmatch '^Licenses/' })
    $report.SingleFileExecutableOnly = ($runtimeSidecars.Count -eq 0)
    $report.AdditionalDistributionFiles = @($sidecars | ForEach-Object { $_.Path })
    # Native DLL sidecars may be necessary for some WPF dependencies. Report them
    # truthfully rather than treating their presence alone as a packaging failure.

    $stream = [IO.File]::OpenRead($exePath)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) { throw 'Executable has no valid DOS/PE header.' }
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 64 -or $peOffset -gt $stream.Length - 94) { throw 'Executable has an invalid PE header offset.' }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Executable has an invalid PE signature.' }
        $machine = $reader.ReadUInt16()
        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        if ($optionalHeaderSize -lt 70 -or $peOffset + 24 + $optionalHeaderSize -gt $stream.Length) { throw 'Executable has a truncated PE optional header.' }
        $stream.Position = $peOffset + 24
        $magic = $reader.ReadUInt16()
        $stream.Position = $peOffset + 24 + 68
        $subsystem = $reader.ReadUInt16()
        $report.Pe = [ordered]@{
            Machine = ('0x{0:X4}' -f $machine)
            OptionalHeaderMagic = ('0x{0:X4}' -f $magic)
            Subsystem = $subsystem
        }
        Test-Requirement ($machine -eq 0x8664) 'Published executable must be AMD64 (Windows x64).'
        Test-Requirement ($magic -eq 0x20b) 'Published executable must use the PE32+ format.'
        Test-Requirement ($subsystem -eq 2) 'Published executable must use Windows GUI subsystem 2, with no console window.'
    }
    finally { $reader.Dispose(); $stream.Dispose() }

    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
    $report.Version = [ordered]@{
        FileVersion = $version.FileVersion
        ProductVersion = $version.ProductVersion
        ProductName = $version.ProductName
        FileDescription = $version.FileDescription
    }
    Test-Requirement ($version.FileVersion -match '^0\.1\.0(?:\.0)?$') 'Embedded file version must be 0.1.0 (optionally four-part 0.1.0.0).'
    Test-Requirement ($version.ProductVersion -match '^0\.1\.0(?:\.0)?(?:\+.*)?$') 'Embedded product version must be 0.1.0.'
    Test-Requirement ($version.ProductName -eq 'VPN Safe Notify') 'Embedded product name must be VPN Safe Notify.'

    if (-not ('VpnSafeNotify.Packaging.PortableResourceReader' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnSafeNotify.Packaging
{
    public sealed class PortableResources
    {
        public string Manifest { get; set; }
        public int IconGroupCount { get; set; }
    }

    public static class PortableResourceReader
    {
        private delegate bool EnumResourceName(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResourceName callback, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr loadedResource);

        public static PortableResources Read(string path)
        {
            // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE maps only
            // resources. It does not execute entry points or load static imports.
            IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000022);
            if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new PortableResources();
                EnumResourceName countIcon = (m, t, n, p) => { result.IconGroupCount++; return true; };
                EnumResourceNamesW(module, new IntPtr(14), countIcon, IntPtr.Zero);
                IntPtr manifestName = IntPtr.Zero;
                EnumResourceName findManifest = (m, t, n, p) =>
                {
                    if (manifestName == IntPtr.Zero) manifestName = n;
                    return true;
                };
                EnumResourceNamesW(module, new IntPtr(24), findManifest, IntPtr.Zero);
                if (manifestName != IntPtr.Zero)
                {
                    IntPtr resource = FindResourceW(module, manifestName, new IntPtr(24));
                    if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    uint length = SizeofResource(module, resource);
                    if (length == 0 || length > 1024 * 1024) throw new InvalidOperationException("Invalid embedded manifest length.");
                    IntPtr loaded = LoadResource(module, resource);
                    IntPtr data = LockResource(loaded);
                    if (loaded == IntPtr.Zero || data == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    byte[] bytes = new byte[(int)length];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    using (var stream = new System.IO.MemoryStream(bytes))
                    using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8, true))
                        result.Manifest = reader.ReadToEnd().TrimEnd('\0');
                }
                GC.KeepAlive(countIcon);
                GC.KeepAlive(findManifest);
                return result;
            }
            finally { FreeLibrary(module); }
        }
    }
}
'@
    }
    $resources = [VpnSafeNotify.Packaging.PortableResourceReader]::Read($exePath)
    Test-Requirement ($resources.IconGroupCount -ge 1) 'Published executable must contain an embedded RT_GROUP_ICON application icon.'
    if ([string]::IsNullOrWhiteSpace($resources.Manifest)) { throw 'Published executable has no embedded application manifest.' }
    $manifest = [xml]$resources.Manifest
    $executionLevel = $manifest.SelectSingleNode("//*[local-name()='requestedExecutionLevel']")
    $dpiAwareness = $manifest.SelectSingleNode("//*[local-name()='dpiAwareness']")
    $supportedOs = $manifest.SelectSingleNode("//*[local-name()='supportedOS' and @Id='{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}']")
    $level = if ($null -ne $executionLevel) { $executionLevel.GetAttribute('level') } else { '' }
    $uiAccess = if ($null -ne $executionLevel) { $executionLevel.GetAttribute('uiAccess') } else { '' }
    $dpi = if ($null -ne $dpiAwareness) { $dpiAwareness.InnerText } else { '' }
    $report.Resources = [ordered]@{
        IconGroupCount = $resources.IconGroupCount
        ManifestExecutionLevel = $level
        ManifestUiAccess = $uiAccess
        ManifestDpiAwareness = $dpi
        ManifestSupportsWindows10And11 = ($null -ne $supportedOs)
    }
    Test-Requirement ($level -eq 'asInvoker' -and $uiAccess -eq 'false') 'Application manifest must use asInvoker and uiAccess=false; administrator privileges must not be requested.'
    Test-Requirement ($dpi -eq 'PerMonitorV2') 'Application manifest must retain PerMonitorV2 DPI awareness.'
    Test-Requirement ($null -ne $supportedOs) 'Application manifest must declare Windows 10/11 compatibility.'

    $signature = Get-AuthenticodeSignature -LiteralPath $exePath
    $report.Signature = [ordered]@{
        Status = [string]$signature.Status
        StatusMessage = $signature.StatusMessage
        SignerSubject = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
        IsValidSignature = ($signature.Status -eq 'Valid')
    }
    # Unsigned is acceptable. Always record the actual status rather than calling
    # the package signed or treating an absent certificate as a failed build.
    if ($readmes.Count -eq 1) {
        $readmeText = [IO.File]::ReadAllText($readmes[0].FullName)
        Test-Requirement ($readmeText -match '(?i)admin(?:istrator)?') 'Portable README must explain that administrator access is not required.'
        $claimsSigned = $readmeText -match '(?im)^\s*(?:This (?:app|application|executable|package) is |The (?:app|application|executable|package) is |Code signing:\s*)(?:digitally |code-)?signed\b'
        Test-Requirement (-not $claimsSigned -or $signature.Status -eq 'Valid') 'README claims a signed package but the executable does not have a valid signature.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
        $resolvedZip = [IO.Path]::GetFullPath($ZipPath)
        if (-not [IO.File]::Exists($resolvedZip)) { throw "Portable ZIP does not exist: $resolvedZip" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($resolvedZip)
        $verifiedEntries = @()
        $expected = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $inventory) { $expected.Add($file.Path, $file) }
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        # Both common ZIP layouts are valid: files at root, or one enclosing
        # folder named exactly like the portable directory. Do not strip arbitrary
        # directory names or accept a mixture that could conceal extra files.
        $portableFolder = [IO.Path]::GetFileName($report.PortableDirectory)
        $candidatePrefix = $portableFolder + '/'
        $archiveFiles = @($archive.Entries | Where-Object { -not $_.FullName.Replace('\', '/').EndsWith('/') })
        $outsideCandidate = @($archiveFiles | Where-Object { -not $_.FullName.Replace('\', '/').StartsWith($candidatePrefix, [StringComparison]::OrdinalIgnoreCase) })
        $archivePrefix = if ($archiveFiles.Count -gt 0 -and $outsideCandidate.Count -eq 0) { $candidatePrefix } else { '' }
        try {
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                $archiveName = $name
                if ($name -match '(^/|^[A-Za-z]:|(^|/)\.\.(/|$)|(^|/)\.(/|$)|:)' -or $name.Contains([char]0)) {
                    $failures.Add("Unsafe ZIP path: $name")
                    continue
                }
                if ($name.EndsWith('/')) { continue }
                if ($archivePrefix.Length -gt 0) { $name = $name.Substring($archivePrefix.Length) }
                if (-not $seen.Add($name)) { $failures.Add("Duplicate ZIP entry: $name"); continue }
                if (-not $expected.ContainsKey($name)) { $failures.Add("ZIP contains a file absent from portable output: $name"); continue }
                $entryStream = $entry.Open()
                try { $hash = Get-StreamSha256 $entryStream }
                finally { $entryStream.Dispose() }
                $source = $expected[$name]
                Test-Requirement ($entry.Length -eq $source.Bytes -and $hash -eq $source.Sha256) "ZIP content differs from portable file: $name"
                $verifiedEntries += [pscustomobject]@{ Path = $name; ArchivePath = $archiveName; Bytes = $entry.Length; Sha256 = $hash }
            }
            foreach ($file in $inventory) {
                Test-Requirement ($seen.Contains($file.Path)) "ZIP is missing portable file: $($file.Path)"
            }
        }
        finally { $archive.Dispose() }
        $zipFile = Get-Item -LiteralPath $resolvedZip
        $report.Zip = [ordered]@{
            Path = $resolvedZip
            Bytes = $zipFile.Length
            Sha256 = (Get-FileHash -LiteralPath $resolvedZip -Algorithm SHA256).Hash
            ArchiveRootFolder = if ($archivePrefix.Length -gt 0) { $portableFolder } else { $null }
            VerifiedEntries = $verifiedEntries
        }
    }
}
catch {
    $failures.Add($_.Exception.Message)
}

$report.Failures = @($failures.ToArray())
$report.Success = ($failures.Count -eq 0)
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $resolvedReport = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedReport)) | Out-Null
    [IO.File]::WriteAllText($resolvedReport, ($report | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
if (-not $report.Success) {
    throw ('Portable verification failed: ' + ($failures -join '; '))
}
Write-Host "PASS published PE/resources, version, signature evidence and distribution inventory: $($report.PortableDirectory)"
if ($report.Contains('Zip')) { Write-Host "PASS ZIP file names, lengths and SHA256 contents: $($report.Zip.Path)" }
[pscustomobject]$report
