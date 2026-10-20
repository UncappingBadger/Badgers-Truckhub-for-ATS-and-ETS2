using System;
using System.IO;
using System.Text.Json;

namespace TruckHub.Services;

/// <summary>The total route length of the job in progress, as it must survive a restart of the game
/// or of TruckHub. <see cref="Key"/> identifies the job so a record is never applied to another one.</summary>
public sealed record JobDistanceRecord(string Key, double BaselineMeters, DateTime SavedUtc);

public interface IJobDistanceStore
{
    JobDistanceRecord? Load();
    void Save(JobDistanceRecord record);
    void Clear();
}

/// <summary>One small JSON file next to settings.json. Every operation is best-effort: a missing or
/// unreadable file just means the next launch works the total out afresh (see JobDistanceTracker.Seed).</summary>
public sealed class JobDistanceFileStore : IJobDistanceStore
{
    private readonly string _path;

    public JobDistanceFileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TruckHub-Dev", "job-distance.json"))
    {
    }

    public JobDistanceFileStore(string path) => _path = path;

    public JobDistanceRecord? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<JobDistanceRecord>(File.ReadAllText(_path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(JobDistanceRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write-then-replace so a crash mid-write can never leave a half-written file behind.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(record));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort - see class comment.
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Best-effort - see class comment.
        }
    }
}

/// <summary>
/// The "how far along the job am I" baseline behind the Distance card (the "of N mi" total, and what
/// has been driven so far = baseline - the game's remaining navigation distance).
///
/// The baseline is a running maximum of the game's navigation distance, captured once per job and -
/// the point of this class - remembered across restarts. Before this existed it lived only in memory:
/// closing TruckHub, or quitting and reloading the game, threw it away, and the next launch captured the
/// distance REMAINING at that moment as the new "total" (a 1,100-mile job reloaded 600 miles from the
/// end read "of 597 mi", and the miles already driven vanished).
///
/// Where the baseline comes from, in order:
///   1. A saved record for this exact job (same cities, companies, cargo, income, quoted distance) -
///      the normal case after a restart. The record is rewritten whenever the baseline grows.
///   2. Otherwise, a job first seen part-way along (its cargo already loaded on the trailer, and the game's
///      remaining distance under <see cref="MidRouteFraction"/> of the job's quoted length): the quoted length is
///      the best total available. "Cargo loaded" is what separates this from a job that has only just been
///      accepted while the truck still hauls the previous trailer - there the GPS also reads short (it points
///      at the pickup), but nothing has been driven yet. It is not the same basis as the GPS distance, but it is the full job, and it only
///      applies once because step 1 takes over from then on.
///   3. Otherwise (a fresh job, or still on the way to the pickup, cargo not loaded yet): the game's navigation distance now -
///      exactly what it always did, and the same basis as every later reading.
/// The baseline only ever grows (a longer re-route, or the GPS switching from the pickup to the
/// destination, raises it), so "driven so far" can never go negative and always reaches 100% at delivery.
/// </summary>
public sealed class JobDistanceTracker
{
    /// <summary>A job is treated as "already under way" when the remaining distance is below this share of
    /// the quoted length. A fresh job's GPS route is within roughly +-15% of the quote, so 0.8 keeps those
    /// on the navigation basis.</summary>
    public const double MidRouteFraction = 0.8;

    /// <summary>A saved record older than this is ignored (a job abandoned and forgotten about).</summary>
    public static readonly TimeSpan MaxRecordAge = TimeSpan.FromDays(30);

    private const double SaveStepMeters = 100;

    private readonly IJobDistanceStore _store;
    private string? _key;
    private string? _endedKey;
    private double? _baseline;
    private double _lastSaved = -1;

    public JobDistanceTracker(IJobDistanceStore store) => _store = store;

    /// <summary>Total route length for the current job in meters, or null until the first usable reading.</summary>
    public double? BaselineMeters => _baseline;

    /// <summary>Feed every telemetry snapshot. `jobKey` must be empty while the job's details are not all
    /// present yet; nothing is captured or saved until they are.</summary>
    public void Update(bool onJob, string jobKey, double navMeters, double plannedKm, bool cargoLoaded, DateTime nowUtc)
    {
        // Not on a job - also what a closed game looks like. Forget the in-memory baseline but leave the
        // saved record alone: a reload takes a while to bring the job back (job fields lag the connection by
        // tens of seconds), and the record is what the reload needs. JobEnded() is what discards it.
        if (!onJob || string.IsNullOrEmpty(jobKey))
        {
            if (!onJob)
            {
                _endedKey = null; // the game has moved on from the job that was delivered / cancelled
            }

            _key = null;
            _baseline = null;
            _lastSaved = -1;
            return;
        }

        // A delivered / cancelled job can linger for a snapshot or two before the game reports "no job";
        // without this it would be re-seeded (and re-saved) from a near-zero remaining distance.
        if (jobKey == _endedKey)
        {
            return;
        }

        if (_key != jobKey)
        {
            _key = jobKey;
            _baseline = null;
            _lastSaved = -1;
        }

        if (_baseline is null)
        {
            if (navMeters <= 0)
            {
                return; // The game has not computed its route yet; a zero would only be a placeholder.
            }

            var record = _store.Load();
            _baseline = record is { BaselineMeters: > 0 } r && r.Key == jobKey && nowUtc - r.SavedUtc <= MaxRecordAge
                ? Math.Max(r.BaselineMeters, navMeters)
                : Seed(navMeters, plannedKm, cargoLoaded);
            Persist(nowUtc, force: true);
            return;
        }

        if (navMeters > _baseline)
        {
            _baseline = navMeters;
            Persist(nowUtc, force: false);
        }
    }

    /// <summary>The job was delivered or cancelled: its record must not leak onto a later job.</summary>
    public void JobEnded()
    {
        _store.Clear();
        _endedKey = _key;
        _key = null;
        _baseline = null;
        _lastSaved = -1;
    }

    private static double Seed(double navMeters, double plannedKm, bool cargoLoaded)
    {
        var plannedMeters = plannedKm * 1000.0;
        return cargoLoaded && plannedMeters > 0 && navMeters < plannedMeters * MidRouteFraction
            ? plannedMeters
            : navMeters;
    }

    private void Persist(DateTime nowUtc, bool force)
    {
        if (_key is null || _baseline is not { } baseline)
        {
            return;
        }

        if (force || baseline - _lastSaved >= SaveStepMeters)
        {
            _store.Save(new JobDistanceRecord(_key, baseline, nowUtc));
            _lastSaved = baseline;
        }
    }
}
