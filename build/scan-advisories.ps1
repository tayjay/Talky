[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ManifestPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [switch] $AuditOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)] $Value
    )

    $json = $Value | ConvertTo-Json -Depth 64
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function ConvertFrom-NuGetPurl {
    param([Parameter(Mandatory = $true)][string] $Purl)

    $match = [regex]::Match($Purl, '^pkg:nuget/([^@]+)@([^?#]+)$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "Unsupported or unversioned NuGet package URL '$Purl'."
    }

    return [pscustomobject]@{
        Purl = $Purl
        Name = [System.Uri]::UnescapeDataString($match.Groups[1].Value)
        Version = [System.Uri]::UnescapeDataString($match.Groups[2].Value)
    }
}

function Get-NextLink {
    param($Response)

    $linkHeader = [string] $Response.Headers['Link']
    if ($linkHeader -match '<([^>]+)>;\s*rel="next"') {
        $next = [uri] $Matches[1]
        if ($next.Scheme -ne 'https' -or $next.Host -ne 'api.github.com') {
            throw "GitHub advisory pagination returned an untrusted next link '$next'."
        }
        return $next.AbsoluteUri
    }

    return $null
}

$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $manifestFullPath -PathType Leaf)) {
    throw "Reference manifest not found: '$manifestFullPath'."
}
if (Test-Path -LiteralPath $outputFullPath) {
    throw "Advisory report already exists and will not be overwritten: '$outputFullPath'."
}
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFullPath)
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    throw "Advisory report directory must already exist: '$outputDirectory'."
}

$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json -Depth 64
$maximumReportAgeHours = [int] $manifest.advisoryPolicy.maxReportAgeHours
if ($maximumReportAgeHours -le 0) {
    throw 'Manifest advisory max-report-age policy must be positive.'
}
$purlSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($package in @($manifest.packages)) {
    if ($null -eq $package.PSObject.Properties['packageUrl']) {
        throw "Manifest package '$($package.id)' has no advisory-scannable packageUrl."
    }
    [void] $purlSet.Add([string] $package.packageUrl)
}
foreach ($reference in @($manifest.references)) {
    if ($null -ne $reference.PSObject.Properties['originEvidence'] -and
        $null -eq $reference.PSObject.Properties['packageUrl']) {
        throw "Manifest reference '$($reference.id)' has byte-origin evidence but no direct packageUrl."
    }
    if ($null -ne $reference.PSObject.Properties['packageUrl']) {
        if ($null -eq $reference.PSObject.Properties['originEvidence']) {
            throw "Manifest reference '$($reference.id)' has a direct packageUrl without committed byte-origin evidence."
        }
        if (-not [string]::Equals([string] $reference.packageUrl, [string] $reference.originEvidence.packageUrl, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string] $reference.sha256, [string] $reference.originEvidence.assetSha256, [System.StringComparison]::OrdinalIgnoreCase) -or
            [long] $reference.size -ne [long] $reference.originEvidence.assetSize) {
            throw "Manifest reference '$($reference.id)' package mapping does not match its committed byte-origin evidence."
        }
        [void] $purlSet.Add([string] $reference.packageUrl)
    }
}
if ($purlSet.Count -eq 0) {
    throw 'Reference manifest contains no NuGet package URLs to scan.'
}

$packages = @($purlSet | ForEach-Object { ConvertFrom-NuGetPurl $_ } | Sort-Object Name, Version)
$headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'Talky-release-advisory-gate/1.0'
}
$githubToken = if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
    $env:GITHUB_TOKEN
}
elseif (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    $env:GH_TOKEN
}
else {
    $null
}
if ($null -ne $githubToken) {
    $headers.Authorization = "Bearer $githubToken"
}

$advisoriesById = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
$requestCount = 0
$rateLimitRemainingMinimum = $null
$batchSize = 50
$severities = @('high', 'critical')

for ($offset = 0; $offset -lt $packages.Count; $offset += $batchSize) {
    $batchEnd = [Math]::Min($offset + $batchSize - 1, $packages.Count - 1)
    $affectedPackages = @($packages[$offset..$batchEnd] | ForEach-Object { "$($_.Name)@$($_.Version)" }) -join ','

    foreach ($severity in $severities) {
        $encodedAffects = [System.Uri]::EscapeDataString($affectedPackages)
        $nextUrl = "https://api.github.com/advisories?type=reviewed&ecosystem=nuget&severity=$severity&is_withdrawn=false&affects=$encodedAffects&per_page=100"

        while ($null -ne $nextUrl) {
            try {
                $response = Invoke-WebRequest -Uri $nextUrl -Headers $headers -Method Get -TimeoutSec 30 -MaximumRedirection 0
            }
            catch {
                throw "GitHub Advisory API request failed closed for severity '$severity': $($_.Exception.Message)"
            }
            $requestCount++
            if ([int] $response.StatusCode -ne 200) {
                throw "GitHub Advisory API returned unexpected HTTP status '$($response.StatusCode)'."
            }

            $remainingHeader = $response.Headers['X-RateLimit-Remaining']
            if ($null -ne $remainingHeader) {
                $remaining = [int] ([string] $remainingHeader)
                if ($null -eq $rateLimitRemainingMinimum -or $remaining -lt $rateLimitRemainingMinimum) {
                    $rateLimitRemainingMinimum = $remaining
                }
            }

            try {
                $page = @(([string] $response.Content | ConvertFrom-Json -Depth 64))
            }
            catch {
                throw "GitHub Advisory API returned invalid JSON: $($_.Exception.Message)"
            }

            foreach ($advisory in $page) {
                if ($null -eq $advisory) {
                    continue
                }
                $severityValue = [string] $advisory.severity
                $withdrawnProperty = $advisory.PSObject.Properties['withdrawn_at']
                $isWithdrawn = $null -ne $withdrawnProperty -and $null -ne $withdrawnProperty.Value
                if ($isWithdrawn -or ($severityValue -ne 'high' -and $severityValue -ne 'critical')) {
                    continue
                }

                $ghsaId = [string] $advisory.ghsa_id
                if ([string]::IsNullOrWhiteSpace($ghsaId)) {
                    throw 'GitHub Advisory API returned an active high/critical advisory without a GHSA ID.'
                }
                $advisoriesById[$ghsaId] = $advisory
            }

            $nextUrl = Get-NextLink $response
        }
    }
}

