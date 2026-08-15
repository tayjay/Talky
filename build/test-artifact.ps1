[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Release', 'EXILED')]
    [string] $Configuration,

    [Parameter(Mandatory = $true)]
    [string] $ScpSlManagedDir,

    [Parameter(Mandatory = $true)]
    [string] $ExiledDependenciesDir,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [switch] $IncompatibleHarmonyOnly,

    [switch] $LabApiOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool] $Condition,
        [Parameter(Mandatory = $true)][string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-ResourceBytes {
    param(
        [Parameter(Mandatory = $true)][System.Reflection.Assembly] $Assembly,
        [Parameter(Mandatory = $true)][string] $Name
    )

    $stream = $Assembly.GetManifestResourceStream($Name)
    if ($null -eq $stream) {
        throw "Embedded resource '$Name' is missing."
    }

    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][byte[]] $Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($sha.ComputeHash($Bytes))
    }
    finally {
        $sha.Dispose()
    }
}

$repositoryRootFull = [System.IO.Path]::GetFullPath($RepositoryRoot)
$artifactName = 'Talky.dll'
$expectedAssemblyName = 'Talky'
$artifactPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRootFull "Talky\bin\$Configuration\$artifactName"))
$corePath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRootFull "Talky.Core\bin\$Configuration\Talky.Core.dll"))
$shimPath = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRootFull "Talky.Exiled\bin\$Configuration\Talky.Exiled.Shim.dll"))

