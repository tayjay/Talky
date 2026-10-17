using System;
using System.Linq;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using MEC;
using Talky.Bridges;
using Talky.Compatibility;
using Talky.Core;
using Talky.Handlers;
using Talky.Performance;
using Talky.Runtime;
using Talky.Settings;


namespace Talky;

public class Plugin : Plugin<Config>
{
    public VoiceChattingHandler VoiceChattingHandler;
    public FakeLookHandler FakeLookHandler;
    public SSTalkySettings Settings;
    public TalkyRuntime Runtime { get; private set; }
    public TalkyPerformanceMonitor PerformanceMonitor { get; private set; }
    public CedModLookSyncMode LookSyncMode { get; private set; } = CedModLookSyncMode.StockSync;
    public string LookSyncPatchDetail { get; private set; } = "not initialized";
    public static Plugin Instance { get; private set; }

    internal bool IsLookSyncAvailable =>
        _fullyEnabled && LookSyncMode != CedModLookSyncMode.CedModUnsupported;

    private CoroutineHandle _postEnableHandle;
    private int _lifecycleGeneration;
    private bool _enabled;
    private bool _fullyEnabled;
    private bool _pluginsEnabledSubscribed;
    private bool _appliedPreferHarmonyLookSync;
    private CedModPluginState _appliedCedModState = CedModPluginState.Unreadable;
    private CedModCompatibilityReason _appliedCedModReason =
        CedModCompatibilityReason.ConfigurationUnreadable;

    // Uses the EXILED lifecycle path, which runs post-load setup without waiting for LabAPI's load event.
    internal bool ExiledNativeMode;

    public Plugin()
    {
        LegacyTalkyGuard.ThrowIfLegacyPresent();

        if (TalkyBridge.HasLifecycleOwner)
        {
            throw new InvalidOperationException(
                "Another Talky plugin instance still owns an active or incompletely " +
                "released lifecycle. Its Disable() must complete before another " +
                "instance can be constructed.");
        }

        Plugin previous = Instance;
        if (previous != null &&
            previous.HasOwnedNonHarmonyLifecycleState())
        {
            throw new InvalidOperationException(
                "A previous Talky plugin instance still owns runtime state, subscriptions, " +
                "settings, or coroutines. Its Disable() must complete before " +
                "another instance can be constructed.");
        }

        CleanupOrphanedHarmonyOwnership();

        WireExiledBridge();
        Instance = this;
        ExiledRegistration.TryRegisterNativePlugin();
    }

    // Exposes identity and lifecycle to the embedded shim without a reverse assembly reference.
    private void WireExiledBridge()
    {
        if (!TalkyBridge.TryBindLifecycle(this, cfg =>
        {
            LegacyTalkyGuard.ThrowIfLegacyPresent();
            if (cfg != null) Config = cfg;
            ExiledNativeMode = true;
            Enable();
        }, Disable))
        {
            throw new InvalidOperationException(
                "Another Talky instance acquired lifecycle ownership before its " +
                "EXILED callbacks could be bound.");
        }

        TalkyBridge.Name = Name;
        TalkyBridge.Prefix = Name.ToLowerInvariant();
        TalkyBridge.Author = Author;
        TalkyBridge.Version = Version;
    }
    
