using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Appends up/down transitions to a CSV, with size-based rotation.
/// <para>
/// Only <em>transitions</em> are recorded, never individual probes. A board of forty targets at
/// 1 Hz produces about 3.5 million probe results a day; as a log that is both useless and
/// enormous. What you actually want to answer later is "when did it drop, and how long was it
/// out" — which is a few lines a week.
/// </para>
/// </summary>
public sealed class TransitionLog : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int KeepFiles = 2;
    private const string Header = "timestamp,target,event,status,outage_seconds";

    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _disposed;

    public TransitionLog(string path)
    {
        _path = Path.GetFullPath(path);

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (!File.Exists(_path))
                File.WriteAllText(_path, Header + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable log must not stop the board from monitoring.
            _disposed = true;
        }
    }

    public string Path_ => _path;

    public void Write(in StateTransition transition)
    {
        if (_disposed) return;

        var line = string.Join(',',
            transition.When.ToString("o", CultureInfo.InvariantCulture),
            Escape(transition.TargetName),
            EventName(transition),
            transition.Status.Label(),
            transition.Up
                ? transition.DownFor.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
                : "");

        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing a log line is preferable to interrupting monitoring.
            }
        }
    }

    /// <summary>
    /// Appends a failure trace to a sibling <c>.traces.txt</c>.
    /// <para>
    /// Separate from the CSV on purpose: a trace is a dozen lines of hops, and forcing that into
    /// one cell would ruin the thing the CSV is good at — being opened in a spreadsheet and sorted.
    /// The two are correlated by target name and timestamp.
    /// </para>
    /// </summary>
    public void WriteTrace(in TraceResult trace)
    {
        if (_disposed) return;

        var text = new StringBuilder()
            .Append("=== ").Append(trace.When.ToString("o", CultureInfo.InvariantCulture))
            .Append("  ").Append(trace.TargetName)
            .Append("  -> ").Append(trace.Destination.ToString())
            .AppendLine()
            .AppendLine(trace.Summary())
            .AppendLine(trace.ToText())
            .AppendLine()
            .ToString();

        lock (_gate)
        {
            try
            {
                File.AppendAllText(TracePath, text, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same contract as the CSV: losing a diagnostic beats interrupting monitoring.
            }
        }
    }

    /// <summary>Sibling file holding failure traces: <c>events.csv</c> → <c>events.csv.traces.txt</c>.</summary>
    public string TracePath => _path + ".traces.txt";

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < MaxBytes) return;

        // Shift .1 -> .2, drop the oldest, then move the live file down.
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            var to = $"{_path}.{i + 1}";
            if (File.Exists(from)) File.Move(from, to, overwrite: true);
        }

        File.Move(_path, _path + ".1", overwrite: true);
        File.WriteAllText(_path, Header + Environment.NewLine, Encoding.UTF8);
    }

    /// <summary>
    /// Soft transitions are carried as extra values in the existing <c>event</c> column rather
    /// than as a new column. A new column would leave every row written before this build sitting
    /// under the wrong headings in the same file, and a log you cannot open in a spreadsheet years
    /// later is not a log — whereas an unfamiliar value in a known column costs a reader nothing.
    /// </summary>
    private static string EventName(in StateTransition t) => t.Kind switch
    {
        TransitionKind.Degraded => t.Up ? "degraded_cleared" : "degraded",
        TransitionKind.Certificate => "cert_expiring",
        _ => t.Up ? "recovered" : "down",
    };

    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;

    public void Dispose() => _disposed = true;
}
