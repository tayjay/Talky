# Testing Talky

Two layers: deterministic unit tests that run offline, and a manual server verification that
proves both framework paths actually load on a live SCP:SL server.

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

A pass prints `PASS: <n> deterministic assertions.` and exits 0. Repeat for `Debug` and
`EXILED` — the three configurations compile different code via `DefineConstants`, so a green
Release run does not imply the other two build.

Some tests are order-coupled to enum layout. `PerformanceMetric` /
`PerformanceMetricCatalog.Definitions` and the three positional arrays in
`Talky.Tests/Program.cs` (`TestMetricCatalogAndSummary`) must be edited together; the test is
the guard that tells you when they drift apart.

---

## Server verification

Both configurations produce **`Talky.dll`**. They differ by destination, not file name.

> **Deploy one at a time.** Talky's constructor throws `DuplicateNameException` if it finds a
> second assembly declaring the same plugin type. Leaving a copy in the LabAPI folder while
> testing the EXILED build will fail the run.

Success marker, identical in both cases:

```
[INFO] [Talky] Talky was enabled.
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
[Talky] Talky was enabled.
[LabApi] [LOADER] Successfully enabled 'Talky', Version: 1.0.0.0, Author: 'TayTay'
```

There must be **no** `Loaded plugin Talky@...` line here. A Talky in the LabAPI folder should
not register itself into EXILED even when EXILED is present — that is deliberate. EXILED
initializes after LabAPI has already enabled Talky, so `ExiledRegistration.EnsureRegistered`
returns early on `LoaderPlugin.Instance == null`.

### 3. Shut down and remove the LabAPI copy

Stop the server, then delete `Talky.dll` from the LabAPI plugins directory. The next step must
not run with two copies deployed.

### 4. Build EXILED and deploy to EXILED

```bash
msbuild Talky.sln /t:Rebuild /p:Configuration=EXILED /p:DeployPlugin=true
```

No `DeployDirectory` is needed. The EXILED configuration resolves its destination inside the
`DeployBuiltPlugin` target, in order: `ExiledDeployDirectory`, then `EXILED_PLUGINS_DIR`, then
the parent of `ExiledDependenciesDir`. Confirm `Deployed Talky.dll to <exiled-plugins-dir>`.

### 5. Run the server and confirm EXILED enables it natively

```bash
LocalAdmin.exe 7777
```

Expected:

```
[Talky] Loaded plugin Talky@1.0.0.0
[Talky] Talky was enabled.
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
