using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PingBoard.Core;

/// <summary>What the newest GitHub release says, compared against what is running.</summary>
/// <param name="Available">True only when the published release is genuinely newer.</param>
/// <param name="LatestVersion">Version parsed from the release tag, or null when unknown.</param>
/// <param name="DownloadUrl">Installer asset to fetch, or empty when the release carries none.</param>
/// <param name="ReleaseUrl">Human-readable release page, for "see what changed".</param>
/// <param name="Error">Why the check could not complete. Null on success.</param>
/// <param name="DownloadSha256">
/// Lower-case hex SHA-256 of the installer asset as GitHub reports it, or empty when the release
/// predates GitHub publishing asset digests. The downloader verifies against it when present.
/// </param>
public readonly record struct UpdateInfo(
    bool Available,
    Version? LatestVersion,
    string DownloadUrl,
    string ReleaseUrl,
    string? Error,
    string DownloadSha256 = "");

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
            request.Headers.TryAddWithoutValidation("User-Agent", HttpProbe.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new UpdateInfo(false, null, "", ProjectUrl, DescribeFailure(response));

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseRelease(json, current);
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
    /// Why GitHub said no, in words that say what to do about it.
    /// <para>
    /// The rate limit is the case worth spelling out. Unauthenticated API calls are capped at sixty
    /// an hour per public IP address — shared by every machine behind the same router — and GitHub
    /// answers the sixty-first with a bare 403. "GitHub returned 403 rate limit exceeded" reads like
    /// a fault in PingBoard or a blocked account; it is neither, and it clears itself at a time
    /// GitHub states in the response.
    /// </para>
    /// </summary>
    public static string DescribeFailure(HttpResponseMessage response)
    {
        var code = (int)response.StatusCode;

        // 404 is the normal answer while the repository is private, so say something more useful
        // than "not found".
        if (code == 404) return "no public releases found (the repository may still be private)";

        if ((code is 403 or 429) && Header(response, "x-ratelimit-remaining") == "0")
        {
            var reset = long.TryParse(Header(response, "x-ratelimit-reset"), NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime()
                : (DateTimeOffset?)null;

            return "GitHub's hourly limit for anonymous requests from this network has been reached"
                   + (reset is { } at ? $" — try again after {at.ToString("HH:mm", CultureInfo.CurrentCulture)}." : " — try again later.");
        }

        return $"GitHub returned {code} {response.ReasonPhrase}";
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Reads the fields this needs from a <c>releases/latest</c> response.
    /// <para>
    /// Navigated with <see cref="JsonDocument"/> rather than bound to a model — four values out of a
    /// large document, and no schema to keep in step with — and rather than matched with regular
    /// expressions, which is what this used to do: a pattern for <c>"html_url"</c> takes whichever
    /// comes first in the text, and GitHub's field order is not a contract.
    /// </para>
    /// </summary>
    public static UpdateInfo ParseRelease(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = Text(root, "tag_name");
            var htmlUrl = Text(root, "html_url");
            if (htmlUrl.Length == 0) htmlUrl = ProjectUrl;

            var (asset, digest) = FirstInstallerAsset(root);

            if (ParseVersion(tag) is not { } latest)
                return new UpdateInfo(false, null, asset, htmlUrl, $"could not read a version from tag '{tag}'", digest);

            return new UpdateInfo(latest > current, latest, asset, htmlUrl, null, digest);
        }
        catch (JsonException)
        {
            return new UpdateInfo(false, null, "", ProjectUrl, "GitHub sent a response that could not be read");
        }
    }

    /// <summary>
    /// Parses a release tag into a version, tolerating the conventional leading "v".
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;

        // TryParse, not Parse: a component too long for an int would otherwise throw
        // OverflowException out of a method whose callers only expect "a version or null".
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return null;
        }

        var build = 0;
        if (match.Groups[3].Success
            && !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out build))
        {
            return null;
        }

        return new Version(major, minor, build);
    }

    /// <summary>
    /// The first <c>.exe</c> asset, and its SHA-256 when GitHub supplies one (as
    /// <c>"digest": "sha256:…"</c>).
    /// </summary>
    private static (string Url, string Sha256) FirstInstallerAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return ("", "");

        foreach (var asset in assets.EnumerateArray())
        {
            var url = Text(asset, "browser_download_url");
            if (!url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var digest = Text(asset, "digest");
            const string prefix = "sha256:";
            var sha = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                      && digest.Length == prefix.Length + 64
                ? digest[prefix.Length..].ToLowerInvariant()
                : "";

            return (url, sha);
        }

        return ("", "");
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
