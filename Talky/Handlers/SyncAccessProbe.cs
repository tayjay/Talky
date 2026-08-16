using System;
using System.Threading;
using Logger = LabApi.Features.Console.Logger;

namespace Talky.Handlers;

/// <summary>
/// The position-distributor members Talky rewrites head sync through are private in the assembly
/// the server loads and public in the publicized assembly Talky compiles against. Whether the
/// runtime actually rejects those call sites is a property of the deployed build, so each one is
/// probed rather than assumed.
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
/// One-shot outcome reporting for the private-member call sites in <see cref="FakeLookHandler"/>.
///
/// These sites run per receiver per target per tick, so an unconditional log would emit tens of
/// thousands of lines a second and drown the measurement it is meant to inform. Each site reports
/// its first success and its first failure exactly once and counts the rest, which is enough to
/// answer whether the runtime permits the access without perturbing the run.
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

        Logger.Info($"Talky sync access probe [{site}]: PERMITTED on first call.");
    }

    public static void Failed(SyncAccessSite site, Exception exception)
    {
        int index = (int)site;
        FailureCounts[index]++;
        if (Faulted[index] != 0 || Interlocked.Exchange(ref Faulted[index], 1) != 0)
            return;

        Logger.Error(
            $"Talky sync access probe [{site}]: REJECTED by the runtime -- " +
            $"{exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// Writes one line per site with its totals. Called at teardown so a run ends with an explicit
    /// verdict rather than the absence of an error line.
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
                    $"Talky sync access probe [{(SyncAccessSite)index}]: NOT REACHED this session.");
                continue;
            }

            string verdict = failures == 0L
                ? "PERMITTED"
                : successes == 0L ? "REJECTED" : "MIXED";
            Logger.Info(
                $"Talky sync access probe [{(SyncAccessSite)index}]: {verdict}, " +
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
