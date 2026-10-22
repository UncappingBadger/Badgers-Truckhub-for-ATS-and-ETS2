using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TruckHub.Services;

/// <summary>Remembers each truck's steering calibration between runs, so the ALIGN guide lines are right from the first second for a truck that has been driven before.</summary>
public interface ISteeringCalibrationStore
{
    SteeringRecord? Get(string key);
    void Put(string key, SteeringGeometry geometry, SteeringStats? stats);
}

/// <summary>The running sums behind <see cref="SteeringCalibrator"/>'s fits (reversing wheelbase, forward wheelbase, fifth-wheel
/// offset: sum of x squared, sum of x times y, number of samples). Keeping these - instead of just the answer - is what lets the
/// next drive carry on learning where this one stopped.</summary>
public sealed record SteeringStats(double Rxx, double Rxy, double Rw, double Fxx, double Fxy, double Fw, double Cxx, double Cxy, double Cw);

/// <summary>What is kept for one truck: the answer (also all that older files hold) and, newer, the sums behind it.</summary>
public sealed record SteeringRecord(double WheelbaseM, double FifthWheelAheadM, DateTime SavedUtc, SteeringStats? Stats = null);

/// <summary>One small JSON file next to settings.json (hitch-steering.json). Best-effort like the job-distance file: a missing
/// or unreadable file only means the next drive learns the truck again. Newest 40 trucks are kept.</summary>
public sealed class SteeringCalibrationFileStore : ISteeringCalibrationStore
{
    private const int MaxEntries = 40;
    private readonly string _path;
    private readonly Dictionary<string, SteeringRecord> _records;

    public SteeringCalibrationFileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TruckHub-Dev", "hitch-steering.json"))
    {
    }

    public SteeringCalibrationFileStore(string path)
    {
        _path = path;
        _records = Load();
    }

    public SteeringRecord? Get(string key) =>
        _records.TryGetValue(key, out var r) && r.WheelbaseM is > 2.0 and < 15.0 && Math.Abs(r.FifthWheelAheadM) <= 4.0
            ? r
            : null;

    public void Put(string key, SteeringGeometry geometry, SteeringStats? stats)
    {
        _records[key] = new SteeringRecord(Math.Round(geometry.WheelbaseM, 3), Math.Round(geometry.FifthWheelAheadOfRearAxleM, 3), DateTime.UtcNow, stats);
        if (_records.Count > MaxEntries)
        {
            foreach (var old in _records.OrderBy(r => r.Value.SavedUtc).Take(_records.Count - MaxEntries).Select(r => r.Key).ToList())
            {
                _records.Remove(old);
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write-then-replace so a crash mid-write can never leave a half-written file behind.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_records));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Best-effort - see class comment.
        }
    }

    private Dictionary<string, SteeringRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, SteeringRecord>>(File.ReadAllText(_path)) ?? new();
            }
        }
        catch
        {
            // Best-effort - see class comment.
        }

        return new();
    }
}
