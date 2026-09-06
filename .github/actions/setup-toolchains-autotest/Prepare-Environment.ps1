[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('windows-vs2022', 'windows-vs2026', 'ubuntu-24.04', 'macos-15-arm64')]
    [string] $Profile,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [string] $ToolchainRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$ToolchainRoot = [IO.Path]::GetFullPath($ToolchainRoot)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$downloadsRoot = Join-Path $ToolchainRoot 'downloads'
$installations = [Collections.Generic.List[object]]::new()
$runtimes = [Collections.Generic.List[object]]::new()

switch ($Profile) {
    { $_ -in @('windows-vs2022', 'windows-vs2026') } {
        if (-not [OperatingSystem]::IsWindows()) {
            throw "Profile '$Profile' requires Windows."
        }
    }
    'ubuntu-24.04' {
        if (-not [OperatingSystem]::IsLinux()) {
            throw "Profile '$Profile' requires Linux."
        }
    }
    'macos-15-arm64' {
        if (-not [OperatingSystem]::IsMacOS()) {
            throw "Profile '$Profile' requires macOS."
        }
    }
}

$expectedArchitecture = if ($Profile -eq 'macos-15-arm64') { 'Arm64' } else { 'X64' }
$actualArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($actualArchitecture -ne $expectedArchitecture) {
    throw "Profile '$Profile' requires $expectedArchitecture; current host is $actualArchitecture."
}

$null = New-Item -ItemType Directory -Path $ToolchainRoot -Force
$null = New-Item -ItemType Directory -Path $downloadsRoot -Force
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force

function Assert-ToolchainPath {
    param([Parameter(Mandatory)][string] $Path)

    $candidate = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $ToolchainRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $comparison = if ([OperatingSystem]::IsWindows()) {
        [StringComparison]::OrdinalIgnoreCase
    } else {
        [StringComparison]::Ordinal
    }

    if ($candidate -ne $ToolchainRoot -and -not $candidate.StartsWith($rootWithSeparator, $comparison)) {
        throw "Refusing to modify '$candidate' because it is outside '$ToolchainRoot'."
    }

    return $candidate
}

function Reset-ToolchainDirectory {
    param([Parameter(Mandatory)][string] $Path)

    $verified = Assert-ToolchainPath $Path
    if (Test-Path -LiteralPath $verified) {
        Remove-Item -LiteralPath $verified -Recurse -Force
    }

    $null = New-Item -ItemType Directory -Path $verified -Force
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter()][string[]] $ArgumentList = @()
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' exited with code $LASTEXITCODE."
    }
}

function Invoke-CheckedWithEnvironment {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter()][string[]] $ArgumentList = @(),
        [Parameter(Mandatory)][Collections.IDictionary] $Environment
    )

    $previousValues = [ordered]@{}
    try {
        foreach ($entry in $Environment.GetEnumerator()) {
            $name = [string]$entry.Key
            $previousValues[$name] = [Environment]::GetEnvironmentVariable($name)
            [Environment]::SetEnvironmentVariable($name, [string]$entry.Value)
        }

        Invoke-Checked $FilePath $ArgumentList
    } finally {
        foreach ($entry in $previousValues.GetEnumerator()) {
            $value = if ($null -eq $entry.Value) {
                $null
            } else {
                [string]$entry.Value
            }
            [Environment]::SetEnvironmentVariable([string]$entry.Key, $value)
        }
    }
}

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found at '$Path'."
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Resolve-RequiredCommand {
    param(
        [Parameter(Mandatory)][string[]] $Names,
        [Parameter(Mandatory)][string] $Description
    )

    foreach ($name in $Names) {
        $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $command) {
            return $command.Source
        }
    }

    throw "$Description was not found. Tried: $($Names -join ', ')."
}

function Get-ProgramVersion {
    param(
        [Parameter(Mandatory)][string] $Executable,
        [Parameter()][string[]] $Arguments = @('--version')
    )

    $output = (& $Executable @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the version of '$Executable'."
    }

    $match = [regex]::Match(
        $output,
        '(?im)(?:gcc|clang|node|wasmtime)(?:\.exe)?(?:\s+version)?\s+v?(?<version>\d+(?:\.\d+){1,3})')
    if (-not $match.Success) {
        $match = [regex]::Match($output, '(?m)\b(?<version>\d+\.\d+(?:\.\d+){0,2})\b')
    }

    if (-not $match.Success) {
        throw "Could not parse the version reported by '$Executable': $output"
    }

    return $match.Groups['version'].Value
}

