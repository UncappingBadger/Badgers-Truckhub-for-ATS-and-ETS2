using System;
using System.IO;

namespace TruckHub.Services;

/// <summary>
/// Minimal append-only event log for tailing during a play session - not a diagnostics/trace
/// log, just discrete things worth watching for (connect/disconnect, job lifecycle, errors).
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();
    private static readonly string LogPath;

    static AppLogger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TruckHub", "logs");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "truckhub.log");
    }

    public static string FilePath => LogPath;

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
