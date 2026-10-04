[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('Release', 'EXILED')]
    [string] $Configuration,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [switch] $AllowDirtyProvenance
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [AllowEmptyString()][string] $Expected,
        [AllowEmptyString()][string] $Actual
    )

    if (-not [string]::Equals($Expected, $Actual, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label mismatch: expected '$Expected', found '$Actual'."
    }
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

$repositoryRootFull = [System.IO.Path]::GetFullPath($RepositoryRoot)
$packageDirectoryFull = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageDirectoryFull -PathType Container)) {
    throw "Package directory does not exist: '$packageDirectoryFull'."
}

$expectedArtifactName = 'Talky.dll'
$expectedAssemblyName = 'Talky'
$provenancePath = Join-Path $packageDirectoryFull 'provenance.json'
$sbomPath = Join-Path $packageDirectoryFull 'bom.cdx.json'
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf) -or -not (Test-Path -LiteralPath $sbomPath -PathType Leaf)) {
    throw 'Package must contain provenance.json and bom.cdx.json.'
}

$provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -Depth 32
if ([int] $provenance.schemaVersion -ne 1) {
    throw "Unsupported provenance schema '$($provenance.schemaVersion)'."
}
Assert-Equal -Label 'provenance configuration' -Expected $Configuration -Actual ([string] $provenance.build.configuration)
Assert-Equal -Label 'canonical build configuration' -Expected 'Release' -Actual ([string] $provenance.build.canonicalConfiguration)
Assert-Equal -Label 'provenance platform' -Expected 'x64' -Actual ([string] $provenance.build.platform)
Assert-Equal -Label 'provenance solution' -Expected 'Talky.sln' -Actual ([string] $provenance.build.solution)
Assert-Equal -Label 'provenance artifact name' -Expected $expectedArtifactName -Actual ([string] $provenance.artifact.name)
Assert-Equal -Label 'provenance assembly name' -Expected $expectedAssemblyName -Actual ([string] $provenance.artifact.assemblyName)
Assert-Equal -Label 'binding AssemblyVersion' -Expected '1.0.0.0' -Actual ([string] $provenance.artifact.assemblyVersion)
if (-not [bool] $provenance.build.deterministicRequested -or
    -not [bool] $provenance.build.continuousIntegrationBuild -or
    [bool] $provenance.build.unsafeCodeAllowed -or
    [bool] $provenance.build.deploymentEnabled -or
    -not [bool] $provenance.build.rebuildPerformed -or
    -not [bool] $provenance.build.testsPassed -or
    -not [bool] $provenance.build.artifactValidationPassed -or
    [int] $provenance.build.deterministicAssertionCount -le 0 -or
    [int] $provenance.build.harmonyPatchAssertionCount -le 0 -or
    [int] $provenance.build.reproducibility.pinnedToolchainRebuilds -ne 2 -or
    -not [bool] $provenance.build.reproducibility.artifactByteIdentical -or
    -not [bool] $provenance.build.reproducibility.canonicalLaneCopyVerified) {
    throw 'Provenance must attest deterministic, safe, non-deploying rebuild and passing tests.'
}

