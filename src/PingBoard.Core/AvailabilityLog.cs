using System.Globalization;
using System.Text;

namespace PingBoard.Core;

/// <summary>
/// Availability over hours and days, as opposed to the last few minutes.
/// <para>
/// The ring buffer holds a few hundred probes — ten minutes at a 2 s interval — which is the right
/// window for "is something wrong right now" and useless for "was this link reliable this week".
/// The lifetime Uptime column is the opposite failure: it is dragged down forever by one outage
/// three days ago and stops describing anything. This fills the gap in between.
/// </para>
/// <para>
/// Stored as one bucket per hour in a fixed ring of 720 — thirty days — because the alternative is
/// keeping every sample, and 40 targets at 1 Hz for a month is 100 million probes. Each bucket is
/// two counters, so the whole thing is a few kilobytes per target and bounded by construction,
/// which is the same reasoning that governs the probe history.
/// </para>
/// </summary>
public sealed class AvailabilityLog
{
    /// <summary>Thirty days of hourly buckets.</summary>
    public const int MaxHours = 720;

    private readonly Lock _gate = new();

    /// <summary>
    /// Ring indexed by hour modulo capacity. Each slot records which absolute hour it holds, so a
    /// slot left behind by a previous month reads as empty rather than as stale data.
    /// </summary>
    private readonly long[] _hour = new long[MaxHours];

    private readonly int[] _ok = new int[MaxHours];
    private readonly int[] _total = new int[MaxHours];

    public AvailabilityLog() => Array.Fill(_hour, -1);

    /// <summary>Hours since the Unix epoch, in local time — the same clock the user reads.</summary>
    private static long HourOf(DateTimeOffset when) =>
        (long)Math.Floor(when.LocalDateTime.Subtract(DateTime.UnixEpoch).TotalHours);

    /// <summary>
    /// Records one completed probe. Only genuine evidence counts: paused, suspended and
    /// maintenance samples are not statements about the target and would otherwise sink an
    /// availability figure every time the machine slept.
    /// </summary>
    public void Record(TargetStatus status, DateTimeOffset when)
    {
        if (status.IsInactive()) return;

        var hour = HourOf(when);
        var slot = (int)(((hour % MaxHours) + MaxHours) % MaxHours);

        lock (_gate)
        {
            if (_hour[slot] != hour)
            {
                _hour[slot] = hour;
                _ok[slot] = 0;
                _total[slot] = 0;
            }

            _total[slot]++;
            if (status.IsOk()) _ok[slot]++;
        }
    }

    /// <summary>
    /// Availability across the last <paramref name="hours"/>, or null when nothing was recorded in
    /// that span. Null rather than 100%: a target added an hour ago has no seven-day figure, and
    /// showing a perfect one would be a lie of the most flattering kind.
    /// </summary>
    public double? Percent(int hours, DateTimeOffset now)
    {
        var newest = HourOf(now);
        var oldest = newest - Math.Min(hours, MaxHours) + 1;

        long ok = 0, total = 0;

        lock (_gate)
        {
            for (var hour = oldest; hour <= newest; hour++)
            {
                var slot = (int)(((hour % MaxHours) + MaxHours) % MaxHours);
                if (_hour[slot] != hour) continue;

                ok += _ok[slot];
                total += _total[slot];
            }
        }

        return total == 0 ? null : 100d * ok / total;
    }

    /// <summary>Compact <c>hour:ok:total</c> triplets for the state sidecar.</summary>
    public string Encode()
    {
        var sb = new StringBuilder();

        lock (_gate)
        {
            for (var slot = 0; slot < MaxHours; slot++)
            {
                if (_hour[slot] < 0 || _total[slot] == 0) continue;

                if (sb.Length > 0) sb.Append(',');
                sb.Append(_hour[slot]).Append(':').Append(_ok[slot]).Append(':').Append(_total[slot]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Restores what <see cref="Encode"/> wrote, skipping anything malformed.</summary>
    public static AvailabilityLog Decode(string encoded)
    {
        var log = new AvailabilityLog();
        if (encoded.Length == 0) return log;

        foreach (var triplet in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = triplet.Split(':');
            if (parts.Length != 3) continue;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ok)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)) continue;

            if (hour < 0 || total <= 0 || ok < 0 || ok > total) continue;

            var slot = (int)(((hour % MaxHours) + MaxHours) % MaxHours);
            log._hour[slot] = hour;
            log._ok[slot] = ok;
            log._total[slot] = total;
        }

        return log;
    }

    /// <summary>
    /// Formats an availability figure for display.
    /// <para>
    /// Lives here rather than in the view because both rules are about not overstating, which is a
    /// claim about the data rather than a detail of how it looks. A perfect score prints as a bare
    /// <c>100</c> — the decimals carry nothing there. A figure below 100 never rounds <em>up</em>
    /// to it: 99.996 shows as 99.99, because a target that dropped a probe did not have a perfect
    /// period, and near the top is exactly where the decimals mean something. Null is an em dash,
    /// never 100, because "no data" and "flawless" are not the same statement.
    /// </para>
    /// </summary>
    public static string Format(double? percent)
    {
        if (percent is not { } value) return "—";
        if (value >= 100) return "100";

        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded >= 100) rounded = 99.99;

        return rounded.ToString("F2", CultureInfo.CurrentCulture);
    }

    /// <summary>Discards everything, for "reset statistics".</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Fill(_hour, -1);
            Array.Clear(_ok);
            Array.Clear(_total);
        }
    }
}
