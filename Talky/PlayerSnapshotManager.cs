using System.Collections.Generic;
using LabApi.Features.Wrappers;
using MEC;
using UnityEngine;

namespace Talky;

public class PlayerSnapshotManager
{
    public struct PlayerSnapshot
    {
        public Vector3 Position;
        public Vector3 CameraPosition;
        public uint NetworkId;
        public float LastPacketTime;
    }

    public List<PlayerSnapshot> Snapshots = new List<PlayerSnapshot>();

    private CoroutineHandle _updateHandle;

    public void OnRoundStart()
    {
        _updateHandle = Timing.RunCoroutine(UpdateSnapshots());
    }

    public void OnRoundRestart()
    {
        Timing.KillCoroutines(_updateHandle);
        Snapshots.Clear();
    }

    private IEnumerator<float> UpdateSnapshots()
    {
        while (true)
        {
            var list = new List<PlayerSnapshot>(Player.List.Count);
            foreach (var p in Player.List)
            {
                float lastPacketTime = -1;
                if (Plugin.Instance.VoiceChattingHandler.SpeechTrackerCache.TryGetValue(p.NetworkId, out SpeechTracker tracker))
                {
                    lastPacketTime = tracker.LastPacketTime;
                }

                list.Add(new PlayerSnapshot
                {
                    Position = p.Position,
                    CameraPosition = p.Camera.position,
                    NetworkId = p.NetworkId,
                    LastPacketTime = lastPacketTime
                });
            }
            Snapshots = list;
            yield return Timing.WaitForSeconds(0.033f);
        }
    }

    public void RegisterEvents()
    {
        LabApi.Events.Handlers.ServerEvents.RoundStarted += OnRoundStart;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted += OnRoundRestart;
    }

    public void UnregisterEvents()
    {
        LabApi.Events.Handlers.ServerEvents.RoundStarted -= OnRoundStart;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted -= OnRoundRestart;
        Timing.KillCoroutines(_updateHandle);
        Snapshots.Clear();
    }
}
