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

    [switch] $LabApiOnly,

    [ValidateSet('LoaderOnly', 'ApiOnly')]
    [string] $ExiledPresenceOnly
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

function Assert-NoOptionalTypeReference {
    param(
        [Parameter(Mandatory = $true)][System.Type] $Type,
        [Parameter(Mandatory = $true)][string] $Context
    )

    $assemblyName = $Type.Assembly.GetName().Name
    if ($assemblyName -eq '0Harmony' -or
        $assemblyName.StartsWith('Exiled.', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "LabAPI-only metadata '$Context' exposes optional type '$($Type.FullName)' from '$assemblyName'."
    }

    if ($Type.HasElementType) {
        Assert-NoOptionalTypeReference -Type ($Type.GetElementType()) -Context $Context
    }
    if ($Type.IsGenericType) {
        foreach ($argument in $Type.GetGenericArguments()) {
            Assert-NoOptionalTypeReference -Type $argument -Context $Context
        }
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
$dependencyRoots = if ($LabApiOnly -or
    -not [string]::IsNullOrWhiteSpace($ExiledPresenceOnly)) {
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
        (Join-Path $repositoryRootFull "Talky.PatchTests\bin\$Configuration"),
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
    if (($LabApiOnly -or -not [string]::IsNullOrWhiteSpace($ExiledPresenceOnly)) -and
        $requested.Name.StartsWith(
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

    $harmonyAssemblyReferences = @($artifact.GetReferencedAssemblies() | Where-Object {
            [string]::Equals(
                $_.Name,
                '0Harmony',
                [System.StringComparison]::OrdinalIgnoreCase)
        })
    Assert-True ($harmonyAssemblyReferences.Count -eq 0) `
        'Production Talky.dll must discover compatible Harmony runtimes dynamically instead of carrying a version-bound 0Harmony assembly reference.'

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
    $performanceWindowType = $core.GetType('Talky.Performance.PerformanceWindow', $false)
    Assert-True ($null -ne $performanceWindowType) `
        'Embedded core performance metrics API cannot be resolved.'
    Assert-True ($null -ne $performanceWindowType.GetMethod('GetMetric')) `
        'Embedded performance window does not expose metric snapshots.'
    Assert-True ($null -ne $performanceWindowType.GetMethod('GetCounter')) `
        'Embedded performance window does not expose counters.'

    $configType = $core.GetType('Talky.Settings.Config', $false)
    Assert-True ($null -ne $configType) `
        'Embedded core configuration API cannot be resolved.'
    $debugProperty = $configType.GetProperty('Debug')
    Assert-True ($null -ne $debugProperty -and $debugProperty.PropertyType -eq [bool]) `
        'Embedded configuration does not expose the debug metrics switch.'

    if (-not $LabApiOnly -and
        [string]::IsNullOrWhiteSpace($ExiledPresenceOnly)) {
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
    $performanceMonitorType = $artifact.GetType('Talky.Performance.TalkyPerformanceMonitor', $true)
    $instanceFlags = [System.Reflection.BindingFlags]'NonPublic,Instance'
    $logWindowDetail = $performanceMonitorType.GetMethod('LogWindowDetail', $instanceFlags)
    Assert-True ($null -ne $logWindowDetail) `
        'Shipped plugin does not expose debug performance-window metrics.'
    $detailParameters = @($logWindowDetail.GetParameters())
    Assert-True ($detailParameters.Count -eq 1 -and
            $detailParameters[0].ParameterType.FullName -eq $performanceWindowType.FullName) `
        'Debug performance-window metrics use an unexpected contract.'

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

    if (-not [string]::IsNullOrWhiteSpace($ExiledPresenceOnly)) {
        $exiledBridge = $artifact.GetType('Talky.Bridges.ExiledBridge', $true)
        $exiledPresent = $exiledBridge.GetMethod('ExiledPresent', $staticFlags)
        $exiledRegistration = $artifact.GetType('Talky.Bridges.ExiledRegistration', $true)
        $tryRegister = $exiledRegistration.GetMethod('TryRegisterNativePlugin', $staticFlags)
        Assert-True ($null -ne $exiledPresent -and $null -ne $tryRegister) `
            'EXILED presence and registration gates are not discoverable.'
        Assert-True (($exiledPresent.GetMethodImplementationFlags() -band
                [System.Reflection.MethodImplAttributes]::NoInlining) -ne 0) `
            'EXILED presence gate must remain non-inlined.'
        Assert-True (($tryRegister.GetMethodImplementationFlags() -band
                [System.Reflection.MethodImplAttributes]::NoInlining) -ne 0) `
            'EXILED registration gate must remain non-inlined.'
        Assert-True (-not [bool] $exiledPresent.Invoke($null, @())) `
            'EXILED presence gate unexpectedly detected an optional framework identity.'

        $singleIdentity = if ($ExiledPresenceOnly -eq 'LoaderOnly') {
            'Exiled.Loader'
        }
        else {
            'Exiled.API'
        }
        [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
            [System.Reflection.AssemblyName]::new($singleIdentity),
            [System.Reflection.Emit.AssemblyBuilderAccess]::Run) | Out-Null
        Assert-True (-not [bool] $exiledPresent.Invoke($null, @())) `
            "$singleIdentity alone must not enable the EXILED bridge."
        $tryRegister.Invoke($null, @())

        $secondIdentity = if ($ExiledPresenceOnly -eq 'LoaderOnly') {
            'Exiled.API'
        }
        else {
            'Exiled.Loader'
        }
        [System.Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly(
            [System.Reflection.AssemblyName]::new($secondIdentity),
            [System.Reflection.Emit.AssemblyBuilderAccess]::Run) | Out-Null
        Assert-True ([bool] $exiledPresent.Invoke($null, @())) `
            'EXILED presence gate did not require and detect both framework identities.'
        Write-Host "Artifact validation PASS: $singleIdentity-only is rejected; both EXILED identities are required."
        return
    }

    if ($LabApiOnly) {
        $artifactTypes = @($artifact.GetTypes())
        Assert-True ($artifactTypes.Contains($pluginType)) `
            'LabAPI-only type enumeration did not contain the Talky plugin type.'
        $declaredFlags = [System.Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly'
        foreach ($type in $artifactTypes) {
            if ($null -ne $type.BaseType) {
                Assert-NoOptionalTypeReference -Type $type.BaseType -Context "$($type.FullName) base type"
            }
            foreach ($interface in @($type.GetInterfaces())) {
                Assert-NoOptionalTypeReference -Type $interface -Context "$($type.FullName) interface"
            }
            foreach ($field in @($type.GetFields($declaredFlags))) {
                Assert-NoOptionalTypeReference -Type $field.FieldType -Context "$($type.FullName).$($field.Name) field"
            }
            foreach ($constructor in @($type.GetConstructors($declaredFlags))) {
                foreach ($parameter in @($constructor.GetParameters())) {
                    Assert-NoOptionalTypeReference -Type $parameter.ParameterType -Context "$($type.FullName) constructor parameter"
                }
            }
            foreach ($method in @($type.GetMethods($declaredFlags))) {
                Assert-NoOptionalTypeReference -Type $method.ReturnType -Context "$($type.FullName).$($method.Name) return"
                foreach ($parameter in @($method.GetParameters())) {
                    Assert-NoOptionalTypeReference -Type $parameter.ParameterType -Context "$($type.FullName).$($method.Name) parameter"
                }
            }
            foreach ($property in @($type.GetProperties($declaredFlags))) {
                Assert-NoOptionalTypeReference -Type $property.PropertyType -Context "$($type.FullName).$($property.Name) property"
            }
            foreach ($event in @($type.GetEvents($declaredFlags))) {
                Assert-NoOptionalTypeReference -Type $event.EventHandlerType -Context "$($type.FullName).$($event.Name) event"
            }
        }

        $recordingEnabled = $performanceMonitorType.GetProperty(
            'IsDurationRecordingEnabled',
            $instanceFlags)
        Assert-True ($null -ne $recordingEnabled) `
            'Performance monitor does not expose its internal duration-recording gate.'
        $disabledConfig = [Activator]::CreateInstance($configType)
        $performanceConfig = $configType.GetProperty('Performance').GetValue($disabledConfig)
        $modeProperty = $performanceConfig.GetType().GetProperty('Mode')
        $modeProperty.SetValue(
            $performanceConfig,
            [Enum]::Parse($modeProperty.PropertyType, 'Disabled'))
        $disabledMonitor = [Activator]::CreateInstance(
            $performanceMonitorType,
            [object[]] @($disabledConfig))
        try {
            Assert-True (-not [bool] $recordingEnabled.GetValue($disabledMonitor)) `
                'Disabled monitoring unexpectedly enables duration recording.'
        }
        finally {
            ([IDisposable] $disabledMonitor).Dispose()
        }

        $enabledConfig = [Activator]::CreateInstance($configType)
        $enabledMonitor = [Activator]::CreateInstance(
            $performanceMonitorType,
            [object[]] @($enabledConfig))
        try {
            Assert-True ([bool] $recordingEnabled.GetValue($enabledMonitor)) `
                'Enabled monitoring does not permit duration recording.'
            $freeze = $performanceMonitorType.GetMethod(
                'Freeze',
                [System.Reflection.BindingFlags]'Public,Instance',
                $null,
                [Type[]] @(),
                $null)
            Assert-True ($null -ne $freeze) 'Performance monitor freeze gate is missing.'
            $freeze.Invoke($enabledMonitor, @())
            Assert-True (-not [bool] $recordingEnabled.GetValue($enabledMonitor)) `
                'Frozen monitoring unexpectedly enables duration recording.'
        }
        finally {
            ([IDisposable] $enabledMonitor).Dispose()
        }

        $plugin = [Activator]::CreateInstance($pluginType)
        Assert-True ($null -ne $plugin) 'LabAPI-only plugin construction failed.'
        $unexpectedOptional = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
                $_.GetName().Name -eq '0Harmony' -or
                $_.GetName().Name.StartsWith('Exiled.', [System.StringComparison]::OrdinalIgnoreCase)
            })
        Assert-True ($unexpectedOptional.Count -eq 0) `
            'LabAPI-only type enumeration or construction unexpectedly loaded Harmony or EXILED.'
        Write-Host 'Artifact validation PASS: fresh-process LabAPI-only type/signature enumeration and construction with Harmony/EXILED absent.'
        return
    }

    if ($IncompatibleHarmonyOnly) {
        $fakeHarmonyName = [System.Reflection.AssemblyName]::new('0Harmony')
        $fakeHarmonyName.Version = [Version]::new(99, 0, 0, 0)
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

    foreach ($presenceMode in @('LoaderOnly', 'ApiOnly')) {
        & $hostExecutable `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File $PSCommandPath `
            -Configuration $Configuration `
            -ScpSlManagedDir $ScpSlManagedDir `
            -ExiledDependenciesDir $ExiledDependenciesDir `
            -RepositoryRoot $repositoryRootFull `
            -ExiledPresenceOnly $presenceMode
        Assert-True ($LASTEXITCODE -eq 0) `
            "Fresh-process $presenceMode EXILED-presence probe failed."
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
        'Build-test Harmony dependency was not found for the presence probe.'
    $script:allowHarmony = $true
    $loadContext.LoadFromAssemblyPath(
        [System.IO.Path]::GetFullPath($dependencyPaths['0Harmony'])) | Out-Null
    Assert-True ([bool] $harmonyPresent.Invoke($null, @())) `
        'Harmony-present artifact probe did not detect the build-test assembly.'

    # Harmony 2.2's detour engine is not compatible with the PowerShell host's newer CoreCLR,
    # so this artifact-only probe must not initialize or patch through Harmony here. The x64
    # net48 PatchTests runner exercises real apply/inspect/invoke/unpatch behavior separately;
    # a Unity/Mono server remains the live-runtime acceptance gate.
    $harmonyRuntimeBridge = $artifact.GetType(
        'Talky.Bridges.HarmonyRuntimeBridge',
        $true)
    Assert-True ($null -ne $harmonyRuntimeBridge.GetMethod(
            'CreateHarmonyTranspiler',
            $staticFlags)) `
        'Runtime Harmony transpiler factory could not be resolved from the shipped artifact.'
    Assert-True ($null -ne $harmonyRuntimeBridge.GetMethod(
            'TransformInstructions',
            $staticFlags)) `
        'BCL-only Harmony instruction transformer could not be resolved from the shipped artifact.'
    Assert-True ($null -ne $harmonyRuntimeBridge.GetMethod(
            'SelectSerializedMouseLook',
            $staticFlags)) `
        'Harmony look-selector callback could not be resolved from the shipped artifact.'

    Write-Host "Artifact validation PASS: $artifactName; embedded core/shim; Harmony-absent lifecycle facade; Harmony-present discovery/callback metadata."
}
finally {
    $loadContext.remove_Resolving($resolver)
}
