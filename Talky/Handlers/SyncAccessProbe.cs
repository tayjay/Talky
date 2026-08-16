using System;
using System.Threading;
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
    PreviouslySentCedModPrefix = 5,
    PreviouslySentCedModPostfix = 6,
    FpcSyncDataCarriedFields = 7,
    Count = 8,
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
        SuccessCounts[index]++;
        if (Reported[index] != 0 || Interlocked.Exchange(ref Reported[index], 1) != 0)
            return;

        Logger.Info($"Sync access probe [{site}]: PERMITTED on first call.");
    }

    public static void Failed(SyncAccessSite site, Exception exception)
    {
        int index = (int)site;
        FailureCounts[index]++;
        if (Faulted[index] != 0 || Interlocked.Exchange(ref Faulted[index], 1) != 0)
            return;

        Logger.Error(
            $"Sync access probe [{site}]: REJECTED by the runtime -- " +
            $"{exception.GetType().Name}: {exception.Message}");
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