if ([bool] $provenance.source.dirty -and -not $AllowDirtyProvenance) {
    throw 'Deployment-grade packages require clean Git provenance.'
}
if (-not [bool] $provenance.inputs.packageSignatureVerified) {
    throw 'Deployment-grade packages require verified NuGet signatures.'
}
if (-not [bool] $provenance.inputs.packageOriginsVerified) {
    throw 'Deployment-grade packages require live byte-identical signed NuGet origin verification for every direct reference purl.'
}
$harmonyPatchRuntime = $provenance.verification.harmonyPatchRuntime
if ($null -eq $harmonyPatchRuntime -or
    [string] $harmonyPatchRuntime.fileName -ne 'Assembly-CSharp.dll' -or
    [string] $harmonyPatchRuntime.sha256 -notmatch '^[0-9A-F]{64}$' -or
    [long] $harmonyPatchRuntime.size -le 0 -or
    [string] $harmonyPatchRuntime.mvid -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$' -or
    [string] $harmonyPatchRuntime.patchTestReportedSha256 -notmatch '^[0-9A-F]{64}$' -or
    [long] $harmonyPatchRuntime.patchTestReportedSize -le 0 -or
    [string] $harmonyPatchRuntime.patchTestReportedMvid -notmatch '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$' -or
    -not [bool] $harmonyPatchRuntime.unchangedDuringPatchTest) {
    throw 'Package provenance does not attest the supplied runtime used by Harmony patch tests.'
}
Assert-Equal -Label 'Harmony patch-test reported runtime SHA-256' -Expected ([string] $harmonyPatchRuntime.sha256) -Actual ([string] $harmonyPatchRuntime.patchTestReportedSha256)
Assert-Equal -Label 'Harmony patch-test reported runtime size' -Expected ([string] $harmonyPatchRuntime.size) -Actual ([string] $harmonyPatchRuntime.patchTestReportedSize)
Assert-Equal -Label 'Harmony patch-test reported runtime MVID' -Expected ([string] $harmonyPatchRuntime.mvid) -Actual ([string] $harmonyPatchRuntime.patchTestReportedMvid)
if (-not [bool] $provenance.verification.advisoryScan.performed -or
    -not [bool] $provenance.verification.advisoryScan.clean -or
    -not [bool] $provenance.verification.advisoryScan.componentCoverageComplete -or
    -not [bool] $provenance.verification.advisoryScan.releaseEligible) {
    throw 'Deployment-grade packages require a performed, clean, complete per-binary advisory scan.'
}
Assert-Equal -Label 'advisory report name' -Expected 'advisory-scan.json' -Actual ([string] $provenance.verification.advisoryScan.report)

