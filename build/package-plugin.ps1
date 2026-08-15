[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Release', 'EXILED')]
    [string] $Configuration,

    [Parameter(Mandatory = $true)]
    [string] $ScpSlManagedDir,

    [Parameter(Mandatory = $true)]
    [string] $ExiledDependenciesDir,

    [Parameter(Mandatory = $true)]
    [string] $MSBuildPath,

    [string] $CscPath,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [string] $OutputRoot,

    [switch] $IncludePdb,

    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $json = $Value | ConvertTo-Json -Depth 32
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Get-PortableMvid {
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

function Get-ToolFileRecord {
    param([Parameter(Mandatory = $true)][string] $Path)

    $file = Get-Item -LiteralPath $Path
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    return [ordered]@{
        fileName = $file.Name
        fileVersion = [string] $version.FileVersion
        productVersion = [string] $version.ProductVersion
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        size = $file.Length
    }
}

function Get-BuiltModuleRecord {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $EmbeddedResource
    )

    $file = Get-Item -LiteralPath $Path
    $identity = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
    return [ordered]@{
        fileName = $file.Name
        embeddedResource = $EmbeddedResource
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        size = $file.Length
        assemblyName = [string] $identity.Name
        assemblyVersion = [string] $identity.Version
        mvid = Get-PortableMvid $Path
        origin = 'built-from-repository-source'
    }
}

function Get-TopLevelDirectoryIdentity {
    param([Parameter(Mandatory = $true)][string] $Path)

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

    return [ordered]@{
        fileCount = $recordArray.Count
        treeSha256 = $treeHash
    }
}

$repositoryRootFull = [System.IO.Path]::GetFullPath($RepositoryRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRootFull 'artifacts'
}
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
$msbuildFullPath = [System.IO.Path]::GetFullPath($MSBuildPath)
if ([string]::IsNullOrWhiteSpace($CscPath)) {
    $CscPath = Join-Path ([System.IO.Path]::GetDirectoryName($msbuildFullPath)) 'Roslyn\csc.exe'
}
$cscFullPath = [System.IO.Path]::GetFullPath($CscPath)

$manifestPath = Join-Path $repositoryRootFull 'build\reference-manifest.json'
$verifyScript = Join-Path $repositoryRootFull 'build\verify-references.ps1'
& $verifyScript -ManifestPath $manifestPath -RepositoryRoot $repositoryRootFull -ScpSlManagedDir $ScpSlManagedDir -ExiledDependenciesDir $ExiledDependenciesDir -MSBuildPath $msbuildFullPath -CscPath $cscFullPath -VerifyPackageSignature -VerifyPackageOrigins

$trackedChanges = @(& git -C $repositoryRootFull status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect tracked Git state.'
}
$buildRelevantUntracked = @(& git -C $repositoryRootFull ls-files --others --exclude-standard -- '*.cs' '*.csproj' '*.props' '*.targets' '*.ps1' '*.config' '*.json' '*.sln' '*.dll' '*.resx' '*.resources' '*.ruleset' '*.snk' '.editorconfig' 'Directory.Build.*' 'global.json' 'NuGet.config')
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect untracked build inputs.'
}
$dirtyInputs = @($trackedChanges + $buildRelevantUntracked | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$isDirty = $dirtyInputs.Count -gt 0
if ($isDirty -and -not $AllowDirty) {
    throw "Release packaging requires a clean source tree. Dirty build inputs: $($dirtyInputs -join ', ')"
}

$commit = ([string] (& git -C $repositoryRootFull rev-parse --verify HEAD)).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve a full Git commit for provenance.'
}
$branch = ([string] (& git -C $repositoryRootFull rev-parse --abbrev-ref HEAD)).Trim()
$commitTime = ([string] (& git -C $repositoryRootFull show -s --format=%cI $commit)).Trim()

