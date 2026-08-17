using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PingBoard.App.ViewModels;

/// <summary>
/// Measures how wide each column needs to be for the content currently on the board.
/// <para>
/// Text is measured rather than estimated from character counts. The board runs in three different
/// faces — the proportional default, Consolas in the numeric columns, and Cascadia Mono everywhere
/// under the Matrix theme — and it scales with zoom, so a per-character estimate would be wrong by
/// a different amount in each. A <see cref="TextBlock"/> that is never added to the visual tree can
/// be measured directly and gives the real answer.
/// </para>
/// <para>
/// That off-tree detail matters: measuring an element inside the tree from the wrong place is what
/// wedges WinUI layout. This one has no parent, so measuring it invalidates nothing.
/// </para>
/// </summary>
public sealed class ColumnFitter
{
    /// <summary>Cell padding, plus a little slack so text never sits flush against the next column.</summary>
    private const double Padding = 18;

    /// <summary>The status cell also carries the expander chevron and the status glyph.</summary>
    private const double StatusExtras = 46;

    /// <summary>Right-aligned numeric cells carry a 10px right margin in the template.</summary>
    private const double NumericMargin = 10;

    private const double MinWidth = 44;
    private const double MaxWidth = 420;

    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

    private readonly TextBlock _measure = new();

    /// <summary>
    /// Computes a width for every visible column from the header text and every row's content.
    /// </summary>
    public Dictionary<string, double> Measure(
        IEnumerable<TargetRow> rows, double cellFontSize, double headerFontSize, FontFamily? boardFont)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        var materialised = rows as IList<TargetRow> ?? [.. rows];

        // The allowances below are pixel constants describing furniture that itself scales: the
        // expander chevron, the status glyph, cell padding. Leaving them fixed made the status
        // column too narrow at high zoom, so "HTTP ERR" bled into the name beside it.
        var scale = ColumnLayout.Instance.Zoom;

        foreach (var id in ColumnLayout.AllIds)
        {
            if (!ColumnLayout.Instance.IsVisible(id)) continue;

            // The sparkline is a graphic with no text to measure; it keeps its natural width.
            if (id == "Spark") continue;

            var widest = Width(ColumnLayout.HeaderFor(id), headerFontSize, boardFont);

            foreach (var row in materialised)
            {
                var text = CellText(row, id);
                if (text.Length == 0) continue;

                var w = Width(text, cellFontSize, Numeric(id) ? Consolas : boardFont);
                if (w > widest) widest = w;
            }

            widest += Padding * scale;
            if (id == "Status") widest += StatusExtras * scale;
            if (Numeric(id)) widest += NumericMargin * scale;

            widths[id] = Math.Clamp(Math.Ceiling(widest), MinWidth * scale, MaxWidth * scale);
        }

        return widths;
    }

    private double Width(string text, double fontSize, FontFamily? family)
    {
        _measure.Text = text;
        _measure.FontSize = fontSize;
        if (family is not null) _measure.FontFamily = family;

        _measure.Measure(Unbounded);
        return _measure.DesiredSize.Width;
    }

    private static readonly FontFamily Consolas = new("Consolas");

    /// <summary>Columns rendered with NumericCellStyle: monospaced and right-aligned.</summary>
    private static bool Numeric(string id) =>
        id is "Rtt" or "AvgMinMax" or "Loss" or "Fails" or "Uptime" or "Cumulative" or "Ip"
           or "Jitter" or "Avail24h" or "Avail7d" or "Avail30d" or "CertDays";

    /// <summary>The text a row puts in a given column. Must mirror the row template.</summary>
    private static string CellText(TargetRow row, string id) => id switch
    {
        "Status" => row.StatusLabel,
        "Name" => row.Name,
        "Ip" => row.Ip,
        "Hostname" => row.Hostname,
        "LastOk" => row.LastOk,
        "LastNok" => row.LastNok,
        "Cumulative" => row.Cumulative,
        "Rtt" => row.Rtt,
        "AvgMinMax" => row.AvgMinMax,
        "Loss" => row.Loss,
        "Fails" => row.Fails,
        "Uptime" => row.Uptime,
        "Probe" => row.Probe,

        // These were absent, so auto-fit sized them from their header text alone and never saw the
        // values underneath. Harmless for the availability columns, whose headers happen to be
        // wider than "100" — and not a property to rely on, since it holds by luck rather than by
        // anything enforcing it.
        "Jitter" => row.Jitter,
        "Avail24h" => row.Avail24h,
        "Avail7d" => row.Avail7d,
        "Avail30d" => row.Avail30d,
        "CertExpiring" => row.CertExpiring,
        "CertDays" => row.CertDays,
        "SiteName" => row.SiteName,
        "SiteAbbreviation" => row.SiteAbbreviation,
        _ => "",
    };
}
