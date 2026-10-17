using System;
using System.Collections.Generic;
using System.Diagnostics;
using GameCore;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using Talky.Behaviours;
using Talky.Performance;
using Talky.Settings;
using UnityEngine;
using VoiceChat;
using Logger = LabApi.Features.Console.Logger;
using PerformanceCounter = Talky.Performance.PerformanceCounter;

namespace Talky.Runtime;

/// <summary>
/// Owns the frame loop and per-player speech and look state.
/// </summary>
public sealed class TalkyRuntime : IDisposable
{
    private const double RuntimeFaultDiagnosticIntervalSeconds = 60d;

    private readonly Dictionary<uint, SpeechTracker> _speechByNetworkId =
        new Dictionary<uint, SpeechTracker>(128);
    private readonly Dictionary<uint, LookOverride> _lookByNetworkId =
        new Dictionary<uint, LookOverride>(128);
    private readonly List<SpeechTracker> _speechTrackers = new List<SpeechTracker>(128);
    private readonly List<LookOverride> _lookOverrides = new List<LookOverride>(128);
    private readonly List<SpeechTracker> _recentSpeakers = new List<SpeechTracker>(128);
    private readonly HashSet<uint> _activeLookTargets = new HashSet<uint>(128);
    private readonly Dictionary<uint, PendingConsumableEmotion> _pendingConsumables =
        new Dictionary<uint, PendingConsumableEmotion>(128);
    private readonly Dictionary<uint, PendingEmotion> _pendingEmotions =
        new Dictionary<uint, PendingEmotion>(128);
    private readonly List<uint> _duePendingKeys = new List<uint>(128);
    private TalkyRuntimePolicy _policy;
    private EffectivePerformancePolicy _effectivePerformancePolicy;
    private bool _enabled;
    private bool _updateSubscribed;
    private float _nextSpeechTick;
    private float _nextLookTick;
    private float _nextGlanceTick;
    private float _nextRecentPrune;
    private float _lastLookTick;
    private bool _glancePending;
    private double _nextRuntimeFaultDiagnosticAt;
    private int _suppressedRuntimeFaults;

    private long _voicePackets;
    private long _decodedSamples;
    private long _speechTicks;
    private long _lookTicks;
    private long _glanceCandidates;
    private long _emotionWrites;

    public TalkyRuntime()
    {
        Config config = Plugin.Instance?.Config ?? new Config();
        _effectivePerformancePolicy = EffectivePerformancePolicy.Create(
            config,
            Plugin.Instance?.PerformanceMonitor?.Tier ?? PerformanceTier.Full);
        _policy = TalkyRuntimePolicy.FromEffective(_effectivePerformancePolicy);
    }

    public bool IsEnabled => _enabled;
    public TalkyRuntimePolicy Policy => _policy;
    public EffectivePerformancePolicy EffectivePerformancePolicy => _effectivePerformancePolicy;
    public IReadOnlyDictionary<uint, SpeechTracker> SpeechTrackers => _speechByNetworkId;
    public IReadOnlyDictionary<uint, LookOverride> LookOverrides => _lookByNetworkId;
    public int TrackedPlayerCount => _speechTrackers.Count;
    public int RecentSpeakerCount => _recentSpeakers.Count;
    public int ActiveLookTargetCount => _activeLookTargets.Count;
    public bool HasActiveLookTargets => _activeLookTargets.Count != 0;

    public void Enable()
    {
        if (_enabled)
            return;
        if (_updateSubscribed)
        {
            throw new InvalidOperationException(
                "Talky runtime retains an unresolved Unity update subscription.");
        }

        _enabled = true;
        try
        {
            ResetFaultDiagnostics();
            ResetDeadlines(Time.time);
            // Record ownership first so partial subscription can be retried during cleanup.
            _updateSubscribed = true;
            StaticUnityMethods.OnUpdate += OnUpdate;

            foreach (Player player in Player.List)
                RegisterPlayer(player);
        }
        catch
        {
            _enabled = false;
            throw;
        }
    }