    public override void Enable()
    {
        if (_enabled)
            return;

        LegacyTalkyGuard.ThrowIfLegacyPresent();

        if (!TalkyBridge.TryAcquireLifecycle(this))
        {
            throw new InvalidOperationException(
                "Talky cannot enable because a different instance owns the lifecycle " +
                "or a newer instance replaced its lifecycle callbacks.");
        }

        try
        {
            if (!HasOwnedNonHarmonyLifecycleState())
                CleanupOrphanedHarmonyOwnership();

            if (HasOwnedLifecycleState())
            {
                throw new InvalidOperationException(
                    "Talky cannot enable while subscriptions, runtime objects, settings, " +
                    "coroutines, or Harmony patches from an earlier lifecycle remain owned. " +
                    "Retry Disable() after resolving the cleanup failure.");
            }

            _enabled = true;
            _fullyEnabled = false;
            int generation = ++_lifecycleGeneration;
            Config ??= new Config();
            ValidateConfiguration();

            PerformanceMonitor = new TalkyPerformanceMonitor(Config);

            Settings = new SSTalkySettings();
            Settings.Activate();
            Runtime = new TalkyRuntime();
            ApplyCurrentPerformancePolicy();
            Runtime.Enable();

            VoiceChattingHandler = new VoiceChattingHandler();
            FakeLookHandler = new FakeLookHandler();

            VoiceChattingHandler.RegisterEvents();
            if (ExiledNativeMode)
                SchedulePostEnable(generation, 0.1f);
            else
            {
                _pluginsEnabledSubscribed = true;
                ServerEvents.PluginsEnabled += OnPluginsEnabled;
                // Reloads can occur after PluginsEnabled, so schedule bounded post-load setup.
                SchedulePostEnable(generation, 1f);
            }

            Logger.Info("Was enabled.");
        }
        catch
        {
            try
            {
                Disable();
            }
            catch (Exception cleanupException)
            {
                Logger.Error("Failed to roll back a partial enable: " + cleanupException);
            }

            throw;
        }
    }
    
 
    public override void Disable()
    {
        // A loader may still hold an inactive instance from an older assembly copy.
        // It must not remove patches or state owned by the current lifecycle.
        if (TalkyBridge.HasLifecycleOwner && !TalkyBridge.IsLifecycleOwner(this))
            return;

        bool hadOwnedState =
            _enabled ||
            _fullyEnabled ||
            _pluginsEnabledSubscribed ||
            _postEnableHandle.IsValid ||
            Runtime != null ||
            VoiceChattingHandler != null ||
            FakeLookHandler != null ||
            Settings != null ||
            PerformanceMonitor != null;

        _enabled = false;
        _fullyEnabled = false;
        ++_lifecycleGeneration;
        if (_pluginsEnabledSubscribed &&
            TryCleanup(
                "unsubscribe the PluginsEnabled callback",
                () => ServerEvents.PluginsEnabled -= OnPluginsEnabled))
        {
            _pluginsEnabledSubscribed = false;
        }

        CoroutineHandle postEnableHandle = _postEnableHandle;
        if (postEnableHandle.IsValid)
        {
            if (TryCleanup(
                "cancel delayed post-enable setup",
                () => Timing.KillCoroutines(postEnableHandle)))
            {
                _postEnableHandle = default;
            }
        }

        TalkyRuntime runtime = Runtime;
        if (runtime != null)
            TryCleanup("disable the Talky runtime", runtime.Disable);

        VoiceChattingHandler voiceHandler = VoiceChattingHandler;
        if (voiceHandler != null &&
            TryCleanup("unregister voice events", voiceHandler.UnregisterEvents))
        {
            VoiceChattingHandler = null;
        }

        FakeLookHandler fakeLookHandler = FakeLookHandler;
        if (fakeLookHandler != null &&
            TryCleanup("unregister look events", fakeLookHandler.UnregisterEvents))
        {
            FakeLookHandler = null;
        }

        bool patchCleanupSucceeded = TryCleanup(
            "remove the look-sync Harmony patch",
            RemoveLookSyncPatchesIfPresent);
        LookSyncMode = CedModLookSyncMode.CedModUnsupported;
        LookSyncPatchDetail = patchCleanupSucceeded
            ? "disabled; Talky Harmony ownership removed"
            : "disable cleanup failed; possible Talky Harmony ownership retained";

        if (runtime != null &&
            TryCleanup("dispose the Talky runtime", runtime.Dispose))
        {
            Runtime = null;
        }

        SSTalkySettings settings = Settings;
        if (settings != null &&
            TryCleanup("deactivate Talky settings", settings.Deactivate))
        {
            Settings = null;
        }

        bool hasNonMonitorResidue =
            HasOwnedLifecycleState(includePerformanceMonitor: false);

        // Retain the monitor until all other owned state has been released.
        TalkyPerformanceMonitor performanceMonitor = PerformanceMonitor;
        if (!hasNonMonitorResidue && performanceMonitor != null &&
            TryCleanup("dispose the performance monitor", performanceMonitor.Dispose))
        {
            PerformanceMonitor = null;
        }

        if (hadOwnedState)
            Logger.Warn("Was disabled.");

        if (HasOwnedLifecycleState())
        {
            Logger.Error(
                "Cleanup is incomplete. Ownership references were retained and a new " +
                "Enable() will be refused until Disable() succeeds.");
        }
        else
        {
            TalkyBridge.ReleaseLifecycle(this);
        }
    }

