using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TruckHub.Services;

/// <summary>
/// Periodically measures real per-core CPU load and steers TruckHub's own affinity away from
/// whichever cores are currently busiest (the game's threads, typically), instead of guessing
/// fixed core indices - a static "reserve the top N cores" heuristic was tried first and found
/// (via live per-core monitoring) to not match how load actually distributes on real machines.
///
/// Also manages affinity for other registered processes, not just TruckHub itself - added for the
/// GPS map's WebView2 subprocesses (see RegisterProcess/UnregisterProcess), since WebView2's
/// renderer/GPU processes are entirely separate OS processes Windows schedules independently -
/// setting TruckHub's own ProcessorAffinity alone never touched them at all.
/// </summary>
public static class CoreBalancer
{
    private const int SampleWindowMs = 2000;
    private const int CycleIntervalMs = 60_000;
    private const double SwitchMarginPoints = 15.0;
    private const long MinSwitchIntervalTicks = 5 * 60 * TimeSpan.TicksPerSecond;

    private static readonly object ProcessesLock = new();
    private static readonly List<Process> ManagedProcesses = new() { Process.GetCurrentProcess() };

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    private const int SystemProcessorPerformanceInformation = 8;

    private static int ReservedCoreCount
    {
        get
        {
            var cores = Environment.ProcessorCount;
            if (cores >= 4) return 2;
            if (cores >= 2) return 1;
            return 0;
        }
    }

    private static long _currentExcludedMask;
    private static long _lastSwitchTicks;

    public static void Start()
    {
        if (ReservedCoreCount == 0)
        {
            return;
        }

        Task.Run(RunLoop);
    }

    /// <summary>Adds a process to the set CoreBalancer keeps off the busiest cores - e.g. a GPS map
    /// window's WebView2 renderer/GPU subprocesses, registered once they exist. Applies whatever
    /// exclusion is already known immediately (rather than waiting for the next up-to-60s cycle) -
    /// a freshly-opened map window's rendering process shouldn't sit on a hot core for however long
    /// is left before the next scheduled rebalance. Harmless if CoreBalancer never actually started
    /// (ReservedCoreCount == 0, e.g. a 2-3 core machine) - it just never gets an exclusion applied.</summary>
    public static void RegisterProcess(Process process)
    {
        lock (ProcessesLock)
        {
            ManagedProcesses.Add(process);
        }

        if (_currentExcludedMask != 0)
        {
            ApplyAffinity(process, _currentExcludedMask);
        }
    }

    /// <summary>Stops managing a process's affinity - call when it's about to exit (e.g. the GPS
    /// window closing) so a later rebalance doesn't try to touch a process handle that's gone.</summary>
    public static void UnregisterProcess(Process process)
    {
        lock (ProcessesLock)
        {
            ManagedProcesses.Remove(process);
        }
    }

    // Real system-wide headroom, not this app's own usage - the GPS map's WebGL rendering competes
    // with the game (typically the dominant consumer) for the same GPU/CPU, so "is the system
    // generally busy" is what actually determines whether raising the map's own frame rate would
    // help or just take more from something else that needs it. Reuses the exact same per-core
    // samples already taken for the affinity check above rather than a second, separate measurement.
    private const double FpsUpgradeThresholdPercent = 45.0;
    private const double FpsDowngradeThresholdPercent = 60.0;
    private static int _recommendedFps = 30;

    /// <summary>The GPS map's currently-recommended frame rate cap - starts conservative (30) and
    /// only rises to 60 once a real rebalance cycle has actually confirmed headroom.</summary>
    public static int RecommendedFps => _recommendedFps;

    /// <summary>Fires whenever RecommendedFps actually changes (not every rebalance cycle) - the
    /// GPS window subscribes to push the new cap to gpsmap.js's requestAnimationFrame throttle.</summary>
    public static event Action<int>? FpsRecommendationChanged;

    private static void UpdateFpsRecommendation(double overallBusyPercent)
    {
        // A gap between the two thresholds (not one shared cutoff) so a system hovering right at
        // the line doesn't flip the map's frame rate back and forth every cycle.
        var newFps = _recommendedFps switch
        {
            30 when overallBusyPercent < FpsUpgradeThresholdPercent => 60,
            60 when overallBusyPercent > FpsDowngradeThresholdPercent => 30,
            _ => _recommendedFps,
        };

        if (newFps == _recommendedFps)
        {
            return;
        }

        _recommendedFps = newFps;
        AppLogger.Log($"CoreBalancer: system load {overallBusyPercent:F0}% avg - recommending {newFps}fps for the GPS map.");
        FpsRecommendationChanged?.Invoke(newFps);
    }