$commit = [string] $provenance.source.commit
if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw "Provenance commit '$commit' is not a full Git object ID."
}
$informationalVersion = [string] $provenance.artifact.informationalVersion
if (-not $informationalVersion.Contains("git.$commit", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Informational version '$informationalVersion' does not bind the artifact to provenance commit '$commit'."
}

$expectedFiles = [System.Collections.Generic.List[string]]::new()
$expectedFiles.Add($expectedArtifactName)
$expectedFiles.Add("$expectedArtifactName.sha256")
$expectedFiles.Add('provenance.json')
$expectedFiles.Add('bom.cdx.json')
$expectedFiles.Add('advisory-scan.json')
if ($null -ne $provenance.artifact.pdb) {
    $expectedFiles.Add([string] $provenance.artifact.pdb.name)
}

$packageDirectoryInfo = Get-Item -LiteralPath $packageDirectoryFull -Force
if (($packageDirectoryInfo.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Package directory must not be a reparse point.'
}
$packageItems = @(Get-ChildItem -LiteralPath $packageDirectoryFull -Force)
$reparseItems = @($packageItems | Where-Object {
        ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
    })
if ($reparseItems.Count -ne 0) {
    throw "Package contains reparse-point entries: $($reparseItems.Name -join ', ')."
}
$subdirectories = @($packageItems | Where-Object { $_.PSIsContainer })
if ($subdirectories.Count -ne 0) {
    throw "Package contains unexpected subdirectories: $($subdirectories.Name -join ', ')."
}
$actualFiles = @($packageItems | Where-Object { -not $_.PSIsContainer } | Select-Object -ExpandProperty Name)
$actualSet = @($actualFiles | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
$expectedSet = @($expectedFiles | ForEach-Object { $_.ToUpperInvariant() } | Sort-Object -Unique)
if (($actualSet -join "`n") -ne ($expectedSet -join "`n")) {
    $unknown = @($actualSet | Where-Object { $_ -notin $expectedSet })
    $missing = @($expectedSet | Where-Object { $_ -notin $actualSet })
    throw "Package allowlist mismatch. Unknown: [$($unknown -join ', ')]. Missing: [$($missing -join ', ')]."
}

$artifactPath = Join-Path $packageDirectoryFull $expectedArtifactName
$artifactInfo = Get-Item -LiteralPath $artifactPath
$artifactHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
Assert-Equal -Label 'artifact SHA-256' -Expected ([string] $provenance.artifact.sha256) -Actual $artifactHash
Assert-Equal -Label 'reproducibility artifact SHA-256' -Expected $artifactHash -Actual ([string] $provenance.build.reproducibility.artifactSha256)
Assert-Equal -Label 'artifact size' -Expected ([string] $provenance.artifact.size) -Actual ([string] $artifactInfo.Length)
$assemblyIdentity = [System.Reflection.AssemblyName]::GetAssemblyName($artifactPath)
$fileVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($artifactPath)
Assert-Equal -Label 'artifact assembly name' -Expected ([string] $provenance.artifact.assemblyName) -Actual ([string] $assemblyIdentity.Name)
Assert-Equal -Label 'artifact assembly version' -Expected ([string] $provenance.artifact.assemblyVersion) -Actual ([string] $assemblyIdentity.Version)
Assert-Equal -Label 'artifact informational version' -Expected $informationalVersion -Actual ([string] $fileVersionInfo.ProductVersion)
Assert-Equal -Label 'artifact MVID' -Expected ([string] $provenance.artifact.mvid) -Actual (Get-PortableMvid $artifactPath)

$embeddedModules = @($provenance.artifact.embeddedModules)
if ($embeddedModules.Count -ne 2) {
    throw "Provenance must record exactly two embedded managed modules; found $($embeddedModules.Count)."
}
$expectedEmbeddedModules = @{
    'Talky.Core.dll' = [pscustomobject]@{ AssemblyName = 'Talky.Core'; FileName = 'Talky.Core.dll' }
    'Talky.Exiled.Shim.dll.bin' = [pscustomobject]@{ AssemblyName = 'Talky.Exiled.Shim'; FileName = 'Talky.Exiled.Shim.dll' }
}
foreach ($module in $embeddedModules) {
    $resourceName = [string] $module.embeddedResource
    if (-not $expectedEmbeddedModules.ContainsKey($resourceName)) {
        throw "Unexpected embedded module resource '$resourceName' in provenance."
    }
    $expectedModule = $expectedEmbeddedModules[$resourceName]
    Assert-Equal -Label "$resourceName assembly name" -Expected $expectedModule.AssemblyName -Actual ([string] $module.assemblyName)
    Assert-Equal -Label "$resourceName file name" -Expected $expectedModule.FileName -Actual ([string] $module.fileName)
    Assert-Equal -Label "$resourceName origin" -Expected 'built-from-repository-source' -Actual ([string] $module.origin)
    if ([string] $module.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or [long] $module.size -le 0) {
        throw "Embedded module '$resourceName' has invalid hash or size metadata."
    }
    [void] [guid]::Parse([string] $module.mvid)
}
if (@($embeddedModules | Group-Object embeddedResource | Where-Object Count -ne 1).Count -ne 0) {
    throw 'Embedded module resource records must be unique.'
}

$hashLine = (Get-Content -LiteralPath (Join-Path $packageDirectoryFull "$expectedArtifactName.sha256") -Raw).Trim()
if ($hashLine -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
    throw 'Artifact checksum file has an invalid format.'
}
Assert-Equal -Label 'checksum file hash' -Expected $artifactHash -Actual $Matches[1]
Assert-Equal -Label 'checksum file name' -Expected $expectedArtifactName -Actual $Matches[2]

if ($null -ne $provenance.artifact.pdb) {
    if (-not [bool] $provenance.build.reproducibility.pdbIncluded -or
        -not [bool] $provenance.build.reproducibility.pdbByteIdentical) {
        throw 'PDB provenance must attest inclusion and byte-identical pinned-toolchain rebuilds.'
    }
    $pdbPath = Join-Path $packageDirectoryFull ([string] $provenance.artifact.pdb.name)
    $pdbInfo = Get-Item -LiteralPath $pdbPath
    Assert-Equal -Label 'PDB SHA-256' -Expected ([string] $provenance.artifact.pdb.sha256) -Actual ((Get-FileHash -LiteralPath $pdbPath -Algorithm SHA256).Hash)
    Assert-Equal -Label 'reproducibility PDB SHA-256' -Expected ([string] $provenance.artifact.pdb.sha256) -Actual ([string] $provenance.build.reproducibility.pdbSha256)
    Assert-Equal -Label 'PDB size' -Expected ([string] $provenance.artifact.pdb.size) -Actual ([string] $pdbInfo.Length)
}
elseif ([bool] $provenance.build.reproducibility.pdbIncluded) {
    throw 'Reproducibility provenance claims a PDB that is absent from the artifact record.'
}

$manifestPath = Join-Path $repositoryRootFull 'build\reference-manifest.json'
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
Assert-Equal -Label 'reference manifest SHA-256' -Expected $manifestHash -Actual ([string] $provenance.inputs.referenceManifestSha256)
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
foreach ($toolName in @('msbuild', 'csharpCompiler')) {
    $expectedTool = $manifest.toolchain.$toolName
    $actualTool = $provenance.build.toolchain.$toolName
    Assert-Equal -Label "$toolName file name" -Expected ([string] $expectedTool.fileName) -Actual ([string] $actualTool.fileName)
    Assert-Equal -Label "$toolName file version" -Expected ([string] $expectedTool.fileVersion) -Actual ([string] $actualTool.fileVersion)
    Assert-Equal -Label "$toolName product version" -Expected ([string] $expectedTool.productVersion) -Actual ([string] $actualTool.productVersion)
    Assert-Equal -Label "$toolName SHA-256" -Expected ([string] $expectedTool.sha256) -Actual ([string] $actualTool.sha256)
    Assert-Equal -Label "$toolName size" -Expected ([string] $expectedTool.size) -Actual ([string] $actualTool.size)
}
Assert-Equal -Label 'Roslyn provenance file count' -Expected ([string] $manifest.toolchain.roslynTopLevel.fileCount) -Actual ([string] $provenance.build.toolchain.roslynTopLevel.fileCount)
Assert-Equal -Label 'Roslyn provenance tree SHA-256' -Expected ([string] $manifest.toolchain.roslynTopLevel.treeSha256) -Actual ([string] $provenance.build.toolchain.roslynTopLevel.treeSha256)
Assert-Equal -Label 'MSBuild provenance file count' -Expected ([string] $manifest.toolchain.msbuildTopLevel.fileCount) -Actual ([string] $provenance.build.toolchain.msbuildTopLevel.fileCount)
Assert-Equal -Label 'MSBuild provenance tree SHA-256' -Expected ([string] $manifest.toolchain.msbuildTopLevel.treeSha256) -Actual ([string] $provenance.build.toolchain.msbuildTopLevel.treeSha256)

$advisoryReportPath = Join-Path $packageDirectoryFull 'advisory-scan.json'
$advisoryReportHash = (Get-FileHash -LiteralPath $advisoryReportPath -Algorithm SHA256).Hash
Assert-Equal -Label 'advisory report SHA-256' -Expected ([string] $provenance.verification.advisoryScan.reportSha256) -Actual $advisoryReportHash
$advisoryReport = Get-Content -LiteralPath $advisoryReportPath -Raw | ConvertFrom-Json -Depth 64
if ([int] $advisoryReport.schemaVersion -ne 2 -or -not [bool] $advisoryReport.performed) {
    throw 'Advisory report must use schema 2 and attest a performed scan.'
}
Assert-Equal -Label 'advisory scanner' -Expected 'GitHub Global Security Advisories' -Actual ([string] $advisoryReport.scanner.name)
Assert-Equal -Label 'advisory endpoint' -Expected 'https://api.github.com/advisories' -Actual ([string] $advisoryReport.scanner.endpoint)
Assert-Equal -Label 'advisory API version' -Expected '2022-11-28' -Actual ([string] $advisoryReport.scanner.apiVersion)
if ([int] $advisoryReport.scanner.requestCount -lt 2) {
    throw 'Advisory report did not perform both high and critical API queries.'
}
Assert-Equal -Label 'provenance advisory scanner' -Expected ([string] $advisoryReport.scanner.name) -Actual ([string] $provenance.verification.advisoryScan.scanner)
Assert-Equal -Label 'provenance advisory API version' -Expected ([string] $advisoryReport.scanner.apiVersion) -Actual ([string] $provenance.verification.advisoryScan.apiVersion)
Assert-Equal -Label 'provenance advisory scan time' -Expected ([string] $advisoryReport.scanner.queriedAtUtc) -Actual ([string] $provenance.verification.advisoryScan.queriedAtUtc)
$advisoryScanTime = [DateTimeOffset]::Parse([string] $advisoryReport.scanner.queriedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
$maximumAdvisoryAgeHours = [int] $manifest.advisoryPolicy.maxReportAgeHours
if ($maximumAdvisoryAgeHours -le 0) {
    throw 'Reference manifest advisory max-age policy must be positive.'
}
Assert-Equal -Label 'advisory maximum report age' -Expected ([string] $maximumAdvisoryAgeHours) -Actual ([string] $advisoryReport.policy.maxReportAgeHours)
$now = [DateTimeOffset]::UtcNow
if ($advisoryScanTime -gt $now.AddMinutes(5) -or $advisoryScanTime -lt $now.AddHours(-$maximumAdvisoryAgeHours)) {
    throw "Advisory report is outside the permitted freshness window of $maximumAdvisoryAgeHours hour(s)."
}
Assert-Equal -Label 'advisory ecosystem policy' -Expected 'nuget' -Actual ([string] $advisoryReport.policy.ecosystem)
Assert-Equal -Label 'advisory type policy' -Expected 'reviewed' -Actual ([string] $advisoryReport.policy.advisoryType)
if ([bool] $advisoryReport.policy.includeWithdrawn) {
    throw 'Advisory report policy must exclude withdrawn advisories.'
}
if (-not [bool] $advisoryReport.policy.requireIndependentCoverageForEveryReference) {
    throw 'Advisory report policy must require independent coverage for every exact binary reference.'
}
$severitySet = @($advisoryReport.policy.severities | ForEach-Object { ([string] $_).ToLowerInvariant() } | Sort-Object -Unique)
if (($severitySet -join ',') -ne 'critical,high') {
    throw "Advisory report policy must cover exactly high and critical severities; found '$($severitySet -join ',')'."
}
if (@($advisoryReport.activeHighCriticalAdvisories).Count -ne 0) {
    throw 'Advisory report contains active high/critical findings.'
}
if (-not [bool] $advisoryReport.advisoryFindingsClean) {
    throw 'Advisory report does not attest a clean high/critical finding result.'
}

$expectedPurls = @(
    @($manifest.packages | ForEach-Object { [string] $_.packageUrl }) +
    @($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['packageUrl'] } | ForEach-Object { [string] $_.packageUrl }) |
    ForEach-Object { $_.ToUpperInvariant() } |
    Sort-Object -Unique
)
$directMappedReferences = @($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['packageUrl'] })
$originEvidenceReferences = @($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['originEvidence'] })
if ($directMappedReferences.Count -ne $originEvidenceReferences.Count) {
    throw 'Every direct reference purl must have committed byte-origin evidence, and evidence must not exist without a purl.'
}
foreach ($reference in $directMappedReferences) {
    if ($null -eq $reference.PSObject.Properties['originEvidence'] -or
        -not [string]::Equals([string] $reference.packageUrl, [string] $reference.originEvidence.packageUrl, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string] $reference.sha256, [string] $reference.originEvidence.assetSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
        [long] $reference.size -ne [long] $reference.originEvidence.assetSize) {
        throw "Reference '$($reference.id)' has invalid committed byte-origin evidence."
    }
}
$actualPurls = @($advisoryReport.queriedPackages | ForEach-Object { ([string] $_.purl).ToUpperInvariant() } | Sort-Object -Unique)
if (($actualPurls -join "`n") -ne ($expectedPurls -join "`n")) {
    throw 'Advisory report did not query every unique NuGet purl from the reference manifest.'
}

$expectedUnmapped = [System.Collections.Generic.List[string]]::new()
foreach ($reference in @($manifest.references)) {
    if ($null -ne $reference.PSObject.Properties['packageUrl']) {
        continue
    }
    $containedInPurl = $null
    if ([string] $reference.source -eq 'repository') {
        $referencePath = ([string] $reference.path).Replace('\', '/')
        foreach ($package in @($manifest.packages)) {
            $packagePath = ([string] $package.path).Replace('\', '/').TrimEnd('/') + '/'
            if ($referencePath.StartsWith($packagePath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $containedInPurl = [string] $package.packageUrl
                break
            }
        }
    }
    $status = if ($null -eq $containedInPurl) { 'unresolved-no-public-ecosystem-mapping' } else { 'unresolved-no-direct-component-purl' }
    $expectedUnmapped.Add("$($reference.id)|$($reference.sha256)|$($reference.source)|$status|$containedInPurl".ToUpperInvariant())
}
$actualUnmapped = @($advisoryReport.exactHashReferencesWithoutDirectPurl | ForEach-Object {
        "$($_.id)|$($_.sha256)|$($_.source)|$($_.mappingStatus)|$($_.containedInPurl)".ToUpperInvariant()
    } | Sort-Object -Unique)
$expectedUnmappedSet = @($expectedUnmapped | Sort-Object -Unique)
if (($actualUnmapped -join "`n") -ne ($expectedUnmappedSet -join "`n")) {
    throw 'Advisory report did not explicitly classify every exact-hash reference without a direct purl.'
}
Assert-Equal -Label 'advisory manifest reference count' -Expected ([string] @($manifest.references).Count) -Actual ([string] $advisoryReport.componentCoverage.manifestReferenceCount)
Assert-Equal -Label 'advisory direct-purl reference count' -Expected ([string] (@($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['packageUrl'] }).Count)) -Actual ([string] $advisoryReport.componentCoverage.directPurlReferenceCount)
Assert-Equal -Label 'advisory origin-evidence reference count' -Expected ([string] (@($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['originEvidence'] }).Count)) -Actual ([string] $advisoryReport.componentCoverage.originEvidenceReferenceCount)
Assert-Equal -Label 'advisory unresolved reference count' -Expected ([string] $expectedUnmappedSet.Count) -Actual ([string] $advisoryReport.componentCoverage.unresolvedReferenceCount)
Assert-Equal -Label 'advisory queried purl count' -Expected ([string] $expectedPurls.Count) -Actual ([string] $advisoryReport.componentCoverage.queriedUniquePurlCount)
if ($expectedUnmappedSet.Count -ne 0 -or
    -not [bool] $advisoryReport.componentCoverage.complete -or
    -not [bool] $advisoryReport.clean -or
    -not [bool] $advisoryReport.releaseEligible) {
    throw "Deployment-grade package is blocked: $($expectedUnmappedSet.Count) exact binary reference(s) lack independent advisory coverage."
}

$sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json -Depth 32
Assert-Equal -Label 'SBOM format' -Expected 'CycloneDX' -Actual ([string] $sbom.bomFormat)
Assert-Equal -Label 'SBOM specification' -Expected '1.6' -Actual ([string] $sbom.specVersion)
Assert-Equal -Label 'SBOM root component' -Expected 'Talky' -Actual ([string] $sbom.metadata.component.name)
Assert-Equal -Label 'SBOM root version' -Expected $informationalVersion -Actual ([string] $sbom.metadata.component.version)
$sbomSha = @($sbom.metadata.component.hashes | Where-Object { $_.alg -eq 'SHA-256' } | Select-Object -First 1)
if ($sbomSha.Count -ne 1) {
    throw 'SBOM root component must contain exactly one SHA-256 hash.'
}
Assert-Equal -Label 'SBOM artifact SHA-256' -Expected $artifactHash -Actual ([string] $sbomSha[0].content)

$expectedBomRefs = [System.Collections.Generic.List[string]]::new()
foreach ($package in @($manifest.packages)) {
    $bomRef = [string] $package.packageUrl
    $expectedBomRefs.Add($bomRef)
    $matches = @($sbom.components | Where-Object { $_.'bom-ref' -eq $bomRef })
    if ($matches.Count -ne 1) {
        throw "SBOM package component '$bomRef' is missing or duplicated."
    }

    Assert-Equal -Label "$($package.id) SBOM version" -Expected ([string] $package.version) -Actual ([string] $matches[0].version)
    Assert-Equal -Label "$($package.id) SBOM package URL" -Expected ([string] $package.packageUrl) -Actual ([string] $matches[0].purl)
    $hashes = @($matches[0].hashes | Where-Object { $_.alg -eq 'SHA-256' })
    if ($hashes.Count -ne 1) {
        throw "SBOM package component '$bomRef' must contain exactly one SHA-256 hash."
    }
    Assert-Equal -Label "$($package.id) SBOM SHA-256" -Expected ([string] $package.sha256) -Actual ([string] $hashes[0].content)
}

foreach ($reference in @($manifest.references)) {
    $bomRef = "assembly:$($reference.source):$($reference.id):$($reference.sha256)"
    $expectedBomRefs.Add($bomRef)
    $matches = @($sbom.components | Where-Object { $_.'bom-ref' -eq $bomRef })
    if ($matches.Count -ne 1) {
        throw "SBOM reference component '$bomRef' is missing or duplicated."
    }

    Assert-Equal -Label "$($reference.id) SBOM version" -Expected ([string] $reference.assemblyVersion) -Actual ([string] $matches[0].version)
    $hashes = @($matches[0].hashes | Where-Object { $_.alg -eq 'SHA-256' })
    if ($hashes.Count -ne 1) {
        throw "SBOM reference component '$bomRef' must contain exactly one SHA-256 hash."
    }
    Assert-Equal -Label "$($reference.id) SBOM SHA-256" -Expected ([string] $reference.sha256) -Actual ([string] $hashes[0].content)
    $mvidProperties = @($matches[0].properties | Where-Object { $_.name -eq 'talky:mvid' })
    if ($mvidProperties.Count -ne 1) {
        throw "SBOM reference component '$bomRef' must contain exactly one MVID property."
    }
    Assert-Equal -Label "$($reference.id) SBOM MVID" -Expected ([string] $reference.mvid) -Actual ([string] $mvidProperties[0].value)
    if ($null -ne $reference.PSObject.Properties['packageUrl']) {
        Assert-Equal -Label "$($reference.id) SBOM package URL" -Expected ([string] $reference.packageUrl) -Actual ([string] $matches[0].purl)
    }
}

foreach ($module in $embeddedModules) {
    $bomRef = "embedded:$($module.assemblyName):$($module.sha256)"
    $expectedBomRefs.Add($bomRef)
    $matches = @($sbom.components | Where-Object { $_.'bom-ref' -eq $bomRef })
    if ($matches.Count -ne 1) {
        throw "SBOM embedded component '$bomRef' is missing or duplicated."
    }
    Assert-Equal -Label "$($module.assemblyName) SBOM version" -Expected ([string] $module.assemblyVersion) -Actual ([string] $matches[0].version)
    $hashes = @($matches[0].hashes | Where-Object { $_.alg -eq 'SHA-256' })
    if ($hashes.Count -ne 1) {
        throw "SBOM embedded component '$bomRef' must contain exactly one SHA-256 hash."
    }
    Assert-Equal -Label "$($module.assemblyName) SBOM SHA-256" -Expected ([string] $module.sha256) -Actual ([string] $hashes[0].content)
    foreach ($propertyName in @('talky:embeddedResource', 'talky:mvid', 'talky:origin')) {
        $properties = @($matches[0].properties | Where-Object { $_.name -eq $propertyName })
        if ($properties.Count -ne 1) {
            throw "SBOM embedded component '$bomRef' must contain exactly one '$propertyName' property."
        }
    }
    $resourceProperty = @($matches[0].properties | Where-Object name -eq 'talky:embeddedResource')[0]
    $mvidProperty = @($matches[0].properties | Where-Object name -eq 'talky:mvid')[0]
    $originProperty = @($matches[0].properties | Where-Object name -eq 'talky:origin')[0]
    Assert-Equal -Label "$($module.assemblyName) SBOM resource" -Expected ([string] $module.embeddedResource) -Actual ([string] $resourceProperty.value)
    Assert-Equal -Label "$($module.assemblyName) SBOM MVID" -Expected ([string] $module.mvid) -Actual ([string] $mvidProperty.value)
    Assert-Equal -Label "$($module.assemblyName) SBOM origin" -Expected ([string] $module.origin) -Actual ([string] $originProperty.value)
}

$actualBomRefs = @($sbom.components | ForEach-Object { [string] $_.'bom-ref' } | Sort-Object -Unique)
$expectedBomRefSet = @($expectedBomRefs | Sort-Object -Unique)
if (($actualBomRefs -join "`n") -ne ($expectedBomRefSet -join "`n")) {
    throw 'SBOM component inventory does not exactly match the pinned reference manifest.'
}

$rootDependencies = @($sbom.dependencies | Where-Object { $_.ref -eq [string] $sbom.metadata.component.'bom-ref' })
if ($rootDependencies.Count -ne 1) {
    throw 'SBOM must contain exactly one dependency entry for the Talky root component.'
}
$actualDependencies = @($rootDependencies[0].dependsOn | ForEach-Object { [string] $_ } | Sort-Object -Unique)
if (($actualDependencies -join "`n") -ne ($expectedBomRefSet -join "`n")) {
    throw 'SBOM root dependencies do not exactly cover the pinned component inventory.'
}

Write-Host "Package validation PASS: $expectedArtifactName and $($actualFiles.Count - 1) metadata/debug artifact(s); no unallowlisted files."
