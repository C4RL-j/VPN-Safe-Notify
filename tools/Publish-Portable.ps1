param(
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspacePath 'src\VpnSafeNotify\VpnSafeNotify.csproj'
[xml]$projectXml = [IO.File]::ReadAllText($projectPath)
$packageVersion = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($packageVersion)) { throw 'The project must declare a package version.' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $workspacePath ('output\releases\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$releasePath = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $releasePath) { throw "Use a fresh release directory: $releasePath" }
[void][IO.Directory]::CreateDirectory($releasePath)
$stagingPath = Join-Path $releasePath 'publish-staging'
$portableName = "VPN-Safe-Notify-$packageVersion-win-x64-portable"
$portablePath = Join-Path $releasePath $portableName
$publishLog = Join-Path $releasePath 'publish.log'

$publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '-p:PublishProfile=PortableWinX64',
    '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $stagingPath)
& dotnet @publishArguments 2>&1 | Tee-Object -FilePath $publishLog
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE. See $publishLog" }
$originalExe = Join-Path $stagingPath 'VpnSafeNotify.exe'
if (!(Test-Path -LiteralPath $originalExe -PathType Leaf)) { throw 'Publish did not produce VpnSafeNotify.exe.' }
[void][IO.Directory]::CreateDirectory($portablePath)
# Keep every published dependency/notice; never select only the EXE from a
# framework-dependent build and silently discard necessary companion files.
Get-ChildItem -LiteralPath $stagingPath -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $portablePath -Recurse
}
Rename-Item -LiteralPath (Join-Path $portablePath 'VpnSafeNotify.exe') -NewName 'VPN Safe Notify.exe'
$portableReadme = [IO.File]::ReadAllText((Join-Path $workspacePath 'packaging\README.portable.txt')).Replace('__VERSION__', $packageVersion)
[IO.File]::WriteAllText((Join-Path $portablePath 'README.txt'), $portableReadme, [Text.UTF8Encoding]::new($false))
# Include the unmodified notices supplied with the exact restored runtime packs.
$assetsPath = Join-Path ([IO.Path]::GetDirectoryName($projectPath)) 'obj\project.assets.json'
$publishAssets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$runtimeNotices = @(
    @{ Package = 'Microsoft.NETCore.App.Runtime.win-x64'; File = 'LICENSE.TXT'; Target = '.NET-Runtime-LICENSE.txt' },
    @{ Package = 'Microsoft.NETCore.App.Runtime.win-x64'; File = 'THIRD-PARTY-NOTICES.TXT'; Target = '.NET-Runtime-THIRD-PARTY-NOTICES.txt' },
    @{ Package = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; File = 'LICENSE'; Target = '.NET-WindowsDesktop-LICENSE.txt' }
)
$licensePath = Join-Path $portablePath 'Licenses'
[void][IO.Directory]::CreateDirectory($licensePath)
foreach ($notice in $runtimeNotices) {
    $dependency = @($publishAssets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object { $_.name -eq $notice.Package }) | Select-Object -First 1
    if ($null -eq $dependency) { throw "The published runtime pack was not recorded: $($notice.Package)" }
    $packVersion = ([regex]::Match($dependency.version, '\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?')).Value
    $noticeSource = $null
    foreach ($packageFolder in $publishAssets.packageFolders.PSObject.Properties.Name) {
        $candidatePath = Join-Path $packageFolder ($notice.Package.ToLowerInvariant() + '\' + $packVersion + '\' + $notice.File)
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) { $noticeSource = $candidatePath; break }
    }
    if ($null -eq $noticeSource) { throw "Runtime notice missing: $($notice.Package)/$($notice.File)" }
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $licensePath $notice.Target)
}
$result = [ordered]@{
    Version = $packageVersion
    ReleaseDirectory = $releasePath
    PublishDirectory = $stagingPath
    PortableDirectory = $portablePath
    Executable = Join-Path $portablePath 'VPN Safe Notify.exe'
    ZipPath = Join-Path $releasePath ($portableName + '.zip')
    PublishLog = $publishLog
}
[IO.File]::WriteAllText((Join-Path $releasePath 'package-paths.json'), ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