    private static async Task RunLoop()
    {
        while (true)
        {
            try
            {
                Rebalance();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"CoreBalancer: rebalance failed: {ex}");
            }

            await Task.Delay(CycleIntervalMs);
        }
    }

    private static void Rebalance()
    {
        var before = SampleCoreTimes();
        Thread.Sleep(SampleWindowMs);
        var after = SampleCoreTimes();

        if (before == null || after == null || before.Length != after.Length)
        {
            return;
        }

        var coreCount = before.Length;
        var busyPercent = new double[coreCount];
        for (var i = 0; i < coreCount; i++)
        {
            var idleDelta = after[i].IdleTime - before[i].IdleTime;
            var totalDelta = (after[i].KernelTime - before[i].KernelTime) + (after[i].UserTime - before[i].UserTime);
            busyPercent[i] = totalDelta > 0 ? 100.0 * (1.0 - (double)idleDelta / totalDelta) : 0;
        }

        UpdateFpsRecommendation(busyPercent.Average());

        var reserved = Math.Min(ReservedCoreCount, coreCount - 1);
        var busiest = Enumerable.Range(0, coreCount)
            .OrderByDescending(i => busyPercent[i])
            .Take(reserved)
            .ToArray();

        long newExcludedMask = 0;
        foreach (var i in busiest)
        {
            newExcludedMask |= 1L << i;
        }

        if (newExcludedMask == _currentExcludedMask)
        {
            return;
        }

        // Bootstrap (no exclusion applied yet) always applies immediately; afterwards a switch
        // only happens if the newly-identified hot cores are meaningfully hotter than whatever
        // we're currently avoiding, so a normal reading-to-reading wobble doesn't cause a move -
        // reassigning affinity has its own cache/scheduler cost that isn't worth paying for noise.
        if (_currentExcludedMask != 0)
        {
            var currentAvg = AverageLoad(busyPercent, _currentExcludedMask);
            var newAvg = AverageLoad(busyPercent, newExcludedMask);
            if (newAvg - currentAvg < SwitchMarginPoints)
            {
                return;
            }
        }

        // Independent of the 60s check cadence: even a change that clears the noise margin
        // only actually gets applied at most once every 5 minutes, so a real but short-lived
        // swing (a couple of players passing through, a scene transition) doesn't cost a
        // reassignment every single cycle - the 60s check still catches sustained shifts fast,
        // it just isn't allowed to act on all of them.
        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - _lastSwitchTicks < MinSwitchIntervalTicks)
        {
            return;
        }

        _currentExcludedMask = newExcludedMask;
        _lastSwitchTicks = nowTicks;

        List<Process> processes;
        lock (ProcessesLock)
        {
            processes = new List<Process>(ManagedProcesses);
        }

        var appliedCount = 0;
        foreach (var process in processes)
        {
            if (ApplyAffinity(process, newExcludedMask))
            {
                appliedCount++;
            }
        }

        AppLogger.Log($"CoreBalancer: avoiding busiest cores [{string.Join(",", busiest)}] ({appliedCount}/{processes.Count} processes)");
    }

    /// <summary>Sets a process's affinity to everything except the given excluded-core mask.
    /// Returns false (without throwing) if the process has already exited or access is denied -
    /// a scheduling hint should never be able to crash the app, and a WebView2 subprocess that
    /// exited between being registered and the next rebalance is an expected, harmless race, not
    /// something worth logging every single time it happens.</summary>
    private static bool ApplyAffinity(Process process, long excludedMask)
    {
        try
        {
            var coreCount = Environment.ProcessorCount;
            long allowedMask = 0;
            for (var i = 0; i < coreCount; i++)
            {
                if ((excludedMask & (1L << i)) == 0)
                {
                    allowedMask |= 1L << i;
                }
            }

            process.ProcessorAffinity = (IntPtr)allowedMask;
            return true;
        }
        catch
        {
            // Process already exited, access denied, etc. - never let a scheduling hint prevent
            // the app (or this specific subprocess) from actually running.
            return false;
        }
    }

    private static double AverageLoad(double[] busyPercent, long mask)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < busyPercent.Length; i++)
        {
            if ((mask & (1L << i)) != 0)
            {
                sum += busyPercent[i];
                count++;
            }
        }

        return count > 0 ? sum / count : 0;
    }

    private static SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[]? SampleCoreTimes()
    {
        var coreCount = Environment.ProcessorCount;
        var size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size * coreCount);
        try
        {
            var status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, size * coreCount, out _);
            if (status != 0)
            {
                return null;
            }

            var result = new SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[coreCount];
            for (var i = 0; i < coreCount; i++)
            {
                result[i] = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buffer + i * size);
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
