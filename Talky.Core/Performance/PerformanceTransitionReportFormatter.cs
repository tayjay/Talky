using System;
using System.Text;
using Talky.Settings;

namespace Talky.Performance;

/// <summary>
/// Formats a completed observational tier comparison.
/// </summary>
public static class PerformanceTransitionReportFormatter
{
    public static string CreateLogMessage(
        Config config,
        PerformanceTransitionImpact impact)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        PerformanceImpactSnapshot before = impact.Before;
        PerformanceImpactSnapshot after = impact.After;
        string status = ImpactStatus(impact.Outcome);
        string completion = impact.CompletionReason == GovernorReason.None
            ? string.Empty
            : $", completion={impact.CompletionReason}";
        string changes = DescribePolicyChanges(config, impact.FromTier, impact.ToTier);
        string evidence =
            $"baseline={(impact.HasCompleteBaseline ? "complete" : "partial")}, " +
            $"after={(impact.HasCompleteAfter ? "complete" : "partial")}";

        if (!before.HasData || !after.HasData)
        {
            return
                $"Observed tier impact #{impact.Sequence} " +
                $"{impact.FromTier} -> {impact.ToTier}: status={status}, " +
                $"trigger={impact.TransitionReason}{completion}, {evidence}, " +
                $"changes=[{changes}], " +
                $"eligible_samples={before.DurationSeconds:F1}s/{after.DurationSeconds:F1}s; " +
                "comparison unavailable until both sides contain active-server frames.";
        }

        double talkyDelta =
            after.TalkyMillisecondsPerFrame - before.TalkyMillisecondsPerFrame;
        double serverP95Delta =
            after.FrameP95Milliseconds - before.FrameP95Milliseconds;