function Resolve-Compiler {
    param(
        [Parameter(Mandatory)][string[]] $Candidates,
        [Parameter(Mandatory)][int] $Major,
        [Parameter(Mandatory)][string] $Description
    )

    $paths = [Collections.Generic.List[string]]::new()
    foreach ($candidate in $Candidates) {
        if ([IO.Path]::IsPathFullyQualified($candidate)) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $paths.Add((Resolve-Path -LiteralPath $candidate).Path)
            }

            continue
        }

        foreach ($command in @(Get-Command $candidate -All -ErrorAction SilentlyContinue)) {
            if ($null -ne $command.Source) {
                $paths.Add($command.Source)
            }
        }
    }

    foreach ($path in $paths | Select-Object -Unique) {
        $version = Get-ProgramVersion $path
        if ([Version]::Parse($version).Major -eq $Major) {
            return [pscustomobject]@{
                Path = $path
                Version = $version
            }
        }
    }

    throw "$Description major version $Major was not found."
}

function Add-Installation {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][string] $RootPath,
        [Parameter(Mandatory)][string] $Version,
        [Parameter()][Collections.IDictionary] $Environment = [ordered]@{},
        [Parameter()][AllowNull()][string] $SourceUri,
        [Parameter()][AllowNull()][string] $Sha256,
        [Parameter()][AllowNull()][string] $Revision
    )

    $installations.Add([ordered]@{
        id = $Id
        kind = $Kind
        rootPath = [IO.Path]::GetFullPath($RootPath)
        version = $Version
        environment = $Environment
        sourceUri = $SourceUri
        sha256 = $Sha256
        revision = $Revision
    })
}

function Add-Runtime {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Version,
        [Parameter()][AllowNull()][string] $InstallationId,
        [Parameter()][AllowNull()][string] $SourceUri,
        [Parameter()][AllowNull()][string] $Sha256,
        [Parameter()][AllowNull()][string] $Revision
    )

    $runtimes.Add([ordered]@{
        id = $Id
        kind = $Kind
        path = [IO.Path]::GetFullPath($Path)
        version = $Version
        installationId = $InstallationId
        sourceUri = $SourceUri
        sha256 = $Sha256
        revision = $Revision
    })
}

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $Sha256,
        [Parameter()][string] $FileName = ''
    )

    $source = [Uri]$Uri
    if (-not $source.IsAbsoluteUri -or $source.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "Download URI '$Uri' must be an absolute HTTPS URI."
    }

    if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Download '$Uri' has an invalid SHA-256 value '$Sha256'."
    }

    if ([string]::IsNullOrWhiteSpace($FileName)) {
        $FileName = Split-Path -Leaf $source.AbsolutePath
    }

    if ([IO.Path]::GetFileName($FileName) -ne $FileName) {
        throw "Download cache name '$FileName' must be a file name."
    }

    $archive = Join-Path $downloadsRoot $FileName
    if (Test-Path -LiteralPath $archive) {
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Sha256.ToLowerInvariant()) {
            $verified = Assert-ToolchainPath $archive
            Remove-Item -LiteralPath $verified -Force
        }
    }

    if (-not (Test-Path -LiteralPath $archive)) {
        $temporary = Assert-ToolchainPath ($archive + '.downloading')
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }

        try {
            Invoke-WebRequest -Uri $source -OutFile $temporary
            $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $Sha256.ToLowerInvariant()) {
                throw "SHA-256 mismatch for '$Uri': expected $Sha256, found $actual."
            }

            Move-Item -LiteralPath $temporary -Destination $archive
        } finally {
            if (Test-Path -LiteralPath $temporary) {
                Remove-Item -LiteralPath $temporary -Force
            }
        }
    }

    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch for '$Uri': expected $Sha256, found $actual."
    }

    return $archive
}

function Expand-ToolArchive {
    param(
        [Parameter(Mandatory)][string] $Archive,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][string] $ProbePath
    )

    if (Test-Path -LiteralPath (Join-Path $Destination $ProbePath) -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Destination).Path
    }

    $staging = $Destination + '.extracting'
    Reset-ToolchainDirectory $staging
    if ($Archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -LiteralPath $Archive -DestinationPath $staging -Force
    } else {
        $tar = Resolve-RequiredCommand @('tar') 'tar'
        Invoke-Checked $tar @('-xf', $Archive, '-C', $staging)
    }

    $source = $null
    if (Test-Path -LiteralPath (Join-Path $staging $ProbePath) -PathType Leaf) {
        $source = $staging
    } else {
        $source = Get-ChildItem -LiteralPath $staging -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName $ProbePath) -PathType Leaf } |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if ($null -eq $source) {
        throw "Archive '$Archive' does not contain '$ProbePath'."
    }

    Reset-ToolchainDirectory $Destination
    Get-ChildItem -LiteralPath $source -Force |
        Move-Item -Destination $Destination -Force
    $verifiedStaging = Assert-ToolchainPath $staging
    Remove-Item -LiteralPath $verifiedStaging -Recurse -Force

    $null = Resolve-RequiredPath (Join-Path $Destination $ProbePath) "archive probe '$ProbePath'"
    return (Resolve-Path -LiteralPath $Destination).Path
}

