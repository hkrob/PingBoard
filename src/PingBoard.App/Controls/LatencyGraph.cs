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
/// <para>
/// <b>Two views.</b> <see cref="Range"/> 0 is the live window above. 1–3 draw the hourly record
/// (<see cref="AvailabilityLog.Series"/>) over 24 hours, 7 days and 30 days: one bar per hour, or
/// per four hours at 30 days so the bar count stays near the live view's. Each bar is the average
/// as a solid body, a lighter extension up to the ~p95, and a tick at the maximum; an hour that
/// lost probes gets a red strip along the bottom, and an hour with no reply at all is a full-height
/// failure bar, as in the live view. Hours with no probes are left empty. The scale is the highest
/// p95 in the period rather than the highest maximum — one 2-second spike would otherwise flatten
/// a month of 10 ms bars into the axis — so a maximum above it draws its tick at the top edge.
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

    /// <summary>
    /// The selectable spans, by <see cref="Range"/> index: hours covered and hours per bar.
    /// Index 0, the live window, has no span of its own.
    /// </summary>
    public static readonly (int Hours, int GroupHours, string Label)[] Ranges =
    [
        (0, 0, "Live"),
        (24, 1, "24 hours"),
        (24 * 7, 1, "7 days"),
        (AvailabilityLog.MaxHours, 4, "30 days"),
    ];

    /// <summary>Shapes per bar in the long-range views: p95 extension, body, max tick, loss strip.</summary>
    private const int ShapesPerBucket = 4;

    /// <summary>
    /// The long-range views change once an hour in substance, so a redraw requested by every new
    /// probe re-reads the hourly record at most this often.
    /// </summary>
    private const long SpanRefreshMs = 5_000;

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
    private LatencyBucket[] _buckets = [];
    private int _peak = 1;
    private int _min;
    private double _avg;

    /// <summary>The range the current figures were computed for, and when.</summary>
    private int _computedRange = -1;
    private long _computedAt;

    /// <summary>Whether the long-range period has any reply, and so any gridline worth drawing.</summary>
    private bool _spanHasLatency;

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

    /// <summary>Which of <see cref="Ranges"/> to draw; 0 is the live window.</summary>
    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(
        nameof(Range), typeof(int), typeof(LatencyGraph), new PropertyMetadata(0, OnChanged));

    public int Range
    {
        get => (int)GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    private int CurrentRange => Math.Clamp(Range, 0, Ranges.Length - 1);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var graph = (LatencyGraph)d;
        var range = graph.CurrentRange;

        graph.EnsurePool(range == 0 ? MaxBars : MaxBuckets(range) * ShapesPerBucket);

        // A new probe is not news to a month-long view; re-read the record only when the range
        // changed, the row changed, or the last read is old. Brushes are still re-resolved in
        // ArrangeOverride, so a theme switch redraws regardless.
        var stale = e.Property != VersionProperty
                    || range == 0
                    || range != graph._computedRange
                    || Environment.TickCount64 - graph._computedAt >= SpanRefreshMs;

        if (stale) graph.Recompute();
        graph.InvalidateArrange();
    }

    private static int MaxBuckets(int range) => Ranges[range].Hours / Ranges[range].GroupHours;

    /// <summary>
    /// Grows the shape pool to <paramref name="count"/>, outside any layout pass. Only grows: the
    /// live view needs 150 shapes, and only a row someone has switched to a long range pays for
    /// the 720 that 180 bars of four shapes take.
    /// </summary>
    private void EnsurePool(int count)
    {
        while (Children.Count - _fixedChildren < count) Children.Add(new Rectangle());
    }

    /// <summary>
    /// Pulls the history and computes everything textual. Runs outside layout, so setting
    /// <see cref="TextBlock.Text"/> here is free to invalidate measure the normal way.
    /// </summary>
    private void Recompute()
    {
        var range = CurrentRange;
        _computedRange = range;
        _computedAt = Environment.TickCount64;

        if (range == 0) RecomputeLive();
        else RecomputeSpan(range);

        _labelMax.Text = Format(_peak);
        _labelAvg.Text = Format(_avg);
        _labelMin.Text = Format(_min);
    }

    private static string Format(double value) => value >= 100 ? $"{value:0} ms" : $"{value:0.#} ms";

    private void RecomputeLive()
    {
        _buckets = [];
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

        _caption.Text = _history.Length == 0
            ? "no samples yet"
            : $"{_history.Length} probes  ·  peak {_peak} ms  ·  avg {_avg:0.#} ms";
    }

    /// <summary>
    /// Reads the hourly record for a long range. The gridlines are the whole period's minimum and
    /// average and the scale's top; the caption carries the period's own p95, maximum and loss,
    /// read from one merged bucket so the p95 is a true one rather than a summary of bars.
    /// </summary>
    private void RecomputeSpan(int range)
    {
        _history = [];

        var (hours, group, label) = Ranges[range];
        var log = Row?.Target.Availability;
        var now = DateTimeOffset.Now;

        _buckets = log?.Series(hours, group, now) ?? [];
        var whole = log?.Series(hours, hours, now) is [var only] ? only : default;

        var scale = 1;
        foreach (var b in _buckets)
            if (b.HasLatency && b.P95Ms > scale) scale = b.P95Ms;

        _peak = scale;
        _spanHasLatency = whole.HasLatency;
        _min = whole.HasLatency ? whole.MinMs : 0;
        _avg = whole.HasLatency ? whole.AvgMs : 0;

        var bars = group == 1 ? "hourly bars" : $"{group}-hour bars";

        _caption.Text =
            !whole.HasData ? $"{label}  ·  no data in this period yet"
            : !whole.HasLatency ? $"{label}  ·  no replies  ·  loss 100%"
            : $"{label}  ·  {bars}  ·  avg {whole.AvgMs:0.#} ms  ·  ~p95 {whole.P95Ms} ms  ·  "
              + $"max {whole.MaxMs} ms  ·  loss {whole.LossPercent:0.##}%";
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
        var pool = Math.Max(0, Children.Count - _fixedChildren);
        var spanMode = _buckets.Length > 0;
        var bars = spanMode
            ? Math.Min(_buckets.Length, pool / ShapesPerBucket)
            : Math.Min(_history.Length, pool);

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

        if (spanMode && !_spanHasLatency)
        {
            // A period of nothing but failures has no latency to rule lines at.
            foreach (var hidden in new UIElement[] { _gridMax, _gridMin, _gridAvg, _labelMax, _labelMin, _labelAvg })
                hidden.Arrange(new Rect(0, 0, 0, 0));
        }
        else
        {
            ArrangeGrid(_gridMax, _labelMax, _peak, plotTop, plotHeight, plotWidth, idleBrush, takenY);
            ArrangeGrid(_gridMin, _labelMin, _min, plotTop, plotHeight, plotWidth, idleBrush, takenY);
            ArrangeGrid(_gridAvg, _labelAvg, _avg, plotTop, plotHeight, plotWidth, idleBrush, takenY);
        }

        var slot = plotWidth / bars;
        var barWidth = Math.Max(1.0, slot - 0.5);

        if (spanMode)
        {
            ArrangeSpan(bars, slot, barWidth, plotTop, plotHeight, okBrush, badBrush);
            _caption.Arrange(new Rect(Gutter, finalSize.Height - LabelHeight,
                                      Math.Max(0, finalSize.Width - Gutter), LabelHeight));
            return finalSize;
        }

        for (var i = 0; i < bars; i++)
        {
            var bar = (Rectangle)Children[_fixedChildren + i];
            bar.Opacity = 1;
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

    /// <summary>
    /// Lays out the long-range bars. Four pooled shapes per bucket, in paint order: the lighter
    /// p95 extension, the average body over it, the maximum tick, and the loss strip on top.
    /// </summary>
    private void ArrangeSpan(int bars, double slot, double barWidth, double top, double height,
                             Brush okBrush, Brush badBrush)
    {
        var bottom = top + height;
        double Scaled(double ms) => height * Math.Min(ms, _peak) / _peak;

        for (var i = 0; i < bars; i++)
        {
            var b = _buckets[i];
            var x = Gutter + i * slot;
            var first = _fixedChildren + i * ShapesPerBucket;

            var extension = (Rectangle)Children[first];
            var body = (Rectangle)Children[first + 1];
            var tick = (Rectangle)Children[first + 2];
            var loss = (Rectangle)Children[first + 3];

            if (!b.HasLatency)
            {
                extension.Arrange(new Rect(0, 0, 0, 0));
                body.Arrange(new Rect(0, 0, 0, 0));
                tick.Arrange(new Rect(0, 0, 0, 0));

                // Probes but no reply: a solid failure bar, as the live view draws an outage.
                // No probes at all: nothing — the machine was off or the target paused.
                if (b.HasData)
                {
                    loss.Fill = badBrush;
                    loss.Opacity = 1;
                    loss.Arrange(new Rect(x, top, barWidth, height));
                }
                else loss.Arrange(new Rect(0, 0, 0, 0));

                continue;
            }

            var bodyHeight = Math.Max(1.0, Scaled(b.AvgMs));
            var extensionHeight = Math.Max(bodyHeight, Scaled(b.P95Ms));

            extension.Fill = okBrush;
            extension.Opacity = 0.4;
            extension.Arrange(new Rect(x, bottom - extensionHeight, barWidth, extensionHeight));

            body.Fill = okBrush;
            body.Opacity = 1;
            body.Arrange(new Rect(x, bottom - bodyHeight, barWidth, bodyHeight));

            tick.Fill = okBrush;
            tick.Opacity = 0.8;
            tick.Arrange(new Rect(x, Math.Max(top, bottom - Scaled(b.MaxMs) - 1), barWidth, 1.5));

            if (b.Ok < b.Total)
            {
                loss.Fill = badBrush;
                loss.Opacity = 1;
                loss.Arrange(new Rect(x, bottom - 3, barWidth, 3));
            }
            else loss.Arrange(new Rect(0, 0, 0, 0));
        }

        for (var i = _fixedChildren + bars * ShapesPerBucket; i < Children.Count; i++)
            Children[i].Arrange(new Rect(0, 0, 0, 0));
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
