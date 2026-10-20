#if DEBUG
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;

namespace TruckHub.Services;

/// <summary>
/// DEBUG builds only (compiled out of Release): once a minute, one CSV line in logs\memdiag.csv that splits this
/// process's memory into managed and native, and counts JIT work, so a slow rise in Task Manager's number can be
/// traced to what is actually growing. Every tenth line first forces a full blocking GC, which makes the managed
/// heap figure "what is really still referenced" - flat across hours means the growth is NOT a managed leak, rising
/// means it is. All values are MB unless noted. Never writes anything the app reads back.
///   private_mb        Process.PrivateMemorySize64 (committed private memory, all of it)
///   gc_committed_mb   memory the GC has committed (heap incl. free space it is holding on to)
///   gc_heap_mb        GC heap size at the last collection;  frag_mb = free space inside it
///   gen0/1/2/loh/poh  generation sizes after the last collection
///   native_mb         private_mb - gc_committed_mb: everything that is not the GC heap
///   alloc_mb_min      managed allocation rate since the previous line
///   jit_methods/il_kb methods compiled so far / IL bytes compiled (a slow climb that stops = JIT warm-up)
///   live_after_gc_mb  only on forced lines: GC.GetTotalMemory after a full compacting collection
/// </summary>
internal static class DebugMemoryLog
{
    private static Timer? _timer;
    private static int _tick;
    private static long _lastAllocated;
    private static string _path = "";

    public static void Start()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TruckHub-Dev", "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "memdiag.csv");
            if (!File.Exists(_path))
            {
                File.WriteAllText(_path,
                    "time,pid,uptime_min,private_mb,ws_mb,gc_committed_mb,gc_heap_mb,frag_mb,gen0_mb,gen1_mb,gen2_mb,loh_mb,poh_mb,native_mb," +
                    "gc0,gc1,gc2,alloc_mb_min,pause_ms,jit_methods,jit_il_kb,handles,threads,live_after_gc_mb\n");
            }

            _lastAllocated = GC.GetTotalAllocatedBytes(false);
            _timer = new Timer(_ => Tick(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
        catch
        {
            // Diagnostics must never affect the app.
        }
    }

    private static void Tick()
    {
        try
        {
            _tick++;
            var forced = _tick % 10 == 0;
            double live = -1;
            if (forced)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                live = GC.GetTotalMemory(false) / 1048576.0;
            }

            var p = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            // GenerationInfo is a span (a ref struct), so copy the sizes out instead of capturing it.
            var gen = new double[5];
            var gens = info.GenerationInfo;
            for (var i = 0; i < gens.Length && i < gen.Length; i++)
            {
                gen[i] = gens[i].SizeAfterBytes / 1048576.0;
            }

            var allocated = GC.GetTotalAllocatedBytes(false);
            var allocRate = (allocated - _lastAllocated) / 1048576.0;
            _lastAllocated = allocated;

            var privateMb = p.PrivateMemorySize64 / 1048576.0;
            var committedMb = info.TotalCommittedBytes / 1048576.0;
            var line = string.Join(",",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Environment.ProcessId,
                ((DateTime.Now - p.StartTime).TotalMinutes).ToString("F1", CultureInfo.InvariantCulture),
                privateMb.ToString("F1", CultureInfo.InvariantCulture),
                (p.WorkingSet64 / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                committedMb.ToString("F1", CultureInfo.InvariantCulture),
                (info.HeapSizeBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                (info.FragmentedBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture),
                gen[0].ToString("F1", CultureInfo.InvariantCulture), gen[1].ToString("F1", CultureInfo.InvariantCulture),
                gen[2].ToString("F1", CultureInfo.InvariantCulture), gen[3].ToString("F1", CultureInfo.InvariantCulture),
                gen[4].ToString("F1", CultureInfo.InvariantCulture),
                (privateMb - committedMb).ToString("F1", CultureInfo.InvariantCulture),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                allocRate.ToString("F1", CultureInfo.InvariantCulture),
                GC.GetTotalPauseDuration().TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
                JitInfo.GetCompiledMethodCount(false),
                (JitInfo.GetCompiledILBytes(false) / 1024).ToString(CultureInfo.InvariantCulture),
                p.HandleCount, p.Threads.Count,
                live >= 0 ? live.ToString("F1", CultureInfo.InvariantCulture) : "");
            File.AppendAllText(_path, line + "\n");
        }
        catch
        {
            // Diagnostics must never affect the app.
        }
    }
}
#endif