function Add-VisualStudio {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][int] $ProductMajor
    )

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $vswhere = Resolve-RequiredPath(
        (Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe')) 'vswhere'
    $instances = (& $vswhere -all -prerelease -products '*' -format json -utf8 | Out-String) |
        ConvertFrom-Json
    $instance = $instances |
        Where-Object { ([Version]$_.installationVersion).Major -eq $ProductMajor } |
        Sort-Object { [Version]$_.installationVersion } -Descending |
        Select-Object -First 1
    if ($null -eq $instance) {
        throw "Visual Studio product major $ProductMajor was not found."
    }

    $root = Resolve-RequiredPath $instance.installationPath "Visual Studio $ProductMajor"
    $null = Resolve-RequiredPath (Join-Path $root 'VC\Tools\MSVC') 'MSVC toolsets'
    Add-Installation $Id 'VisualStudio' $root $instance.installationVersion
}

function Add-WindowsSdk {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Version
    )

    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $kitRoot = Join-Path $programFilesX86 'Windows Kits\10'
    $includeRoot = Resolve-RequiredPath(
        (Join-Path $kitRoot "Include\$Version")) "Windows SDK $Version include root"
    $null = Resolve-RequiredPath(
        (Join-Path $kitRoot "Lib\$Version")) "Windows SDK $Version library root"
    Add-Installation $Id 'WindowsSdk' $includeRoot $Version
}

function Add-CompilerInstallation {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Kind,
        [Parameter(Mandatory)][string[]] $Candidates,
        [Parameter(Mandatory)][int] $Major,
        [Parameter(Mandatory)][string] $CxxName
    )

    $compiler = Resolve-Compiler $Candidates $Major $Id
    $cxxCompiler = Resolve-Compiler @($CxxName) $Major "$Id C++ compiler"
    $environment = [ordered]@{
        CC = $compiler.Path
        CXX = $cxxCompiler.Path
    }
    Add-Installation $Id $Kind $compiler.Path $compiler.Version $environment
}

function Add-Xcode {
    param(
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $ApplicationName
    )

    $developerRoot = Resolve-RequiredPath(
        "/Applications/$ApplicationName.app/Contents/Developer") $ApplicationName
    $previous = [Environment]::GetEnvironmentVariable('DEVELOPER_DIR')
    try {
        [Environment]::SetEnvironmentVariable('DEVELOPER_DIR', $developerRoot)
        $versionText = (& /usr/bin/xcodebuild -version | Out-String)
        if ($LASTEXITCODE -ne 0) {
            throw "xcodebuild failed for '$developerRoot'."
        }
    } finally {
        [Environment]::SetEnvironmentVariable('DEVELOPER_DIR', $previous)
    }

    $match = [regex]::Match($versionText, '(?m)^Xcode\s+(?<version>\d+(?:\.\d+){1,2})')
    if (-not $match.Success) {
        throw "Could not parse the Xcode version for '$developerRoot'."
    }

    Add-Installation $Id 'Xcode' $developerRoot $match.Groups['version'].Value (
        [ordered]@{ DEVELOPER_DIR = $developerRoot })
}

function Prepare-WindowsEnvironment {
    if ($Profile -eq 'windows-vs2022') {
        Add-VisualStudio 'vs2022' 17
        Add-WindowsSdk 'windows-sdk-17763' '10.0.17763.0'
        Add-WindowsSdk 'windows-sdk-19041' '10.0.19041.0'
        Add-WindowsSdk 'windows-sdk-22621' '10.0.22621.0'
        Add-WindowsSdk 'windows-sdk-26100' '10.0.26100.0'
    } else {
        Add-VisualStudio 'vs2026' 18
        Add-WindowsSdk 'windows-sdk-26100' '10.0.26100.0'
    }

    $llvm = Resolve-Compiler @(
        'clang-20.exe',
        'clang.exe',
        'C:\Program Files\LLVM\bin\clang.exe',
        'C:\Program Files\LLVM-20\bin\clang.exe'
    ) 20 'LLVM'
    $clangDirectory = Split-Path -Parent $llvm.Path
    $clangxx = Resolve-RequiredPath (Join-Path $clangDirectory 'clang++.exe') 'clang++ 20'
    $clangxxVersion = Get-ProgramVersion $clangxx
    if ([Version]::Parse($clangxxVersion).Major -ne 20) {
        throw "clang++ at '$clangxx' is version $clangxxVersion, expected major version 20."
    }
    Add-Installation 'llvm-20' 'Llvm' $llvm.Path $llvm.Version (
        [ordered]@{
            CC = $llvm.Path
            CXX = $clangxx
            PATH = "$clangDirectory$([IO.Path]::PathSeparator)$env:PATH"
        })
}

