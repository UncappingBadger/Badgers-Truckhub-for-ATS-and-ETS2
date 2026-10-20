using System;
using System.Threading;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Reads the game's telemetry from TruckHub's own SCS Telemetry Hub plugin (its local HTTP/SSE feed, see
/// <see cref="ScsTelemetryHubClient"/>) and republishes it as our own plain snapshot, marshaled onto the thread that
/// constructed this service. It also turns the plugin's named gameplay events and the job on/off edge into the app's
/// own events and log lines.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private readonly ScsTelemetryHubClient _hubClient;
    private readonly SynchronizationContext _uiContext;

    private TelemetrySnapshot _lastSnapshot = TelemetrySnapshot.Disconnected;
    private bool _lastSdkActive;
    private bool _lastOnJob;

    public event Action<TelemetrySnapshot>? SnapshotUpdated;

    /// <summary>
    /// Fires when a job is successfully delivered (not cancelled). Carries no payload - subscribers
    /// that need the job's details should track them from SnapshotUpdated while OnJob is true, since
    /// by the time this fires the telemetry's own job fields may already be cleared for "no job".
    /// </summary>
    public event Action? JobDelivered;

    /// <summary>Fires when a job is cancelled/failed - the counterpart to JobDelivered, so subscribers
    /// know to discard whatever they were tracking instead of logging it as a completed job.</summary>
    public event Action? JobCancelled;

    /// <summary>Latest snapshot, readable synchronously (e.g. from a global hotkey handler) without waiting for the next update.</summary>
    public TelemetrySnapshot LastSnapshot => _lastSnapshot;

    public TelemetryService()
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TelemetryService must be constructed on a thread with a SynchronizationContext (e.g. the UI thread).");

        _hubClient = new ScsTelemetryHubClient();
        _hubClient.SnapshotUpdated += snapshot => UpdateCommon(snapshot);
        _hubClient.GameplayEvent += OnHubGameplayEvent;
        _hubClient.Start();
    }

    /// <summary>Named gameplay events from the hub plugin. refuel_started/ended/paid have no official SDK signal
    /// (SDK 1.14's gameplay-event catalog has none), so the plugin derives them from the tick-to-tick fuel change.</summary>
    private void OnHubGameplayEvent(string name)
    {
        switch (name)
        {
            case "job_delivered":
                AppLogger.Log("Job delivered");
                _uiContext.Post(_ => JobDelivered?.Invoke(), null);
                break;
            case "job_cancelled":
                AppLogger.Log("Job cancelled");
                _uiContext.Post(_ => JobCancelled?.Invoke(), null);
                break;
            case "player_fined": AppLogger.Log("Fined by police"); break;
            case "tollgate_paid": AppLogger.Log("Tollgate paid"); break;
            case "ferry_used": AppLogger.Log("Ferry used"); break;
            case "train_used": AppLogger.Log("Train used"); break;
            case "refuel_started": AppLogger.Log("Refueling started"); break;
            case "refuel_ended": AppLogger.Log("Refueling ended"); break;
            case "refuel_paid": AppLogger.Log("Refuel paid"); break;
        }
    }

    /// <summary>Everything that happens for every snapshot: connected/disconnected logging, job-started detection (an
    /// edge on OnJob), and publishing the snapshot itself.</summary>
    private void UpdateCommon(TelemetrySnapshot snapshot)
    {
        if (snapshot.SdkActive != _lastSdkActive)
        {
            AppLogger.Log(snapshot.SdkActive ? $"Connected to {snapshot.Game}" : "Disconnected (game closed or SDK inactive)");
            _lastSdkActive = snapshot.SdkActive;
        }

        if (snapshot.OnJob && !_lastOnJob)
        {
            AppLogger.Log(
                $"Job started: {snapshot.CompanySource}/{snapshot.CitySource} -> " +
                $"{snapshot.CompanyDestination}/{snapshot.CityDestination}, " +
                $"cargo={snapshot.CargoName} {snapshot.CargoMassKg / 1000:0}T, " +
                $"income={snapshot.Income}, plannedKm={snapshot.PlannedDistanceKm}");
        }
        _lastOnJob = snapshot.OnJob;

        _lastSnapshot = snapshot;
        _uiContext.Post(_ => SnapshotUpdated?.Invoke(snapshot), null);
    }

    public void Dispose() => _hubClient.Dispose();
}
