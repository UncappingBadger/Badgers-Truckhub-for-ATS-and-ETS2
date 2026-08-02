using System;

namespace TruckHub.Models;

/// <summary>
/// One delivered job, captured for the session logbook (see MainWindow's slide-out drawer).
/// Session-only - never persisted to disk, cleared when TruckHub closes.
/// </summary>
public sealed class LogEntry
{
    public DateTime DeliveredAt { get; init; }
    public string Pickup { get; init; } = "";
    public string DropOff { get; init; } = "";

    /// <summary>Raw km, always metric regardless of the user's unit preference - MileageDisplay is
    /// the already-formatted string shown in the UI; this is kept alongside for the session total.</summary>
    public double DistanceKm { get; init; }
    public string MileageDisplay { get; init; } = "";

    /// <summary>Raw payout in the job's currency - PayoutDisplay is the formatted string; this is
    /// kept alongside for the session total.</summary>
    public double Income { get; init; }
    public string PayoutDisplay { get; init; } = "";
}
