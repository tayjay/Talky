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
using Logger = LabApi.Features.Console.Logger;
using PerformanceCounter = Talky.Performance.PerformanceCounter;

namespace Talky.Runtime;

/// <summary>
/// Single frame-loop owner for all Talky player state.
/// </summary>
public sealed class TalkyRuntime : IDisposable
{
    private readonly Dictionary<uint, SpeechTracker> _speechByNetworkId =
        new Dictionary<uint, SpeechTracker>(128);
    private readonly Dictionary<uint, LookOverride> _lookByNetworkId =
        new Dictionary<uint, LookOverride>(128);
    private readonly List<SpeechTracker> _speechTrackers = new List<SpeechTracker>(128);
    private readonly List<LookOverride> _lookOverrides = new List<LookOverride>(128);
    private readonly List<SpeechTracker> _recentSpeakers = new List<SpeechTracker>(128);
    private readonly HashSet<uint> _activeLookTargets = new HashSet<uint>(128);
    private readonly List<PendingConsumableEmotion> _pendingConsumables =
        new List<PendingConsumableEmotion>(32);
    private readonly List<PendingEmotion> _pendingEmotions = new List<PendingEmotion>(32);

    private TalkyRuntimePolicy _policy;
    private EffectivePerformancePolicy _effectivePerformancePolicy;
    private bool _enabled;
    private float _nextSpeechTick;
    private float _nextLookTick;
    private float _nextGlanceTick;
    private float _nextRecentPrune;
    private float _lastLookTick;
    private bool _glancePending;

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

        _enabled = true;
        ResetDeadlines(Time.time);
        StaticUnityMethods.OnUpdate += OnUpdate;

