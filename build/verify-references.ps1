[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ManifestPath,

    [string] $ProjectPath,

    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [string] $ScpSlManagedDir,

    [string] $ExiledDependenciesDir,

    [Parameter(Mandatory = $true)]
    [string] $MSBuildPath,

    [Parameter(Mandatory = $true)]
    [string] $CscPath,

    [switch] $SkipToolchainVerification,

    [switch] $VerifyPackageSignature,

    [switch] $VerifyPackageOrigins
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedRelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return $Path.Replace('\', '/').TrimStart('/')
}

function Get-FullPath {
    param(
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $ChildPath
    )

    if ([System.IO.Path]::IsPathRooted($ChildPath)) {
        return [System.IO.Path]::GetFullPath($ChildPath)
    }

    return [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($BasePath, $ChildPath))
}

function Get-ReferencePath {
    param([Parameter(Mandatory = $true)] $Reference)

    switch ([string] $Reference.source) {
        'repository' {
            return Get-FullPath -BasePath $script:RepositoryRootFull -ChildPath ([string] $Reference.path)
        }
        'scpslManaged' {
            if ([string]::IsNullOrWhiteSpace($ScpSlManagedDir)) {
                throw "ScpSlManagedDir is required to verify '$($Reference.id)'."
            }

            return Get-FullPath -BasePath $ScpSlManagedDir -ChildPath ([string] $Reference.path)
        }
        'exiledDependencies' {
            if ([string]::IsNullOrWhiteSpace($ExiledDependenciesDir)) {
                throw "ExiledDependenciesDir is required to verify '$($Reference.id)'."
            }

            return Get-FullPath -BasePath $ExiledDependenciesDir -ChildPath ([string] $Reference.path)
        }
        default {
            throw "Unknown reference source '$($Reference.source)' for '$($Reference.id)'."
        }
    }
}

function Get-ProjectHintIdentity {
    param(
        [Parameter(Mandatory = $true)][string] $HintPath,
        [Parameter(Mandatory = $true)][string] $ProjectDirectory
    )

    $scpslPrefix = '$(ScpSlManagedDir)'
    $exiledPrefix = '$(ExiledDependenciesDir)'

    if ($HintPath.StartsWith($scpslPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        if ([string]::IsNullOrWhiteSpace($ScpSlManagedDir)) {
            throw "Project reference '$HintPath' requires ScpSlManagedDir."
        }

        $relative = $HintPath.Substring($scpslPrefix.Length).TrimStart('\', '/')
        return [pscustomobject]@{ Source = 'scpslManaged'; Path = Get-NormalizedRelativePath $relative }
    }

    if ($HintPath.StartsWith($exiledPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        if ([string]::IsNullOrWhiteSpace($ExiledDependenciesDir)) {
            throw "Project reference '$HintPath' requires ExiledDependenciesDir."
        }

        $relative = $HintPath.Substring($exiledPrefix.Length).TrimStart('\', '/')
        return [pscustomobject]@{ Source = 'exiledDependencies'; Path = Get-NormalizedRelativePath $relative }
    }

    if ($HintPath.Contains('$(')) {
        throw "Unsupported MSBuild property in reference HintPath '$HintPath'."
    }

    $fullPath = Get-FullPath -BasePath $ProjectDirectory -ChildPath $HintPath
    $relativePath = [System.IO.Path]::GetRelativePath($script:RepositoryRootFull, $fullPath)
    if ($relativePath.StartsWith('..')) {
        throw "Unmanifested reference outside the repository: '$HintPath'."
    }

    return [pscustomobject]@{ Source = 'repository'; Path = Get-NormalizedRelativePath $relativePath }
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [AllowEmptyString()][string] $Expected,
        [AllowEmptyString()][string] $Actual
    )

    if (-not [string]::Equals($Expected, $Actual, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label drift: expected '$Expected', found '$Actual'."
    }
}

function Get-TopLevelDirectoryIdentity {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Toolchain directory is missing: '$Path'."
    }

    $records = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $Path -File)) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $records.Add("$($file.Name)`t$($file.Length)`t$hash")
    }

    $recordArray = $records.ToArray()
    [Array]::Sort($recordArray, [System.StringComparer]::Ordinal)
    $payload = $recordArray -join "`n"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $treeHash = [Convert]::ToHexString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($payload)))
    }
    finally {
        $sha.Dispose()
    }

    return [pscustomobject]@{
        FileCount = $recordArray.Count
        TreeSha256 = $treeHash
    }
}

