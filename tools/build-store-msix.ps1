[CmdletBinding()]
param(
    # Test is the default channel, as everywhere else. A stable package is built for
    # an upload to Partner Center and belongs beside the stable build it came from.
    [switch] $Stable,

    # The stable folder's slug, so the package lands in the release it belongs to
    # rather than in one of its own.
    [ValidatePattern('^[a-z0-9]+(?:-[a-z0-9]+)*$')]
    [string] $Slug
)

$ErrorActionPreference = 'Stop'

$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $windowsRoot
$versionFile = Join-Path $windowsRoot 'Directory.Build.props'
$manifestTemplate = Join-Path $windowsRoot 'StorePackage\AppxManifest.template.xml'
$assetDirectory = Join-Path $windowsRoot 'StorePackage\Assets'
$toolProject = Join-Path $PSScriptRoot 'store-package\StorePackage.csproj'

[xml]$versionDocument = Get-Content -LiteralPath $versionFile -Raw
$version = @($versionDocument.Project.PropertyGroup.Version | Where-Object { $_ })[0].Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props contains an unsupported version: $version"
}

$packageVersion = "$version.0"
if ($Stable -and -not $Slug) {
    throw 'A stable package needs -Slug, naming the stable release folder it belongs in.'
}
$releaseDirectory = if ($Stable) {
    Join-Path $repositoryRoot "releases\stable\$version-$Slug"
} else {
    Join-Path $repositoryRoot "releases\test\${version}t-store-msix"
}
$packagePath = Join-Path $releaseDirectory "Arsenal-$version-x64.msix"

# A stable package joins a folder the stable build already made, so only the package
# itself has to be absent. A test package owns its folder outright.
$releaseDirectoryExisted = Test-Path -LiteralPath $releaseDirectory
if ($Stable) {
    if (Test-Path -LiteralPath $packagePath) {
        throw "The package already exists and will not be overwritten: $packagePath"
    }
} elseif ($releaseDirectoryExisted) {
    throw "The release folder already exists and will not be overwritten: $releaseDirectory"
}

foreach ($requiredAsset in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetDirectory $requiredAsset))) {
        throw "Missing Store asset: $requiredAsset"
    }
}

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("arsenal-store-" + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $workingDirectory 'package-root'
$validationRoot = Join-Path $workingDirectory 'unpacked'
$toolPackages = Join-Path $workingDirectory 'packages'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

try {
    dotnet restore $toolProject --locked-mode --packages $toolPackages
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned Windows SDK packaging tool failed.' }

    $makeAppx = Get-ChildItem -LiteralPath $toolPackages -Filter makeappx.exe -Recurse |
        Where-Object { $_.FullName -match '\\x64\\makeappx\.exe$' } |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $makeAppx) { throw 'The pinned Windows SDK package did not contain x64 MakeAppx.exe.' }

    dotnet publish (Join-Path $windowsRoot 'Arsenal.UI\Arsenal.UI.csproj') `
        -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:GITHUB_ACTIONS=true -p:ArsenalStoreBuild=true `
        -m:1 -nr:false -o $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'The Store-targeted Arsenal publish failed.' }

    $manifest = (Get-Content -LiteralPath $manifestTemplate -Raw).Replace('__PACKAGE_VERSION__', $packageVersion)
    Set-Content -LiteralPath (Join-Path $packageRoot 'AppxManifest.xml') -Value $manifest -Encoding utf8
    Copy-Item -LiteralPath $assetDirectory -Destination (Join-Path $packageRoot 'Assets') -Recurse
    Copy-Item -LiteralPath (Join-Path $windowsRoot 'LICENSE') -Destination $packageRoot
    Copy-Item -LiteralPath (Join-Path $windowsRoot 'NOTICE.md') -Destination $packageRoot

    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    & $makeAppx pack /d $packageRoot /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed to create the MSIX.' }

    & $makeAppx unpack /p $packagePath /d $validationRoot /o
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx could not unpack the completed MSIX.' }

    [xml]$packedManifest = Get-Content -LiteralPath (Join-Path $validationRoot 'AppxManifest.xml') -Raw
    $identity = $packedManifest.Package.Identity
    if ($identity.Name -ne 'KanishkaWijesuriya.Arsenal' -or
        $identity.Publisher -ne 'CN=21C81A2B-E7BB-4805-8024-170E50FBA71F' -or
        $identity.ProcessorArchitecture -ne 'x64' -or
        $identity.Version -ne $packageVersion) {
        throw 'The packed manifest identity does not match the Partner Center identity.'
    }

    $publishedVersion = (Get-Item -LiteralPath (Join-Path $validationRoot 'Arsenal.exe')).VersionInfo.ProductVersion
    if ($publishedVersion -ne $version) {
        throw "The executable is stamped $publishedVersion, expected $version."
    }

    Write-Output "Package: $packagePath"
    Write-Output "Identity: KanishkaWijesuriya.Arsenal"
    Write-Output "Publisher: CN=21C81A2B-E7BB-4805-8024-170E50FBA71F"
    Write-Output "Architecture: x64"
    Write-Output "Version: $packageVersion"
    Write-Output "Executable ProductVersion: $publishedVersion"
}
catch {
    # Undo only what this run made. A stable package joins a folder that already holds
    # the build it belongs to, so there the half-written package goes and the folder
    # and its executable stay.
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    if (-not $releaseDirectoryExisted -and (Test-Path -LiteralPath $releaseDirectory)) {
        $resolvedRelease = [System.IO.Path]::GetFullPath($releaseDirectory)
        $resolvedReleases = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'releases'))
        if ($resolvedRelease.StartsWith($resolvedReleases + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
        }
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $workingDirectory) {
        $resolvedWorking = [System.IO.Path]::GetFullPath($workingDirectory)
        $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolvedWorking.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedWorking -Recurse -Force
        }
    }
}
