# Testing Talky

Validation includes offline deterministic/patch tests, a contained Unity Mono member-access
probe, and manual server verification that proves both framework paths load on a live server.

Run the unit tests on every change. Run the server verification before releasing, and any time
you touch plugin registration, the EXILED shim, the Harmony bridge, or the build/deploy config.

---

## Prerequisites

The build resolves game and EXILED assemblies from two directories. Supply them as MSBuild
properties, environment variables, or (in Rider) solution-level custom global properties:

| Property | Environment variable | Points at |
|---|---|---|
| `ScpSlManagedDir` | `SCPSL_MANAGED_DIR` | `<server>\SCPSL_Data\Managed` |
| `ExiledDependenciesDir` | `EXILED_DEPENDENCIES_DIR` | `%AppData%\EXILED\Plugins\dependencies` |

Rider stores these in `Talky.sln.DotSettings.user` under
`Environment/Hierarchy/Build/SolBuilderDuo/CustomGlobalProperties`. That file is per-user and
intentionally untracked, so a fresh clone must set them once.

The managed directory must match the version pinned in `build/reference-manifest.json`.
`build/verify-references.ps1` runs as part of every build and fails with a size or hash drift
message if you point it at a different server install — that error means the wrong directory,
not a broken manifest.

Rider's background solution loading skips the build-time verifier so it can resolve references
for indexing. Ordinary builds still verify dependency hashes and package contents, but accept
the MSBuild/Roslyn version selected by the IDE. Use a toolchain that supports C# 12 and .NET
Framework 4.8; an exact compiler version is only required for release packaging.

`build/package-plugin.ps1` and `DeployValidatedPlugin` retain exact toolchain verification.
To require that check during an ordinary build too, pass `/p:TalkyVerifyToolchain=true`.
The verification script also checks the toolchain by default when invoked directly.

If Rider still displays cached unresolved references after changing the build targets, reload
`Talky.sln`. Its toolchain and the two directory properties above are configured under
**Settings > Build, Execution, Deployment > Toolset and Build**.

That exact pin is compile-input provenance only; it is not a runtime compatibility allowlist.
Qualify each current game build by passing its `Assembly-CSharp.dll` to `Talky.PatchTests` and
then running the live harness. Harmony admission uses method signatures, decoded instruction
anchors, transactional apply/cleanup, and patch ownership; runtime MVID, IL digest, and file
hash are diagnostic evidence only.

Deployment targets:

| | Directory |
|---|---|
| LabAPI | `%AppData%\SCP Secret Laboratory\LabAPI\plugins\global` |
| EXILED | `%AppData%\EXILED\Plugins` |

---

## Unit tests

`Talky.Tests` is a hand-rolled runner, not a test framework. It builds as part of the solution
and exits non-zero on the first failed assertion.

```bash
msbuild Talky.sln /t:Rebuild /p:Configuration=Release
```

Then run the produced executable:

```
Talky.Tests\bin\Release\Talky.Tests.exe
```

A pass prints `PASS: <n> deterministic assertions.` and exits 0. Repeat for `Debug` when
changing debug-specific behavior. Release is the sole production compilation and embeds
`Talky.Core.dll` and `Talky.Exiled.Shim.dll.bin` inside `Talky.dll`. The legacy EXILED solution
lane is only a compatibility alias; do not use it as a separate production build.

The canonical guarantee applies to `Talky.sln` and the package scripts. A direct production-
project build such as `msbuild Talky\Talky.csproj /p:Configuration=EXILED` is a legacy,
unsupported path and its output must not be treated as a release artifact.

Some tests are order-coupled to enum layout. `PerformanceMetric` /
`PerformanceMetricCatalog.Definitions` and the three positional arrays in
`Talky.Tests/Program.cs` (`TestMetricCatalogAndSummary`) must be edited together; the test is
the guard that tells you when they drift apart.

### Direct game-member access

Production projects compile with `AllowUnsafeBlocks=true` (`/unsafe`) against the pinned
publicized game reference. Decoder and overlay accesses use direct game members. Metadata
binding is retained for optional
assemblies and Harmony/CedMod integration, but production assemblies must not import reflective
member reads, writes, or invocation. Test fixtures use metadata to inspect and isolate runtime
behavior.

```powershell
pwsh -File build/test-no-reflective-access.ps1 -Configuration Release
pwsh -File build/test-direct-member-il.ps1 -Configuration Release
pwsh -File build/test-mono-access.ps1 -MonoRuntimePath <server>\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll -ScpSlManagedDir <server>\SCPSL_Data\Managed -ExiledDependenciesDir <dependencies> -CscPath <Roslyn>\csc.exe
pwsh -File build/test-mono-harmony-load.ps1 -MonoRuntimePath <server>\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll -ScpSlManagedDir <server>\SCPSL_Data\Managed -ExiledDependenciesDir <dependencies> -CscPath <Roslyn>\csc.exe
```