foreach ($requiredPath in @($artifactPath, $corePath, $shimPath)) {
    Assert-True (Test-Path -LiteralPath $requiredPath -PathType Leaf) `
        "Required artifact is missing: '$requiredPath'."
}

$dependencyPaths = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
$dependencyRoots = if ($LabApiOnly) {
    @(
        ([System.IO.Path]::GetFullPath($ScpSlManagedDir)),
        ([System.IO.Path]::GetDirectoryName($artifactPath))
    )
}
else {
    @(
        (Join-Path $repositoryRootFull 'packages\ExMod.Exiled.9.14.2\lib\net48'),
        ([System.IO.Path]::GetFullPath($ScpSlManagedDir)),
        ([System.IO.Path]::GetFullPath($ExiledDependenciesDir)),
        ([System.IO.Path]::GetDirectoryName($artifactPath))
    )
}
foreach ($root in $dependencyRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        continue
    }

    foreach ($candidate in @(Get-ChildItem -LiteralPath $root -Filter '*.dll' -File)) {
        try {
            $name = [System.Reflection.AssemblyName]::GetAssemblyName($candidate.FullName).Name
            if (-not $dependencyPaths.ContainsKey($name)) {
                $dependencyPaths.Add($name, $candidate.FullName)
            }
        }
        catch {
            # Native/non-managed files are not assembly resolver candidates.
        }
    }
}
$dependencyPaths['Talky.Core'] = $corePath

$script:allowHarmony = $false
$resolver = [System.Func[
    System.Runtime.Loader.AssemblyLoadContext,
    System.Reflection.AssemblyName,
    System.Reflection.Assembly]] {
    param($context, $requested)

    if ([string]::Equals($requested.Name, '0Harmony', [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $script:allowHarmony) {
        return $null
    }
    if ($LabApiOnly -and $requested.Name.StartsWith(
            'Exiled.',
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $null
    }

    $resolvedPath = $null
    if ($dependencyPaths.TryGetValue($requested.Name, [ref] $resolvedPath)) {
        return $context.LoadFromAssemblyPath([System.IO.Path]::GetFullPath($resolvedPath))
    }

    return $null
}

$loadContext = [System.Runtime.Loader.AssemblyLoadContext]::Default
$loadContext.add_Resolving($resolver)
try {
    $artifact = $loadContext.LoadFromAssemblyPath($artifactPath)
    Assert-True ([string]::Equals(
            $artifact.GetName().Name,
            $expectedAssemblyName,
            [System.StringComparison]::Ordinal)) `
        "Artifact assembly name does not match the $Configuration lane."

    $resources = @($artifact.GetManifestResourceNames() | Sort-Object)
    Assert-True (($resources -join "`n") -eq "Talky.Core.dll`nTalky.Exiled.Shim.dll.bin") `
        'Artifact embedded-resource allowlist is not exact.'

    $embeddedCore = Read-ResourceBytes -Assembly $artifact -Name 'Talky.Core.dll'
    $embeddedShim = Read-ResourceBytes -Assembly $artifact -Name 'Talky.Exiled.Shim.dll.bin'
    Assert-True ((Get-Sha256 $embeddedCore) -eq
            (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash) `
        'Embedded Talky.Core does not match the lane output.'
    Assert-True ((Get-Sha256 $embeddedShim) -eq
            (Get-FileHash -LiteralPath $shimPath -Algorithm SHA256).Hash) `
        'Embedded Talky.Exiled.Shim does not match the lane output.'

    $coreStream = [System.IO.MemoryStream]::new($embeddedCore, $false)
    try {
        $core = $loadContext.LoadFromStream($coreStream)
    }
    finally {
        $coreStream.Dispose()
    }
    Assert-True ($null -ne $core.GetType('Talky.Performance.TalkyDiagnosticsSnapshot', $false)) `
        'Embedded core diagnostics API cannot be resolved.'

    if (-not $LabApiOnly) {
        # The shim is loaded from an embedded stream and therefore has no dependency-probing base
        # directory. Preload its managed framework base before resolving the derived plugin type.
        foreach ($dependencyName in @(
                'YamlDotNet',
                'NorthwoodLib',
                'CommandSystem.Core',
                'LabApi',
                'Assembly-CSharp-firstpass',
                'Assembly-CSharp',
                'Mirror',
                'UnityEngine.CoreModule',
                'Exiled.API')) {
            if ($dependencyPaths.ContainsKey($dependencyName)) {
                $loadContext.LoadFromAssemblyPath(
                    [System.IO.Path]::GetFullPath($dependencyPaths[$dependencyName])) | Out-Null
            }
        }

        $shimStream = [System.IO.MemoryStream]::new($embeddedShim, $false)
        try {
            $shim = $loadContext.LoadFromStream($shimStream)
        }
        finally {
            $shimStream.Dispose()
        }
        Assert-True ($null -ne $shim.GetType('Talky.Exiled.TalkyExiledPlugin', $false)) `
            'Embedded EXILED shim plugin type cannot be resolved.'
    }

    $pluginType = $artifact.GetType('Talky.Plugin', $true)
    Assert-True ($null -ne $pluginType.GetMethod('TryGetDiagnosticsSnapshot')) `
        'Shipped plugin does not expose TryGetDiagnosticsSnapshot.'

    $harmonyBridge = $artifact.GetType('Talky.Bridges.HarmonyBridge', $true)
    $staticFlags = [System.Reflection.BindingFlags]'Public,NonPublic,Static'
    $harmonyPresent = $harmonyBridge.GetMethod('HarmonyPresent', $staticFlags)
    $removePatches = $harmonyBridge.GetMethod('RemovePatches', $staticFlags)
    $hasOwnedPatchState = $harmonyBridge.GetMethod('HasOwnedPatchState', $staticFlags)
    Assert-True (-not [bool] $harmonyPresent.Invoke($null, @())) `
        'Harmony-absent artifact probe unexpectedly detected Harmony.'
    $removePatches.Invoke($null, @())
    Assert-True (-not [bool] $hasOwnedPatchState.Invoke($null, @())) `
        'Harmony-absent cleanup retained patch ownership.'

    if ($LabApiOnly) {
        Assert-True ($Configuration -eq 'Release') `
            'LabAPI-only artifact probing is valid only for the Release lane.'
        $plugin = [Activator]::CreateInstance($pluginType)
        Assert-True ($null -ne $plugin) 'LabAPI-only plugin construction failed.'
        $unexpectedOptional = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
                $_.GetName().Name -eq '0Harmony' -or
                $_.GetName().Name.StartsWith('Exiled.', [System.StringComparison]::OrdinalIgnoreCase)
            })
        Assert-True ($unexpectedOptional.Count -eq 0) `
            'LabAPI-only construction unexpectedly loaded Harmony or EXILED.'
        Write-Host 'Artifact validation PASS: fresh-process LabAPI-only construction with Harmony/EXILED absent.'
        return
    }

    if ($IncompatibleHarmonyOnly) {
        $fakeHarmonyName = [System.Reflection.AssemblyName]::new('0Harmony')
        $fakeHarmonyName.Version = [Version]::new(2, 2, 2, 0)
        [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
            $fakeHarmonyName,
            [System.Reflection.Emit.AssemblyBuilderAccess]::Run) | Out-Null
        Assert-True ([bool] $harmonyPresent.Invoke($null, @())) `
            'Incompatible-Harmony probe did not detect the fake loaded identity.'

        $dynamicName = [System.Reflection.AssemblyName]::new(
            'CedMod.ArtifactTest.Incompatible')
        $dynamicAssembly = [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
            $dynamicName,
            [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
        $applyPatches = $harmonyBridge.GetMethod('ApplyPatches', $staticFlags)
        $applyArguments = @($dynamicAssembly, $null)
        $applied = [bool] $applyPatches.Invoke($null, $applyArguments)
        Assert-True (-not $applied) `
            'Binary-incompatible Harmony/CedMod combination was accepted.'
        Assert-True (-not [string]::IsNullOrWhiteSpace([string] $applyArguments[1])) `
            'Binary-incompatible Harmony rejection did not return a diagnostic.'
        Assert-True (-not [bool] $hasOwnedPatchState.Invoke($null, @())) `
            'Incompatible Harmony created false Talky patch ownership.'
        $removePatches.Invoke($null, @())
        Write-Host 'Artifact validation PASS: incompatible 0Harmony is contained without lifecycle ownership.'
        return
    }

    $hostExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ($Configuration -eq 'Release') {
        & $hostExecutable `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $PSCommandPath `
            -Configuration $Configuration `
            -ScpSlManagedDir $ScpSlManagedDir `
            -ExiledDependenciesDir $ExiledDependenciesDir `
            -RepositoryRoot $repositoryRootFull `
            -LabApiOnly
        Assert-True ($LASTEXITCODE -eq 0) `
            'Fresh-process LabAPI-only construction probe failed.'
    }

    & $hostExecutable `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $PSCommandPath `
        -Configuration $Configuration `
        -ScpSlManagedDir $ScpSlManagedDir `
        -ExiledDependenciesDir $ExiledDependenciesDir `
        -RepositoryRoot $repositoryRootFull `
        -IncompatibleHarmonyOnly
    Assert-True ($LASTEXITCODE -eq 0) `
        'Fresh-process incompatible-Harmony containment probe failed.'

    Assert-True ($dependencyPaths.ContainsKey('0Harmony')) `
        'Pinned Harmony dependency was not found for the presence probe.'
    $script:allowHarmony = $true
    $loadContext.LoadFromAssemblyPath(
        [System.IO.Path]::GetFullPath($dependencyPaths['0Harmony'])) | Out-Null
    Assert-True ([bool] $harmonyPresent.Invoke($null, @())) `
        'Harmony-present artifact probe did not detect the pinned assembly.'

    # Harmony 2.2's detour engine is not compatible with the PowerShell host's newer CoreCLR,
    # so this artifact-only probe must not initialize or patch through Harmony here. The exact
    # known/unknown CedMod admission policy is exercised by the deterministic net48 suite; real
    # patch lifecycle remains a same-runtime server-host acceptance gate.
    $harmonyRuntimeBridge = $artifact.GetType(
        'Talky.Bridges.HarmonyRuntimeBridge',
        $true)
    Assert-True ($null -ne $harmonyRuntimeBridge.GetMethod('Prefix', $staticFlags)) `
        'Harmony prefix callback could not be resolved from the shipped artifact.'
    Assert-True ($null -ne $harmonyRuntimeBridge.GetMethod('Postfix', $staticFlags)) `
        'Harmony postfix callback could not be resolved from the shipped artifact.'

    Write-Host "Artifact validation PASS: $artifactName; embedded core/shim; Harmony-absent lifecycle facade; Harmony-present discovery/callback metadata."
}
finally {
    $loadContext.remove_Resolving($resolver)
}
