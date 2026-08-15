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
        [nameof(CertExpiring)] = 76,
        [nameof(CertDays)] = 76,
    };

    /// <summary>
    /// Hidden on a fresh install: the six original ones plus the two certificate columns, which
    /// only ever say anything for an HTTPS target and would otherwise be two columns of dashes on
    /// a board of pings.
    /// <para>
    /// This governs a first run only. An existing board restores <see cref="HiddenCsv"/>, which
    /// names the columns that were hidden when it was written and so cannot mention a column that
    /// did not exist yet — a new column therefore appears on an existing board and stays out of the
    /// way on a new one, which is the right behaviour in both cases.
    /// </para>
    /// </summary>
    public static readonly string[] DefaultHidden =
    [
        nameof(AvgMinMax), nameof(Fails), nameof(Uptime), nameof(Probe),
        nameof(Avail7d), nameof(Avail30d), nameof(CertExpiring), nameof(CertDays),
    ];

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
    public GridLength CertExpiring => Width(nameof(CertExpiring));
    public GridLength CertDays => Width(nameof(CertDays));

    private GridLength Width([CallerMemberName] string id = "")
    {
        if (_hidden.Contains(id)) return new GridLength(0);

        // A fitted width is already in final pixels — it was measured at the current font size,
        // which the zoom has been applied to — so it must not be scaled a second time.
        return _fitted.TryGetValue(id, out var fitted)
            ? new GridLength(fitted)
            : new GridLength(Natural[id] * _zoom);
    }

    // ------------------------------------------------------------------ order
    //
    // Column order is data rather than markup. It used to be baked into both templates twice
    // over: each cell carried a literal Grid.Column, and each ColumnDefinition was "Hostname's
    // width" only because Hostname happened to be fourth. Rearranging meant editing two files in
    // lockstep, and getting it wrong drifts the header out of alignment with the rows.
    //
    // Now every cell binds Grid.Column to its own index and every ColumnDefinition binds to "the
    // width of whatever sits at position n", so moving a column is a permutation of one list.

    private readonly List<string> _order = [.. Natural.Keys];

    public IReadOnlyList<string> Order => _order;

    /// <summary>Position of a column, or 0 if it is somehow unknown.</summary>
    public int IndexOf(string id)
    {
        var index = _order.IndexOf(id);
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// Moves a column by <paramref name="delta"/> positions. Returns false when it did not move,
    /// so the caller can leave the UI alone.
    /// </summary>
    public bool Move(string id, int delta)
    {
        var from = _order.IndexOf(id);
        if (from < 0) return false;

        var to = Math.Clamp(from + delta, 0, _order.Count - 1);
        if (to == from) return false;

        _order.RemoveAt(from);
        _order.Insert(to, id);
        RaiseAll();
        return true;
    }

    public void ResetOrder()
    {
        _order.Clear();
        _order.AddRange(Natural.Keys);
        RaiseAll();
    }

    /// <summary>
    /// Round-trips through the UI state file. Unknown ids are dropped and missing ones appended,
    /// so a config written by an older or newer build still yields every column exactly once
    /// rather than losing one or listing it twice.
    /// </summary>
    public string OrderCsv
    {
        get => string.Join(',', _order);
        set
        {
            var restored = new List<string>();

            foreach (var id in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Natural.ContainsKey(id) && !restored.Contains(id))
                    restored.Add(id);

            foreach (var id in Natural.Keys)
                if (!restored.Contains(id))
                    restored.Add(id);

            _order.Clear();
            _order.AddRange(restored);
            RaiseAll();
        }
    }

    private GridLength WidthAt(int position) =>
        position < _order.Count ? Width(_order[position]) : new GridLength(0);

    // Width of whatever column currently sits at each position.
    public GridLength Pos0 => WidthAt(0);
    public GridLength Pos1 => WidthAt(1);
    public GridLength Pos2 => WidthAt(2);
    public GridLength Pos3 => WidthAt(3);
    public GridLength Pos4 => WidthAt(4);
    public GridLength Pos5 => WidthAt(5);
    public GridLength Pos6 => WidthAt(6);
    public GridLength Pos7 => WidthAt(7);
    public GridLength Pos8 => WidthAt(8);
    public GridLength Pos9 => WidthAt(9);
    public GridLength Pos10 => WidthAt(10);
    public GridLength Pos11 => WidthAt(11);
    public GridLength Pos12 => WidthAt(12);
    public GridLength Pos13 => WidthAt(13);
    public GridLength Pos14 => WidthAt(14);
    public GridLength Pos15 => WidthAt(15);
    public GridLength Pos16 => WidthAt(16);
    public GridLength Pos17 => WidthAt(17);
    public GridLength Pos18 => WidthAt(18);
    public GridLength Pos19 => WidthAt(19);

    // Where each column currently sits.
    public int IdxStatus => IndexOf(nameof(Status));
    public int IdxName => IndexOf(nameof(Name));
    public int IdxIp => IndexOf(nameof(Ip));
    public int IdxHostname => IndexOf(nameof(Hostname));
    public int IdxLastOk => IndexOf(nameof(LastOk));
    public int IdxLastNok => IndexOf(nameof(LastNok));
    public int IdxCumulative => IndexOf(nameof(Cumulative));
    public int IdxRtt => IndexOf(nameof(Rtt));
    public int IdxAvgMinMax => IndexOf(nameof(AvgMinMax));
    public int IdxLoss => IndexOf(nameof(Loss));
    public int IdxJitter => IndexOf(nameof(Jitter));
    public int IdxFails => IndexOf(nameof(Fails));
    public int IdxSpark => IndexOf(nameof(Spark));
    public int IdxUptime => IndexOf(nameof(Uptime));
    public int IdxProbe => IndexOf(nameof(Probe));
    public int IdxAvail24h => IndexOf(nameof(Avail24h));
    public int IdxAvail7d => IndexOf(nameof(Avail7d));
    public int IdxAvail30d => IndexOf(nameof(Avail30d));
    public int IdxCertExpiring => IndexOf(nameof(CertExpiring));
    public int IdxCertDays => IndexOf(nameof(CertDays));

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

    /// <summary>
    /// Toolbar text and glyphs. Every control up there is a Control with its own FontSize from its
    /// style, so none of them inherit and each has to be told explicitly — the same reason the row
    /// cells needed binding.
    /// </summary>
    public double ToolbarFontSize => Math.Round(13 * _zoom, 1);

    /// <summary>Tab strip labels, a little smaller than the toolbar.</summary>
    public double TabFontSize => Math.Round(12 * _zoom, 1);

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
    public Visibility CertExpiringVis => Vis(nameof(CertExpiring));
    public Visibility CertDaysVis => Vis(nameof(CertDays));

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
        foreach (var id in Natural.Keys)
        {
            Raise(id);

            // Where the column sits, for the cells that bind Grid.Column to it.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Idx" + id));
        }

        // And the width of each position, for the ColumnDefinitions.
        for (var position = 0; position < Natural.Count; position++)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Pos" + position));

        foreach (var name in new[]
                 {
                     nameof(TotalWidth), nameof(RowHeight), nameof(CellFontSize),
                     nameof(HeaderFontSize), nameof(GlyphFontSize), nameof(ChevronFontSize),
                     nameof(ToolbarFontSize), nameof(TabFontSize),
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
        nameof(CertExpiring) => "Expiring",
        nameof(CertDays) => "Cert days",
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