function Prepare-UbuntuEnvironment {
    [Environment]::SetEnvironmentVariable('DEBIAN_FRONTEND', 'noninteractive')
    Invoke-Checked (Resolve-RequiredCommand @('sudo') 'sudo') @('apt-get', 'update')
    $packages = @(
        'gcc-12', 'g++-12', 'gcc-12-multilib', 'g++-12-multilib',
        'gcc-13', 'g++-13', 'gcc-13-multilib', 'g++-13-multilib',
        'gcc-14', 'g++-14', 'gcc-14-multilib', 'g++-14-multilib',
        'clang-16', 'llvm-16', 'lld-16',
        'clang-17', 'llvm-17', 'lld-17',
        'clang-18', 'llvm-18', 'lld-18'
    )
    Invoke-Checked (Resolve-RequiredCommand @('sudo') 'sudo') (
        @('apt-get', 'install', '--yes', '--no-install-recommends') + $packages)

    foreach ($major in @(12, 13, 14)) {
        Add-CompilerInstallation "gcc-$major" 'Gnu' @("gcc-$major") $major "g++-$major"
    }

    foreach ($major in @(16, 17, 18)) {
        Add-CompilerInstallation "clang-$major" 'Llvm' @("clang-$major") $major "clang++-$major"
    }
}

function Prepare-MacEnvironment {
    Add-Xcode 'xcode-16.4' 'Xcode_16.4'
    Add-Xcode 'xcode-26.3' 'Xcode_26.3'
    Add-CompilerInstallation 'gcc-14' 'Gnu' @(
        '/opt/homebrew/bin/gcc-14',
        'gcc-14'
    ) 14 'g++-14'

    $llvm = Resolve-Compiler @(
        '/opt/homebrew/opt/llvm@18/bin/clang',
        '/opt/homebrew/opt/llvm/bin/clang',
        'clang-18'
    ) 18 'Homebrew LLVM'
    $clangDirectory = Split-Path -Parent $llvm.Path
    $clangxx = Resolve-RequiredPath (Join-Path $clangDirectory 'clang++') 'Homebrew clang++ 18'
    $clangxxVersion = Get-ProgramVersion $clangxx
    if ([Version]::Parse($clangxxVersion).Major -ne 18) {
        throw "clang++ at '$clangxx' is version $clangxxVersion, expected major version 18."
    }
    Add-Installation 'llvm-18' 'Llvm' $llvm.Path $llvm.Version (
        [ordered]@{
            CC = $llvm.Path
            CXX = $clangxx
            PATH = "$clangDirectory$([IO.Path]::PathSeparator)$env:PATH"
        })
}

function Prepare-AndroidNdks {
    $platform = if ([OperatingSystem]::IsWindows()) {
        'windows'
    } elseif ([OperatingSystem]::IsMacOS()) {
        'darwin'
    } else {
        'linux'
    }
    $releases = @(
        [pscustomobject]@{
            Id = 'android-25.2.9519653'
            Version = '25.2.9519653'
            Release = 'r25c'
        },
        [pscustomobject]@{
            Id = 'android-27.2.12479018'
            Version = '27.2.12479018'
            Release = 'r27c'
        }
    )
    $hashes = @{
        'r25c:windows' = 'f70093964f6cbbe19268f9876a20f92d3a593db3ad2037baadd25fd8d71e84e2'
        'r25c:linux' = '769ee342ea75f80619d985c2da990c48b3d8eaf45f48783a2d48870d04b46108'
        'r25c:darwin' = 'b01bae969a5d0bfa0da18469f650a1628dc388672f30e0ba231da5c74245bc92'
        'r27c:windows' = '27e49f11e0cee5800983d8af8f4acd5bf09987aa6f790d4439dda9f3643d2494'
        'r27c:linux' = '59c2f6dc96743b5daf5d1626684640b20a6bd2b1d85b13156b90333741bad5cc'
        'r27c:darwin' = '8c5685457c58a88527367d46d3f14e8c727d962c39f85344cff0c0768a73c3b7'
    }

    foreach ($release in $releases) {
        $uri = "https://dl.google.com/android/repository/android-ndk-$($release.Release)-$platform.zip"
        $sha256 = $hashes["$($release.Release):$platform"]
        $archive = Get-VerifiedDownload $uri $sha256
        $ndkRoot = Expand-ToolArchive $archive (
            (Join-Path $ToolchainRoot "android-ndk-$($release.Version)")) 'source.properties'
        $sourceProperties = Resolve-RequiredPath (
            (Join-Path $ndkRoot 'source.properties')) "Android NDK $($release.Version) metadata"
        $metadata = Get-Content -LiteralPath $sourceProperties -Raw
        $revision = [regex]::Match(
            $metadata,
            '(?m)^\s*Pkg\.Revision\s*=\s*(?<version>\S+)\s*$').Groups['version'].Value
        if ($revision -ne $release.Version) {
            throw "Android NDK archive '$uri' reports revision '$revision'; expected '$($release.Version)'."
        }

        Add-Installation $release.Id 'AndroidNdk' $ndkRoot $release.Version (
            [ordered]@{
                ANDROID_NDK_HOME = $ndkRoot
                ANDROID_NDK_ROOT = $ndkRoot
            }) $uri $sha256 $release.Release
    }
}

