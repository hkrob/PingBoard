using System.Globalization;
using System.Text;

namespace PingBoard.App;

/// <summary>
/// Appends unhandled exceptions to a file next to the settings.
/// <para>
/// WinUI 3 can tear the process down on an unhandled exception without surfacing anything at all.
/// For a tool meant to sit running for days, "it was gone when I came back" is not a usable bug
/// report — this is what makes it one.
/// </para>
/// </summary>
public static class CrashLog
{
    private static readonly Lock Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(AppPaths.DataDirectory, "crash.log");

    public static void Write(Exception ex)
    {
        try
        {
            var sb = new StringBuilder()
                .Append("=== ")
                .Append(DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture))
                .AppendLine(" ===")
                .AppendLine(ex.ToString())
                .AppendLine();

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);

                // Keep the log bounded; a crash loop must not fill the disk.
                if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024)
                    File.Delete(Path);

                File.AppendAllText(Path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Logging a crash must never itself crash.
        }
    }
}

/// <summary>Well-known locations. Unpackaged apps get no <c>ApplicationData.Current</c>.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PingBoard");

    /// <summary>Remembers the last config file opened, and window placement.</summary>
    public static string UiStateFile { get; } = System.IO.Path.Combine(DataDirectory, "ui-state.ini");

    public static string DefaultConfigFile { get; } = System.IO.Path.Combine(DataDirectory, "config.ini");

    /// <summary>
    /// The application's own transition history, feeding the outage window.
    /// <para>
    /// Fixed here rather than following the config file, unlike the events CSV. The events CSV is a
    /// document about one board and belongs beside it; this is the app remembering what it saw, and
    /// a board opened from a USB stick should not lose its outage history because the stick was
    /// unplugged.
    /// </para>
    /// </summary>
    public static string OutageFile { get; } = System.IO.Path.Combine(DataDirectory, "outages.csv");
}
