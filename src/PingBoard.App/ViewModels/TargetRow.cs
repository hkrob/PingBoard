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

    /// <summary>
    /// Mean absolute successive difference across consecutive replies. Already computed on every
    /// stats pass; until now it only appeared in the hover tooltip.
    /// </summary>
    [ObservableProperty] public partial string Jitter { get; private set; } = "—";
    [ObservableProperty] public partial string Fails { get; private set; } = "";
    [ObservableProperty] public partial string Uptime { get; private set; } = "—";

    // Rolling availability. Uptime is lifetime-cumulative, which the README rightly complains
    // about: one outage three days ago drags it down forever and it stops describing anything.
    // These are the actionable version.
    [ObservableProperty] public partial string Avail24h { get; private set; } = "—";
    [ObservableProperty] public partial string Avail7d { get; private set; } = "—";
    [ObservableProperty] public partial string Avail30d { get; private set; } = "—";
    [ObservableProperty] public partial string Probe { get; private set; } = "icmp";

    /// <summary>
    /// Whether this target's certificate is inside the warning window, as a plain yes/no.
    /// <para>
    /// A dash rather than "no" when there is no certificate to speak of. An ICMP target is not a
    /// target whose certificate is fine — it has none — and answering a question that was never
    /// asked is how a column ends up being read as a clean bill of health.
    /// </para>
    /// </summary>
    [ObservableProperty] public partial string CertExpiring { get; private set; } = "—";

    /// <summary>Whole days until expiry, negative once past it.</summary>
    [ObservableProperty] public partial string CertDays { get; private set; } = "—";

    /// <summary>The physical site this target belongs to, or "—" for none. See <see cref="SiteConfig"/>.</summary>
    [ObservableProperty] public partial string SiteName { get; private set; } = "—";

    /// <summary>
    /// The site's short form, looked up from the registry by name — never typed on the target
    /// itself, so every target at a site reads identically. "—" both when there is no site and when
    /// the site has never had an abbreviation set for it.
    /// </summary>
    [ObservableProperty] public partial string SiteAbbreviation { get; private set; } = "—";

    /// <summary>Every tag on this target, comma-joined for display — "—" when it has none.</summary>
    [ObservableProperty] public partial string Tags { get; private set; } = "—";

    [ObservableProperty] public partial string ThemeKey { get; private set; } = "StatusIdleBrush";

    /// <summary>
    /// Re-raises the status brush binding without the key having changed, so a palette swap
    /// repaints the row. Used by the Matrix theme, where the key stays the same but the brush it
    /// names is now a different object.
    /// </summary>
    public void RefreshStatusBrush() => OnPropertyChanged(nameof(ThemeKey));
    [ObservableProperty] public partial double RowOpacity { get; private set; } = 1.0;

    // ------------------------------------------------------------------ failure trace
    //
    // The trace renders as rows nested under its host rather than in a separate pane, because the
    // question it answers — "where did the path break" — only means anything next to the row that
    // went red. A ListView cannot nest rows, so the hops live inside the item template and the
    // container simply grows: virtualization, selection, export and column alignment all carry on
    // working, which a TreeView would have cost us for a purely visual gain.

    /// <summary>Chevron, shown only on rows that actually have a trace to expand.</summary>
    [ObservableProperty] public partial Visibility TraceSectionVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>The nested hop rows.</summary>
    [ObservableProperty] public partial Visibility DetailVisibility { get; private set; } = Visibility.Collapsed;

    [ObservableProperty] public partial string DetailGlyph { get; private set; } = ((char)0xE76C).ToString();
    [ObservableProperty] public partial string TraceSummary { get; private set; } = "";
    [ObservableProperty] public partial string TraceCaption { get; private set; } = "";

    public System.Collections.ObjectModel.ObservableCollection<string> TraceHops { get; } = [];
    private static readonly string ChevronRight = ((char)0xE76C).ToString();
    private static readonly string ChevronDown = ((char)0xE70D).ToString();

    /// <summary>Timestamp of the trace currently rendered, so a 4 Hz refresh does not rebuild it.</summary>
    private DateTimeOffset? _traceStamp;

    /// <summary>
    /// Opens the nested area and shows progress before the trace starts. A trace walks up to
    /// thirty hops at a second each, so without this the row would sit unchanged for several
    /// seconds after the click and read as a menu item that did nothing.
    /// </summary>
    public void BeginTrace()
    {
        TraceSectionVisibility = Visibility.Visible;
        DetailVisibility = Visibility.Visible;
        DetailGlyph = ChevronDown;
        TraceCaption = "Tracing�";
        TraceSummary = "";
        TraceHops.Clear();

        // Clears the guard so the completed result is rendered even if it carries the same
        // timestamp semantics as the one already shown.
        _traceStamp = null;
    }

    /// <summary>Reports that a requested trace could not run at all.</summary>
    public void TraceUnavailable(string reason)
    {
        TraceCaption = "";
        TraceSummary = reason;
        TraceHops.Clear();
    }

    public void ToggleDetail()
    {
        var expanded = DetailVisibility == Visibility.Visible;
        DetailVisibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        DetailGlyph = expanded ? ChevronRight : ChevronDown;
    }

    /// <summary>
    /// Pulls in the latest trace. Rebuilding the hop list on every tick would churn an
    /// ObservableCollection forty times a second for data that changes only on a failure, so the
    /// trace timestamp gates the work.
    /// </summary>
    private void RefreshTrace()
    {
        if (Target.LastTrace is not { } trace)
        {
            TraceSectionVisibility = Visibility.Collapsed;
            return;
        }

        TraceSectionVisibility = Visibility.Visible;

        if (_traceStamp == trace.When) return;
        _traceStamp = trace.When;

        TraceSummary = trace.Summary();
        TraceCaption = "Path at " + trace.When.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                                  + " → " + trace.Destination;

        TraceHops.Clear();
        foreach (var hop in trace.Hops) TraceHops.Add(hop.ToString());
    }

    /// <summary>
    /// Availability, or an em dash when the period has no data at all.
    /// <para>
    /// A target added this morning genuinely has no thirty-day figure. Showing 100% for it would
    /// be a lie of the most flattering kind — the number people quote in reports.
    /// </para>
    /// <para>
    /// Two formatting rules, both about not overstating. A perfect score prints as a bare
    /// <c>100</c>: the decimals carry no information there and cost a third of the column. And a
    /// figure below 100 never rounds <em>up</em> to it — 99.996 shows as 99.99, because a target
    /// that dropped a probe did not have a perfect period, and near the top is exactly where the
    /// decimals mean something.
    /// </para>
    /// </summary>
    private static string FormatAvailability(double? percent) => AvailabilityLog.Format(percent);

    /// <summary>Bumped whenever history changes, so the sparkline knows to redraw.</summary>
    [ObservableProperty] public partial int HistoryVersion { get; private set; }

    /// <summary>Latest snapshot, used for sorting and for the sparkline's data pull.</summary>
    public TargetSnapshot Snapshot { get; private set; }

    public Visibility DownBadge => Snapshot.DownFor is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <param name="certWarnDays">
    /// How close to expiry counts as expiring. Passed in rather than read from a static, because it
    /// is a setting the user can change while the board is running and a copy held here would go
    /// stale the moment they did.
    /// </param>
    /// <param name="timeoutMs">
    /// This target's effective probe timeout — the per-target override if it has one, else the
    /// global default. Same reasoning as <paramref name="certWarnDays"/>: read fresh every refresh
    /// rather than cached, since either setting can change under a running board.
    /// <para>
    /// The default here is only ever seen for the one <c>Refresh()</c> call the constructor makes
    /// before the row has been told anything real — <see cref="MainViewModel.RefreshRows"/> passes
    /// the actual value within the next tick, so a row is never left showing a stale placeholder.
    /// </para>
    /// </param>
    /// <param name="sites">
    /// The site registry, for the abbreviation lookup — a target only ever stores the site's name
    /// (<see cref="TargetConfig.Site"/>), never its abbreviation, so every target at a site reads
    /// identically rather than drifting. Null on the constructor's placeholder call, same as
    /// <paramref name="certWarnDays"/> and <paramref name="timeoutMs"/>.
    /// </param>
    public void Refresh(
        int certWarnDays = 14, int timeoutMs = 2000, IReadOnlyList<SiteConfig>? sites = null)
    {
        var s = Target.Snapshot();
        Snapshot = s;

        var now = DateTimeOffset.Now;

        if (s.Certificate is { HasCertificate: true } cert)
        {
            var days = cert.DaysRemaining(now);
            CertExpiring = cert.IsExpiring(now, certWarnDays) ? "yes" : "no";
            CertDays = days.ToString(CultureInfo.CurrentCulture);
        }
        else
        {
            // Covers both "not an HTTPS target" and "the read failed". Neither is a statement that
            // the certificate is fine, so neither may print "no".
            CertExpiring = "—";
            CertDays = "—";
        }

        var site = Target.Config.Site;
        if (site.Length > 0)
        {
            SiteName = site;

            var match = sites?.FirstOrDefault(
                x => string.Equals(x.Name, site, StringComparison.OrdinalIgnoreCase));
            SiteAbbreviation = match is { Abbreviation.Length: > 0 } ? match.Abbreviation : "—";
        }
        else
        {
            SiteName = "—";
            SiteAbbreviation = "—";
        }

        Tags = Target.Config.Tags.Count > 0 ? string.Join(", ", Target.Config.Tags) : "—";

        StatusLabel = s.Status.Label();
        StatusGlyph = s.Status.Glyph();
        ThemeKey = BrushKeyFor(s.Status);
        RefreshTrace();

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

        // Needs at least two consecutive replies to mean anything; one sample has nothing to
        // differ from, and printing 0.0 would read as a perfectly stable link.
        Jitter = st is { HasData: true, OkSamples: > 1 }
            ? st.JitterMs.ToString("F1", CultureInfo.CurrentCulture)
            : "—";
        Fails = s.ConsecutiveFailures > 0 ? s.ConsecutiveFailures.ToString(CultureInfo.CurrentCulture) : "";

        var counters = Target.Counters;
        // Same treatment as the rolling figures: it is the same kind of number and had the same
        // two problems.
        Uptime = counters.Total > 0 ? FormatAvailability(counters.UptimePercent) : "—";

        var asOf = DateTimeOffset.Now;
        Avail24h = FormatAvailability(Target.Availability.Percent(24, asOf));
        Avail7d = FormatAvailability(Target.Availability.Percent(24 * 7, asOf));
        Avail30d = FormatAvailability(Target.Availability.Percent(24 * 30, asOf));

        // A switch, not a "is it TCP" test. The original binary check silently reported every
        // HTTP and HTTPS target as icmp once those kinds were added — the column claimed the
        // board was doing something other than what it was actually doing, which is worse than
        // showing nothing. The port is omitted where it is the conventional one for the scheme.
        Probe = s.Probe.UsesPort() && s.Port != s.Probe.DefaultPort()
            ? $"{s.Probe.Label()}:{s.Port}"
            : s.Probe.Label();

        // The tooltip carries the raw IPStatus, which distinguishes "nothing answered" from
        // "a router actively told us it could not deliver".
        StatusTooltip = BuildTooltip(s, timeoutMs);

        HistoryVersion++;
        OnPropertyChanged(nameof(DownBadge));
    }

    private static string BuildTooltip(in TargetSnapshot s, int timeoutMs)
    {
        var parts = new List<string> { $"{s.Name} — {s.Status.Label()}" };

        if (s.IcmpStatus != System.Net.NetworkInformation.IPStatus.Unknown
            && s.Status != TargetStatus.Ok)
        {
            parts.Add($"ICMP: {s.IcmpStatus}");
        }

        if (s.DownFor is { } down) parts.Add($"Down for {FormatSpan(down)}");
        if (s.Stats.HasData) parts.Add($"Jitter {s.Stats.JitterMs:F1} ms over {s.Stats.Samples} samples");

        // Answers the question avg/min/max cannot: those columns only ever see successful replies,
        // so a probe that hits the timeout never touches max — max can look perfectly reasonable
        // on a link where the timeout itself is too tight, with only Loss% moving and nothing on
        // screen saying why. Shown only when it has actually happened, on the same "don't state
        // the unremarkable" rule as the jitter and failure-streak lines beside it.
        if (s.Stats.TimeoutSamples > 0)
        {
            parts.Add(
                $"{s.Stats.TimeoutSamples} of {s.Stats.Samples} samples hit the {timeoutMs} ms timeout");
        }

        if (s.ConsecutiveFailures > 0) parts.Add($"{s.ConsecutiveFailures} consecutive failures");
        if (CertificateLine(s.Certificate) is { } cert) parts.Add(cert);

        return string.Join('\n', parts);
    }

    /// <summary>
    /// The certificate line for an HTTPS target's tooltip, or null when there is nothing to say.
    /// <para>
    /// Reads as days rather than as a date because the question is always "have I got time", and
    /// converting <c>14 Nov 2026</c> into that answer is work the reader should not be doing.
    /// </para>
    /// </summary>
    private static string? CertificateLine(CertificateInfo? certificate)
    {
        if (certificate is not { } cert) return null;
        if (!cert.HasCertificate) return $"Certificate: {cert.Error}";

        var days = cert.DaysRemaining(DateTimeOffset.Now);
        var expiry = cert.NotAfter.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);

        var line = days switch
        {
            < 0 => $"Certificate EXPIRED {expiry}",
            0 => $"Certificate expires today ({expiry})",
            1 => $"Certificate expires tomorrow ({expiry})",
            _ => $"Certificate expires in {days} days ({expiry})",
        };

        return cert.Trusted ? line : line + " — not trusted";
    }

    private static string BrushKeyFor(TargetStatus status) => status switch
    {
        TargetStatus.Ok => "StatusOkBrush",
        TargetStatus.Timeout => "StatusTimeoutBrush",
        TargetStatus.Unreachable => "StatusUnreachableBrush",
        TargetStatus.DnsFail => "StatusDnsBrush",
        TargetStatus.Refused => "StatusRefusedBrush",
        TargetStatus.Degraded => "StatusDegradedBrush",
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
