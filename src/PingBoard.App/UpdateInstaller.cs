using System.Diagnostics;
using System.Net.Http;

namespace PingBoard.App;

/// <summary>
/// Downloads a release installer and hands it to Windows to run.
/// <para>
/// Two deliberate limits. It only accepts an <c>https</c> URL on <c>github.com</c>, so a malformed
/// or redirected response cannot turn "check for updates" into "run an arbitrary executable". And
/// it never runs anything without the user having clicked through — the caller asks first.
/// </para>
/// <para>
/// The installer is left to replace the running application on its own: the Inno Setup script
/// already closes any running instance before it copies files, so PingBoard simply exits and lets
/// it get on with it.
/// </para>
/// </summary>
public static class UpdateInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Fetches the installer into the temp folder.
    /// </summary>
    /// <param name="expectedSha256">
    /// The digest GitHub publishes for the asset, as lower-case hex, or empty to skip the check for
    /// a release old enough not to carry one. When present a mismatch is fatal: a truncated or
    /// altered installer is deleted rather than handed to Windows to run.
    /// </param>
    /// <returns>The downloaded path, or an error message. Exactly one is non-null.</returns>
    public static async Task<(string Path, string? Error)> DownloadAsync(
        string url, string expectedSha256, CancellationToken ct)
    {
        if (!IsTrusted(url))
            return ("", "the download link was not a GitHub HTTPS address");

        try
        {
            var name = System.IO.Path.GetFileName(new Uri(url).LocalPath);
            if (name.Length == 0 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ("", "the release asset was not an installer");

            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);

            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return ("", $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}");

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string actual;
                await using (var written = File.OpenRead(target))
                {
                    var hash = await System.Security.Cryptography.SHA256.HashDataAsync(written, ct).ConfigureAwait(false);
                    actual = Convert.ToHexStringLower(hash);
                }

                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(target); } catch (IOException) { /* it will not be run either way */ }
                    return ("", "the downloaded installer did not match the checksum GitHub published for it, so it was discarded");
                }
            }

            return (target, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                     or UnauthorizedAccessException or UriFormatException)
        {
            return ("", ex.Message);
        }
    }

    /// <summary>Runs the downloaded installer and exits, so it can replace the running files.</summary>
    /// <returns>
    /// False if the process could not be started — e.g. antivirus quarantined the freshly-written
    /// exe. The caller needs this: without it, a swallowed failure here leaves the About dialog
    /// telling the user "PingBoard will close" while the button sits disabled and nothing happens.
    /// </returns>
    public static bool Launch(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            App.Window?.ExitApplication();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return false;
        }
    }

    /// <summary>
    /// The asset URL must be GitHub over HTTPS. It arrives in a network response, and a downloader
    /// that will fetch and execute whatever it is handed is a remote code execution primitive.
    /// <para>
    /// This checks the URL the release API returned, which is always
    /// <c>github.com/{owner}/{repo}/releases/download/...</c>. GitHub then redirects that to a
    /// signed, short-lived CDN address — currently <c>release-assets.githubusercontent.com</c>,
    /// previously <c>objects.githubusercontent.com</c> — and <see cref="HttpClient"/> follows it
    /// without consulting this method.
    /// </para>
    /// <para>
    /// Listing those CDN hosts here would therefore be theatre: they are never tested, and naming
    /// them implies a guarantee that is not being made. The guarantee actually offered is that the
    /// chain <em>starts</em> at GitHub over TLS; where GitHub redirects from there is GitHub's to
    /// decide, and pinning a hostname they have already changed once would only break the updater
    /// the next time they change it.
    /// </para>
    /// </summary>
    private static bool IsTrusted(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));
}