        return
            $"Observed tier impact #{impact.Sequence} " +
            $"{impact.FromTier} -> {impact.ToTier}: status={status}, " +
            $"trigger={impact.TransitionReason}{completion}, {evidence}, " +
            $"changes=[{changes}], " +
            $"eligible_samples={before.DurationSeconds:F1}s/{after.DurationSeconds:F1}s; " +
            $"talky={before.TalkyMillisecondsPerFrame:F3}->" +
            $"{after.TalkyMillisecondsPerFrame:F3} ms/frame (delta={talkyDelta:F3}), " +
            $"server_p95={before.FrameP95Milliseconds:F2}->" +
            $"{after.FrameP95Milliseconds:F2} ms (delta={serverP95Delta:F2}), " +
            $"voice/face/look/scheduler/sync=" +
            $"{before.VoiceMillisecondsPerFrame:F3}/" +
            $"{before.FaceMillisecondsPerFrame:F3}/" +
            $"{before.LookMillisecondsPerFrame:F3}/" +
            $"{before.SchedulerMillisecondsPerFrame:F3}/" +
            $"{before.SyncMillisecondsPerFrame:F3}->" +
            $"{after.VoiceMillisecondsPerFrame:F3}/" +
            $"{after.FaceMillisecondsPerFrame:F3}/" +
            $"{after.LookMillisecondsPerFrame:F3}/" +
            $"{after.SchedulerMillisecondsPerFrame:F3}/" +
            $"{after.SyncMillisecondsPerFrame:F3} ms/frame, " +
            $"load players/speakers/looks=" +
            $"{before.AveragePlayers:F1}/{before.AverageRecentSpeakers:F1}/" +
            $"{before.AverageActiveLooks:F1}->" +
            $"{after.AveragePlayers:F1}/{after.AverageRecentSpeakers:F1}/" +
            $"{after.AverageActiveLooks:F1}, " +
            $"voice_packets/s={before.VoicePacketsPerSecond:F1}->" +
            $"{after.VoicePacketsPerSecond:F1}, " +
            $"sync_entries/s={before.SyncEntriesPerSecond:F1}->" +
            $"{after.SyncEntriesPerSecond:F1}. " +
            "Server change is observational and may include unrelated workload changes; " +
            "subsystem costs include sampled estimates and transition-application overhead.";
    }

    private static string DescribePolicyChanges(
        Config config,
        PerformanceTier fromTier,
        PerformanceTier toTier)
    {
        EffectivePerformancePolicy before =
            EffectivePerformancePolicy.Create(config, fromTier);
        EffectivePerformancePolicy after =
            EffectivePerformancePolicy.Create(config, toTier);
        return DescribePolicyChanges(before, after);
    }

    public static string DescribePolicyChanges(
        EffectivePerformancePolicy before,
        EffectivePerformancePolicy after)
    {
        if (before == null)
            throw new ArgumentNullException(nameof(before));
        if (after == null)
            throw new ArgumentNullException(nameof(after));

        StringBuilder changes = new StringBuilder();
        bool hasChanges = false;

        if (Math.Abs(before.FaceIntervalSeconds - after.FaceIntervalSeconds) > 0.000001f)
        {
            AppendChange(
                changes,
                $"face_rate {FormatRate(before.FaceIntervalSeconds)}->" +
                $"{FormatRate(after.FaceIntervalSeconds)}",
                ref hasChanges);
        }

        if (before.HeadMovementEnabled != after.HeadMovementEnabled)
        {
            AppendChange(
                changes,
                $"head_motion {OnOff(before.HeadMovementEnabled)}->" +
                $"{OnOff(after.HeadMovementEnabled)}",
                ref hasChanges);
        }

        if (before.HeadBobbingEnabled != after.HeadBobbingEnabled)
        {
            AppendChange(
                changes,
                $"head_bob {OnOff(before.HeadBobbingEnabled)}->" +
                $"{OnOff(after.HeadBobbingEnabled)}",
                ref hasChanges);
        }

        if (before.GlancingEnabled != after.GlancingEnabled)
        {
            AppendChange(
                changes,
                $"glancing {OnOff(before.GlancingEnabled)}->" +
                $"{OnOff(after.GlancingEnabled)}",
                ref hasChanges);
        }

        if ((before.GlancingEnabled || after.GlancingEnabled) &&
            Math.Abs(before.GlanceIntervalSeconds - after.GlanceIntervalSeconds) > 0.000001f)
        {
            AppendChange(
                changes,
                $"glance_rate {FormatRate(before.GlanceIntervalSeconds)}->" +
                $"{FormatRate(after.GlanceIntervalSeconds)}",
                ref hasChanges);
        }

        if ((before.HeadMovementEnabled || after.HeadMovementEnabled) &&
            Math.Abs(before.LookIntervalSeconds - after.LookIntervalSeconds) > 0.000001f)
        {
            AppendChange(
                changes,
                $"look_rate {FormatRate(before.LookIntervalSeconds)}->" +
                $"{FormatRate(after.LookIntervalSeconds)}",
                ref hasChanges);
        }

        if (before.SpectatorHeadMovementsEnabled != after.SpectatorHeadMovementsEnabled)
        {
            AppendChange(
                changes,
                $"spectator_sync {OnOff(before.SpectatorHeadMovementsEnabled)}->" +
                $"{OnOff(after.SpectatorHeadMovementsEnabled)}",
                ref hasChanges);
        }

        if (before.FaceReactionsEnabled != after.FaceReactionsEnabled)
        {
            AppendChange(
                changes,
                $"face_reactions {OnOff(before.FaceReactionsEnabled)}->" +
                $"{OnOff(after.FaceReactionsEnabled)}",
                ref hasChanges);
        }

        if (before.ConsumableEmotesEnabled != after.ConsumableEmotesEnabled)
        {
            AppendChange(
                changes,
                $"consumable_emotes {OnOff(before.ConsumableEmotesEnabled)}->" +
                $"{OnOff(after.ConsumableEmotesEnabled)}",
                ref hasChanges);
        }

        return hasChanges ? changes.ToString() : "no effective feature change";
    }

    private static void AppendChange(StringBuilder builder, string value, ref bool hasChanges)
    {
        if (hasChanges)
            builder.Append(", ");
        builder.Append(value);
        hasChanges = true;
    }

    private static string FormatRate(float intervalSeconds)
    {
        return intervalSeconds <= 0f
            ? "frame"
            : $"{1d / intervalSeconds:F1}Hz";
    }

    private static string OnOff(bool value)
    {
        return value ? "on" : "off";
    }

    private static string ImpactStatus(PerformanceImpactOutcome outcome)
    {
        switch (outcome)
        {
            case PerformanceImpactOutcome.RecoveryAccepted:
                return "kept";
            case PerformanceImpactOutcome.RecoveryRejected:
                return "rejected";
            case PerformanceImpactOutcome.Interrupted:
                return "interrupted";
            default:
                return "observed";
        }
    }
}
