using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Renders what the board knows as CSV, so it can leave the application.
/// <para>
/// The reason this exists at all: a monitor whose data cannot be got out is a monitor you have to
/// be believed about. The common case is not analysis, it is evidence — an ISP or a supplier
/// disputing that anything was ever wrong, answered with a list of every drop, when it started and
/// how long it lasted. That argument is won by a file you can attach, not by a screenshot of a
/// board showing the present moment.
/// </para>
/// <para>
/// Every writer here returns a string rather than taking a path. Choosing where a file goes is the
/// caller's business, it keeps this class free of I/O failure modes, and it makes the output
/// directly assertable in the self-test.
/// </para>
/// </summary>
public static class Export
{
    /// <summary>
    /// Outages, newest first — the sheet you attach to the complaint.
    /// </summary>
    public static string Outages(IReadOnlyList<Outage> outages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,kind,started,ended,duration_seconds,duration,cause,ongoing");

        foreach (var o in outages)
        {
            sb.Append(Escape(o.TargetName)).Append(',')
              .Append(o.Kind == TransitionKind.Degraded ? "degraded" : "down").Append(',')
              .Append(Stamp(o.Start)).Append(',')
              .Append(o.End is { } end ? Stamp(end) : "").Append(',')
              .Append(Number(o.Duration.TotalSeconds)).Append(',')
              .Append(o.DurationText).Append(',')
              .Append(o.Cause == TargetStatus.Unknown ? "" : o.Cause.Label()).Append(',')
              .Append(o.Ongoing ? "yes" : "no")
              .AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// One row per target: what it is, how it is doing now, and how it has done over the month.
    /// </summary>
    public static string Board(IEnumerable<PingTarget> targets, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("target,address,ip,hostname,probe,port,tab,enabled,status,")
          .Append("last_rtt_ms,avg_ms,min_ms,max_ms,jitter_ms,loss_percent,samples,")
          .Append("ok_count,nok_count,uptime_percent,avail_24h,avail_7d,avail_30d,")
          .Append("last_ok,last_nok,down_for_seconds,")
          .AppendLine("cert_subject,cert_issuer,cert_expires,cert_days_left,cert_trusted");

        foreach (var target in targets)
        {
            var s = target.Snapshot();
            var stats = s.Stats;

            sb.Append(Escape(s.Name)).Append(',')
              .Append(Escape(s.Address)).Append(',')
              .Append(Escape(s.DisplayIp)).Append(',')
              .Append(Escape(s.DisplayHostname)).Append(',')
              .Append(s.Probe.Label()).Append(',')
              .Append(s.Probe.UsesPort() ? s.Port.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(Escape(target.Config.Tab)).Append(',')
              .Append(s.Enabled ? "yes" : "no").Append(',')
              .Append(s.Status.Label()).Append(',')
              .Append(s.LastRttMs >= 0 ? s.LastRttMs.ToString(CultureInfo.InvariantCulture) : "").Append(',');

            if (stats.HasData)
            {
                sb.Append(Number(stats.AvgMs)).Append(',')
                  .Append(stats.MinMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(stats.MaxMs.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(Number(stats.JitterMs)).Append(',')
                  .Append(Number(stats.LossPercent)).Append(',')
                  .Append(stats.Samples.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            else sb.Append(",,,,,,");

            sb.Append(s.OkCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.NokCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(target.Counters.Total > 0 ? Number(target.Counters.UptimePercent) : "").Append(',')
              .Append(Percent(target.Availability.Percent(24, now))).Append(',')
              .Append(Percent(target.Availability.Percent(24 * 7, now))).Append(',')
              .Append(Percent(target.Availability.Percent(AvailabilityLog.MaxHours, now))).Append(',')
              .Append(s.LastOk is { } ok ? Stamp(ok) : "").Append(',')
              .Append(s.LastNok is { } nok ? Stamp(nok) : "").Append(',')
              .Append(s.DownFor is { } down ? Number(down.TotalSeconds) : "").Append(',');

            if (s.Certificate is { HasCertificate: true } cert)
            {
                sb.Append(Escape(cert.ShortSubject)).Append(',')
                  .Append(Escape(cert.Issuer)).Append(',')
                  .Append(Stamp(cert.NotAfter)).Append(',')
                  .Append(cert.DaysRemaining(now).ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(cert.Trusted ? "yes" : "no");
            }
            else sb.Append(",,,,");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Every retained sample, one row per probe — the raw material behind the graphs.
    /// <para>
    /// Bounded by the ring buffers rather than by anything here: this is the retained window,
    /// typically a few hundred samples per target, not the whole history of the world.
    /// </para>
    /// </summary>
    public static string History(IEnumerable<PingTarget> targets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("target,when,status,rtt_ms");

        foreach (var target in targets)
        {
            var name = Escape(target.Config.Name);

            foreach (var sample in target.HistorySnapshot())
            {
                // Restored samples from a previous run carry no wall clock of their own; skip the
                // empty ones rather than exporting rows stamped with the year 1.
                if (sample.When == default) continue;

                sb.Append(name).Append(',')
                  .Append(Stamp(sample.When)).Append(',')
                  .Append(sample.Status.Label()).Append(',')
                  .Append(sample.HasRtt ? sample.RttMs.ToString(CultureInfo.InvariantCulture) : "")
                  .AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sortable, unambiguous, and understood by every spreadsheet — which round-trip "o" format is
    /// not, being rendered as text by most of them.
    /// </summary>
    private static string Stamp(DateTimeOffset when) =>
        when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Number(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Percent(double? value) =>
        value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal)
        || value.Contains('"', StringComparison.Ordinal)
        || value.Contains('\n', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
}
