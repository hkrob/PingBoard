using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace PingBoard.Core;

/// <summary>What the newest GitHub release says, compared against what is running.</summary>
/// <param name="Available">True only when the published release is genuinely newer.</param>
/// <param name="LatestVersion">Version parsed from the release tag, or null when unknown.</param>
/// <param name="DownloadUrl">Installer asset to fetch, or empty when the release carries none.</param>
/// <param name="ReleaseUrl">Human-readable release page, for "see what changed".</param>
/// <param name="Error">Why the check could not complete. Null on success.</param>
public readonly record struct UpdateInfo(
    bool Available,
    Version? LatestVersion,
    string DownloadUrl,
    string ReleaseUrl,
    string? Error);

/// <summary>
/// Asks GitHub whether a newer release exists.
/// <para>
/// Read-only and best-effort. It reports what is available and never installs anything on its own:
/// replacing the binary of a monitor someone is relying on, without being asked, is not a decision
/// this code gets to make. The caller shows the answer and the user chooses.
/// </para>
/// <para>
/// A failure here is not an application error. An unreachable network, a rate limit, or a private
/// repository all produce the same shrug — the message is surfaced where the user asked the
/// question and nowhere else.
/// </para>
/// </summary>
public static class UpdateCheck
{
    private const string LatestReleaseApi = "https://api.github.com/repos/hkrob/PingBoard/releases/latest";

    /// <summary>The project page. Also what the About box links to.</summary>
    public const string ProjectUrl = "https://github.com/hkrob/PingBoard";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Fetches the newest release and compares it with <paramref name="current"/>.
    /// </summary>
    public static async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.TryAddWithoutValidation("User-Agent", "PingBoard");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 is the normal answer while the repository is private, so say something more
                // useful than "not found".
                var reason = (int)response.StatusCode == 404
                    ? "no public releases found (the repository may still be private)"
                    : $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}";

                return new UpdateInfo(false, null, "", ProjectUrl, reason);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var tag = Field(json, "tag_name");
            var htmlUrl = Field(json, "html_url");
            var asset = FirstInstallerAsset(json);

            if (ParseVersion(tag) is not { } latest)
                return new UpdateInfo(false, null, asset, htmlUrl, $"could not read a version from tag '{tag}'");

            return new UpdateInfo(latest > current, latest, asset, htmlUrl, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new UpdateInfo(false, null, "", ProjectUrl, "could not reach GitHub: " + ex.Message);
        }
    }

    /// <summary>
    /// Parses a release tag into a version, tolerating the conventional leading "v".
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;

        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var build = match.Groups[3].Success
            ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
            : 0;

        return new Version(major, minor, build);
    }

    /// <summary>
    /// The first <c>.exe</c> asset. Hand-rolled rather than deserialised: the response is large and
    /// we want four strings from it, and a JSON model would have to track GitHub's schema forever.
    /// </summary>
    private static string FirstInstallerAsset(string json)
    {
        foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"",
                                          RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            var url = m.Groups[1].Value;
            if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return url;
        }

        return "";
    }

    private static string Field(string json, string name)
    {
        var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"]*)\"",
                            RegexOptions.None, TimeSpan.FromSeconds(2));
        return m.Success ? m.Groups[1].Value : "";
    }
}