function Find-EmbeddedExecutable {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string[]] $Names,
        [Parameter(Mandatory)][string] $Description
    )

    foreach ($name in $Names) {
        $match = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $name |
            Sort-Object FullName |
            Select-Object -First 1
        if ($null -ne $match) {
            return $match.FullName
        }
    }

    throw "$Description was not found below '$Root'."
}

function Prepare-Emscripten {
    param(
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][string] $ReleaseRevision
    )

    $emsdkRevision = '5eb0bde7585670252e8ba05e9d361627bffd08b5'
    $emsdkUri = 'https://github.com/emscripten-core/emsdk.git'
    $packageRootUri = 'https://storage.googleapis.com/webassembly/emscripten-releases-builds'
    $dependencyRootUri = "$packageRootUri/deps"
    $id = "emscripten-$Version"
    $emsdkRoot = Join-Path $ToolchainRoot "emsdk-$Version"
    $git = Resolve-RequiredCommand @('git') 'git'
    if ((Test-Path -LiteralPath $emsdkRoot) -and
        -not (Test-Path -LiteralPath (Join-Path $emsdkRoot '.git') -PathType Container)) {
        Reset-ToolchainDirectory $emsdkRoot
    }

    if (-not (Test-Path -LiteralPath (Join-Path $emsdkRoot '.git') -PathType Container)) {
        Invoke-Checked $git @('clone', $emsdkUri, $emsdkRoot)
    }

    Invoke-Checked $git @('-C', $emsdkRoot, 'remote', 'set-url', 'origin', $emsdkUri)
    Invoke-Checked $git @('-C', $emsdkRoot, 'fetch', '--depth', '1', 'origin', $emsdkRevision)
    Invoke-Checked $git @('-C', $emsdkRoot, 'checkout', '--detach', '--force', $emsdkRevision)
    $actualEmsdkRevision = (& $git -C $emsdkRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualEmsdkRevision -ne $emsdkRevision) {
        throw "emsdk at '$emsdkRoot' is revision '$actualEmsdkRevision'; expected '$emsdkRevision'."
    }

    $releaseTagsPath = Resolve-RequiredPath (
        (Join-Path $emsdkRoot 'emscripten-releases-tags.json')) 'emsdk release manifest'
    $releaseTags = Get-Content -LiteralPath $releaseTagsPath -Raw |
        ConvertFrom-Json -AsHashtable
    if (-not $releaseTags.ContainsKey('releases') -or
        -not $releaseTags['releases'].ContainsKey($Version)) {
        throw "Emscripten $Version is absent from emsdk revision $emsdkRevision."
    }

    $actualReleaseRevision = [string]$releaseTags['releases'][$Version]
    if ($actualReleaseRevision -ne $ReleaseRevision) {
        throw "Emscripten $Version resolves to $actualReleaseRevision at emsdk revision " +
            "$emsdkRevision; expected $ReleaseRevision."
    }

    if ([OperatingSystem]::IsWindows()) {
        $hostKey = 'windows'
        $releasePlatform = 'win'
        $releaseFile = 'wasm-binaries.zip'
        $nodeFile = 'node-v24.19.0-win-x64.zip'
        $nodeSha256 = '57f71ab3652e797d84acddc79c81cc9ff1c6ddb2a1974cdb83f00fee9bff4c73'
        $pythonFile = 'python-3.13.3-0-win-amd64.zip'
        $pythonSha256 = '6fe7a540c6b8b185780467cf7495e884ee62316ec4abb19e1c735b8a77c62465'
    } elseif ([OperatingSystem]::IsMacOS()) {
        $hostKey = 'macos-arm64'
        $releasePlatform = 'mac'
        $releaseFile = 'wasm-binaries-arm64.tar.xz'
        $nodeFile = 'node-v24.19.0-darwin-arm64.tar.gz'
        $nodeSha256 = '8294b7aa9b03997481c06babf1e8b270c859358f27da57a11509afe537ac381d'
        $pythonFile = 'python-3.13.3-0-macos-arm64.tar.gz'
        $pythonSha256 = '2b0899d7ade9463b0c909ef23aeea27546a4f412637998d817a49718bc2bac19'
    } else {
        $hostKey = 'linux'
        $releasePlatform = 'linux'
        $releaseFile = 'wasm-binaries.tar.xz'
        $nodeFile = 'node-v24.19.0-linux-x64.tar.xz'
        $nodeSha256 = '14b342e71204f811bde6153be8e04b62aef63c236fef92b55f9c83154b409647'
        $pythonFile = $null
        $pythonSha256 = $null
    }

    $releaseHashes = @{
        '3.1.64:windows' = 'eb5b59afb420915daab4c383e5f73d456cc14776dce02fdc852c46522cda5531'
        '3.1.64:linux' = 'c39de24beca60fd580f6dff0eca0e275016042a30234588b19eda82397e299f3'
        '3.1.64:macos-arm64' = '47449057c345a09aa8750be1a357c364ffea9f8a066066cb341a7a2a14bac96a'
        '6.0.9:windows' = 'f7512eab6e69ad9d7de5adbf39e68d7d6773b317b13e70ec5003ef1d10f92980'
        '6.0.9:linux' = 'd5c6c2917fbc1cae1a7d1e581f1c0b2817369dd57f94c7a0d05921476f1a7287'
        '6.0.9:macos-arm64' = 'b60514308507f64f4138d3c55bdb6979f20222288700fde603dced23b65dd533'
    }
    $releaseSha256 = $releaseHashes["$Version`:$hostKey"]
    if ([string]::IsNullOrWhiteSpace($releaseSha256)) {
        throw "No Emscripten $Version download hash is declared for '$hostKey'."
    }

    $emsdkDownloads = Join-Path $emsdkRoot 'downloads'
    $null = New-Item -ItemType Directory -Path $emsdkDownloads -Force
    $releaseUri = "$packageRootUri/$releasePlatform/$ReleaseRevision/$releaseFile"
    $releaseArchive = Get-VerifiedDownload $releaseUri $releaseSha256 (
        "emscripten-$Version-$hostKey-$releaseFile")
    Copy-Item -LiteralPath $releaseArchive -Destination (
        (Join-Path $emsdkDownloads "$ReleaseRevision-$releaseFile")) -Force

    $nodeUri = "$dependencyRootUri/$nodeFile"
    $nodeArchive = Get-VerifiedDownload $nodeUri $nodeSha256
    Copy-Item -LiteralPath $nodeArchive -Destination (Join-Path $emsdkDownloads $nodeFile) -Force

    $pythonUri = $null
    if ($null -ne $pythonFile) {
        $pythonUri = "$dependencyRootUri/$pythonFile"
        $pythonArchive = Get-VerifiedDownload $pythonUri $pythonSha256
        Copy-Item -LiteralPath $pythonArchive -Destination (
            (Join-Path $emsdkDownloads $pythonFile)) -Force
    }

    $emsdkCommand = if ([OperatingSystem]::IsWindows()) {
        Join-Path $emsdkRoot 'emsdk.bat'
    } else {
        Join-Path $emsdkRoot 'emsdk'
    }
    $emsdkCommand = Resolve-RequiredPath $emsdkCommand 'emsdk'
    $previousKeepDownloads = [Environment]::GetEnvironmentVariable('EMSDK_KEEP_DOWNLOADS')
    try {
        [Environment]::SetEnvironmentVariable('EMSDK_KEEP_DOWNLOADS', '1')
        Invoke-Checked $emsdkCommand @('install', $Version)
        Invoke-Checked $emsdkCommand @('activate', $Version, '--embedded')
    } finally {
        [Environment]::SetEnvironmentVariable('EMSDK_KEEP_DOWNLOADS', $previousKeepDownloads)
    }

    $emscriptenRoot = Resolve-RequiredPath(
        (Join-Path $emsdkRoot 'upstream\emscripten')) "Emscripten $Version"
    $versionFile = Resolve-RequiredPath (
        (Join-Path $emscriptenRoot 'emscripten-version.txt')) 'Emscripten version metadata'
    $installedVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim().Trim('"')
    if ($installedVersion -ne $Version) {
        throw "Emscripten at '$emscriptenRoot' is version '$installedVersion'; expected '$Version'."
    }

    $emccName = if ([OperatingSystem]::IsWindows()) { 'emcc.bat' } else { 'emcc' }
    $emxxName = if ([OperatingSystem]::IsWindows()) { 'em++.bat' } else { 'em++' }
    $null = Resolve-RequiredPath (Join-Path $emscriptenRoot $emccName) 'Emscripten C compiler'
    $null = Resolve-RequiredPath (Join-Path $emscriptenRoot $emxxName) 'Emscripten C++ compiler'
    $config = Resolve-RequiredPath (Join-Path $emsdkRoot '.emscripten') 'Emscripten config'
    $nodeName = if ([OperatingSystem]::IsWindows()) { 'node.exe' } else { 'node' }
    $node = Find-EmbeddedExecutable (Join-Path $emsdkRoot 'node') @($nodeName) 'Emscripten Node'
    $nodeVersion = Get-ProgramVersion $node
    if ($nodeVersion -ne '24.19.0') {
        throw "Emscripten $Version uses Node $nodeVersion; expected 24.19.0."
    }

    $pathParts = @(
        $emscriptenRoot,
        (Split-Path -Parent $node),
        $env:PATH
    )

    if ($null -ne $pythonFile) {
        $pythonNames = if ([OperatingSystem]::IsWindows()) {
            @('python.exe')
        } else {
            @('python3', 'python')
        }
        $python = Find-EmbeddedExecutable (
            (Join-Path $emsdkRoot 'python')) $pythonNames 'Emscripten Python'
    } else {
        $python = Resolve-RequiredCommand @('python3', 'python') 'Python'
    }
    $pythonVersion = Get-ProgramVersion $python @('--version')
    if ($null -ne $pythonFile -and $pythonVersion -ne '3.13.3') {
        throw "Emscripten $Version uses Python $pythonVersion; expected 3.13.3."
    }

    $pythonRevision = if ($null -ne $pythonFile) {
        'python-3.13.3'
    } else {
        "system-python-$pythonVersion"
    }
    $pathParts = @(
        (Split-Path -Parent $python),
        $pathParts
    )

    $environment = [ordered]@{
        EMSDK = $emsdkRoot
        EM_CONFIG = $config
        EMSDK_NODE = $node
        EMSDK_PYTHON = $python
        PATH = $pathParts -join [IO.Path]::PathSeparator
    }

    $embuilder = Resolve-RequiredPath (
        (Join-Path $emscriptenRoot 'embuilder.py')) 'Emscripten system library builder'
    Invoke-CheckedWithEnvironment $python @(
        $embuilder,
        'build',
        'sysroot',
        'MINIMAL'
    ) $environment
    Invoke-CheckedWithEnvironment $python @(
        $embuilder,
        '--pic',
        'build',
        'MINIMAL_PIC'
    ) $environment

    $libraryRoot = Resolve-RequiredPath (
        (Join-Path $emscriptenRoot 'cache\sysroot\lib\wasm32-emscripten')) (
        "Emscripten $Version default system libraries")
    $null = Resolve-RequiredPath (Join-Path $libraryRoot 'pic') (
        "Emscripten $Version PIC system libraries")

    Add-Installation $id 'Emscripten' $emscriptenRoot $Version $environment (
        $releaseUri) $releaseSha256 (
        "emsdk=$emsdkRevision; emscripten=$ReleaseRevision")
    Add-Runtime "node-$Version" 'Node' $node $nodeVersion $id (
        $nodeUri) $nodeSha256 'node-v24.19.0'
    Add-Runtime "python-$Version" 'Python' $python $pythonVersion $id (
        $pythonUri) $pythonSha256 $pythonRevision
}

