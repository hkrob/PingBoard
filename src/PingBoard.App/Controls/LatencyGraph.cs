using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PingBoard.App.ViewModels;
using PingBoard.Core;
using Windows.Foundation;

namespace PingBoard.App.Controls;

/// <summary>
/// The rolling window as a latency chart: one bar per probe scaled to RTT, failures in the failure
/// colour, with min/avg/max gridlines and labels.
/// <para>
/// The sparkline answers "is something wrong, and roughly what shape is it" in 44 bars with no
/// axis. This answers the next question — <em>how much</em>, against what baseline. A link that
/// normally sits at 8 ms and is now at 40 ms is not down and loses no packets, so every other
/// column reads healthy; only a plot scaled to its own history shows it.
/// </para>
/// <para>
/// <b>Nothing here mutates layout state from inside layout.</b> That rule is the whole design, and
/// it was learned the hard way: earlier versions added children, called <c>Measure</c>, and set
/// <see cref="TextBlock.Text"/> from <see cref="ArrangeOverride"/>. Each of those invalidates
/// layout from within layout, and WinUI responds by raising <c>LayoutCycleException</c> and
/// <em>abandoning</em> the pass — which freezes the entire window and kills hit-testing while the
/// process still reports as responsive and idle. There is no CPU spike to give it away.
/// </para>
/// <para>
/// So: children are created once, and all text and geometry are computed in
/// <see cref="Recompute"/>, called from the property-changed callback — outside any layout pass.
/// <see cref="ArrangeOverride"/> only assigns brushes and calls <c>Arrange</c>, neither of which
/// invalidates anything. Bars carry no explicit Width/Height either; a <see cref="Rectangle"/>
/// defaults to <c>Stretch.Fill</c> and simply fills the rect it is arranged into.
/// </para>
/// </summary>
public sealed partial class LatencyGraph : Panel
{
    /// <summary>Left gutter for the axis labels.</summary>
    private const double Gutter = 46;

    /// <summary>
    /// Bar cap. The ring holds up to 10,000 samples; one shape each would put thousands of
    /// elements into a row the user merely expanded.
    /// </summary>
    private const int MaxBars = 150;

    private const double PadTop = 8;
    private const double PadBottom = 14;
    private const double LabelHeight = 14;

    private readonly Rectangle _gridMax = new();
    private readonly Rectangle _gridAvg = new();
    private readonly Rectangle _gridMin = new();
    private readonly TextBlock _labelMax = NewLabel();
    private readonly TextBlock _labelAvg = NewLabel();
    private readonly TextBlock _labelMin = NewLabel();
    private readonly TextBlock _caption = new() { FontSize = 10, Opacity = 0.5 };

    private readonly int _fixedChildren;

    private ProbeResult[] _history = [];
    private int _peak = 1;
    private int _min;
    private double _avg;
    private bool _poolBuilt;

