namespace TruckHub.Models;

/// <summary>A live position update pushed to the GPS map's WebView2 page.</summary>
public sealed record GpsLivePosition(double Lon, double Lat, double BearingDegrees, float SpeedKph);