$findingRecords = [System.Collections.Generic.List[object]]::new()
foreach ($entry in @($advisoriesById.GetEnumerator() | Sort-Object Key)) {
    $advisory = $entry.Value
    $affected = @($advisory.vulnerabilities | ForEach-Object {
            $firstPatched = if ($null -eq $_.first_patched_version) {
                $null
            }
            elseif ($null -ne $_.first_patched_version.PSObject.Properties['identifier']) {
                [string] $_.first_patched_version.identifier
            }
            else {
                [string] $_.first_patched_version
            }
            [ordered]@{
                ecosystem = [string] $_.package.ecosystem
                name = [string] $_.package.name
                vulnerableVersionRange = [string] $_.vulnerable_version_range
                firstPatchedVersion = $firstPatched
            }
        })
    $findingRecords.Add([ordered]@{
            ghsaId = [string] $advisory.ghsa_id
            cveId = [string] $advisory.cve_id
            severity = [string] $advisory.severity
            summary = [string] $advisory.summary
            htmlUrl = [string] $advisory.html_url
            publishedAt = [string] $advisory.published_at
            updatedAt = [string] $advisory.updated_at
            affectedPackages = $affected
        })
}

$unmappedReferences = [System.Collections.Generic.List[object]]::new()
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

    $unmappedReferences.Add([ordered]@{
            id = [string] $reference.id
            assemblyName = [string] $reference.assemblyName
            assemblyVersion = [string] $reference.assemblyVersion
            source = [string] $reference.source
            sha256 = [string] $reference.sha256
            mappingStatus = if ($null -eq $containedInPurl) { 'unresolved-no-public-ecosystem-mapping' } else { 'unresolved-no-direct-component-purl' }
            containedInPurl = $containedInPurl
        })
}

$advisoryFindingsClean = $findingRecords.Count -eq 0
$componentCoverageComplete = $unmappedReferences.Count -eq 0
$clean = $advisoryFindingsClean -and $componentCoverageComplete
$report = [ordered]@{
    schemaVersion = 2
    performed = $true
    clean = $clean
    releaseEligible = $clean
    scanner = [ordered]@{
        name = 'GitHub Global Security Advisories'
        endpoint = 'https://api.github.com/advisories'
        apiVersion = '2022-11-28'
        queriedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        authentication = if ($null -eq $githubToken) { 'unauthenticated-public' } else { 'token' }
        requestCount = $requestCount
        rateLimitRemainingMinimum = $rateLimitRemainingMinimum
    }
    policy = [ordered]@{
        ecosystem = 'nuget'
        advisoryType = 'reviewed'
        severities = $severities
        includeWithdrawn = $false
        requireIndependentCoverageForEveryReference = $true
        maxReportAgeHours = $maximumReportAgeHours
        failureMode = 'fail-closed-on-network-api-active-finding-or-unresolved-component'
    }
    advisoryFindingsClean = $advisoryFindingsClean
    componentCoverage = [ordered]@{
        complete = $componentCoverageComplete
        manifestReferenceCount = @($manifest.references).Count
        directPurlReferenceCount = @($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['packageUrl'] }).Count
        originEvidenceReferenceCount = @($manifest.references | Where-Object { $null -ne $_.PSObject.Properties['originEvidence'] }).Count
        unresolvedReferenceCount = $unmappedReferences.Count
        queriedUniquePurlCount = $packages.Count
    }
    queriedPackages = @($packages | ForEach-Object {
            [ordered]@{ purl = $_.Purl; name = $_.Name; version = $_.Version }
        })
    activeHighCriticalAdvisories = @($findingRecords)
    exactHashReferencesWithoutDirectPurl = @($unmappedReferences)
}
Write-JsonFile -Path $outputFullPath -Value $report

if (-not $advisoryFindingsClean) {
    $ids = @($findingRecords | ForEach-Object { $_.ghsaId }) -join ', '
    $message = "Advisory gate FAILED: $($findingRecords.Count) active high/critical finding(s): $ids. Report preserved at '$outputFullPath'."
    if ($AuditOnly) {
        Write-Warning "AUDIT ONLY: $message"
    }
    else {
        throw $message
    }
}

if (-not $componentCoverageComplete) {
    $blockerIds = @($unmappedReferences | ForEach-Object { $_.id }) -join ', '
    $message = "Advisory coverage gate FAILED: $($unmappedReferences.Count) exact binary reference(s) lack an independent component purl: $blockerIds. Report preserved at '$outputFullPath'."
    if ($AuditOnly) {
        Write-Warning "AUDIT ONLY: $message"
    }
    else {
        throw $message
    }
}

if ($clean) {
    Write-Host "Advisory scan PASS: $($packages.Count) unique NuGet purl(s), no active high/critical reviewed advisories, and independent coverage for all $(@($manifest.references).Count) exact binary reference(s)."
}
elseif ($AuditOnly) {
    Write-Host "Advisory audit complete but NOT release-eligible: findingsClean=$advisoryFindingsClean; componentCoverageComplete=$componentCoverageComplete."
}
