using System;
using System.Data;
using System.Linq;
using System.Reflection;
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
    public static Plugin Instance { get; private set; }

    private CoroutineHandle _postEnableHandle;
    private int _lifecycleGeneration;
    private bool _enabled;
    private bool _fullyEnabled;
    private bool _pluginsEnabledSubscribed;

    // Set by the EXILED native path (TalkyBridge.Enable) before Enable() runs, so Enable() runs
    // post-load setup directly instead of subscribing to LabAPI's ServerEvents.PluginsEnabled,
    // which has already fired by the time EXILED enables the embedded native plugin.
    internal bool ExiledNativeMode;

    public Plugin()
    {
        Assembly ownAssembly = typeof(Plugin).Assembly;
        if(AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
               assembly != ownAssembly &&
               assembly.GetType(typeof(Plugin).FullName, false) != null))
            throw new DuplicateNameException("Multiple Talky plugin assemblies detected. Please remove legacy versions of Talky to use this.");

        Plugin previous = Instance;
        if (previous != null &&
            (previous._enabled || previous._fullyEnabled ||
             previous.HasOwnedLifecycleState()))
        {
            throw new InvalidOperationException(
                "A previous Talky plugin instance still owns runtime state, subscriptions, " +
                "settings, coroutines, or Harmony patches. Its Disable() must complete before " +
                "another instance can be constructed.");
        }

        Instance = this;
        WireExiledBridge();
        ExiledRegistration.TryRegisterNativePlugin();
    }

    // Wires identity + lifecycle for the embedded EXILED shim (Talky.Exiled.Shim.dll). The shim reads
    // these through TalkyBridge so it needs no compile-time reference back to this assembly.
    // Harmless on LabAPI-only servers, where the shim is never loaded.
    private void WireExiledBridge()
    {
        TalkyBridge.Name = Name;
        TalkyBridge.Prefix = Name.ToLower();
        TalkyBridge.Author = Author;
        TalkyBridge. Version = Version;
        TalkyBridge.Enable = cfg =>
        {
            if (cfg != null) Config = cfg;   // adopt EXILED's loaded config
            ExiledNativeMode = true;
            Enable();
        };
        TalkyBridge.Disable = Disable;
    }
    
    public override void Enable()
    {
        if (_enabled)
            return;

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
        try
        {
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
                // PluginsEnabled is an initial-load signal. A reload after that signal needs a
                // bounded fallback so the plugin cannot remain half-enabled forever.
                SchedulePostEnable(generation, 1f);
            }

            Logger.Info("Talky was enabled.");
        }
        catch
        {
            try
            {
                Disable();
            }
            catch (Exception cleanupException)
            {
                Logger.Error("Talky failed to roll back a partial enable: " + cleanupException);
            }

            throw;
        }
    }
    
 
    public override void Disable()
    {
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

        TryCleanup("remove the CedMod sync patch", RemoveCedModPatchIfPresent);

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

        // A later Disable() retry reuses the monitor and disposes it only after all other
        // ownership is demonstrably gone.
        TalkyPerformanceMonitor performanceMonitor = PerformanceMonitor;
        if (!hasNonMonitorResidue && performanceMonitor != null &&
            TryCleanup("dispose the performance monitor", performanceMonitor.Dispose))
        {
            PerformanceMonitor = null;
        }

        if (hadOwnedState)
            Logger.Warn("Talky was disabled.");

        if (HasOwnedLifecycleState())
        {
            Logger.Error(
                "Talky cleanup is incomplete. Ownership references were retained and a new " +
                "Enable() will be refused until Disable() succeeds.");
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
            Logger.Error($"Talky failed to {operation}: {exception}");
            return false;
        }
    }

    private static void RemoveCedModPatchIfPresent()
    {
        if (HarmonyBridge.HarmonyPresent() || HarmonyBridge.HasOwnedPatchState())
            HarmonyBridge.RemovePatches();
    }

    private bool HasOwnedLifecycleState(bool includePerformanceMonitor = true)
    {
        return _pluginsEnabledSubscribed ||
               _postEnableHandle.IsValid ||
               Runtime != null ||
               VoiceChattingHandler != null ||
               FakeLookHandler != null ||
               Settings != null ||
               (includePerformanceMonitor && PerformanceMonitor != null) ||
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
                    "Talky could not release its PluginsEnabled callback; cleanup will retry: " +
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

            bool patchApplied = false;
            string patchDetail = "CedMod helper patch not required";
            if (selection.State == CedModPluginState.FakeSync)
            {
                CedModRuntimeProbe selectedProbe =
                    labApiProbe.State == CedModPluginState.FakeSync
                        ? labApiProbe
                        : exiledProbe;
                if (!HarmonyBridge.HarmonyPresent())
                {
                    patchDetail = "0Harmony is not loaded";
                }
                else
                {
                    patchApplied = HarmonyBridge.ApplyPatches(
                        selectedProbe.Assembly,
                        out patchDetail);
                    if (!patchApplied && HarmonyBridge.HasOwnedPatchState())
                    {
                        throw new InvalidOperationException(
                            "Talky cannot continue while a failed CedMod patch attempt " +
                            "retains possible Harmony ownership.");
                    }
                }
            }
            else
            {
                RemoveCedModPatchIfPresent();
            }

            CedModLookSyncPlan plan = CedModCompatibilityPolicy.Resolve(
                selection,
                patchApplied);
            FakeLookHandler.ConfigureSyncMode(plan.Mode);
            FakeLookHandler.RegisterEvents();
            _fullyEnabled = true;

            LogCedModCompatibility(
                labApiProbe,
                exiledProbe,
                plan,
                patchDetail);

            Logger.Debug("Talky was fully enabled.", Config.Debug);
        }
        catch (Exception exception)
        {
            Logger.Error("Talky post-enable setup failed: " + exception);
            bool eventCleanupSucceeded = true;
            try
            {
                FakeLookHandler?.UnregisterEvents();
            }
            catch (Exception cleanupException)
            {
                eventCleanupSucceeded = false;
                Logger.Error("Talky failed to remove partial event subscriptions: " + cleanupException);
            }

            bool patchCleanupSucceeded =
                TryCleanup("remove the CedMod sync patch", RemoveCedModPatchIfPresent);

            if (!eventCleanupSucceeded || !patchCleanupSucceeded)
            {
                Logger.Error(
                    "Talky will disable instead of re-registering over unresolved event or " +
                    "Harmony ownership.");
                Disable();
                return;
            }

            try
            {
                FakeLookHandler.ConfigureSyncMode(CedModLookSyncMode.CedModUnsupported);
                FakeLookHandler.RegisterEvents();
                _fullyEnabled = true;
                Logger.Error(
                    "Talky recovered in common-events-only mode; outbound head-sync " +
                    "rewriting is disabled, while other Talky features remain enabled.");
            }
            catch (Exception recoveryException)
            {
                Logger.Error("Talky post-enable recovery failed: " + recoveryException);
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
        Runtime?.ResetFailureQuarantine();
        ApplyCurrentPerformancePolicy();
        Runtime?.RefreshDiagnosticsGauges();
    }

    internal void ApplyCurrentPerformancePolicy()
    {
        if (Runtime == null)
            return;

        EffectivePerformancePolicy policy = PerformanceMonitor?.CurrentPolicy ??
                                            EffectivePerformancePolicy.Create(Config, PerformanceTier.Full);
        Runtime.ApplyPerformancePolicy(policy);
    }

    private int ValidateConfiguration()
    {
        FunctionalConfigValidationResult functionalValidation =
            FunctionalConfigValidator.ValidateAndApply(Config);
        PerformanceConfigValidationResult validation =
            PerformanceConfigValidator.ValidateAndApply(Config);
        int correctionCount =
            functionalValidation.CorrectionCount + validation.CorrectionCount;

        if (functionalValidation.WasCorrected)
        {
            Logger.Warn(
                $"Talky corrected {functionalValidation.CorrectionCount} functional " +
                $"setting(s): {functionalValidation.CorrectionSummary}");
        }

        if (validation.WasCorrected)
        {
            Logger.Warn(
                $"Talky corrected {validation.CorrectionCount} performance setting(s): " +
                validation.CorrectionSummary);
        }

        return correctionCount;
    }
        
    private void LogCedModCompatibility(
        CedModRuntimeProbe labApiProbe,
        CedModRuntimeProbe exiledProbe,
        CedModLookSyncPlan plan,
        string patchDetail)
    {
        string diagnostic =
            $"CedMod sync compatibility: mode={plan.Mode}, reason={plan.Reason}; " +
            $"LabAPI[{labApiProbe.Detail}]; EXILED[{exiledProbe.Detail}]; " +
            $"patch[{patchDetail}]";
        switch (plan.Mode)
        {
            case CedModLookSyncMode.CedModFakeSync:
                Logger.Warn(diagnostic);
                break;
            case CedModLookSyncMode.CedModUnsupported:
                Logger.Error(
                    diagnostic + "; outbound head-sync rewriting is disabled, while " +
                    "other Talky features remain enabled.");
                break;
            default:
                Logger.Debug(diagnostic, Config.Debug);
                break;
        }
    }

        
    public override string Author { get; } = "TayTay";
    public override Version Version { get; } = typeof(Plugin).Assembly.GetName().Version;
    public override string Name { get; } = "Talky";
    public override string Description { get; } = "A plugin that adds animations when players talk, react, and perform common actions.";
    public override Version RequiredApiVersion { get; } = new Version(1, 1, 7, 0);
    
    public string githubRepo = "tayjay/Talky";
        
}