function Assert-ToolFile {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Expected
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: '$Path'."
    }

    $file = Get-Item -LiteralPath $Path
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    Assert-Equal -Label "$Label file name" -Expected ([string] $Expected.fileName) -Actual $file.Name
    Assert-Equal -Label "$Label file version" -Expected ([string] $Expected.fileVersion) -Actual ([string] $version.FileVersion)
    Assert-Equal -Label "$Label product version" -Expected ([string] $Expected.productVersion) -Actual ([string] $version.ProductVersion)
    Assert-Equal -Label "$Label size" -Expected ([string] $Expected.size) -Actual ([string] $file.Length)
    Assert-Equal -Label "$Label SHA-256" -Expected ([string] $Expected.sha256) -Actual ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash)
}

function Get-AssemblyMvid {
    param([Parameter(Mandatory = $true)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadataReader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
            $moduleDefinition = $metadataReader.GetModuleDefinition()
            return $metadataReader.GetGuid($moduleDefinition.Mvid).ToString('D')
        }
        finally {
            $peReader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-NuGetPackageIdentity {
    param([Parameter(Mandatory = $true)][string] $Path)

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        try {
            $nuspecEntries = @($archive.Entries | Where-Object {
                    $_.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase)
                })
            if ($nuspecEntries.Count -ne 1) {
                throw "NuGet archive '$Path' must contain exactly one nuspec; found $($nuspecEntries.Count)."
            }

            $nuspecStream = $nuspecEntries[0].Open()
            try {
                $document = [System.Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($nuspecStream)
            }
            finally {
                $nuspecStream.Dispose()
            }
            $idNode = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
            $versionNode = $document.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")
            if ($null -eq $idNode -or $null -eq $versionNode) {
                throw "NuGet archive '$Path' nuspec has no package ID/version."
            }
            return [pscustomobject]@{ Id = [string] $idNode.InnerText; Version = [string] $versionNode.InnerText }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ReferenceOriginEvidence {
    param(
        [Parameter(Mandatory = $true)] $Reference,
        [switch] $VerifyDownload
    )

    if ($null -eq $Reference.PSObject.Properties['originEvidence']) {
        throw "Reference '$($Reference.id)' has a packageUrl but no committed byte-origin evidence."
    }

    $evidence = $Reference.originEvidence
    Assert-Equal -Label "$($Reference.id) origin evidence type" -Expected 'official-nuget-package-asset' -Actual ([string] $evidence.type)
    Assert-Equal -Label "$($Reference.id) origin package URL" -Expected ([string] $Reference.packageUrl) -Actual ([string] $evidence.packageUrl)
    Assert-Equal -Label "$($Reference.id) origin signature policy" -Expected 'repository-signed' -Actual ([string] $evidence.signaturePolicy)
    Assert-Equal -Label "$($Reference.id) origin asset SHA-256" -Expected ([string] $Reference.sha256) -Actual ([string] $evidence.assetSha256)
    Assert-Equal -Label "$($Reference.id) origin asset size" -Expected ([string] $Reference.size) -Actual ([string] $evidence.assetSize)
    if ([string] $evidence.nupkgSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Reference '$($Reference.id)' origin package SHA-256 is invalid."
    }

    $purlMatch = [regex]::Match([string] $Reference.packageUrl, '^pkg:nuget/([^@]+)@([^?#]+)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $purlMatch.Success) {
        throw "Reference '$($Reference.id)' has an unsupported or unversioned NuGet purl."
    }
    $originPackageId = [System.Uri]::UnescapeDataString($purlMatch.Groups[1].Value).ToLowerInvariant()
    $originPackageVersion = [System.Uri]::UnescapeDataString($purlMatch.Groups[2].Value).ToLowerInvariant()
    $expectedDownloadPath = "/v3-flatcontainer/$originPackageId/$originPackageVersion/$originPackageId.$originPackageVersion.nupkg"
    $downloadUri = [uri] ([string] $evidence.downloadUrl)
    if ($downloadUri.Scheme -ne 'https' -or $downloadUri.Host -ne 'api.nuget.org' -or
        -not [string]::Equals($downloadUri.AbsolutePath, $expectedDownloadPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Reference '$($Reference.id)' origin download URL does not match its official NuGet purl identity."
    }

    $assetPath = ([string] $evidence.assetPath).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($assetPath) -or $assetPath.StartsWith('/') -or
        $assetPath.Contains('../') -or $assetPath.Contains('/..')) {
        throw "Reference '$($Reference.id)' origin asset path is unsafe."
    }

    if (-not $VerifyDownload) {
        return
    }

    Add-Type -AssemblyName System.IO.Compression
    $temporaryPackage = [System.IO.Path]::Combine(
        [System.IO.Path]::GetTempPath(),
        "talky-origin-$([guid]::NewGuid().ToString('N')).nupkg")
    try {
        try {
            Invoke-WebRequest -Uri $downloadUri.AbsoluteUri -OutFile $temporaryPackage -TimeoutSec 60 -MaximumRedirection 0
        }
        catch {
            throw "Reference '$($Reference.id)' official NuGet origin download failed closed: $($_.Exception.Message)"
        }

        Assert-Equal -Label "$($Reference.id) official package SHA-256" -Expected ([string] $evidence.nupkgSha256) -Actual ((Get-FileHash -LiteralPath $temporaryPackage -Algorithm SHA256).Hash)
        $stream = [System.IO.File]::OpenRead($temporaryPackage)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new(
                $stream,
                [System.IO.Compression.ZipArchiveMode]::Read,
                $false)
            try {
                $entries = @($archive.Entries | Where-Object {
                        [string]::Equals($_.FullName, $assetPath, [System.StringComparison]::Ordinal)
                    })
                if ($entries.Count -ne 1) {
                    throw "Reference '$($Reference.id)' official package must contain exactly one '$assetPath' asset; found $($entries.Count)."
                }

                Assert-Equal -Label "$($Reference.id) official asset size" -Expected ([string] $evidence.assetSize) -Actual ([string] $entries[0].Length)
                $entryStream = $entries[0].Open()
                try {
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try {
                        $assetHash = [Convert]::ToHexString($sha.ComputeHash($entryStream))
                    }
                    finally {
                        $sha.Dispose()
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
                Assert-Equal -Label "$($Reference.id) official asset SHA-256" -Expected ([string] $Reference.sha256) -Actual $assetHash
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        $verificationOutput = @(& dotnet nuget verify $temporaryPackage --all 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet signature verification failed for the official '$($Reference.id)' origin archive: $($verificationOutput -join [Environment]::NewLine)"
        }
        if (-not (($verificationOutput -join [Environment]::NewLine) -match '(?m)^Signature type:\s+Repository\s*$')) {
            throw "Official '$($Reference.id)' origin archive does not satisfy the repository-signed policy."
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPackage -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPackage -Force
        }
    }
}

$script:RepositoryRootFull = [System.IO.Path]::GetFullPath($RepositoryRoot)
$manifestFullPath = Get-FullPath -BasePath $script:RepositoryRootFull -ChildPath $ManifestPath
if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "Reference manifest not found: '$manifestFullPath'."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json -Depth 32
if ([int] $manifest.schemaVersion -ne 1) {
    throw "Unsupported reference manifest schema '$($manifest.schemaVersion)'."
}

$msbuildFullPath = [System.IO.Path]::GetFullPath($MSBuildPath)
$cscFullPath = [System.IO.Path]::GetFullPath($CscPath)
if (-not $SkipToolchainVerification) {
    Assert-ToolFile -Label 'MSBuild tool' -Path $msbuildFullPath -Expected $manifest.toolchain.msbuild
    $msbuildDirectoryIdentity = Get-TopLevelDirectoryIdentity -Path ([System.IO.Path]::GetDirectoryName($msbuildFullPath))
    Assert-Equal -Label 'MSBuild top-level file count' -Expected ([string] $manifest.toolchain.msbuildTopLevel.fileCount) -Actual ([string] $msbuildDirectoryIdentity.FileCount)
    Assert-Equal -Label 'MSBuild top-level tree SHA-256' -Expected ([string] $manifest.toolchain.msbuildTopLevel.treeSha256) -Actual ([string] $msbuildDirectoryIdentity.TreeSha256)
    Assert-ToolFile -Label 'C# compiler' -Path $cscFullPath -Expected $manifest.toolchain.csharpCompiler
    $roslynIdentity = Get-TopLevelDirectoryIdentity -Path ([System.IO.Path]::GetDirectoryName($cscFullPath))
    Assert-Equal -Label 'Roslyn top-level file count' -Expected ([string] $manifest.toolchain.roslynTopLevel.fileCount) -Actual ([string] $roslynIdentity.FileCount)
    Assert-Equal -Label 'Roslyn top-level tree SHA-256' -Expected ([string] $manifest.toolchain.roslynTopLevel.treeSha256) -Actual ([string] $roslynIdentity.TreeSha256)
}

$allReferences = @($manifest.references)
$referencesToVerify = $allReferences

foreach ($reference in $allReferences) {
    $hasPackageUrl = $null -ne $reference.PSObject.Properties['packageUrl']
    $hasOriginEvidence = $null -ne $reference.PSObject.Properties['originEvidence']
    if ($hasPackageUrl -ne $hasOriginEvidence) {
        throw "Reference '$($reference.id)' must have both packageUrl and originEvidence, or neither."
    }
    if ($hasPackageUrl) {
        Assert-ReferenceOriginEvidence -Reference $reference -VerifyDownload:$VerifyPackageOrigins
    }
}

if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) {
    $projectFullPath = Get-FullPath -BasePath $script:RepositoryRootFull -ChildPath $ProjectPath
    if (-not (Test-Path -LiteralPath $projectFullPath -PathType Leaf)) {
        throw "Project file not found: '$projectFullPath'."
    }

    [xml] $projectXml = Get-Content -LiteralPath $projectFullPath -Raw
    $projectDirectory = [System.IO.Path]::GetDirectoryName($projectFullPath)
    $selectedReferences = [System.Collections.Generic.List[object]]::new()
    $hintNodes = @($projectXml.SelectNodes("//*[local-name()='Reference']/*[local-name()='HintPath']"))

    foreach ($hintNode in $hintNodes) {
        $identity = Get-ProjectHintIdentity -HintPath ([string] $hintNode.InnerText) -ProjectDirectory $projectDirectory
        $matches = @($allReferences | Where-Object {
                [string]::Equals([string] $_.source, [string] $identity.Source, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals((Get-NormalizedRelativePath ([string] $_.path)), [string] $identity.Path, [System.StringComparison]::OrdinalIgnoreCase)
            })

        if ($matches.Count -ne 1) {
            throw "Reference HintPath '$($hintNode.InnerText)' is not uniquely pinned in '$manifestFullPath'."
        }

        if (-not ($selectedReferences | Where-Object { $_.id -eq $matches[0].id })) {
            $selectedReferences.Add($matches[0])
        }
    }

    $referencesToVerify = @($selectedReferences)
}

foreach ($reference in $referencesToVerify) {
    $path = Get-ReferencePath $reference
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Pinned reference '$($reference.id)' is missing: '$path'."
    }

    $file = Get-Item -LiteralPath $path
    Assert-Equal -Label "$($reference.id) size" -Expected ([string] $reference.size) -Actual ([string] $file.Length)
    Assert-Equal -Label "$($reference.id) SHA-256" -Expected ([string] $reference.sha256) -Actual ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)

    $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($path)
    Assert-Equal -Label "$($reference.id) assembly name" -Expected ([string] $reference.assemblyName) -Actual ([string] $assemblyName.Name)
    Assert-Equal -Label "$($reference.id) assembly version" -Expected ([string] $reference.assemblyVersion) -Actual ([string] $assemblyName.Version)
    Assert-Equal -Label "$($reference.id) MVID" -Expected ([string] $reference.mvid) -Actual (Get-AssemblyMvid $path)
}

$manifestPackages = @($manifest.packages)
$packageConfigPaths = if (-not [string]::IsNullOrWhiteSpace($ProjectPath)) {
    $candidate = Join-Path ([System.IO.Path]::GetDirectoryName($projectFullPath)) 'packages.config'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { @($candidate) } else { @() }
}
else {
    @(Get-ChildItem -LiteralPath $script:RepositoryRootFull -Filter 'packages.config' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|packages)[\\/]' } |
        Select-Object -ExpandProperty FullName)
}

foreach ($packageConfigPath in $packageConfigPaths) {
    [xml] $packageConfig = Get-Content -LiteralPath $packageConfigPath -Raw
    foreach ($declaredPackage in @($packageConfig.packages.package)) {
        $packageMatches = @($manifestPackages | Where-Object {
                [string]::Equals([string] $_.id, [string] $declaredPackage.id, [System.StringComparison]::OrdinalIgnoreCase)
            })
        if ($packageMatches.Count -ne 1) {
            throw "Package '$($declaredPackage.id)' from '$packageConfigPath' is not pinned in the reference manifest."
        }

        Assert-Equal -Label "$($declaredPackage.id) package version" -Expected ([string] $packageMatches[0].version) -Actual ([string] $declaredPackage.version)
    }
}

foreach ($package in $manifestPackages) {
    $packageDirectory = Get-FullPath -BasePath $script:RepositoryRootFull -ChildPath ([string] $package.path)
    $nupkgPath = Get-FullPath -BasePath $script:RepositoryRootFull -ChildPath ([string] $package.nupkg)
    if (-not (Test-Path -LiteralPath $nupkgPath -PathType Leaf)) {
        throw "Pinned package archive is missing: '$nupkgPath'."
    }

    Assert-Equal -Label "$($package.id) package SHA-256" -Expected ([string] $package.sha256) -Actual ((Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash)
    Assert-Equal -Label "$($package.id) package signature policy" -Expected 'repository-signed' -Actual ([string] $package.signaturePolicy)
    Assert-Equal -Label "$($package.id) package purl" -Expected "pkg:nuget/$($package.id)@$($package.version)" -Actual ([string] $package.packageUrl)
    $packageIdentity = Get-NuGetPackageIdentity -Path $nupkgPath
    Assert-Equal -Label "$($package.id) nuspec ID" -Expected ([string] $package.id) -Actual ([string] $packageIdentity.Id)
    Assert-Equal -Label "$($package.id) nuspec version" -Expected ([string] $package.version) -Actual ([string] $packageIdentity.Version)

    $packageDllDirectory = Join-Path $packageDirectory 'lib\net48'
    $actualPackageDlls = @(Get-ChildItem -LiteralPath $packageDllDirectory -Filter '*.dll' -File | ForEach-Object { $_.FullName })
    $expectedPackageDlls = @($allReferences | Where-Object {
            $_.source -eq 'repository' -and
            (Get-NormalizedRelativePath ([string] $_.path)).StartsWith((Get-NormalizedRelativePath ([string] $package.path)) + '/', [System.StringComparison]::OrdinalIgnoreCase) -and
            ([string] $_.path).EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase)
        } | ForEach-Object { Get-ReferencePath $_ })

    $actualSet = @($actualPackageDlls | ForEach-Object { [System.IO.Path]::GetFullPath($_).ToUpperInvariant() } | Sort-Object -Unique)
    $expectedSet = @($expectedPackageDlls | ForEach-Object { [System.IO.Path]::GetFullPath($_).ToUpperInvariant() } | Sort-Object -Unique)
    if (($actualSet -join "`n") -ne ($expectedSet -join "`n")) {
        $unknown = @($actualSet | Where-Object { $_ -notin $expectedSet })
        $missing = @($expectedSet | Where-Object { $_ -notin $actualSet })
        throw "Package '$($package.id)' DLL inventory drift. Unknown: [$($unknown -join ', ')]. Missing: [$($missing -join ', ')]."
    }

    if ($VerifyPackageSignature) {
        $verificationOutput = @(& dotnet nuget verify $nupkgPath --all 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet signature verification failed for '$nupkgPath': $($verificationOutput -join [Environment]::NewLine)"
        }
        if (-not (($verificationOutput -join [Environment]::NewLine) -match '(?m)^Signature type:\s+Repository\s*$')) {
            throw "Package '$($package.id)' does not satisfy the repository-signed policy."
        }
    }
}

$signatureStatus = if ($VerifyPackageSignature) { ', local NuGet signatures' } else { '' }
$originStatus = if ($VerifyPackageOrigins) { ', and official byte-identical signed NuGet origins' } else { '' }
$toolchainStatus = if ($SkipToolchainVerification) { '' } else { 'pinned MSBuild/Roslyn toolchain, ' }
Write-Host "Reference verification PASS: $toolchainStatus$($referencesToVerify.Count) assembly reference(s), $($manifestPackages.Count) package archive(s)$signatureStatus$originStatus."