        // This is a one-time/reload path, not a frame hot path.
        foreach (Player player in Player.List)
            RegisterPlayer(player);
    }

    public void Disable()
    {
        if (!_enabled && _speechTrackers.Count == 0 && _lookOverrides.Count == 0)
            return;

        if (_enabled)
        {
            StaticUnityMethods.OnUpdate -= OnUpdate;
            _enabled = false;
        }

        ClearPlayers(true);
        _pendingConsumables.Clear();
        _pendingEmotions.Clear();
        _activeLookTargets.Clear();
    }

    public void Dispose()
    {
        Disable();
    }

    public void ApplyPolicy(TalkyRuntimePolicy policy)
    {
        TalkyRuntimePolicy next = policy ?? TalkyRuntimePolicy.FromConfig(Plugin.Instance?.Config);
        bool headMotionDisabled = _policy == null ||
                                  (_policy.EnableHeadMotion && !next.EnableHeadMotion);
        bool glancingDisabled = _policy == null ||
                                (_policy.EnableGlancing && !next.EnableGlancing);
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

            if (headMotionDisabled || !next.EnableHeadMotion)
                look.ClearOffsets();
            else if (glancingDisabled || !next.EnableGlancing)
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
        if (player?.ReferenceHub == null)
            return;

        uint networkId = player.NetworkId;
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
        _activeLookTargets.Remove(networkId);

        if (_speechByNetworkId.TryGetValue(networkId, out SpeechTracker speech))
        {
            DetachProxyTarget(speech.Player);
            _speechByNetworkId.Remove(networkId);
            RemoveRecentSpeaker(speech);
            RemoveSpeechTracker(speech);
            speech.Dispose();
            if (destroyComponents && speech != null)
                UnityEngine.Object.Destroy(speech);
        }

        if (_lookByNetworkId.TryGetValue(networkId, out LookOverride look))
        {
            _lookByNetworkId.Remove(networkId);
            RemoveLookOverride(look);
            look.Dispose();
            if (destroyComponents && look != null)
                UnityEngine.Object.Destroy(look);
        }
    }

    public void OnRoundRestart()
    {
        ClearPlayers(true);
        _pendingConsumables.Clear();
        _pendingEmotions.Clear();
        _activeLookTargets.Clear();
        ResetDeadlines(Time.time);

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.ResetForRound();
        if (monitor != null)
            ApplyPerformancePolicy(monitor.CurrentPolicy);
    }

    public bool TryGetSpeechTracker(uint networkId, out SpeechTracker tracker) =>
        _speechByNetworkId.TryGetValue(networkId, out tracker) && tracker != null;

    public bool TryGetLookOverride(uint networkId, out LookOverride look) =>
        _lookByNetworkId.TryGetValue(networkId, out look) && look != null;

    public bool IsLookTargetActive(uint networkId) => _activeLookTargets.Contains(networkId);

    public void HandleVoice(uint networkId, byte[] data, int length)
    {
        if (TryGetSpeechTracker(networkId, out SpeechTracker tracker))
            HandleVoice(tracker, data, length);
    }

    public void HandleVoice(SpeechTracker source, byte[] data, int length)
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
            int decoded = source.DecodeVoicePacket(data, length);
            if (decoded <= 0)
                return;

            _decodedSamples += decoded;
            monitor?.Count(PerformanceCounter.DecodedSamples, decoded);
            SpeechTracker target = ResolveVoiceTarget(source, source.Proxy);
            if (!target.PresentVoiceFrom(source, decoded, now))
                return;

            if (_policy.EnableGlancing)
                AddRecentSpeaker(target);
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
        if (!_enabled || eventArgs?.Player == null || eventArgs.UsableItem == null)
            return;

        _pendingConsumables.Add(new PendingConsumableEmotion(
            eventArgs.Player.NetworkId,
            eventArgs,
            Time.time + Mathf.Max(0f, delaySeconds),
            durationMilliseconds,
            eventArgs.UsableItem.Type));
    }

    public void TriggerEmotion(uint networkId, EmotionPresetType preset, int durationMilliseconds)
    {
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
        long glanceStarted = PerformanceProfiler.NotSampled;
        try
        {
            float now = Time.time;
            long schedulerStarted = monitor?.Begin(PerformanceMetric.SchedulerOverhead, 1) ??
                                    PerformanceProfiler.NotSampled;
            try
            {
                ProcessPendingConsumables(now);
                ProcessPendingEmotions(now);
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
                    for (int i = 0; i < _speechTrackers.Count; i++)
                    {
                        SpeechTracker tracker = _speechTrackers[i];
                        if (tracker == null)
                            continue;

                        tracker.TickSpeech(now, resetTime, lowThreshold, highThreshold);
                        _speechTicks++;
                    }
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
                glanceStarted = monitor?.Begin(PerformanceMetric.GlanceBatch, 1) ??
                                PerformanceProfiler.NotSampled;
                _glancePending = false;
            }

            if (evaluateGlance || now >= _nextRecentPrune)
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
                            _policy);
                        RefreshLookActivity(look);
                        _lookTicks++;
                    }

                    _glanceCandidates += glanceCandidates;
                    if (glanceCandidates > 0)
                        monitor?.Count(PerformanceCounter.GlanceCandidates, glanceCandidates);
                }
                finally
                {
                    monitor?.End(PerformanceMetric.LookBatch, lookStarted);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Talky runtime update failed: " + exception);
        }
        finally
        {
            monitor?.End(PerformanceMetric.GlanceBatch, glanceStarted);
            if (monitor != null)
            {
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
                if (!ReferenceEquals(effective, _effectivePerformancePolicy))
                {
                    long schedulerStarted =
                        monitor.Begin(PerformanceMetric.SchedulerOverhead, 1);
                    try
                    {
                        ApplyPerformancePolicy(effective);
                    }
                    finally
                    {
                        monitor.End(PerformanceMetric.SchedulerOverhead, schedulerStarted);
                    }
                }
            }
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
            // An acyclic chain cannot traverse more registered trackers than exist.
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
        for (int i = _pendingConsumables.Count - 1; i >= 0; i--)
        {
            PendingConsumableEmotion pending = _pendingConsumables[i];
            if (now < pending.DueTime)
                continue;

            RemoveAtSwap(_pendingConsumables, i);
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
                _pendingEmotions.Add(new PendingEmotion(
                    pending.NetworkId,
                    now + 2.4f,
                    EmotionPresetType.AwkwardSmile,
                    500));
            }
            else if (pending.ItemType == ItemType.SCP207)
            {
                _pendingEmotions.Add(new PendingEmotion(
                    pending.NetworkId,
                    now + 2.5f,
                    EmotionPresetType.Happy,
                    500));
            }
        }
    }

    private void ProcessPendingEmotions(float now)
    {
        for (int i = _pendingEmotions.Count - 1; i >= 0; i--)
        {
            PendingEmotion pending = _pendingEmotions[i];
            if (now < pending.DueTime)
                continue;

            RemoveAtSwap(_pendingEmotions, i);
            TriggerEmotion(pending.NetworkId, pending.Preset, pending.DurationMilliseconds);
        }
    }

    private void RemoveScheduledActions(uint networkId)
    {
        for (int i = _pendingConsumables.Count - 1; i >= 0; i--)
        {
            if (_pendingConsumables[i].NetworkId == networkId)
                RemoveAtSwap(_pendingConsumables, i);
        }

        for (int i = _pendingEmotions.Count - 1; i >= 0; i--)
        {
            if (_pendingEmotions[i].NetworkId == networkId)
                RemoveAtSwap(_pendingEmotions, i);
        }
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
                _speechTrackers.RemoveAt(_speechTrackers.Count - 1);
                tracker?.Dispose();
                if (destroyComponents && tracker != null)
                    UnityEngine.Object.Destroy(tracker);
            }
        }

        // Defensive cleanup for a partial registration where a look component existed alone.
        while (_lookOverrides.Count > 0)
        {
            LookOverride look = _lookOverrides[_lookOverrides.Count - 1];
            _lookOverrides.RemoveAt(_lookOverrides.Count - 1);
            look?.Dispose();
            if (destroyComponents && look != null)
                UnityEngine.Object.Destroy(look);
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

    private static void RemoveAtSwap<T>(List<T> list, int index)
    {
        int lastIndex = list.Count - 1;
        if (index != lastIndex)
            list[index] = list[lastIndex];
        list.RemoveAt(lastIndex);
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