    public void Disable()
    {
        // Make retained callbacks inert while preserving ownership for retryable cleanup.
        _enabled = false;
        Exception firstFailure = null;
        if (_updateSubscribed)
        {
            try
            {
                StaticUnityMethods.OnUpdate -= OnUpdate;
                _updateSubscribed = false;
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        }

        try
        {
            ClearPlayers(true);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            Plugin.Instance?.Settings?.ResetPlayerState();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        _pendingConsumables.Clear();
        _pendingEmotions.Clear();
        _duePendingKeys.Clear();
        _activeLookTargets.Clear();
        ResetFaultDiagnostics();

        if (firstFailure != null)
        {
            throw new InvalidOperationException(
                "Talky runtime cleanup is incomplete and must be retried.",
                firstFailure);
        }
    }

    public void Dispose()
    {
        Disable();
    }

    public void ApplyPolicy(TalkyRuntimePolicy policy)
    {
        TalkyRuntimePolicy next = policy ?? TalkyRuntimePolicy.FromConfig(Plugin.Instance?.Config);
        bool lookSyncAvailable = Plugin.Instance?.IsLookSyncAvailable == true;
        if (!lookSyncAvailable)
        {
            next = new TalkyRuntimePolicy(
                next.SpeechInterval,
                next.LookInterval,
                next.GlanceInterval,
                false,
                false,
                false);
            _activeLookTargets.Clear();
        }

        _policy = next;
        ResetDeadlines(Time.time);

        if (!next.EnableGlancing)
        {
            _glancePending = false;
            ClearRecentSpeakers();
        }

        for (int i = 0; i < _lookOverrides.Count; i++)
        {
            LookOverride look = _lookOverrides[i];
            if (look == null)
                continue;

            if (!next.EnableHeadMotion)
                look.ClearOffsets(lookSyncAvailable);
            else if (!next.EnableGlancing)
                look.ClearGlanceOffset();

            RefreshLookActivity(look);
        }
    }

    public void ApplyPerformancePolicy(EffectivePerformancePolicy policy)
    {
        if (policy == null)
            return;

        _effectivePerformancePolicy = policy;
        ApplyPolicy(TalkyRuntimePolicy.FromEffective(policy));
    }

    public void RegisterPlayer(Player player)
    {
        if (!_enabled || player?.ReferenceHub == null)
            return;

        uint networkId = player.NetworkId;
        RemoveScheduledActions(networkId);
        Plugin.Instance?.Settings?.ForgetPlayer(networkId);
        if (!_speechByNetworkId.TryGetValue(networkId, out SpeechTracker speech) || speech == null)
        {
            if (!player.ReferenceHub.TryGetComponent(out speech) ||
                speech == null ||
                speech.IsDisposed)
            {
                speech = player.ReferenceHub.gameObject.AddComponent<SpeechTracker>();
            }

            speech.Initialize(player);
            speech.RuntimeIndex = _speechTrackers.Count;
            _speechTrackers.Add(speech);
            _speechByNetworkId[networkId] = speech;
        }
        else
        {
            speech.Initialize(player);
        }

        if (!_lookByNetworkId.TryGetValue(networkId, out LookOverride look) || look == null)
        {
            if (!player.ReferenceHub.TryGetComponent(out look) ||
                look == null ||
                look.IsDisposed)
            {
                look = player.ReferenceHub.gameObject.AddComponent<LookOverride>();
            }

            look.Initialize(player);
            look.RuntimeIndex = _lookOverrides.Count;
            _lookOverrides.Add(look);
            _lookByNetworkId[networkId] = look;
        }
        else
        {
            look.Initialize(player);
        }

        float now = Time.time;
        look.BindSpeechTracker(speech);
        speech.OnSpawned(now);
        look.OnSpawned(now);
        RefreshLookActivity(look);
    }

    public void UnregisterPlayer(uint networkId, bool destroyComponents = true)
    {
        RemoveScheduledActions(networkId);
        Plugin.Instance?.Settings?.ForgetPlayer(networkId);
        _activeLookTargets.Remove(networkId);

        if (_speechByNetworkId.TryGetValue(networkId, out SpeechTracker speech))
        {
            DetachProxyTarget(speech.Player);
            // Remove the tracker while its recent-speaker index is still valid.
            RemoveRecentSpeaker(speech);
            speech.Dispose();
            if (destroyComponents && speech != null)
                UnityEngine.Object.Destroy(speech);
            _speechByNetworkId.Remove(networkId);
            RemoveSpeechTracker(speech);
        }

        if (_lookByNetworkId.TryGetValue(networkId, out LookOverride look))
        {
            look.Dispose();
            if (destroyComponents && look != null)
                UnityEngine.Object.Destroy(look);
            _lookByNetworkId.Remove(networkId);
            RemoveLookOverride(look);
        }
    }

    public void OnRoundRestart()
    {
        if (!_enabled)
            return;

        ClearPlayers(true);
        _pendingConsumables.Clear();
        _pendingEmotions.Clear();
        _duePendingKeys.Clear();
        _activeLookTargets.Clear();
        Plugin.Instance?.Settings?.ResetPlayerState();
        ResetFaultDiagnostics();
        ResetDeadlines(Time.time);

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.ResetForRound();
        if (monitor != null)
            ApplyPerformancePolicy(monitor.CurrentPolicy);
    }

    public void ResetFaultDiagnostics()
    {
        _nextRuntimeFaultDiagnosticAt = 0d;
        _suppressedRuntimeFaults = 0;
    }

    public bool TryGetSpeechTracker(uint networkId, out SpeechTracker tracker) =>
        _speechByNetworkId.TryGetValue(networkId, out tracker) && tracker != null;

    public bool TryGetLookOverride(uint networkId, out LookOverride look) =>
        _lookByNetworkId.TryGetValue(networkId, out look) && look != null;

    public bool IsLookTargetActive(uint networkId) => _activeLookTargets.Contains(networkId);

    public void HandleVoice(uint networkId, byte[] data, int length)
    {
        HandleVoice(networkId, data, length, VoiceChatChannel.None);
    }

    public void HandleVoice(
        uint networkId,
        byte[] data,
        int length,
        VoiceChatChannel channel)
    {
        if (TryGetSpeechTracker(networkId, out SpeechTracker tracker))
            HandleVoice(tracker, data, length, channel);
    }

    public void HandleVoice(SpeechTracker source, byte[] data, int length)
    {
        HandleVoice(source, data, length, VoiceChatChannel.None);
    }

    public void HandleVoice(
        SpeechTracker source,
        byte[] data,
        int length,
        VoiceChatChannel channel)
    {
        if (!_enabled || source == null)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        long started = monitor?.Begin(PerformanceMetric.VoicePacket) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            float now = Time.time;
            _voicePackets++;
            monitor?.Count(PerformanceCounter.VoicePackets);
            int decoded = source.DecodeVoicePacket(data, length, now);
            if (decoded <= 0)
                return;

            _decodedSamples += decoded;
            monitor?.Count(PerformanceCounter.DecodedSamples, decoded);
            SpeechTracker target = ResolveVoiceTarget(source, source.Proxy);
            if (!target.PresentVoiceFrom(source, decoded, now))
                return;

            if (_policy.EnableGlancing &&
                target.LastPresentedVoiceWasAudible)
            {
                if (channel == VoiceChatChannel.Proximity)
                    AddRecentSpeaker(target);
                else
                {
                    // Remote channels must not retain proximity-based glance eligibility.
                    RemoveRecentSpeaker(target);
                    monitor?.Count(PerformanceCounter.RemoteChannelGlanceRejections);
                }
            }
        }
        finally
        {
            monitor?.End(PerformanceMetric.VoicePacket, started);
        }
    }

    public void ScheduleConsumableEmotion(
        PlayerUsingItemEventArgs eventArgs,
        float delaySeconds,
        int durationMilliseconds)
    {
        if (!_enabled ||
            eventArgs?.Player == null || eventArgs.UsableItem == null)
            return;

        uint networkId = eventArgs.Player.NetworkId;
        if (!TryGetSpeechTracker(networkId, out _))
            return;

        if (_pendingEmotions.Remove(networkId))
        {
            Plugin.Instance?.PerformanceMonitor?.Count(
                PerformanceCounter.PendingActionsCoalesced);
        }

        bool coalesced = _pendingConsumables.ContainsKey(networkId);
        _pendingConsumables[networkId] = new PendingConsumableEmotion(
            networkId,
            eventArgs,
            Time.time + Mathf.Max(0f, delaySeconds),
            durationMilliseconds,
            eventArgs.UsableItem.Type);
        Plugin.Instance?.PerformanceMonitor?.Count(
            coalesced
                ? PerformanceCounter.PendingActionsCoalesced
                : PerformanceCounter.PendingActionsQueued);
    }

    public void TriggerEmotion(uint networkId, EmotionPresetType preset, int durationMilliseconds)
    {
        if (!_enabled)
            return;

        if (TryGetSpeechTracker(networkId, out SpeechTracker tracker))
            tracker.OverrideEmotion(preset, durationMilliseconds);
    }

    public void RefreshLookActivity(LookOverride look)
    {
        if (look?.Player == null)
            return;

        uint networkId = look.Player.NetworkId;
        if (look.HasActiveOffsets)
            _activeLookTargets.Add(networkId);
        else
            _activeLookTargets.Remove(networkId);
    }

    private void ExpirePendingLookCorrections(int currentFrame)
    {
        if (_activeLookTargets.Count == 0)
            return;

        for (int i = 0; i < _lookOverrides.Count; i++)
        {
            LookOverride look = _lookOverrides[i];
            if (look != null && look.TryExpireZeroCorrection(currentFrame))
                RefreshLookActivity(look);
        }
    }

    public void RecordEmotionWrite()
    {
        _emotionWrites++;
        Plugin.Instance?.PerformanceMonitor?.Count(PerformanceCounter.EmotionWrites);
    }

    public void RecordGlanceCandidate()
    {
        _glanceCandidates++;
        Plugin.Instance?.PerformanceMonitor?.Count(PerformanceCounter.GlanceCandidates);
    }

    public TalkyRuntimeCounters CaptureCounters()
    {
        return new TalkyRuntimeCounters(
            _voicePackets,
            _decodedSamples,
            _speechTicks,
            _lookTicks,
            _glanceCandidates,
            _emotionWrites,
            _speechTrackers.Count,
            _recentSpeakers.Count,
            _activeLookTargets.Count);
    }

    private void OnUpdate()
    {
        if (!_enabled)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        bool glanceEvaluated = false;
        long glanceDurationTicks = 0L;
        bool updateFailed = false;
        try
        {
            float now = Time.time;
            long schedulerStarted = monitor?.Begin(PerformanceMetric.SchedulerOverhead, 1) ??
                                    PerformanceProfiler.NotSampled;
            try
            {
                ProcessPendingConsumables(now);
                ProcessPendingEmotions(now);
                Plugin.Instance?.Settings?.ProcessPendingDefaultPresets(now);
                if (!_policy.EnableHeadMotion && _activeLookTargets.Count != 0)
                    ExpirePendingLookCorrections(Time.frameCount);
            }
            finally
            {
                monitor?.End(PerformanceMetric.SchedulerOverhead, schedulerStarted);
            }

            if (now >= _nextSpeechTick)
            {
                _nextSpeechTick = NextDeadline(now, _policy.SpeechInterval);
                Config config = Plugin.Instance?.Config;
                int resetTime = config?.EmotionResetTime ?? 500;
                double lowThreshold = config?.LowDbThreshold ?? -80d;
                double highThreshold = config?.HighDbThreshold ?? -30d;
                long faceStarted = monitor?.Begin(PerformanceMetric.FaceBatch, 1) ??
                                   PerformanceProfiler.NotSampled;
                try
                {
                    int faceTicks = 0;
                    for (int i = 0; i < _speechTrackers.Count; i++)
                    {
                        SpeechTracker tracker = _speechTrackers[i];
                        if (tracker == null)
                            continue;

                        tracker.TickSpeech(now, resetTime, lowThreshold, highThreshold);
                        _speechTicks++;
                        faceTicks++;
                    }

                    if (faceTicks > 0)
                        monitor?.Count(PerformanceCounter.FaceTicks, faceTicks);
                }
                finally
                {
                    monitor?.End(PerformanceMetric.FaceBatch, faceStarted);
                }
            }

            if (_policy.EnableGlancing && now >= _nextGlanceTick)
            {
                _nextGlanceTick = NextDeadline(now, _policy.GlanceInterval);
                _glancePending = true;
            }

            bool lookDue = _policy.EnableHeadMotion && now >= _nextLookTick;
            bool evaluateGlance = lookDue && _policy.EnableGlancing && _glancePending;
            if (evaluateGlance)
            {
                glanceEvaluated = true;
                _glancePending = false;
            }

            if (_policy.EnableGlancing && (evaluateGlance || now >= _nextRecentPrune))
            {
                schedulerStarted = monitor?.Begin(PerformanceMetric.SchedulerOverhead, 1) ??
                                   PerformanceProfiler.NotSampled;
                try
                {
                    PruneRecentSpeakers(now);
                    _nextRecentPrune = now + 1f;

                    if (evaluateGlance)
                    {
                        for (int i = 0; i < _recentSpeakers.Count; i++)
                        {
                            SpeechTracker tracker = _recentSpeakers[i];
                            if (tracker != null)
                                tracker.CaptureGlanceSnapshot();
                        }
                    }
                }
                finally
                {
                    monitor?.End(PerformanceMetric.SchedulerOverhead, schedulerStarted);
                }
            }

            if (lookDue)
            {
                _nextLookTick = NextDeadline(now, _policy.LookInterval);
                float deltaTime = _lastLookTick > 0f
                    ? Mathf.Max(0f, now - _lastLookTick)
                    : Time.deltaTime;
                _lastLookTick = now;

                long lookStarted = monitor?.Begin(PerformanceMetric.LookBatch, 1) ??
                                   PerformanceProfiler.NotSampled;
                try
                {
                    long glanceCandidates = 0;
                    int lookTicks = 0;
                    int glanceTicks = 0;
                    int glanceSelections = 0;
                    for (int i = 0; i < _lookOverrides.Count; i++)
                    {
                        LookOverride look = _lookOverrides[i];
                        if (look == null)
                            continue;

                        glanceCandidates += look.TickLook(
                            now,
                            deltaTime,
                            _recentSpeakers,
                            evaluateGlance,
                            _policy,
                            monitor?.IsDurationRecordingEnabled == true,
                            out long lookGlanceDurationTicks);
                        glanceDurationTicks += lookGlanceDurationTicks;
                        RefreshLookActivity(look);
                        _lookTicks++;
                        lookTicks++;
                        if (evaluateGlance)
                        {
                            glanceTicks++;
                            if (look.GlanceSelectedOnLastTick)
                                glanceSelections++;
                        }
                    }

                    _glanceCandidates += glanceCandidates;
                    if (glanceCandidates > 0)
                        monitor?.Count(PerformanceCounter.GlanceCandidates, glanceCandidates);
                    if (lookTicks > 0)
                        monitor?.Count(PerformanceCounter.LookTicks, lookTicks);
                    if (glanceTicks > 0)
                        monitor?.Count(PerformanceCounter.GlanceTicks, glanceTicks);
                    if (glanceSelections > 0)
                        monitor?.Count(PerformanceCounter.GlanceSelections, glanceSelections);
                }
                finally
                {
                    monitor?.End(PerformanceMetric.LookBatch, lookStarted);
                }
            }

        }
        catch (Exception exception)
        {
            updateFailed = true;
            HandleUpdateFailure(exception);
        }
        finally
        {
            try
            {
                if (glanceEvaluated)
                    monitor?.RecordDurationTicks(PerformanceMetric.GlanceBatch, glanceDurationTicks);
                CompleteFrameMonitoring(monitor);
            }
            catch (Exception exception)
            {
                if (!updateFailed)
                    HandleUpdateFailure(exception);
                else
                    monitor?.Count(PerformanceCounter.RuntimeUpdateFailures);
            }
        }
    }

    private void CompleteFrameMonitoring(TalkyPerformanceMonitor monitor)
    {
        if (monitor == null)
            return;

        monitor.OnFrame(
            MonotonicSeconds(),
            Time.unscaledDeltaTime * 1000d,
            Time.smoothDeltaTime * 1000d,
            ServerStatic.ServerTickrate,
            IdleMode.IdleModeActive,
            RoundStart.RoundStarted &&
            (RoundSummary.singleton == null || !RoundSummary.singleton.IsRoundEnded),
            _speechTrackers.Count,
            _recentSpeakers.Count,
            _activeLookTargets.Count);

        EffectivePerformancePolicy effective = monitor.CurrentPolicy;
        if (ReferenceEquals(effective, _effectivePerformancePolicy))
            return;

        long schedulerStarted = monitor.Begin(PerformanceMetric.SchedulerOverhead, 1);
        try
        {
            ApplyPerformancePolicy(effective);
        }
        finally
        {
            monitor.End(PerformanceMetric.SchedulerOverhead, schedulerStarted);
        }
    }

    private void HandleUpdateFailure(Exception exception)
    {
        Type failureType = exception?.GetType() ?? typeof(Exception);
        Plugin.Instance?.PerformanceMonitor?.Count(PerformanceCounter.RuntimeUpdateFailures);

        // Report persistent frame-loop failures once a minute with an aggregate count.
        _suppressedRuntimeFaults++;
        double nowSeconds = MonotonicSeconds();
        if (nowSeconds >= _nextRuntimeFaultDiagnosticAt)
        {
            TryLogRuntimeError(
                "Runtime update failed with " + failureType.Name +
                "; occurrences since the previous diagnostic=" +
                _suppressedRuntimeFaults + ".");
            _suppressedRuntimeFaults = 0;
            _nextRuntimeFaultDiagnosticAt =
                nowSeconds + RuntimeFaultDiagnosticIntervalSeconds;
        }
    }

    private static void TryLogRuntimeError(string message)
    {
        try
        {
            Logger.Error(message);
        }
        catch
        {
            // Keep logging failures out of the frame loop.
        }
    }

    private void AddRecentSpeaker(SpeechTracker tracker)
    {
        if (tracker == null || tracker.RecentSpeakerIndex >= 0)
            return;

        tracker.RecentSpeakerIndex = _recentSpeakers.Count;
        _recentSpeakers.Add(tracker);
    }

    private SpeechTracker ResolveVoiceTarget(SpeechTracker source, Player proxy)
    {
        if (source == null)
            return null;

        SpeechTracker target = source;
        Player candidate = proxy;
        int remainingHops = _speechTrackers.Count;

        while (candidate != null)
        {
            // A valid proxy chain cannot exceed the registered tracker count.
            if (remainingHops-- <= 0)
                return source;

            Player targetPlayer = target.Player;
            if (candidate.IsDestroyed ||
                targetPlayer == null ||
                ReferenceEquals(candidate, targetPlayer) ||
                ReferenceEquals(candidate.ReferenceHub, targetPlayer.ReferenceHub) ||
                !TryGetSpeechTracker(candidate.NetworkId, out SpeechTracker proxyTracker) ||
                proxyTracker.IsDisposed ||
                proxyTracker.Player == null ||
                proxyTracker.Player.IsDestroyed)
            {
                break;
            }

            target = proxyTracker;
            candidate = target.Proxy;
        }

        return target;
    }

    private void DetachProxyTarget(Player departingPlayer)
    {
        if (departingPlayer == null)
            return;

        ReferenceHub departingHub = departingPlayer.ReferenceHub;
        for (int i = 0; i < _speechTrackers.Count; i++)
        {
            SpeechTracker tracker = _speechTrackers[i];
            Player proxy = tracker?.Proxy;
            if (proxy != null &&
                (ReferenceEquals(proxy, departingPlayer) ||
                 ReferenceEquals(proxy.ReferenceHub, departingHub)))
            {
                tracker.Proxy = null;
            }
        }
    }

    private void PruneRecentSpeakers(float now)
    {
        float duration = Plugin.Instance?.Config?.GlanceMaxDuration ?? 1000f;
        for (int i = _recentSpeakers.Count - 1; i >= 0; i--)
        {
            SpeechTracker tracker = _recentSpeakers[i];
            if (tracker == null || !tracker.WasRecentlySpeaking(now, duration))
                RemoveRecentSpeakerAt(i);
        }
    }

    private void RemoveRecentSpeaker(SpeechTracker tracker)
    {
        if (tracker == null)
            return;

        int index = tracker.RecentSpeakerIndex;
        if (index >= 0 && index < _recentSpeakers.Count && _recentSpeakers[index] == tracker)
            RemoveRecentSpeakerAt(index);
        else
            tracker.RecentSpeakerIndex = -1;
    }

    private void ClearRecentSpeakers()
    {
        for (int i = 0; i < _recentSpeakers.Count; i++)
        {
            SpeechTracker tracker = _recentSpeakers[i];
            if (tracker != null)
                tracker.RecentSpeakerIndex = -1;
        }

        _recentSpeakers.Clear();
    }

    private void RemoveRecentSpeakerAt(int index)
    {
        int lastIndex = _recentSpeakers.Count - 1;
        SpeechTracker removed = _recentSpeakers[index];
        if (index != lastIndex)
        {
            SpeechTracker moved = _recentSpeakers[lastIndex];
            _recentSpeakers[index] = moved;
            moved.RecentSpeakerIndex = index;
        }

        _recentSpeakers.RemoveAt(lastIndex);
        if (removed != null)
            removed.RecentSpeakerIndex = -1;
    }

    private void RemoveSpeechTracker(SpeechTracker tracker)
    {
        int index = tracker.RuntimeIndex;
        if (index < 0 || index >= _speechTrackers.Count || _speechTrackers[index] != tracker)
        {
            index = _speechTrackers.IndexOf(tracker);
            if (index < 0)
                return;
        }

        int lastIndex = _speechTrackers.Count - 1;
        if (index != lastIndex)
        {
            SpeechTracker moved = _speechTrackers[lastIndex];
            _speechTrackers[index] = moved;
            moved.RuntimeIndex = index;
        }

        _speechTrackers.RemoveAt(lastIndex);
        tracker.RuntimeIndex = -1;
    }

    private void RemoveLookOverride(LookOverride look)
    {
        int index = look.RuntimeIndex;
        if (index < 0 || index >= _lookOverrides.Count || _lookOverrides[index] != look)
        {
            index = _lookOverrides.IndexOf(look);
            if (index < 0)
                return;
        }

        int lastIndex = _lookOverrides.Count - 1;
        if (index != lastIndex)
        {
            LookOverride moved = _lookOverrides[lastIndex];
            _lookOverrides[index] = moved;
            moved.RuntimeIndex = index;
        }

        _lookOverrides.RemoveAt(lastIndex);
        look.RuntimeIndex = -1;
    }

    private void ProcessPendingConsumables(float now)
    {
        _duePendingKeys.Clear();
        foreach (KeyValuePair<uint, PendingConsumableEmotion> pair in _pendingConsumables)
        {
            if (now >= pair.Value.DueTime)
                _duePendingKeys.Add(pair.Key);
        }

        for (int i = 0; i < _duePendingKeys.Count; i++)
        {
            uint networkId = _duePendingKeys[i];
            if (!_pendingConsumables.TryGetValue(networkId, out PendingConsumableEmotion pending))
                continue;

            _pendingConsumables.Remove(networkId);
            PlayerUsingItemEventArgs eventArgs = pending.EventArgs;
            if (eventArgs == null || !eventArgs.IsAllowed ||
                eventArgs.UsableItem == null || eventArgs.UsableItem.IsDestroyed ||
                !eventArgs.UsableItem.IsUsing ||
                !TryGetSpeechTracker(pending.NetworkId, out SpeechTracker tracker))
            {
                continue;
            }

            tracker.OverrideEmotion(EmotionPresetType.Scared, pending.DurationMilliseconds);
            if (pending.ItemType == ItemType.AntiSCP207)
            {
                QueuePendingEmotion(new PendingEmotion(
                    pending.NetworkId,
                    now + 2.4f,
                    EmotionPresetType.AwkwardSmile,
                    500));
            }
            else if (pending.ItemType == ItemType.SCP207)
            {
                QueuePendingEmotion(new PendingEmotion(
                    pending.NetworkId,
                    now + 2.5f,
                    EmotionPresetType.Happy,
                    500));
            }
        }

        _duePendingKeys.Clear();
    }

    private void ProcessPendingEmotions(float now)
    {
        _duePendingKeys.Clear();
        foreach (KeyValuePair<uint, PendingEmotion> pair in _pendingEmotions)
        {
            if (now >= pair.Value.DueTime)
                _duePendingKeys.Add(pair.Key);
        }

        for (int i = 0; i < _duePendingKeys.Count; i++)
        {
            uint networkId = _duePendingKeys[i];
            if (!_pendingEmotions.TryGetValue(networkId, out PendingEmotion pending))
                continue;

            _pendingEmotions.Remove(networkId);
            TriggerEmotion(pending.NetworkId, pending.Preset, pending.DurationMilliseconds);
        }

        _duePendingKeys.Clear();
    }

    private void RemoveScheduledActions(uint networkId)
    {
        _pendingConsumables.Remove(networkId);
        _pendingEmotions.Remove(networkId);
    }

    private void QueuePendingEmotion(PendingEmotion pending)
    {
        bool coalesced = _pendingEmotions.ContainsKey(pending.NetworkId);
        _pendingEmotions[pending.NetworkId] = pending;
        Plugin.Instance?.PerformanceMonitor?.Count(
            coalesced
                ? PerformanceCounter.PendingActionsCoalesced
                : PerformanceCounter.PendingActionsQueued);
    }

    private void ClearPlayers(bool destroyComponents)
    {
        while (_speechTrackers.Count > 0)
        {
            SpeechTracker tracker = _speechTrackers[_speechTrackers.Count - 1];
            uint networkId = tracker?.Player?.NetworkId ?? 0u;
            if (networkId != 0u && _speechByNetworkId.ContainsKey(networkId))
                UnregisterPlayer(networkId, destroyComponents);
            else
            {
                tracker?.Dispose();
                if (destroyComponents && tracker != null)
                    UnityEngine.Object.Destroy(tracker);
                if (tracker == null)
                    _speechTrackers.RemoveAt(_speechTrackers.Count - 1);
                else
                    RemoveSpeechTracker(tracker);
            }
        }

        // Clear any look component left by partial registration.
        while (_lookOverrides.Count > 0)
        {
            LookOverride look = _lookOverrides[_lookOverrides.Count - 1];
            look?.Dispose();
            if (destroyComponents && look != null)
                UnityEngine.Object.Destroy(look);
            if (look == null)
                _lookOverrides.RemoveAt(_lookOverrides.Count - 1);
            else
                RemoveLookOverride(look);
        }

        _speechByNetworkId.Clear();
        _lookByNetworkId.Clear();
        _recentSpeakers.Clear();
        _activeLookTargets.Clear();
    }

    private void ResetDeadlines(float now)
    {
        _nextSpeechTick = now;
        _nextLookTick = now;
        _nextGlanceTick = now;
        _nextRecentPrune = now;
        _lastLookTick = 0f;
        _glancePending = false;
    }

    private static float NextDeadline(float now, float interval)
    {
        return interval <= 0f ? now : now + interval;
    }

    private static double MonotonicSeconds()
    {
        return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    private readonly struct PendingConsumableEmotion
    {
        public PendingConsumableEmotion(
            uint networkId,
            PlayerUsingItemEventArgs eventArgs,
            float dueTime,
            int durationMilliseconds,
            ItemType itemType)
        {
            NetworkId = networkId;
            EventArgs = eventArgs;
            DueTime = dueTime;
            DurationMilliseconds = durationMilliseconds;
            ItemType = itemType;
        }

        public uint NetworkId { get; }
        public PlayerUsingItemEventArgs EventArgs { get; }
        public float DueTime { get; }
        public int DurationMilliseconds { get; }
        public ItemType ItemType { get; }
    }

    private readonly struct PendingEmotion
    {
        public PendingEmotion(
            uint networkId,
            float dueTime,
            EmotionPresetType preset,
            int durationMilliseconds)
        {
            NetworkId = networkId;
            DueTime = dueTime;
            Preset = preset;
            DurationMilliseconds = durationMilliseconds;
        }

        public uint NetworkId { get; }
        public float DueTime { get; }
        public EmotionPresetType Preset { get; }
        public int DurationMilliseconds { get; }
    }
}

public readonly struct TalkyRuntimeCounters
{
    public TalkyRuntimeCounters(
        long voicePackets,
        long decodedSamples,
        long speechTicks,
        long lookTicks,
        long glanceCandidates,
        long emotionWrites,
        int trackedPlayers,
        int recentSpeakers,
        int activeLookTargets)
    {
        VoicePackets = voicePackets;
        DecodedSamples = decodedSamples;
        SpeechTicks = speechTicks;
        LookTicks = lookTicks;
        GlanceCandidates = glanceCandidates;
        EmotionWrites = emotionWrites;
        TrackedPlayers = trackedPlayers;
        RecentSpeakers = recentSpeakers;
        ActiveLookTargets = activeLookTargets;
    }

    public long VoicePackets { get; }
    public long DecodedSamples { get; }
    public long SpeechTicks { get; }
    public long LookTicks { get; }
    public long GlanceCandidates { get; }
    public long EmotionWrites { get; }
    public int TrackedPlayers { get; }
    public int RecentSpeakers { get; }
    public int ActiveLookTargets { get; }
}
