using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PingBoard.Core;

namespace PingBoard.App.ViewModels;

/// <summary>
/// One row on the board: a display projection of a <see cref="PingTarget"/>.
/// <para>
/// Refreshed from an immutable <see cref="TargetSnapshot"/> at a fixed 4 Hz rather than on each
/// probe completion. That decoupling is deliberate — at one probe per second across forty targets,
/// marshalling every individual result to the UI thread would mean forty-plus dispatcher hops a
/// second to redraw text that a human cannot read changing that fast.
/// </para>
/// <para>
/// Every setter goes through <c>SetProperty</c>, which suppresses the change notification when the
/// value is unchanged. Most refreshes touch only the RTT and status of a couple of rows, so the
/// steady-state cost of a tick is close to nothing.
/// </para>
/// </summary>
public sealed partial class TargetRow : ObservableObject
{
    public TargetRow(PingTarget target)
    {
        Target = target;
        Refresh();
    }

    /// <summary>The live engine object. The UI reads snapshots from it; it never reads the UI.</summary>
    public PingTarget Target { get; }

    public string Name => Target.Config.Name;

    [ObservableProperty] public partial string StatusLabel { get; private set; } = "—";
    [ObservableProperty] public partial string StatusGlyph { get; private set; } = "";
    [ObservableProperty] public partial string StatusTooltip { get; private set; } = "";
    [ObservableProperty] public partial string Ip { get; private set; } = "";
    [ObservableProperty] public partial string Hostname { get; private set; } = "";
    [ObservableProperty] public partial string LastOk { get; private set; } = "never";
    [ObservableProperty] public partial string LastNok { get; private set; } = "never";
    [ObservableProperty] public partial string Cumulative { get; private set; } = "0 / 0";
    [ObservableProperty] public partial string Rtt { get; private set; } = "—";
    [ObservableProperty] public partial string AvgMinMax { get; private set; } = "—";
    [ObservableProperty] public partial string Loss { get; private set; } = "—";
    [ObservableProperty] public partial string Fails { get; private set; } = "";
    [ObservableProperty] public partial string Uptime { get; private set; } = "—";
    [ObservableProperty] public partial string Probe { get; private set; } = "icmp";
    [ObservableProperty] public partial string ThemeKey { get; private set; } = "StatusIdleBrush";

    /// <summary>
    /// Re-raises the status brush binding without the key having changed, so a palette swap
    /// repaints the row. Used by the Matrix theme, where the key stays the same but the brush it
    /// names is now a different object.
    /// </summary>
    public void RefreshStatusBrush() => OnPropertyChanged(nameof(ThemeKey));
    [ObservableProperty] public partial double RowOpacity { get; private set; } = 1.0;

    /// <summary>Bumped whenever history changes, so the sparkline knows to redraw.</summary>
    [ObservableProperty] public partial int HistoryVersion { get; private set; }

    /// <summary>Latest snapshot, used for sorting and for the sparkline's data pull.</summary>
    public TargetSnapshot Snapshot { get; private set; }

    public Visibility DownBadge => Snapshot.DownFor is not null ? Visibility.Visible : Visibility.Collapsed;

    public void Refresh()
    {
        var s = Target.Snapshot();
        Snapshot = s;

        StatusLabel = s.Status.Label();
        StatusGlyph = s.Status.Glyph();
        ThemeKey = BrushKeyFor(s.Status);

        // Paused and suspended rows are dimmed so the eye skips them, but they keep their glyph
        // and label — dimming is reinforcement, never the only signal.
        RowOpacity = s.Status is TargetStatus.Paused or TargetStatus.Suspended ? 0.45 : 1.0;

        Ip = s.DisplayIp;
        Hostname = s.DisplayHostname;

        LastOk = Format(s.LastOk);
        LastNok = Format(s.LastNok);
        Cumulative = $"{s.OkCount:N0} / {s.NokCount:N0}";

        Rtt = s.LastRttMs >= 0 ? s.LastRttMs.ToString(CultureInfo.CurrentCulture) : "—";

        var st = s.Stats;
        AvgMinMax = st is { HasData: true, OkSamples: > 0 }
            ? $"{st.AvgMs:F0} / {st.MinMs} / {st.MaxMs}"
            : "—";

        Loss = st.HasData ? st.LossPercent.ToString("F1", CultureInfo.CurrentCulture) : "—";
        Fails = s.ConsecutiveFailures > 0 ? s.ConsecutiveFailures.ToString(CultureInfo.CurrentCulture) : "";

        var counters = Target.Counters;
        Uptime = counters.Total > 0 ? counters.UptimePercent.ToString("F2", CultureInfo.CurrentCulture) : "—";

        Probe = s.Probe == ProbeKind.Tcp ? $"tcp:{s.Port}" : "icmp";

        // The tooltip carries the raw IPStatus, which distinguishes "nothing answered" from
        // "a router actively told us it could not deliver".
        StatusTooltip = BuildTooltip(s);

        HistoryVersion++;
        OnPropertyChanged(nameof(DownBadge));
    }

    private static string BuildTooltip(in TargetSnapshot s)
    {
        var parts = new List<string> { $"{s.Name} — {s.Status.Label()}" };

        if (s.IcmpStatus != System.Net.NetworkInformation.IPStatus.Unknown
            && s.Status != TargetStatus.Ok)
        {
            parts.Add($"ICMP: {s.IcmpStatus}");
        }

        if (s.DownFor is { } down) parts.Add($"Down for {FormatSpan(down)}");
        if (s.Stats.HasData) parts.Add($"Jitter {s.Stats.JitterMs:F1} ms over {s.Stats.Samples} samples");
        if (s.ConsecutiveFailures > 0) parts.Add($"{s.ConsecutiveFailures} consecutive failures");

        return string.Join('\n', parts);
    }

    private static string BrushKeyFor(TargetStatus status) => status switch
    {
        TargetStatus.Ok => "StatusOkBrush",
        TargetStatus.Timeout => "StatusTimeoutBrush",
        TargetStatus.Unreachable => "StatusUnreachableBrush",
        TargetStatus.DnsFail => "StatusDnsBrush",
        TargetStatus.Refused => "StatusRefusedBrush",
        _ => "StatusIdleBrush",
    };

    /// <summary>
    /// Recent times read as "how long ago"; older ones as a clock time. A monitoring board is
    /// scanned, not studied, and "12s" answers the question faster than a timestamp does.
    /// </summary>
    private static string Format(DateTimeOffset? when)
    {
        if (when is not { } t) return "never";

        var age = DateTimeOffset.Now - t;
        if (age < TimeSpan.Zero) return t.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        if (age.TotalSeconds < 90) return $"{(int)age.TotalSeconds}s ago";
        if (age.TotalHours < 12) return t.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        return t.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
    }

    public static string FormatSpan(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes:D2}m"
        : span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:D2}s"
            : $"{span.TotalSeconds:F0}s";
}
