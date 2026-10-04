using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using Logger = LabApi.Features.Console.Logger;

namespace Talky.Handlers;

/// <summary>
/// Identifies publicized sync-member call sites that require runtime verification.
/// </summary>
internal enum SyncAccessSite
{
    GetPrevSyncData = 0,
    BufferPlayerIds = 1,
    PreviouslySentStock = 2,
    GetNewSyncData = 3,
    BufferSyncDataWrite = 4,
    Count = 5,
}

/// <summary>
/// Reports each site's first success and failure plus aggregate teardown totals.
/// </summary>
internal static class SyncAccessProbe
{
    private static readonly int[] Reported = new int[(int)SyncAccessSite.Count];
    private static readonly int[] Faulted = new int[(int)SyncAccessSite.Count];
    private static readonly long[] SuccessCounts = new long[(int)SyncAccessSite.Count];
    private static readonly long[] FailureCounts = new long[(int)SyncAccessSite.Count];

    public static void Succeeded(SyncAccessSite site)
    {
        int index = (int)site;
        Interlocked.Increment(ref SuccessCounts[index]);
        if (Reported[index] != 0 || Interlocked.Exchange(ref Reported[index], 1) != 0)
            return;

        try
        {
            Logger.Info($"Sync access probe [{site}]: PERMITTED on first call.");
        }
        catch
        {
            // Diagnostics must not disrupt outbound position distribution.
        }
    }

    public static void Failed(SyncAccessSite site, Exception exception)
    {
        int index = (int)site;
        Interlocked.Increment(ref FailureCounts[index]);
        if (Faulted[index] != 0 || Interlocked.Exchange(ref Faulted[index], 1) != 0)
            return;

        try
        {
            Logger.Error(
                $"Sync access probe [{site}]: REJECTED by the runtime -- " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        catch
        {
            // Diagnostics must not disrupt outbound position distribution.
        }
    }

    /// <summary>
    /// Returns true only for CLR/member-shape failures that cannot become healthy by retrying.
    /// Operational exceptions are deliberately not latched for the process lifetime.
    /// </summary>
    internal static bool IsStructuralFailure(Exception exception)
    {
        while ((exception is TypeInitializationException ||
                exception is TargetInvocationException) &&
               exception.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception is MemberAccessException ||
               exception is MissingMemberException ||
               exception is TypeLoadException ||
               exception is InvalidProgramException ||
               exception is VerificationException ||
               exception is BadImageFormatException;
    }

    /// <summary>
    /// Best-effort rollback used when a rewrite aborts before the outgoing buffer and
    /// delta-compression cache have both committed the same value.
    /// </summary>
    internal static bool TryRestoreCommittedValue<TKey, TValue>(
        IDictionary<TKey, TValue> values,
        TKey key,
        TValue committedValue,
        out Exception failure)
    {
        try
        {
            values[key] = committedValue;
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = exception;
            return false;
        }
    }

    /// <summary>
    /// Logs aggregate access totals during teardown.
    /// </summary>
    public static void LogSummary()
    {
        for (int index = 0; index < (int)SyncAccessSite.Count; index++)
        {
            long successes = SuccessCounts[index];
            long failures = FailureCounts[index];
            if (successes == 0L && failures == 0L)
            {
                Logger.Info(
                    $"Sync access probe [{(SyncAccessSite)index}]: NOT REACHED this session.");
                continue;
            }

            string verdict = failures == 0L
                ? "PERMITTED"
                : successes == 0L ? "REJECTED" : "MIXED";
            Logger.Info(
                $"Sync access probe [{(SyncAccessSite)index}]: {verdict}, " +
                $"permitted={successes}, rejected={failures}.");
        }
    }

    public static void Reset()
    {
        Array.Clear(Reported, 0, Reported.Length);
        Array.Clear(Faulted, 0, Faulted.Length);
        Array.Clear(SuccessCounts, 0, SuccessCounts.Length);
        Array.Clear(FailureCounts, 0, FailureCounts.Length);
    }
}

/// <summary>
/// Contains every runtime access that is compiled against publicized game members.
/// Callers contain no such member references, so a JIT-time access rejection is caught
/// outside these non-inlined leaves and can fail closed without taking down Talky.
/// </summary>
internal static class SyncMemberAccess
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FpcSyncData GetPreviousSyncData(
        ReferenceHub receiver,
        ReferenceHub target)
    {
        return FpcServerPositionDistributor.GetPrevSyncData(receiver, target);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int[] GetBufferPlayerIds()
    {
        return FpcServerPositionDistributor._bufferPlayerIDs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<uint, Dictionary<uint, FpcSyncData>> GetPreviouslySent()
    {
        return FpcServerPositionDistributor.PreviouslySent;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FpcSyncData GetNewSyncData(
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule movement,
        bool isInvisible)
    {
        return FpcServerPositionDistributor.GetNewSyncData(
            receiver,
            target,
            movement,
            isInvisible);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void WriteBufferSyncData(int index, FpcSyncData value)
    {
        FpcServerPositionDistributor._bufferSyncData[index] = value;
    }
}
