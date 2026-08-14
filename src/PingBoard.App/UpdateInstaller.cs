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
    /// <returns>The downloaded path, or an error message. Exactly one is non-null.</returns>
    public static async Task<(string Path, string? Error)> DownloadAsync(string url, CancellationToken ct)
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
    public static void Launch(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
            App.Window?.ExitApplication();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// Only GitHub over HTTPS. The URL comes from a network response, and a downloader that will
    /// fetch and execute whatever it is handed is a remote code execution primitive.
    /// </summary>
    private static bool IsTrusted(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}