    private static bool TryCleanup(string operation, Action cleanup)
    {
        try
        {
            cleanup();
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error($"Failed to {operation}: {exception}");
            return false;
        }
    }

    private static void RemoveLookSyncPatchesIfPresent()
    {
        if (HarmonyBridge.HarmonyPresent() || HarmonyBridge.HasOwnedPatchState())
            HarmonyBridge.RemovePatches();
    }

    private static void CleanupOrphanedHarmonyOwnership()
    {
        if (!HarmonyBridge.HasOwnedPatchState())
            return;

        try
        {
            HarmonyBridge.RemovePatches();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Talky found prior Harmony ownership and could not remove it safely. " +
                "Restart the server or resolve the Harmony cleanup failure before enabling.",
                exception);
        }

        if (HarmonyBridge.HasOwnedPatchState())
        {
            throw new InvalidOperationException(
                "Talky still detects prior Harmony ownership after owner-scoped cleanup.");
        }
    }

    private bool HasOwnedNonHarmonyLifecycleState(bool includePerformanceMonitor = true)
    {
        return _enabled ||
               _fullyEnabled ||
               _pluginsEnabledSubscribed ||
               _postEnableHandle.IsValid ||
               Runtime != null ||
               VoiceChattingHandler != null ||
               FakeLookHandler != null ||
               Settings != null ||
               (includePerformanceMonitor && PerformanceMonitor != null);
    }

    private bool HasOwnedLifecycleState(bool includePerformanceMonitor = true)
    {
        return HasOwnedNonHarmonyLifecycleState(includePerformanceMonitor) ||
               HarmonyBridge.HasOwnedPatchState();
    }

    public void OnPluginsEnabled()
    {
        if (_pluginsEnabledSubscribed)
        {
            try
            {
                ServerEvents.PluginsEnabled -= OnPluginsEnabled;
                _pluginsEnabledSubscribed = false;
            }
            catch (Exception exception)
            {
                PerformanceMonitor?.Count(PerformanceCounter.CleanupFailures);
                Logger.Error(
                    "Could not release its PluginsEnabled callback; cleanup will retry: " +
                    exception);
            }
        }

        if (!_enabled)
            return;

        SchedulePostEnable(_lifecycleGeneration, 0.1f);
    }

    private void SchedulePostEnable(int generation, float delay)
    {
        if (_postEnableHandle.IsValid)
        {
            Timing.KillCoroutines(_postEnableHandle);
            _postEnableHandle = default;
        }

        _postEnableHandle = Timing.CallDelayed(delay, () => CompleteEnable(generation));
    }

    private void CompleteEnable(int generation)
    {
        _postEnableHandle = default;
        if (!_enabled || _fullyEnabled || generation != _lifecycleGeneration ||
            FakeLookHandler == null || Runtime == null)
        {
            return;
        }

        try
        {
            CedModRuntimeProbe labApiProbe = CedModBridge.ProbeCandidates(
                PluginLoader.EnabledPlugins.Cast<object>(),
                "LabAPI");
            CedModRuntimeProbe exiledProbe = ExiledBridge.ProbeCedModExiled();
            CedModSelection selection = CedModCompatibilityPolicy.Select(
                labApiProbe.State,
                exiledProbe.State);
            _appliedCedModState = selection.State;
            _appliedCedModReason = selection.Reason;

            bool preferHarmonyLookSync = Config.PreferHarmonyLookSync;
            bool patchApplied = false;
            string patchDetail;
            if (!preferHarmonyLookSync)
            {
                RemoveLookSyncPatchesIfPresent();
                patchDetail = "disabled by prefer_harmony_look_sync=false";
            }
            else if (selection.State == CedModPluginState.FakeSync)
            {
                CedModRuntimeProbe selectedProbe =
                    labApiProbe.State == CedModPluginState.FakeSync
                        ? labApiProbe
                        : exiledProbe;
                patchApplied = HarmonyBridge.ApplyCedModPatches(
                    selectedProbe.Assembly,
                    out patchDetail);
            }
            else if (selection.State == CedModPluginState.NotPresent ||
                     selection.State == CedModPluginState.StockSync)
            {
                patchApplied = HarmonyBridge.ApplyStockPatches(out patchDetail);
            }
            else
            {
                RemoveLookSyncPatchesIfPresent();
                patchDetail = "look-sync patch not attempted for an unsupported CedMod state";
            }

            bool patchOwnershipPossible = HarmonyBridge.HasOwnedPatchState();
            if (!patchApplied && patchOwnershipPossible)
            {
                throw new InvalidOperationException(
                    "Talky cannot continue while a failed look-sync patch attempt retains " +
                    "possible Harmony ownership.");
            }

            CedModLookSyncPlan plan = CedModCompatibilityPolicy.Resolve(
                selection,
                preferHarmonyLookSync,
                patchApplied,
                HarmonyBridge.HarmonyPresent(),
                patchOwnershipPossible);
            FakeLookHandler.ConfigureSyncMode(plan.Mode);
            FakeLookHandler.RegisterEvents();
            _appliedPreferHarmonyLookSync = preferHarmonyLookSync;
            LookSyncMode = plan.Mode;
            LookSyncPatchDetail = patchDetail;
            _fullyEnabled = true;
            ApplyCurrentPerformancePolicy();

            LogCedModCompatibility(
                labApiProbe,
                exiledProbe,
                plan,
                patchDetail);

            Logger.Debug("Was fully enabled.", Config.Debug);
        }
        catch (Exception exception)
        {
            Logger.Error("Post-enable setup failed: " + exception);
            bool eventCleanupSucceeded = true;
            try
            {
                FakeLookHandler?.UnregisterEvents();
            }
            catch (Exception cleanupException)
            {
                eventCleanupSucceeded = false;
                Logger.Error("Failed to remove partial event subscriptions: " + cleanupException);
            }

            bool patchCleanupSucceeded =
                TryCleanup("remove the look-sync Harmony patch", RemoveLookSyncPatchesIfPresent);

            if (!eventCleanupSucceeded || !patchCleanupSucceeded)
            {
                Logger.Error(
                    "Will disable instead of re-registering over unresolved event or " +
                    "Harmony ownership.");
                Disable();
                return;
            }

            try
            {
                FakeLookHandler.ConfigureSyncMode(CedModLookSyncMode.CedModUnsupported);
                FakeLookHandler.RegisterEvents();
                _appliedPreferHarmonyLookSync = Config.PreferHarmonyLookSync;
                LookSyncMode = CedModLookSyncMode.CedModUnsupported;
                LookSyncPatchDetail = "post-enable recovery disabled outbound look sync";
                _fullyEnabled = true;
                ApplyCurrentPerformancePolicy();
                Logger.Error(
                    "Recovered in common-events-only mode; outbound head-sync " +
                    "rewriting is disabled, while other Talky features remain enabled.");
            }
            catch (Exception recoveryException)
            {
                Logger.Error("Post-enable recovery failed: " + recoveryException);
                Disable();
            }
        }
    }

    public override void LoadConfigs()
    {
        base.LoadConfigs();
        Config ??= new Config();
        ValidateConfiguration();
        PerformanceMonitor?.Reconfigure(Config);
        Runtime?.ResetFaultDiagnostics();
        ApplyCurrentPerformancePolicy();
        if (_fullyEnabled && Config.PreferHarmonyLookSync != _appliedPreferHarmonyLookSync)
        {
            Logger.Warn(
                "prefer_harmony_look_sync changed while Talky is active; reload the plugin " +
                "or restart the server to change look-sync hooks safely.");
        }
        if (_fullyEnabled)
        {
            CedModSelection currentSelection = CedModCompatibilityPolicy.Select(
                CedModBridge.ProbeCandidates(
                        PluginLoader.EnabledPlugins.Cast<object>(),
                        "LabAPI")
                    .State,
                ExiledBridge.ProbeCedModExiled().State);
            if (currentSelection.State != _appliedCedModState ||
                currentSelection.Reason != _appliedCedModReason)
            {
                Logger.Warn(
                    "CedMod look-sync state changed while Talky is active; reload the plugin " +
                    "or restart the server before changing look-sync hooks safely.");
            }
        }
    }

    internal void ApplyCurrentPerformancePolicy()
    {
        if (Runtime == null)
            return;

        EffectivePerformancePolicy policy = PerformanceMonitor?.CurrentPolicy ??
                                            EffectivePerformancePolicy.Create(Config, PerformanceTier.Full);
        Runtime.ApplyPerformancePolicy(policy);
    }

    /// <summary>
    /// Records a runtime structural-access failure without tearing down Talky's
    /// voice, face, settings, runtime, or event-subscription ownership.
    /// </summary>
    internal void ReportLookSyncRuntimeFailure(string detail)
    {
        LookSyncMode = CedModLookSyncMode.CedModUnsupported;
        LookSyncPatchDetail = detail;
        ApplyCurrentPerformancePolicy();
        Logger.Error(
            "Outbound head-sync rewriting was disabled for this Talky lifecycle; " +
            "voice and face features remain enabled. " + detail +
            ". Update Talky's game references or the installed game/plugin build " +
            "before retrying.");
    }

    private void ValidateConfiguration()
    {
        // Normalize an omitted translations mapping before registering settings.
        Config.Translations ??= new TranslationsConfig();

        TalkyConfigValidationResult rootValidation =
            TalkyConfigValidator.ValidateAndApply(Config);
        if (rootValidation.WasCorrected)
        {
            Logger.Warn(
                $"Corrected {rootValidation.CorrectionCount} Talky setting(s): " +
                rootValidation.CorrectionSummary);
        }

        PerformanceConfigValidationResult validation =
            PerformanceConfigValidator.ValidateAndApply(Config);
        if (validation.WasCorrected)
        {
            Logger.Warn(
                $"Corrected {validation.CorrectionCount} performance setting(s): " +
                validation.CorrectionSummary);
        }
    }
        
    private void LogCedModCompatibility(
        CedModRuntimeProbe labApiProbe,
        CedModRuntimeProbe exiledProbe,
        CedModLookSyncPlan plan,
        string patchDetail)
    {
        string diagnostic =
            $"Look-sync compatibility: mode={plan.Mode}, reason={plan.Reason}; " +
            $"LabAPI[{labApiProbe.Detail}]; EXILED[{exiledProbe.Detail}]; " +
            $"patch[{patchDetail}]";
        switch (plan.Mode)
        {
            case CedModLookSyncMode.CedModFakeSync:
            case CedModLookSyncMode.StockHarmony:
                Logger.Info(diagnostic);
                break;
            case CedModLookSyncMode.CedModUnsupported:
                Logger.Error(
                    diagnostic + "; outbound head-sync rewriting is disabled, while " +
                    "other Talky features remain enabled.");
                break;
            default:
                if (plan.Reason == CedModCompatibilityReason.PatchUnavailable)
                    Logger.Warn(diagnostic + "; using the vanilla event fallback.");
                else
                    Logger.Debug(diagnostic, Config.Debug);
                break;
        }
    }

        
    public override string Author { get; } = "TayTay";
    public override Version Version { get; } = new Version(1, 0, 0, 3);
    public override string Name { get; } = "Talky";
    public override string Description { get; } = "A plugin that adds animations when players talk, react, and perform common actions.";
    public override Version RequiredApiVersion { get; } = new Version(1, 1, 7, 0);
    
    public string githubRepo = "tayjay/Talky";
        
}