function Prepare-WasiSdks {
    $platform = if ([OperatingSystem]::IsWindows()) {
        'x86_64-windows'
    } elseif ([OperatingSystem]::IsMacOS()) {
        'arm64-macos'
    } else {
        'x86_64-linux'
    }
    $hashes = @{
        '33:x86_64-linux' = '0ba8b5bfaeb2adf3f29bab5841d76cf5318ab8e1642ea195f88baba1abd47bce'
        '33:arm64-macos' = '85c997a2665ead91673b5bb88b7d0df3fc8900df3bfa244f720d478187bbdc78'
        '33:x86_64-windows' = 'df14ca2a2127c2d6b6be07e6f5549b3af9c1b3c0112430c200a4749970c59f06'
        '34:x86_64-linux' = 'b761e3a0721dbae9c09a0059e5fdb2bf917d1b4a8a7b430fb3b5aafb0984b2c4'
        '34:arm64-macos' = '9c59398106b417f8f14913380fdf0097a8cc0ff4af9eb3ce0065a859e88d49e9'
        '34:x86_64-windows' = 'cccb5c323a9b34f0349a9b09e8804a0a7632c68c3310f4b5f437ed57d7e71d8f'
    }
    $clangProbe = if ([OperatingSystem]::IsWindows()) { 'bin\clang.exe' } else { 'bin/clang' }

    foreach ($version in @(33, 34)) {
        $uri = "https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-$version/wasi-sdk-$version.0-$platform.tar.gz"
        $hashKey = [string]::Concat($version, ':', $platform)
        $sha256 = $hashes[$hashKey]
        $archive = Get-VerifiedDownload $uri $sha256
        $root = Expand-ToolArchive $archive (
            (Join-Path $ToolchainRoot "wasi-sdk-$version")) $clangProbe
        Add-Installation "wasi-sdk-$version" 'WasiSdk' $root "$version.0" (
            [ordered]@{ WASI_SDK_PATH = $root }) $uri $sha256 "wasi-sdk-$version"
    }
}