$artifactName = if ($Configuration -eq 'Release') { 'Talky.dll' } else { 'Talky.EXILED.dll' }
$expectedAssemblyName = if ($Configuration -eq 'Release') { 'Talky' } else { 'Talky.EXILED' }
$artifactPath = Join-Path $repositoryRootFull "Talky\bin\$Configuration\$artifactName"
$pdbName = [System.IO.Path]::ChangeExtension($artifactName, '.pdb')
$pdbPath = Join-Path ([System.IO.Path]::GetDirectoryName($artifactPath)) $pdbName

$solutionPath = Join-Path $repositoryRootFull 'Talky.sln'
$cscDirectory = [System.IO.Path]::GetDirectoryName($cscFullPath)
$cscFileName = [System.IO.Path]::GetFileName($cscFullPath)
$buildArguments = @(
    $solutionPath,
    '/t:Rebuild',
    "/p:Configuration=$Configuration",
    '/p:Platform=x64',
    '/p:DeployPlugin=false',
    "/p:ScpSlManagedDir=$ScpSlManagedDir",
    "/p:ExiledDependenciesDir=$ExiledDependenciesDir",
    "/p:CscToolPath=$cscDirectory",
    "/p:CscToolExe=$cscFileName",
    '/m:1',
    '/v:minimal',
    '/nologo'
)
$invokePinnedRebuild = {
    & $msbuildFullPath @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Pinned-toolchain $Configuration rebuild failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "Pinned-toolchain rebuild did not produce '$artifactPath'."
    }
}

