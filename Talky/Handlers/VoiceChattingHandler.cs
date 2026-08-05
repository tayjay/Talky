using System;
using System.Collections.Generic;
using LabApi.Events.Arguments.PlayerEvents;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerStatsSystem;
using Talky.Behaviours;
using Talky.Runtime;
using VoiceChat;

namespace Talky.Handlers;

public sealed class VoiceChattingHandler
{
    private static readonly IReadOnlyDictionary<uint, SpeechTracker> EmptyTrackers =
        new Dictionary<uint, SpeechTracker>();

    private bool _registered;

    /// <summary>
    /// Compatibility view for integrations that previously read the handler cache.
    /// Runtime owns the mutable dictionary.
    /// </summary>
    public IReadOnlyDictionary<uint, SpeechTracker> SpeechTrackerCache =>
        Plugin.Instance?.Runtime?.SpeechTrackers ?? EmptyTrackers;

    private TalkyRuntime Runtime => Plugin.Instance?.Runtime;

    private void OnNewVoiceSending(PlayerSendingVoiceMessageEventArgs ev)
    {
        if (ev.Message.Channel is VoiceChatChannel.Proximity or
            VoiceChatChannel.Radio or
            VoiceChatChannel.Intercom or
            VoiceChatChannel.Scp1576)
        {
            Runtime?.HandleVoice(ev.Player.NetworkId, ev.Message.Data, ev.Message.DataLength);
        }
    }

    private void OnHurt(PlayerHurtEventArgs ev)
    {
        if (Plugin.Instance?.Config?.EnableReactionOnHurt != true)
            return;

        int damage = 1000;
        if (ev.DamageHandler is StandardDamageHandler standardDamageHandler)
            damage = (int)Math.Floor(standardDamageHandler.DealtHealthDamage);

        if (damage <= Plugin.Instance.Config.MiniumDamageForReaction)
            return;

        Runtime?.TriggerEmotion(
            ev.Player.NetworkId,
            EmotionPresetType.Angry,
            Math.Min(1000, damage * 100));
    }

    private void OnSpawn(PlayerSpawnedEventArgs ev)
    {
        Runtime?.RegisterPlayer(ev.Player);
    }

    private void OnUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (Plugin.Instance?.Config?.EnableEmoteOnConsumables != true)
            return;

        float waitTime;
        int openTime;
        switch (ev.UsableItem.Type)
        {
            case ItemType.Adrenaline:
                waitTime = 1f;
                openTime = 1000;
                break;
            case ItemType.SCP500:
            case ItemType.Painkillers:
                waitTime = 1f;
                openTime = 500;
                break;
            case ItemType.SCP207:
            case ItemType.AntiSCP207:
                waitTime = 1f;
                openTime = 1000;
                break;
            case ItemType.Medkit:
                waitTime = 4f;
                openTime = 500;
                break;
            default:
                return;
        }

        Runtime?.ScheduleConsumableEmotion(ev, waitTime, openTime);
    }

    private void OnRoundRestart()
    {
        Runtime?.OnRoundRestart();
    }

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        Runtime?.UnregisterPlayer(ev.Player.NetworkId);
    }

    public void RegisterEvents()
    {
        if (_registered)
            return;

        _registered = true;
        LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnNewVoiceSending;
        LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
        LabApi.Events.Handlers.PlayerEvents.UsingItem += OnUsingItem;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted += OnRoundRestart;
        LabApi.Events.Handlers.PlayerEvents.Left += OnPlayerLeft;
    }

    public void UnregisterEvents()
    {
        if (!_registered)
            return;

        LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnNewVoiceSending;
        LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
        LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnUsingItem;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted -= OnRoundRestart;
        LabApi.Events.Handlers.PlayerEvents.Left -= OnPlayerLeft;
        _registered = false;
    }
}