function Prepare-Wasmtime {
    $version = '45.0.0'
    if ([OperatingSystem]::IsWindows()) {
        $platform = 'x86_64-windows'
        $extension = 'zip'
        $sha256 = 'edb9572c6e8ae7c51053af826a8bc85bf205a759c9e83ddb08a941b26e297706'
        $probe = 'wasmtime.exe'
    } elseif ([OperatingSystem]::IsMacOS()) {
        $platform = 'aarch64-macos'
        $extension = 'tar.xz'
        $sha256 = '8c589a1feb6578ddfd76d4ee07bac551d7f3069d6cef9b2ae5e87e630b5198db'
        $probe = 'wasmtime'
    } else {
        $platform = 'x86_64-linux'
        $extension = 'tar.xz'
        $sha256 = '9d92e6dc04630f617e0e5d532327a5a917ac4898587e07f4fb7a5fc7fffef760'
        $probe = 'wasmtime'
    }

    $uri = "https://github.com/bytecodealliance/wasmtime/releases/download/v$version/wasmtime-v$version-$platform.$extension"
    $archive = Get-VerifiedDownload $uri $sha256
    $root = Expand-ToolArchive $archive (
        (Join-Path $ToolchainRoot "wasmtime-$version")) $probe
    $runtime = Resolve-RequiredPath (Join-Path $root $probe) "Wasmtime $version"
    $actualVersion = Get-ProgramVersion $runtime
    if ($actualVersion -ne $version) {
        throw "Wasmtime at '$runtime' is version '$actualVersion'; expected '$version'."
    }

    Add-Runtime "wasmtime-$version" 'Wasmtime' $runtime $actualVersion $null (
        $uri) $sha256 "v$version"
}