& $invokePinnedRebuild
$firstBuildArtifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
$firstBuildPdbHash = $null
if ($IncludePdb) {
    if (-not (Test-Path -LiteralPath $pdbPath -PathType Leaf)) {
        throw "PDB inclusion was requested but the first rebuild did not produce '$pdbPath'."
    }
    $firstBuildPdbHash = (Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash
}

& $invokePinnedRebuild
$secondBuildArtifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
if (-not [string]::Equals($firstBuildArtifactHash, $secondBuildArtifactHash, [System.StringComparison]::Ordinal)) {
    throw "Deterministic rebuild FAILED for ${artifactName}: first '$firstBuildArtifactHash', second '$secondBuildArtifactHash'."
}
$secondBuildPdbHash = $null
if ($IncludePdb) {
    if (-not (Test-Path -LiteralPath $pdbPath -PathType Leaf)) {
        throw "PDB inclusion was requested but the second rebuild did not produce '$pdbPath'."
    }
    $secondBuildPdbHash = (Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash
    if (-not [string]::Equals($firstBuildPdbHash, $secondBuildPdbHash, [System.StringComparison]::Ordinal)) {
        throw "Deterministic rebuild FAILED for ${pdbName}: first '$firstBuildPdbHash', second '$secondBuildPdbHash'."
    }
}
Write-Host "Deterministic rebuild PASS: two pinned-toolchain builds produced identical $artifactName$(if ($IncludePdb) { " and $pdbName" } else { '' })."

$testExecutable = Join-Path $repositoryRootFull "Talky.Tests\bin\$Configuration\Talky.Tests.exe"
if (-not (Test-Path -LiteralPath $testExecutable -PathType Leaf)) {
    throw "Lane test executable is missing after rebuild: '$testExecutable'."
}
$testOutput = @(& $testExecutable 2>&1)
$testExitCode = $LASTEXITCODE
$testOutput | ForEach-Object { Write-Host $_ }
if ($testExitCode -ne 0) {
    throw "$Configuration deterministic tests failed with exit code $testExitCode."
}
$assertionMatch = [regex]::Match(($testOutput -join [Environment]::NewLine), 'PASS:\s+(\d+)\s+deterministic assertions\.')
if (-not $assertionMatch.Success) {
    throw 'Lane tests exited successfully but did not emit the expected deterministic assertion summary.'
}
$deterministicAssertionCount = [int] $assertionMatch.Groups[1].Value

& (Join-Path $repositoryRootFull 'build\test-artifact.ps1') -Configuration $Configuration -ScpSlManagedDir $ScpSlManagedDir -ExiledDependenciesDir $ExiledDependenciesDir -RepositoryRoot $repositoryRootFull

$embeddedModules = @(
    Get-BuiltModuleRecord -Path (Join-Path $repositoryRootFull "Talky.Core\bin\$Configuration\Talky.Core.dll") -EmbeddedResource 'Talky.Core.dll'
    Get-BuiltModuleRecord -Path (Join-Path $repositoryRootFull "Talky.Exiled\bin\$Configuration\Talky.Exiled.Shim.dll") -EmbeddedResource 'Talky.Exiled.Shim.dll.bin'
)

$artifactInfo = Get-Item -LiteralPath $artifactPath
$artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
$assemblyIdentity = [System.Reflection.AssemblyName]::GetAssemblyName($artifactPath)
$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($artifactPath)
$informationalVersion = [string] $fileVersionInfo.ProductVersion
if (-not [string]::Equals([string] $assemblyIdentity.Name, $expectedAssemblyName, [System.StringComparison]::Ordinal)) {
    throw "Artifact assembly name '$($assemblyIdentity.Name)' does not match the $Configuration lane ('$expectedAssemblyName')."
}
if (-not $informationalVersion.Contains("git.$commit", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact informational version '$informationalVersion' does not contain Git commit '$commit'."
}

$laneRoot = Join-Path $outputRootFull $Configuration.ToLowerInvariant()
$packageBaseName = "Talky-$($Configuration.ToLowerInvariant())-$($commit.Substring(0, 12))-$($artifactHash.Substring(0, 12).ToLowerInvariant())"
New-Item -ItemType Directory -Path $laneRoot -Force | Out-Null
$workingDirectory = Join-Path $laneRoot (".$packageBaseName.incomplete-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workingDirectory | Out-Null

$stagedArtifactPath = Join-Path $workingDirectory $artifactName
Copy-Item -LiteralPath $artifactPath -Destination $stagedArtifactPath
$hashFileName = "$artifactName.sha256"
[System.IO.File]::WriteAllText(
    (Join-Path $workingDirectory $hashFileName),
    "$artifactHash  $artifactName" + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$pdbRecord = $null
if ($IncludePdb) {
    if (-not (Test-Path -LiteralPath $pdbPath -PathType Leaf)) {
        throw "PDB inclusion was requested but '$pdbPath' does not exist."
    }

    $stagedPdbPath = Join-Path $workingDirectory $pdbName
    Copy-Item -LiteralPath $pdbPath -Destination $stagedPdbPath
    $pdbInfo = Get-Item -LiteralPath $pdbPath
    $pdbRecord = [ordered]@{
        name = $pdbName
        sha256 = (Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash
        size = $pdbInfo.Length
    }
}

$advisoryReportName = 'advisory-scan.json'
$advisoryReportPath = Join-Path $workingDirectory $advisoryReportName
try {
    & (Join-Path $repositoryRootFull 'build\scan-advisories.ps1') -ManifestPath $manifestPath -OutputPath $advisoryReportPath
}
catch {
    if (Test-Path -LiteralPath $advisoryReportPath -PathType Leaf) {
        Write-Warning "Release packaging stopped; the fail-closed advisory report is preserved at '$advisoryReportPath'."
    }
    throw
}
$advisoryReport = Get-Content -LiteralPath $advisoryReportPath -Raw | ConvertFrom-Json -Depth 64
if (-not [bool] $advisoryReport.performed -or
    -not [bool] $advisoryReport.advisoryFindingsClean -or
    -not [bool] $advisoryReport.componentCoverage.complete -or
    -not [bool] $advisoryReport.clean -or
    -not [bool] $advisoryReport.releaseEligible) {
    throw 'Advisory scanner did not attest complete per-binary coverage and a performed, clean, release-eligible result.'
}
$advisoryReportHash = (Get-FileHash -LiteralPath $advisoryReportPath -Algorithm SHA256).Hash
$stageName = "$packageBaseName-$($advisoryReportHash.Substring(0, 12).ToLowerInvariant())"
$stageDirectory = Join-Path $laneRoot $stageName
if (Test-Path -LiteralPath $stageDirectory) {
    throw "Package staging directory already exists and will not be overwritten: '$stageDirectory'."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$dotnetVersion = ([string] (& dotnet --version)).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to capture the .NET SDK version for provenance.'
}

$provenance = [ordered]@{
    schemaVersion = 1
    subject = 'Talky SCP:SL plugin'
    source = [ordered]@{
        repository = 'Talky'
        commit = $commit
        branch = $branch
        commitTimeUtc = $commitTime
        dirty = $isDirty
        dirtyBuildInputs = @($dirtyInputs)
    }
    build = [ordered]@{
        configuration = $Configuration
        platform = 'x64'
        solution = 'Talky.sln'
        targetFramework = 'net48'
        deterministicRequested = $true
        continuousIntegrationBuild = $true
        unsafeCodeAllowed = $false
        deploymentEnabled = $false
        rebuildPerformed = $true
        testsPassed = $true
        deterministicAssertionCount = $deterministicAssertionCount
        artifactValidationPassed = $true
        reproducibility = [ordered]@{
            pinnedToolchainRebuilds = 2
            artifactByteIdentical = $true
            artifactSha256 = $secondBuildArtifactHash
            pdbIncluded = [bool] $IncludePdb
            pdbByteIdentical = if ($IncludePdb) { $true } else { $null }
            pdbSha256 = $secondBuildPdbHash
        }
        toolchain = [ordered]@{
            msbuild = Get-ToolFileRecord $msbuildFullPath
            msbuildTopLevel = Get-TopLevelDirectoryIdentity ([System.IO.Path]::GetDirectoryName($msbuildFullPath))
            csharpCompiler = Get-ToolFileRecord $cscFullPath
            roslynTopLevel = Get-TopLevelDirectoryIdentity ([System.IO.Path]::GetDirectoryName($cscFullPath))
        }
    }
    verification = [ordered]@{
        powershell = [string] $PSVersionTable.PSVersion
        dotnetSdkForNuGetSignature = $dotnetVersion
        advisoryScan = [ordered]@{
            performed = $true
            clean = $true
            componentCoverageComplete = $true
            releaseEligible = $true
            report = $advisoryReportName
            reportSha256 = $advisoryReportHash
            scanner = [string] $advisoryReport.scanner.name
            apiVersion = [string] $advisoryReport.scanner.apiVersion
            queriedAtUtc = [string] $advisoryReport.scanner.queriedAtUtc
        }
    }
    artifact = [ordered]@{
        name = $artifactName
        sha256 = $artifactHash
        size = $artifactInfo.Length
        assemblyName = [string] $assemblyIdentity.Name
        assemblyVersion = [string] $assemblyIdentity.Version
        informationalVersion = $informationalVersion
        mvid = Get-PortableMvid $artifactPath
        pdb = $pdbRecord
        embeddedModules = $embeddedModules
    }
    inputs = [ordered]@{
        referenceManifest = 'build/reference-manifest.json'
        referenceManifestSha256 = $manifestHash
        runtimeVersion = [string] $manifest.runtimeVersion
        packageSignatureVerified = $true
        packageOriginsVerified = $true
    }
}
Write-JsonFile -Path (Join-Path $workingDirectory 'provenance.json') -Value $provenance

$components = [System.Collections.Generic.List[object]]::new()
foreach ($package in @($manifest.packages)) {
    $components.Add([ordered]@{
            type = 'library'
            'bom-ref' = [string] $package.packageUrl
            group = 'NuGet'
            name = [string] $package.id
            version = [string] $package.version
            hashes = @([ordered]@{ alg = 'SHA-256'; content = [string] $package.sha256 })
            purl = [string] $package.packageUrl
            properties = @(
                [ordered]@{ name = 'talky:signaturePolicy'; value = [string] $package.signaturePolicy },
                [ordered]@{ name = 'talky:packagePath'; value = [string] $package.nupkg }
            )
        })
}
foreach ($reference in @($manifest.references)) {
    $component = [ordered]@{
            type = 'library'
            'bom-ref' = "assembly:$($reference.source):$($reference.id):$($reference.sha256)"
            name = [string] $reference.assemblyName
            version = [string] $reference.assemblyVersion
            hashes = @([ordered]@{ alg = 'SHA-256'; content = [string] $reference.sha256 })
            properties = @(
                [ordered]@{ name = 'talky:referenceId'; value = [string] $reference.id },
                [ordered]@{ name = 'talky:source'; value = [string] $reference.source },
                [ordered]@{ name = 'talky:path'; value = [string] $reference.path },
                [ordered]@{ name = 'talky:mvid'; value = [string] $reference.mvid }
            )
        }
    if ($null -ne $reference.PSObject.Properties['packageUrl']) {
        $component['purl'] = [string] $reference.packageUrl
    }
    $components.Add($component)
}
foreach ($module in $embeddedModules) {
    $components.Add([ordered]@{
            type = 'library'
            'bom-ref' = "embedded:$($module.assemblyName):$($module.sha256)"
            group = 'Talky'
            name = [string] $module.assemblyName
            version = [string] $module.assemblyVersion
            hashes = @([ordered]@{ alg = 'SHA-256'; content = [string] $module.sha256 })
            properties = @(
                [ordered]@{ name = 'talky:embeddedResource'; value = [string] $module.embeddedResource },
                [ordered]@{ name = 'talky:mvid'; value = [string] $module.mvid },
                [ordered]@{ name = 'talky:origin'; value = [string] $module.origin }
            )
        })
}

$uuidHex = $artifactHash.Substring(0, 32).ToLowerInvariant()
$uuidHex = $uuidHex.Substring(0, 12) + '5' + $uuidHex.Substring(13)
$variantNibble = ([Convert]::ToInt32($uuidHex.Substring(16, 1), 16) -band 3) -bor 8
$uuidHex = $uuidHex.Substring(0, 16) + $variantNibble.ToString('x') + $uuidHex.Substring(17)
$uuidText = '{0}-{1}-{2}-{3}-{4}' -f $uuidHex.Substring(0, 8), $uuidHex.Substring(8, 4), $uuidHex.Substring(12, 4), $uuidHex.Substring(16, 4), $uuidHex.Substring(20, 12)
$rootBomRef = "talky:${Configuration}:$artifactHash"
$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    serialNumber = "urn:uuid:$uuidText"
    version = 1
    metadata = [ordered]@{
        timestamp = $commitTime
        component = [ordered]@{
            type = 'application'
            'bom-ref' = $rootBomRef
            name = 'Talky'
            version = $informationalVersion
            hashes = @([ordered]@{ alg = 'SHA-256'; content = $artifactHash })
        }
        properties = @(
            [ordered]@{ name = 'talky:configuration'; value = $Configuration },
            [ordered]@{ name = 'talky:gitCommit'; value = $commit },
            [ordered]@{ name = 'talky:referenceManifestSha256'; value = $manifestHash }
        )
    }
    components = @($components)
    dependencies = @([ordered]@{
            ref = $rootBomRef
            dependsOn = @($components | ForEach-Object { $_['bom-ref'] })
        })
}
Write-JsonFile -Path (Join-Path $workingDirectory 'bom.cdx.json') -Value $sbom

$validationArguments = @{
    PackageDirectory = $workingDirectory
    Configuration = $Configuration
    RepositoryRoot = $repositoryRootFull
}
if ($AllowDirty) {
    $validationArguments.AllowDirtyProvenance = $true
}
& (Join-Path $repositoryRootFull 'build\validate-package.ps1') @validationArguments

Move-Item -LiteralPath $workingDirectory -Destination $stageDirectory
Write-Host "Package staging PASS: $stageDirectory"
