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
/// A compact per-target history strip: one vertical bar per recent probe, height scaled to RTT,
/// failures drawn as a full-height bar in the failure colour.
/// <para>
/// This is the column that turns a board into a diagnosis. "Loss 4%" tells you something is wrong;
/// a strip showing four evenly spaced drops tells you it is periodic, and a strip showing one
/// solid block tells you it was a single outage. Those are different problems.
/// </para>
/// <para>
/// Drawn with pooled <see cref="Rectangle"/> children rather than a bitmap: at ~40 bars per row
/// the element count is trivial, and reusing the shapes means a redraw allocates nothing.
/// </para>
/// </summary>
public sealed partial class Sparkline : Panel
{
    private const int MaxBars = 44;
    private const double BarGap = 1.0;
    private const double MinBarHeight = 2.0;

    private int _renderedVersion = -1;

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(TargetRow), typeof(Sparkline),
        new PropertyMetadata(null, OnRowChanged));

    public TargetRow? Row
    {
        get => (TargetRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>
    /// Bumped by the view model on each refresh. Binding to it is what drives the redraw, without
    /// the control needing to poll or subscribe to the engine.
    /// </summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(Sparkline),
        new PropertyMetadata(0, OnVersionChanged));

    public int Version
    {
        get => (int)GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    private static void OnRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Sparkline)d).Invalidate();

    private static void OnVersionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Sparkline)d).Invalidate();

    private void Invalidate()
    {
        _renderedVersion = -1;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);

        var width = double.IsInfinity(availableSize.Width) ? MaxBars * 2.0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 18.0 : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var row = Row;
        if (row is null || finalSize.Width <= 0 || finalSize.Height <= 0)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        if (_renderedVersion == row.HistoryVersion && Children.Count > 0)
        {
            ArrangeExisting(finalSize);
            return finalSize;
        }

        _renderedVersion = row.HistoryVersion;

        var history = row.Target.RecentHistory(MaxBars);
        EnsureChildren(history.Length);

        if (history.Length == 0)
        {
            foreach (var child in Children) child.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        // Scale to the largest RTT in view, so a link that normally sits at 2 ms still shows
        // meaningful variation instead of a flat line at the bottom.
        var peak = 1;
        foreach (var r in history)
            if (r.Status.IsOk() && r.HasRtt && r.RttMs > peak)
                peak = r.RttMs;

        var slot = finalSize.Width / history.Length;
        var barWidth = Math.Max(1.0, slot - BarGap);

        var okBrush = Resolve("SparkOkBrush", Colors.SeaGreen);
        var badBrush = Resolve("SparkBadBrush", Colors.IndianRed);
        var idleBrush = Resolve("StatusIdleBrush", Colors.Gray);

        for (var i = 0; i < history.Length; i++)
        {
            var rect = (Rectangle)Children[i];
            ref readonly var result = ref history[i];

            double height;
            if (result.Status.IsOk() && result.HasRtt)
            {
                rect.Fill = okBrush;
                height = Math.Max(MinBarHeight, finalSize.Height * result.RttMs / peak);
            }
            else if (result.Status.IsFailure())
            {
                // Failures are full height so they read as a solid block, which is what makes a
                // sustained outage visually distinct from scattered packet loss.
                rect.Fill = badBrush;
                height = finalSize.Height;
            }
            else
            {
                rect.Fill = idleBrush;
                height = MinBarHeight;
            }

            rect.Width = barWidth;
            rect.Height = height;
            rect.Arrange(new Rect(i * slot, finalSize.Height - height, barWidth, height));
        }

        // Any pooled shapes beyond the current sample count are parked at zero size.
        for (var i = history.Length; i < Children.Count; i++)
            Children[i].Arrange(new Rect(0, 0, 0, 0));

        return finalSize;
    }

    private void ArrangeExisting(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0) return;

        var slot = finalSize.Width / count;
        for (var i = 0; i < count; i++)
        {
            var rect = (Rectangle)Children[i];
            rect.Arrange(new Rect(i * slot, finalSize.Height - rect.Height, rect.Width, rect.Height));
        }
    }

    private void EnsureChildren(int needed)
    {
        while (Children.Count < needed)
            Children.Add(new Rectangle { RadiusX = 0.5, RadiusY = 0.5 });
    }

    private Brush Resolve(string key, Windows.UI.Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }
}