switch ($Profile) {
    { $_ -in @('windows-vs2022', 'windows-vs2026') } { Prepare-WindowsEnvironment }
    'ubuntu-24.04' { Prepare-UbuntuEnvironment }
    'macos-15-arm64' { Prepare-MacEnvironment }
}

if ($Profile -ne 'windows-vs2026') {
    Prepare-AndroidNdks
    Prepare-Emscripten '3.1.64' 'fd61bacaf40131f74987e649a135f1dd559aff60'
    Prepare-Emscripten '6.0.9' 'f04ea239d533260dd1db760dd2d668d5f9a88d6b'
    Prepare-WasiSdks
    Prepare-Wasmtime
}

$runnerOs = [Environment]::GetEnvironmentVariable('RUNNER_OS')
if ([string]::IsNullOrWhiteSpace($runnerOs)) {
    $runnerOs = if ([OperatingSystem]::IsWindows()) {
        'Windows'
    } elseif ([OperatingSystem]::IsMacOS()) {
        'macOS'
    } else {
        'Linux'
    }
}
$runnerArchitecture = [Environment]::GetEnvironmentVariable('RUNNER_ARCH')
if ([string]::IsNullOrWhiteSpace($runnerArchitecture)) {
    $runnerArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
}

$manifest = [ordered]@{
    schemaVersion = 1
    profile = $Profile
    runner = [ordered]@{
        imageLabel = switch ($Profile) {
            'windows-vs2022' { 'windows-2022' }
            'windows-vs2026' { 'windows-2025-vs2026' }
            'ubuntu-24.04' { 'ubuntu-24.04' }
            'macos-15-arm64' { 'macos-15' }
        }
        imageOS = [Environment]::GetEnvironmentVariable('ImageOS')
        imageVersion = [Environment]::GetEnvironmentVariable('ImageVersion')
        os = $runnerOs
        architecture = $runnerArchitecture
    }
    environment = [ordered]@{}
    installations = $installations
    runtimes = $runtimes
}

$json = $manifest | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($OutputPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Host "Prepared '$Profile' and wrote '$OutputPath'."