    public LatencyGraph()
    {
        Children.Add(_gridMax);
        Children.Add(_gridAvg);
        Children.Add(_gridMin);
        Children.Add(_labelMax);
        Children.Add(_labelAvg);
        Children.Add(_labelMin);
        Children.Add(_caption);

        _fixedChildren = Children.Count;
    }

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(TargetRow), typeof(LatencyGraph), new PropertyMetadata(null, OnChanged));

    public TargetRow? Row
    {
        get => (TargetRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>Bumped by the row on each refresh; binding to it is what drives the redraw.</summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(LatencyGraph), new PropertyMetadata(0, OnChanged));

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (LatencyGraph)d;
        graph.BuildPool();
        graph.Recompute();
        graph.InvalidateArrange();
    }

    /// <summary>Creates the bar pool once, outside any layout pass.</summary>
    private void BuildPool()
    {
        if (_poolBuilt) return;
        _poolBuilt = true;

        for (var i = 0; i < MaxBars; i++) Children.Add(new Rectangle());
    }

    /// <summary>
    /// Pulls the history and computes everything textual. Runs outside layout, so setting
    /// <see cref="TextBlock.Text"/> here is free to invalidate measure the normal way.
    /// </summary>
    private void Recompute()
    {
        _history = Row?.Target.RecentHistory(MaxBars) ?? [];

        var peak = 1;
        var min = int.MaxValue;
        long sum = 0;
        var ok = 0;

        foreach (var r in _history)
        {
            if (!r.Status.IsOk() || !r.HasRtt) continue;
            ok++;
            sum += r.RttMs;
            if (r.RttMs > peak) peak = r.RttMs;
            if (r.RttMs < min) min = r.RttMs;
        }

        _peak = peak;
        _min = ok == 0 ? 0 : min;
        _avg = ok > 0 ? sum / (double)ok : 0;

        _labelMax.Text = Format(_peak);
        _labelAvg.Text = Format(_avg);
        _labelMin.Text = Format(_min);

        _caption.Text = _history.Length == 0
            ? "no samples yet"
            : $"{_history.Length} probes  ·  peak {_peak} ms  ·  avg {_avg:0.#} ms";

        static string Format(double value) => value >= 100 ? $"{value:0} ms" : $"{value:0.#} ms";
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);

        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 110 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var plotWidth = finalSize.Width - Gutter;
        var bars = Math.Min(_history.Length, Math.Max(0, Children.Count - _fixedChildren));

        if (plotWidth <= 8 || finalSize.Height <= PadTop + PadBottom || bars == 0)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        var plotTop = PadTop;
        var plotHeight = Math.Max(1, finalSize.Height - PadTop - PadBottom);

        var okBrush = BoardPalette.Resolve("SparkOkBrush", Colors.SeaGreen);
        var badBrush = BoardPalette.Resolve("SparkBadBrush", Colors.IndianRed);
        var idleBrush = BoardPalette.Resolve("StatusIdleBrush", Colors.Gray);

        // Gridlines first, so the bars paint over them.
        //
        // Labels are suppressed when they would collide. On a healthy link peak, average and
        // minimum sit within a millisecond of each other, and three labels centred on three
        // near-identical gridlines print on top of one another into an unreadable smudge. The
        // gridline is still drawn — it is the label that has nowhere to go.
        var takenY = new List<double>(3);

        ArrangeGrid(_gridMax, _labelMax, _peak, plotTop, plotHeight, plotWidth, idleBrush, takenY);
        ArrangeGrid(_gridMin, _labelMin, _min, plotTop, plotHeight, plotWidth, idleBrush, takenY);
        ArrangeGrid(_gridAvg, _labelAvg, _avg, plotTop, plotHeight, plotWidth, idleBrush, takenY);

        var slot = plotWidth / bars;
        var barWidth = Math.Max(1.0, slot - 0.5);

        for (var i = 0; i < bars; i++)
        {
            var bar = (Rectangle)Children[_fixedChildren + i];
            ref readonly var result = ref _history[i];

            double height;
            if (result.Status.IsOk() && result.HasRtt)
            {
                bar.Fill = okBrush;
                height = Math.Max(1.0, plotHeight * result.RttMs / _peak);
            }
            else if (result.Status.IsFailure())
            {
                // Full height, so an outage reads as a solid block rather than as missing data.
                bar.Fill = badBrush;
                height = plotHeight;
            }
            else
            {
                bar.Fill = idleBrush;
                height = 1.0;
            }

            bar.Arrange(new Rect(Gutter + i * slot, plotTop + plotHeight - height, barWidth, height));
        }

        for (var i = _fixedChildren + bars; i < Children.Count; i++)
            Children[i].Arrange(new Rect(0, 0, 0, 0));

        _caption.Arrange(new Rect(Gutter, finalSize.Height - LabelHeight,
                                  Math.Max(0, finalSize.Width - Gutter), LabelHeight));

        return finalSize;
    }

    /// <param name="takenY">
    /// Label positions already used. Drawn in priority order — peak, then minimum, then average —
    /// so when they crowd together it is the least informative one that gets dropped.
    /// </param>
    private void ArrangeGrid(Rectangle line, TextBlock label, double value,
                             double top, double height, double width, Brush brush,
                             List<double> takenY)
    {
        var y = top + height - height * value / _peak;

        line.Fill = brush;
        line.Opacity = 0.3;
        line.Arrange(new Rect(Gutter, y, width, 1));

        if (takenY.Exists(used => Math.Abs(used - y) < LabelHeight))
        {
            label.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        takenY.Add(y);
        label.Foreground = brush;
        label.Arrange(new Rect(0, y - LabelHeight / 2, Gutter - 6, LabelHeight));
    }

    private static TextBlock NewLabel() =>
        new() { FontSize = 10, TextAlignment = TextAlignment.Right, Opacity = 0.75 };
}
