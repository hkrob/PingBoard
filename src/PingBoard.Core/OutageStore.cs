using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// The application's own durable record of transitions, so the outage list survives a restart.
/// <para>
/// This is deliberately a second file rather than a reuse of <see cref="TransitionLog"/>, and the
/// split is by job. That one is written for a person: it rotates, it prints
/// <c>UNREACHABLE</c> rather than an enum member, and its path is configurable precisely so it can
/// be pointed at a share and opened in a spreadsheet next year. Parsing it back would mean turning
/// display labels into values and stitching across rotated files, which makes a human-facing format
/// load-bearing for program behaviour — the reliable way to guarantee that improving the wording
/// breaks the feature.
/// </para>
/// <para>
/// This file is the opposite: a sidecar beside the board's own config, exact round-trip, and
/// bounded. Nobody is expected to read it. It sits beside the config rather than beside the
/// application because two boards are supported side by side, and one shared file would have each
/// of them loading the other's outages — see <see cref="ConfigStore.OutagePathFor"/>.
/// </para>
/// </summary>
public sealed class OutageStore
{
    private const string Header = "when,target,kind,up,seconds,status,threshold";

    /// <summary>
    /// Rewrite once the file exceeds this many rows. Twice the journal's capacity, so a compaction
    /// is rare and each one drops roughly half the file rather than trimming a line at a time.
    /// </summary>
    private const int CompactAbove = TransitionJournal.Capacity * 2;

    private readonly string _path = "";
    private readonly Lock _gate = new();
    private bool _disabled;
    private int _rows;

    public OutageStore(string path)
    {
        // GetFullPath is inside the try, not before it. It rejects a malformed path — an embedded
        // null, an invalid device name — by throwing ArgumentException, so leaving it outside meant
        // the one thing this class promises never to do was exactly what a bad path would cause.
        try
        {
            _path = Path.GetFullPath(path);

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException)
        {
            // An unusable outage log must never stop the board from monitoring.
            _path = "";
            _disabled = true;
        }
    }

    public string Path_ => _path;

    /// <summary>
    /// True once the file holds appreciably more than the journal can. Compaction needs the
    /// journal's contents, which this class does not have, so it reports the condition and leaves
    /// the caller to hand back what should be kept. Without this a board left running for months
    /// would append forever and only ever compact at startup.
    /// </summary>
    public bool NeedsCompaction
    {
        get { lock (_gate) return _rows > CompactAbove; }
    }

    /// <summary>
    /// Reads back what previous runs recorded, oldest first, and compacts the file if it has grown
    /// past <see cref="CompactAbove"/>.
    /// <para>
    /// Every parse failure is skipped rather than aborting the load. A half-written final line
    /// after a power cut must cost that one transition, not the entire history.
    /// </para>
    /// </summary>
    public IReadOnlyList<StateTransition> Load()
    {
        if (_disabled) return [];

        lock (_gate)
        {
            string[] lines;

            try
            {
                if (!File.Exists(_path)) return [];
                lines = File.ReadAllLines(_path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
                return [];
            }

            var result = new List<StateTransition>(Math.Min(lines.Length, TransitionJournal.Capacity));

            foreach (var line in lines)
            {
                if (line.Length == 0 || line.StartsWith("when,", StringComparison.Ordinal)) continue;
                if (TryParse(line, out var transition)) result.Add(transition);
            }

            _rows = result.Count;

            if (result.Count > CompactAbove)
            {
                var keep = result.GetRange(result.Count - TransitionJournal.Capacity, TransitionJournal.Capacity);
                if (RewriteLocked(keep)) result = keep;
            }

            return result;
        }
    }

    /// <summary>Appends one transition. Best-effort: a lost line beats an interrupted board.</summary>
    public void Append(in StateTransition transition)
    {
        if (_disabled) return;

        var line = Format(transition);

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                    File.WriteAllText(_path, Header + Environment.NewLine, Encoding.UTF8);

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
                _rows++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deliberately not disabling: a transient lock from a backup agent should not cost
                // every future line.
            }
        }
    }

    /// <summary>Replaces the file with exactly these transitions. Used by Clear and by compaction.</summary>
    public void Rewrite(IReadOnlyList<StateTransition> transitions)
    {
        if (_disabled) return;
        lock (_gate) RewriteLocked(transitions);
    }

    private bool RewriteLocked(IReadOnlyList<StateTransition> transitions)
    {
        var sb = new StringBuilder().AppendLine(Header);
        foreach (var t in transitions) sb.AppendLine(Format(t));

        try
        {
            // Write beside the target and move into place, so an interrupted compaction cannot
            // leave a truncated history where a complete one used to be.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
            _rows = transitions.Count;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Format(in StateTransition t) => string.Join(',',
        t.When.ToString("o", CultureInfo.InvariantCulture),
        Escape(t.TargetName),
        t.Kind.ToString(),
        t.Up ? "1" : "0",
        t.DownFor.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
        t.Status.ToString(),
        t.Threshold.ToString(CultureInfo.InvariantCulture));

    internal static bool TryParse(string line, out StateTransition transition)
    {
        transition = default;

        var fields = SplitCsv(line);
        if (fields.Count < 7) return false;

        if (!DateTimeOffset.TryParse(
                fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            return false;

        if (fields[1].Length == 0) return false;
        if (!Enum.TryParse<TransitionKind>(fields[2], out var kind)) return false;
        if (!Enum.TryParse<TargetStatus>(fields[5], out var status)) return false;

        var up = fields[3] == "1";

        _ = double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds);
        _ = int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var threshold);

        // A negative or absurd duration in a hand-edited file would render as a nonsense outage
        // length rather than being caught later.
        if (seconds < 0 || double.IsNaN(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds) seconds = 0;

        transition = new StateTransition(
            fields[1], up, when, TimeSpan.FromSeconds(seconds), status, threshold, kind);

        return true;
    }

    /// <summary>
    /// Minimal RFC 4180 reader: only enough to undo <see cref="Escape"/>, since this reads back
    /// what this class wrote rather than arbitrary spreadsheets.
    /// </summary>
    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>(7);
        var value = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"') { value.Append(c); continue; }

                // A doubled quote inside a quoted field is one literal quote.
                if (i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; continue; }
                quoted = false;
                continue;
            }

            switch (c)
            {
                case '"' when value.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(value.ToString());
                    value.Clear();
                    break;
                default:
                    value.Append(c);
                    break;
            }
        }

        fields.Add(value.ToString());
        return fields;
    }

    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
}
