using System;
using System.Diagnostics;

namespace TruckHub.Services;

/// <summary>Keeps TruckHub off a guaranteed-free slice of CPU cores, so ATS/ETS2 always has
/// uncontested room regardless of what TruckHub's own UI (gauge animations, telemetry polling)
/// is doing - same principle as PalHub's PinToDedicatedCores and ZoidHub's CpuAffinity, ported
/// here since TruckHub had never had this despite running directly alongside the game it's
/// overlaying. No subprocess tree to walk here (unlike ZoidHub's render workers) - just the one
/// process.</summary>
public static class CpuAffinity
{
    /// <summary>Cores permanently off-limits to TruckHub. Matches PalHub/ZoidHub's exact
    /// thresholds for consistency across this user's apps: 2 reserved on 4+-core machines, 1 on
    /// 2-3 core machines, 0 (untouched) below that - a scheduling hint must never make a small
    /// machine unusable.</summary>
    public static int ReservedForOtherApps
    {
        get
        {
            var cores = Environment.ProcessorCount;
            if (cores >= 4) return 2;
            if (cores >= 2) return 1;
            return 0;
        }
    }

    /// <summary>How many cores TruckHub is allowed to use at all.</summary>
    public static int AvailableCores => Math.Max(1, Environment.ProcessorCount - ReservedForOtherApps);

    /// <summary>Confines the current process to the bottom AvailableCores cores, leaving the top
    /// ReservedForOtherApps cores untouched. Call once, early in startup.</summary>
    public static void PinCurrentProcess()
    {
        try
        {
            var reserved = ReservedForOtherApps;
            if (reserved == 0)
            {
                return;
            }

            long mask = 0;
            for (var i = 0; i < AvailableCores; i++)
            {
                mask |= 1L << i;
            }

            Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
        }
        catch
        {
            // Never let a scheduling hint prevent the app from actually running.
        }
    }
}
