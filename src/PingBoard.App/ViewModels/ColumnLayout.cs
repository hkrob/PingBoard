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
        [nameof(Status)] = 132,
        [nameof(Name)] = 130,
        [nameof(Ip)] = 130,
        [nameof(Hostname)] = 200,
        [nameof(LastOk)] = 88,
        [nameof(LastNok)] = 88,
        [nameof(Cumulative)] = 120,
        [nameof(Rtt)] = 64,
        [nameof(AvgMinMax)] = 132,
        [nameof(Loss)] = 68,
        [nameof(Fails)] = 52,
        [nameof(Spark)] = 110,
        [nameof(Uptime)] = 68,
        [nameof(Probe)] = 74,
    };

    /// <summary>Hidden by default: the six requested columns plus RTT, loss and history stay on.</summary>
    public static readonly string[] DefaultHidden =
        [nameof(AvgMinMax), nameof(Fails), nameof(Uptime), nameof(Probe)];

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
    public GridLength Fails => Width(nameof(Fails));
    public GridLength Spark => Width(nameof(Spark));
    public GridLength Uptime => Width(nameof(Uptime));
    public GridLength Probe => Width(nameof(Probe));

    private GridLength Width([CallerMemberName] string id = "") =>
        _hidden.Contains(id) ? new GridLength(0) : new GridLength(Natural[id]);

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
    public Visibility FailsVis => Vis(nameof(Fails));
    public Visibility SparkVis => Vis(nameof(Spark));
    public Visibility UptimeVis => Vis(nameof(Uptime));
    public Visibility ProbeVis => Vis(nameof(Probe));

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

    /// <summary>Sum of visible widths, so the header and rows can size the scroll extent together.</summary>
    public double TotalWidth => Natural.Where(kv => !_hidden.Contains(kv.Key)).Sum(kv => kv.Value);

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