The raw-PE check verifies the shipped decoder and grab-animation member accesses without
asking the desktop CLR to load Unity interface implementations. The Mono access probe uses
the [Mono embedding API](https://www.mono-project.com/docs/advanced/embedding/) in a separate
process to qualify direct access to stock game members with the production compiler flags. It does not
start the game loop or enable the plugin, and it does not replace live voice/animation testing.
The late-Harmony probe starts with Harmony unavailable, runs the ownership and cleanup checks
used during construction, byte-loads the real dependency, then verifies binding, patch apply,
ownership, and cleanup in that same Unity Mono process. It does not construct the plugin or
initialize the server's filesystem configuration.

---

## Server verification

Both production lanes use the same **`Talky.dll`** bytes. Release is built once and copied to
the EXILED output directory; only the deployment destination differs.

> **Deploy one at a time.** Shared lifecycle ownership refuses a second active instance while
> allowing EXILED to reload disabled assembly copies. Leaving copies in both framework plugin
> directories is unsupported.

Current Talky also refuses construction and enable when a legacy `Talky.LabAPI`,
`Talky.LabAPI.Debug`, or `Talky.EXILED` assembly is loaded or present in the configured plugin
directories. This checks assembly identity, so renaming an old DLL does not bypass the guard.
Disabled legacy DLLs must also be removed. Renaming the current universal DLL to an old
filename does not make it legacy.

With the default `prefer_harmony_look_sync: true`, a compatible Harmony runtime selects
`StockHarmony`. If Harmony is absent, no Talky patch ownership remains, and CedMod uses stock
sync or is absent, Talky selects the `StockSync` event path. An incompatible Harmony runtime,
unresolved patch ownership, or CedMod fake sync still requires safe patch admission; those
conditions must not silently select stock events.

Harmony discovery must also recover in the same process: query and clean up while Harmony is
absent, load its DLL, then apply and remove Talky's patch. A fresh process with Harmony already
loaded does not cover the LabAPI-before-EXILED dependency-loading sequence.

Success marker, identical in both cases:

```
[INFO] [Talky] Was enabled.
```

### 1. Build Release and deploy to LabAPI

```bash
msbuild Talky.sln /t:Rebuild /p:Configuration=Release /p:DeployPlugin=true /p:DeployDirectory=<labapi-plugins-dir>
```

Confirm the build log contains `Deployed Talky.dll to <labapi-plugins-dir>`.

### 2. Run the server and confirm it enables

```bash
LocalAdmin.exe 7777
```

Expected, in order:

```
[LabApi] [LOADER] Successfully loaded Talky
[Talky] Was enabled.
[LabApi] [LOADER] Successfully enabled 'Talky', Version: 1.0.0.0, Author: 'TayTay'
```

There must be **no** `Loaded plugin Talky@...` line here. A Talky in the LabAPI folder should
not register itself into EXILED even when EXILED is present — that is deliberate. EXILED
initializes after LabAPI has already enabled Talky, so `ExiledRegistration.EnsureRegistered`
returns early on `LoaderPlugin.Instance == null`.

### 3. Shut down and remove the LabAPI copy

Stop the server, then delete `Talky.dll` from the LabAPI plugins directory. The next step must
not run with two copies deployed.

### 4. Build Release and deploy the same embedded-shim artifact to EXILED

```bash
msbuild Talky.sln /t:Rebuild /p:Configuration=Release /p:DeployPlugin=true /p:DeployDirectory=<exiled-plugins-dir>
```

The explicit destination selects EXILED deployment. Confirm `Deployed Talky.dll to
<exiled-plugins-dir>`. The build and embedded payload are identical to LabAPI deployment.

### 5. Run the server and confirm EXILED enables it natively

```bash
LocalAdmin.exe 7777
```

Expected:

```
[Talky] Loaded plugin Talky@1.0.0.0
[Talky] Was enabled.
[Exiled.API] Talky v1.0.0 by TayTay has been enabled!
```

The third line is the one that matters — it proves EXILED ran the shim's `OnEnabled` through
its own enable pass, rather than Talky merely being present.

### 6. Shut down and remove the EXILED copy

Stop the server and delete `Talky.dll` from the EXILED plugins directory, so the next run
starts from a known-clean state.

---

## Reading the log

**`Skipping loading duplicate LabAPI plugin Talky`, twice — benign.** EXILED loads LabAPI
plugins once for the global context and again for the port-level context, and the global pass
is repeated during the second. Two of these lines are normal and do not indicate a conflict.

**Loads but never enables.** If `Loaded plugin Talky@...` appears without
`Talky v1.0.0 by TayTay has been enabled!`, check `is_enabled` for Talky in the EXILED plugin
config (`%AppData%\EXILED\Configs\<port>-config.yml`). EXILED loads a disabled plugin and then
skips enabling it, with no error.

**No Talky lines at all under EXILED.** The assembly was not found or not loaded — check that
`Talky.dll` is in the EXILED plugins directory and that no stale `Talky.EXILED.dll` remains
from a build predating the artifact rename.

**Everything after `Successfully loaded Talky` is missing.** The constructor threw. The most
likely cause is a second Talky assembly still deployed in the other framework's directory.
