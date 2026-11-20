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
/// <para>
/// Each bucket also carries the hour's round-trip times: a count, min, max and sum, and a
/// 32-bin latency histogram from which an approximate 95th percentile is read. The histogram
/// rather than a stored p95 because percentiles do not combine — the p95 of a week is not any
/// function of seven daily p95s — while histograms simply add, so one structure answers an hour,
/// a 4-hour bar on the 30-day graph and a whole month alike. With its counters a slot is about
/// 164 bytes, so roughly 118 KB per target — still fixed by construction.
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

    // Latency, per slot. Counted separately from _total because a failed probe has no round-trip
    // time: dividing the RTT sum by every probe would understate the average by the loss rate.
    private readonly int[] _rttCount = new int[MaxHours];
    private readonly int[] _rttMin = new int[MaxHours];
    private readonly int[] _rttMax = new int[MaxHours];
    private readonly long[] _rttSum = new long[MaxHours];

    /// <summary>Histogram counts, <see cref="LatencyBins"/> per slot, flattened.</summary>
    private readonly int[] _bins = new int[MaxHours * LatencyBins];

    /// <summary>Number of latency histogram bins per hour.</summary>
    public const int LatencyBins = 32;

    /// <summary>
    /// Inclusive upper edge, in milliseconds, of each histogram bin; the last is open-ended.
    /// <para>
    /// Hand-picked round numbers rather than an exact geometric series, so a percentile read off a
    /// bin edge is a number a person would write down. The steps are about 25–35% apart above
    /// 10 ms — the resolution of the "~p95" figure — and finer below, where a LAN lives and 1 ms
    /// is a meaningful difference. The reported value is the edge of the bin the percentile falls
    /// in, clamped to the hour's observed min and max, so it errs high and never past the data.
    /// </para>
    /// </summary>
    private static readonly int[] BinEdges =
    [
        0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50,
        60, 80, 100, 125, 150, 200, 250, 300, 400, 500, 650, 800, 1000, 1500, 2500, int.MaxValue,
    ];

    /// <summary>The histogram bin a round-trip time falls in.</summary>
    private static int BinOf(int rttMs)
    {
        var index = Array.BinarySearch(BinEdges, rttMs);
        return index >= 0 ? index : ~index;
    }

    public AvailabilityLog() => Array.Fill(_hour, -1);

    /// <summary>Hours since the Unix epoch, in local time — the same clock the user reads.</summary>
    private static long HourOf(DateTimeOffset when) =>
        (long)Math.Floor(when.LocalDateTime.Subtract(DateTime.UnixEpoch).TotalHours);

    private static int SlotOf(long hour) => (int)(((hour % MaxHours) + MaxHours) % MaxHours);

    /// <summary>
    /// Empties a slot and claims it for <paramref name="hour"/>. Every per-slot field goes here,
    /// so a slot reused after thirty days can never mix last month's latency with this hour's.
    /// Caller holds the lock.
    /// </summary>
    private void Claim(int slot, long hour)
    {
        _hour[slot] = hour;
        _ok[slot] = 0;
        _total[slot] = 0;
        _rttCount[slot] = 0;
        _rttMin[slot] = 0;
        _rttMax[slot] = 0;
        _rttSum[slot] = 0;
        Array.Clear(_bins, slot * LatencyBins, LatencyBins);
    }

    /// <summary>
    /// Records one completed probe. Only genuine evidence counts: paused, suspended and
    /// maintenance samples are not statements about the target and would otherwise sink an
    /// availability figure every time the machine slept.
    /// <para>
    /// The round-trip time joins the latency figures only for a reply — <see cref="TargetStatus.Ok"/>
    /// or <see cref="TargetStatus.Degraded"/> with a non-negative RTT. Pass -1 when there is none.
    /// </para>
    /// </summary>
    public void Record(TargetStatus status, DateTimeOffset when, int rttMs = -1)
    {
        if (status.IsInactive()) return;

        var hour = HourOf(when);
        var slot = SlotOf(hour);

        lock (_gate)
        {
            if (_hour[slot] != hour) Claim(slot, hour);

            _total[slot]++;
            if (!status.IsOk()) return;

            _ok[slot]++;
            if (rttMs < 0) return;

            if (_rttCount[slot] == 0 || rttMs < _rttMin[slot]) _rttMin[slot] = rttMs;
            if (_rttCount[slot] == 0 || rttMs > _rttMax[slot]) _rttMax[slot] = rttMs;
            _rttCount[slot]++;
            _rttSum[slot] += rttMs;
            _bins[slot * LatencyBins + BinOf(rttMs)]++;
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
                var slot = SlotOf(hour);
                if (_hour[slot] != hour) continue;

                ok += _ok[slot];
                total += _total[slot];
            }
        }

        return total == 0 ? null : 100d * ok / total;
    }

    /// <summary>
    /// The last <paramref name="hours"/> as consecutive buckets of <paramref name="groupHours"/>
    /// hours each, oldest first, the newest ending with the current hour. An immutable copy — the
    /// UI draws from this, never from the live arrays.
    /// <para>
    /// Grouping merges the hours inside the lock: counts and histograms add, min and max take the
    /// extreme, and the percentile is read from the merged histogram, so a 4-hour bar's p95 is the
    /// p95 of those four hours' replies and not an average of four percentiles. A bucket with no
    /// recorded hour is still returned, as an empty one, so a chart can leave a gap where the
    /// machine was off rather than closing it up and misplacing everything after it.
    /// </para>
    /// </summary>
    public LatencyBucket[] Series(int hours, int groupHours, DateTimeOffset now)
    {
        hours = Math.Clamp(hours, 1, MaxHours);
        groupHours = Math.Clamp(groupHours, 1, hours);
        var count = hours / groupHours;

        var newest = HourOf(now);
        var oldest = newest - (long)count * groupHours + 1;

        var result = new LatencyBucket[count];
        Span<int> bins = stackalloc int[LatencyBins];

        lock (_gate)
        {
            for (var g = 0; g < count; g++)
            {
                var first = oldest + (long)g * groupHours;
                int ok = 0, total = 0, rttCount = 0, min = 0, max = 0;
                long sum = 0;
                bins.Clear();

                for (var hour = first; hour < first + groupHours; hour++)
                {
                    var slot = SlotOf(hour);
                    if (_hour[slot] != hour) continue;

                    ok += _ok[slot];
                    total += _total[slot];

                    if (_rttCount[slot] == 0) continue;

                    if (rttCount == 0 || _rttMin[slot] < min) min = _rttMin[slot];
                    if (rttCount == 0 || _rttMax[slot] > max) max = _rttMax[slot];
                    rttCount += _rttCount[slot];
                    sum += _rttSum[slot];

                    var offset = slot * LatencyBins;
                    for (var b = 0; b < LatencyBins; b++) bins[b] += _bins[offset + b];
                }

                result[g] = new LatencyBucket(
                    StartOf(first), groupHours, total, ok, rttCount, min, max,
                    rttCount == 0 ? 0 : sum / (double)rttCount,
                    rttCount == 0 ? 0 : Percentile(bins, rttCount, 0.95, min, max));
            }
        }

        return result;
    }

    /// <summary>Local wall-clock start of an hour number, the inverse of <see cref="HourOf"/>.</summary>
    private static DateTime StartOf(long hour) =>
        DateTime.SpecifyKind(DateTime.UnixEpoch.AddHours(hour), DateTimeKind.Unspecified);

    /// <summary>
    /// The upper edge of the bin holding the <paramref name="fraction"/> quantile, clamped to the
    /// observed range. Nearest-rank: the smallest bin whose cumulative count reaches
    /// ⌈fraction × count⌉.
    /// </summary>
    private static int Percentile(ReadOnlySpan<int> bins, int count, double fraction, int min, int max)
    {
        var rank = (long)Math.Ceiling(fraction * count);
        long seen = 0;

        for (var b = 0; b < bins.Length; b++)
        {
            seen += bins[b];
            if (seen >= rank) return Math.Clamp(BinEdges[b], min, max);
        }

        return max;
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

            var slot = SlotOf(hour);
            if (log._hour[slot] != hour) log.Claim(slot, hour);
            log._ok[slot] = ok;
            log._total[slot] = total;
        }

        return log;
    }

    /// <summary>
    /// Latency for the state sidecar, one entry per hour that has any:
    /// <c>hour:min:max:sum:bin.count/bin.count…</c>, zero bins omitted. The reply count is the
    /// sum of the bin counts, so it is not stored twice.
    /// <para>
    /// A separate key from <see cref="Encode"/> rather than extra fields on those triplets, because
    /// 1.11 and earlier discard any availability entry that is not exactly three numbers — widening
    /// it would make a downgrade silently lose all availability. As its own key, older builds
    /// ignore it and keep everything else.
    /// </para>
    /// </summary>
    public string EncodeLatency()
    {
        var sb = new StringBuilder();

        lock (_gate)
        {
            for (var slot = 0; slot < MaxHours; slot++)
            {
                if (_hour[slot] < 0 || _rttCount[slot] == 0) continue;

                if (sb.Length > 0) sb.Append(',');
                sb.Append(_hour[slot]).Append(':')
                  .Append(_rttMin[slot]).Append(':')
                  .Append(_rttMax[slot]).Append(':')
                  .Append(_rttSum[slot]).Append(':');

                var offset = slot * LatencyBins;
                var firstBin = true;
                for (var b = 0; b < LatencyBins; b++)
                {
                    if (_bins[offset + b] == 0) continue;
                    if (!firstBin) sb.Append('/');
                    sb.Append(b).Append('.').Append(_bins[offset + b]);
                    firstBin = false;
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Both sidecar strings from one hold of the lock.
    /// <para>
    /// <see cref="DecodeLatency"/> rejects an hour claiming more replies than the availability
    /// side has successful probes. Encoding the two separately would let a reply land in between,
    /// leaving the saved latency one ahead of the saved availability — and the next load would
    /// discard that whole hour's latency as corrupt. One lock hold makes the pair consistent.
    /// </para>
    /// </summary>
    public (string Availability, string Latency) EncodeState()
    {
        lock (_gate) return (Encode(), EncodeLatency());
    }

    /// <summary>
    /// Restores what <see cref="EncodeLatency"/> wrote into this log, skipping any entry that is
    /// malformed or internally inconsistent — a corrupt sidecar costs that hour, never the launch.
    /// <para>
    /// Applied after <see cref="Decode"/>, and only to hours the availability side already holds
    /// with at least as many successful probes as the entry has replies: latency without the
    /// availability that produced it, or more replies than successes, can only be damage. An
    /// absent key — any sidecar written before 1.12 — leaves the log with availability and no
    /// latency, which is what such a board actually knows.
    /// </para>
    /// </summary>
    public void DecodeLatency(string encoded)
    {
        if (encoded.Length == 0) return;

        Span<int> bins = stackalloc int[LatencyBins];

        foreach (var entry in encoded.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length != 5) continue;

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)) continue;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)) continue;
            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sum)) continue;
            if (hour < 0 || min < 0 || max < min) continue;

            bins.Clear();
            long count = 0;
            var valid = parts[4].Length > 0;

            foreach (var pair in parts[4].Split('/'))
            {
                var dot = pair.IndexOf('.');
                if (dot <= 0
                    || !int.TryParse(pair.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bin)
                    || !int.TryParse(pair.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    || bin < 0 || bin >= LatencyBins || n <= 0 || bins[bin] != 0)
                {
                    valid = false;
                    break;
                }

                bins[bin] = n;
                count += n;
            }

            if (!valid || count == 0 || count > int.MaxValue) continue;
            if (sum < min * count || sum > max * count) continue;
            if (bins[BinOf(min)] == 0 || bins[BinOf(max)] == 0) continue;

            var slot = SlotOf(hour);

            lock (_gate)
            {
                if (_hour[slot] != hour || _ok[slot] < count) continue;

                _rttCount[slot] = (int)count;
                _rttMin[slot] = min;
                _rttMax[slot] = max;
                _rttSum[slot] = sum;
                bins.CopyTo(_bins.AsSpan(slot * LatencyBins, LatencyBins));
            }
        }
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
            Array.Clear(_rttCount);
            Array.Clear(_rttMin);
            Array.Clear(_rttMax);
            Array.Clear(_rttSum);
            Array.Clear(_bins);
        }
    }
}

/// <summary>
/// One bar's worth of the long-term record: <see cref="Hours"/> consecutive hours starting at
/// <see cref="Start"/> (local wall clock). Latency fields are meaningful only when
/// <see cref="HasLatency"/>; availability only when <see cref="HasData"/>.
/// </summary>
/// <param name="P95Ms">
/// Approximate 95th-percentile round-trip time: the upper edge of the histogram bin it falls in,
/// clamped to <see cref="MinMs"/>…<see cref="MaxMs"/>. Errs high by up to one bin step.
/// </param>
public readonly record struct LatencyBucket(
    DateTime Start, int Hours, int Total, int Ok, int RttCount,
    int MinMs, int MaxMs, double AvgMs, int P95Ms)
{
    public bool HasData => Total > 0;
    public bool HasLatency => RttCount > 0;
    public double LossPercent => Total == 0 ? 0 : 100d * (Total - Ok) / Total;
}
