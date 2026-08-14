using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace PingBoard.App.ViewModels;

/// <summary>
/// Single source of truth for column widths and visibility, shared by the header row and every
/// data row.
/// <para>
/// WinUI 3 has no <c>DataGrid</c> — the Community Toolkit dropped it at v8.0 and never ported it —
/// so the board is a virtualized <see cref="Microsoft.UI.Xaml.Controls.ListView"/> with a
/// hand-built header. The obvious failure mode of that approach is the header drifting out of
/// alignment with the rows. This object exists to make that impossible: it is registered once as
/// an application resource, and both the header grid and the row template bind their
/// <c>ColumnDefinition.Width</c> to the same properties. Hiding a column sets its width to zero in
/// one place and both move together.
/// </para>
/// </summary>
public sealed class ColumnLayout : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Natural widths. A hidden column collapses to zero rather than being removed, which keeps
    // the column index of every other cell stable.
    private static readonly Dictionary<string, double> Natural = new()
    {
        // Wider than the text needs: the leading cell also carries the trace expander, and the
        // header binds the same value, so the two stay aligned without touching anything else.
        [nameof(Status)] = 152,
        [nameof(Name)] = 130,
        [nameof(Ip)] = 130,
        [nameof(Hostname)] = 200,
        [nameof(LastOk)] = 88,
        [nameof(LastNok)] = 88,
        [nameof(Cumulative)] = 120,
        [nameof(Rtt)] = 64,
        [nameof(AvgMinMax)] = 132,
        [nameof(Loss)] = 68,
        [nameof(Jitter)] = 74,
        [nameof(Fails)] = 52,
        [nameof(Spark)] = 110,
        [nameof(Uptime)] = 68,
        [nameof(Probe)] = 74,
        [nameof(Avail24h)] = 78,
        [nameof(Avail7d)] = 78,
        [nameof(Avail30d)] = 78,
    };

    /// <summary>Hidden by default: the six requested columns plus RTT, loss and history stay on.</summary>
    public static readonly string[] DefaultHidden =
        [nameof(AvgMinMax), nameof(Fails), nameof(Uptime), nameof(Probe), nameof(Avail7d), nameof(Avail30d)];

    /// <summary>
    /// Shared instance. Both the header grid and the row template bind here via <c>x:Bind</c> to a
    /// static path, which is what guarantees they can never disagree about a column's width.
    /// <para>
    /// Declared after <see cref="Natural"/> and <see cref="DefaultHidden"/> on purpose: static
    /// field initializers run in declaration order, and the constructor reads both.
    /// </para>
    /// </summary>
    public static ColumnLayout Instance { get; } = new();

    private readonly HashSet<string> _hidden = [];

    public ColumnLayout()
    {
        foreach (var id in DefaultHidden) _hidden.Add(id);
    }

    public GridLength Status => Width(nameof(Status));
    public GridLength Name => Width(nameof(Name));
    public GridLength Ip => Width(nameof(Ip));
    public GridLength Hostname => Width(nameof(Hostname));
    public GridLength LastOk => Width(nameof(LastOk));
    public GridLength LastNok => Width(nameof(LastNok));
    public GridLength Cumulative => Width(nameof(Cumulative));
    public GridLength Rtt => Width(nameof(Rtt));
    public GridLength AvgMinMax => Width(nameof(AvgMinMax));
    public GridLength Loss => Width(nameof(Loss));
    public GridLength Jitter => Width(nameof(Jitter));
    public GridLength Fails => Width(nameof(Fails));
    public GridLength Spark => Width(nameof(Spark));
    public GridLength Uptime => Width(nameof(Uptime));
    public GridLength Probe => Width(nameof(Probe));
    public GridLength Avail24h => Width(nameof(Avail24h));
    public GridLength Avail7d => Width(nameof(Avail7d));
    public GridLength Avail30d => Width(nameof(Avail30d));

    private GridLength Width([CallerMemberName] string id = "")
    {
        if (_hidden.Contains(id)) return new GridLength(0);

        // A fitted width is already in final pixels — it was measured at the current font size,
        // which the zoom has been applied to — so it must not be scaled a second time.
        return _fitted.TryGetValue(id, out var fitted)
            ? new GridLength(fitted)
            : new GridLength(Natural[id] * _zoom);
    }

    // ------------------------------------------------------------------ auto-fit
    //
    // Widths measured from what is actually on the board, rather than the fixed guesses above.
    //
    // The hazard is jitter, not measurement: RTT and "Last OK" change several times a second, and
    // a column that resizes on every tick makes the board unreadable and unclickable. So a fitted
    // width is only adopted when it differs from the current one by more than a few pixels, and
    // the caller throttles how often it recomputes. Both together mean a column moves when the
    // content genuinely changes shape - a longer hostname, a third digit of latency - and stays
    // put otherwise.

    /// <summary>Smallest change worth moving a column for. Below this the board would just twitch.</summary>
    private const double FitThreshold = 6;

    private readonly Dictionary<string, double> _fitted = [];

    private bool _autoFit;

    public bool AutoFit
    {
        get => _autoFit;
        set
        {
            if (_autoFit == value) return;

            _autoFit = value;
            if (!value) _fitted.Clear();       // fall back to the natural widths
            RaiseAll();
        }
    }

    /// <summary>
    /// Stops continuously re-fitting but keeps the widths already measured, so a one-shot "fit
    /// now" does not quietly switch continuous fitting on as a side effect.
    /// </summary>
    public void StopTracking() => _autoFit = false;

    /// <summary>
    /// Adopts newly measured widths. Returns true when anything actually moved, so the caller can
    /// avoid raising change notifications for a board that is already the right shape.
    /// </summary>
    public bool ApplyFit(IReadOnlyDictionary<string, double> measured)
    {
        if (!_autoFit) return false;

        var moved = false;

        foreach (var (id, width) in measured)
        {
            if (!Natural.ContainsKey(id)) continue;

            if (_fitted.TryGetValue(id, out var current) && Math.Abs(current - width) < FitThreshold)
                continue;

            _fitted[id] = width;
            moved = true;
        }

        if (moved) RaiseAll();
        return moved;
    }

    // ------------------------------------------------------------------ zoom
    //
    // Zoom lives here rather than as a render transform because WinUI has no LayoutTransform, and
    // a RenderTransform scales pixels without telling layout — text goes blurry, columns stop
    // matching the header, and hit-testing lands in the wrong place. Scaling the widths and font
    // sizes that layout already reads keeps everything crisp and aligned by construction.

    private const double MinZoom = 0.7;
    private const double MaxZoom = 2.5;
    private const double ZoomStep = 0.1;

    private double _zoom = 1.0;

    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);
            if (Math.Abs(clamped - _zoom) < 0.001) return;

            _zoom = clamped;
            RaiseAll();
        }
    }

    /// <summary>Row height, so rows grow with the text rather than clipping it.</summary>
    public double RowHeight => Math.Round(28 * _zoom);

    public double CellFontSize => Math.Round(13 * _zoom, 1);
    public double HeaderFontSize => Math.Round(12 * _zoom, 1);
    public double GlyphFontSize => Math.Round(12 * _zoom, 1);
    public double ChevronFontSize => Math.Round(10 * _zoom, 1);

    /// <summary>Percentage for the status bar, so the current zoom is discoverable.</summary>
    public string ZoomLabel => $"{Math.Round(_zoom * 100)}%";

    public void ZoomIn() => Zoom = _zoom + ZoomStep;
    public void ZoomOut() => Zoom = _zoom - ZoomStep;
    public void ZoomReset() => Zoom = 1.0;

    public bool IsDefaultZoom => Math.Abs(_zoom - 1.0) < 0.001;

    // A zero-width column is not enough on its own: a TextBlock arranged into zero width still
    // paints its text, which bleeds over the neighbouring column. Cells bind their Visibility here
    // as well as their width.
    public Visibility StatusVis => Vis(nameof(Status));
    public Visibility NameVis => Vis(nameof(Name));
    public Visibility IpVis => Vis(nameof(Ip));
    public Visibility HostnameVis => Vis(nameof(Hostname));
    public Visibility LastOkVis => Vis(nameof(LastOk));
    public Visibility LastNokVis => Vis(nameof(LastNok));
    public Visibility CumulativeVis => Vis(nameof(Cumulative));
    public Visibility RttVis => Vis(nameof(Rtt));
    public Visibility AvgMinMaxVis => Vis(nameof(AvgMinMax));
    public Visibility LossVis => Vis(nameof(Loss));
    public Visibility JitterVis => Vis(nameof(Jitter));
    public Visibility FailsVis => Vis(nameof(Fails));
    public Visibility SparkVis => Vis(nameof(Spark));
    public Visibility UptimeVis => Vis(nameof(Uptime));
    public Visibility ProbeVis => Vis(nameof(Probe));
    public Visibility Avail24hVis => Vis(nameof(Avail24h));
    public Visibility Avail7dVis => Vis(nameof(Avail7d));
    public Visibility Avail30dVis => Vis(nameof(Avail30d));

    private Visibility Vis(string id) =>
        _hidden.Contains(id) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsVisible(string id) => !_hidden.Contains(id);

    public void SetVisible(string id, bool visible)
    {
        if (!Natural.ContainsKey(id)) return;

        var changed = visible ? _hidden.Remove(id) : _hidden.Add(id);
        if (!changed) return;

        Raise(id);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalWidth)));
    }

    public void Toggle(string id) => SetVisible(id, !IsVisible(id));

    /// <summary>Notifies both the width and the visibility property for a column.</summary>
    private void Raise(string id)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(id));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(id + "Vis"));
    }

    /// <summary>
    /// Re-raises every derived property. Used by zoom, which moves all of them at once — the
    /// column widths, the font sizes and the row height have to change in the same frame or the
    /// header and rows would be briefly measured against different scales.
    /// </summary>
    private void RaiseAll()
    {
        foreach (var id in Natural.Keys) Raise(id);

        foreach (var name in new[]
                 {
                     nameof(TotalWidth), nameof(RowHeight), nameof(CellFontSize),
                     nameof(HeaderFontSize), nameof(GlyphFontSize), nameof(ChevronFontSize),
                     nameof(Zoom), nameof(ZoomLabel), nameof(IsDefaultZoom),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Sum of visible widths, so the header and rows can size the scroll extent together.</summary>
    public double TotalWidth =>
        Natural.Keys
            .Where(id => !_hidden.Contains(id))
            .Sum(id => _fitted.TryGetValue(id, out var fitted) ? fitted : Natural[id] * _zoom);

    public static IEnumerable<string> AllIds => Natural.Keys;

    /// <summary>Human-readable header text, also used in the column-picker menu.</summary>
    public static string HeaderFor(string id) => id switch
    {
        nameof(Status) => "Status",
        nameof(Name) => "Name",
        nameof(Ip) => "IP",
        nameof(Hostname) => "Hostname",
        nameof(LastOk) => "Last OK",
        nameof(LastNok) => "Last NOK",
        nameof(Cumulative) => "OK / NOK",
        nameof(Rtt) => "RTT",
        nameof(AvgMinMax) => "avg / min / max",
        nameof(Loss) => "Loss %",
        nameof(Jitter) => "Jitter",
        nameof(Avail24h) => "24h %",
        nameof(Avail7d) => "7d %",
        nameof(Avail30d) => "30d %",
        nameof(Fails) => "Fail",
        nameof(Spark) => "History",
        nameof(Uptime) => "Uptime",
        nameof(Probe) => "Probe",
        _ => id,
    };

    /// <summary>Serializes hidden columns for the UI state file.</summary>
    public string HiddenCsv
    {
        get => string.Join(',', _hidden);
        set
        {
            _hidden.Clear();
            foreach (var id in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Natural.ContainsKey(id))
                    _hidden.Add(id);

            foreach (var id in Natural.Keys) Raise(id);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalWidth)));
        }
    }
}
