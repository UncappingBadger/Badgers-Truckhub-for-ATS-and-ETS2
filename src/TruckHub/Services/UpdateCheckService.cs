using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TruckHub.Models;

namespace TruckHub.Services;

/// <summary>
/// Checks GitHub's public releases API for a newer TruckHub version than the one currently running.
/// One instance is shared between MainViewModel (a silent check on launch) and SettingsViewModel (a
/// manual "Check for Updates" button), so a result found either way shows up in both places without
/// re-querying GitHub unnecessarily.
/// </summary>
public sealed class UpdateCheckService
{
    private const string ApiUrl =
        "https://api.github.com/repos/UncappingBadger/Badgers-Truckhub-for-ATS-and-ETS2/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Fires whenever a check completes, success or failure, from whichever caller triggered it.</summary>
    public event Action<UpdateCheckResult>? Checked;

    /// <summary>The most recent result, if any check has run yet this session.</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    public async Task<UpdateCheckResult> CheckAsync()
    {
        UpdateCheckResult result;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            // GitHub's API rejects requests with no User-Agent at all.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("TruckHub", CurrentVersionTag));

            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;

            var remote = ParseVersion(tagName);
            var local = CurrentVersion;

            result = new UpdateCheckResult
            {
                CheckSucceeded = true,
                UpdateAvailable = remote is not null && local is not null && IsNewer(remote, local),
                LatestVersion = tagName,
                ReleaseUrl = releaseUrl,
            };
        }
        catch
        {
            // No internet, GitHub unreachable, rate-limited, unexpected response shape - none of
            // these should ever surface as an error to a background/silent check. Callers that want
            // to tell the user "couldn't check" (the manual button) do that themselves by looking at
            // CheckSucceeded, rather than this method throwing.
            result = new UpdateCheckResult { CheckSucceeded = false };
        }

        LastResult = result;
        try
        {
            Checked?.Invoke(result);
        }
        catch (Exception ex)
        {
            // A handler throwing must never fault this method - CheckAsync's callers (the silent
            // launch check and the manual button) both await it, and an unguarded fault here would
            // propagate up and leave the button's "Checking..." state stranded forever.
            AppLogger.Log($"UpdateCheckService: Checked handler threw {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private static Version? CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;

    private static string CurrentVersionTag => CurrentVersion?.ToString(3) ?? "0.0.0";

    // GitHub tags look like "V1.0" or "v1.2.3" - System.Version needs at least two numeric segments
    // and, critically, doesn't compare cleanly across different segment counts (a missing segment
    // parses as -1, not 0), so IsNewer below compares the three numbers directly rather than trusting
    // Version.CompareTo on two versions that might have a different number of segments.
    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var version) ? version : null;
    }

    private static bool IsNewer(Version remote, Version local)
    {
        var remoteMajor = remote.Major;
        var remoteMinor = Math.Max(0, remote.Minor);
        var remoteBuild = Math.Max(0, remote.Build);
        var localMajor = local.Major;
        var localMinor = Math.Max(0, local.Minor);
        var localBuild = Math.Max(0, local.Build);

        if (remoteMajor != localMajor) return remoteMajor > localMajor;
        if (remoteMinor != localMinor) return remoteMinor > localMinor;
        return remoteBuild > localBuild;
    }
}
