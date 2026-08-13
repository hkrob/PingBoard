namespace PingBoard.Core;

/// <summary>
/// Fixed-capacity circular buffer of probe results, with rolling statistics computed on demand.
/// <para>
/// The capacity is preallocated once and never grows. This is deliberate and load-bearing: a
/// <c>List&lt;ProbeResult&gt;</c> appended at 1 Hz across 40 targets grows by roughly 100 MB per
/// day, which is the standard way monitoring tools like this leak until they're killed.
/// </para>
/// <para>Safe for concurrent use: probes write from threadpool threads, the UI reads at 4 Hz.</para>
/// </summary>
public sealed class RingBuffer
{
    private readonly ProbeResult[] _items;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new ProbeResult[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    public void Add(in ProbeResult result)
    {
        lock (_gate)
        {
            _items[_next] = result;
            _next = (_next + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Computes all rolling statistics in a single pass under one lock, rather than exposing the
    /// buffer and letting callers walk it repeatedly.
    /// </summary>
    public RollingStats Stats()
    {
        lock (_gate)
        {
            if (_count == 0) return RollingStats.Empty;

            int ok = 0, counted = 0, min = int.MaxValue, max = 0;
            long sum = 0;

            for (var i = 0; i < _count; i++)
            {
                ref readonly var r = ref _items[i];

                // Paused/Suspended samples are not evidence about the target and must not
                // pollute the loss percentage.
                if (r.Status.IsInactive()) continue;
                counted++;

                if (!r.Status.IsOk()) continue;
                ok++;

                if (!r.HasRtt) continue;
                sum += r.RttMs;
                if (r.RttMs < min) min = r.RttMs;
                if (r.RttMs > max) max = r.RttMs;
            }

            if (counted == 0) return RollingStats.Empty;

            var avg = ok > 0 ? (double)sum / ok : 0d;
            return new RollingStats(
                Samples: counted,
                OkSamples: ok,
                LossPercent: 100d * (counted - ok) / counted,
                MinMs: min == int.MaxValue ? 0 : min,
                MaxMs: max,
                AvgMs: avg,
                JitterMs: Jitter());
        }

        // Mean absolute successive difference across consecutive replies — a better feel for
        // link quality than standard deviation, and cheap to compute.
        double Jitter()
        {
            double total = 0;
            var pairs = 0;
            var prev = -1;

            for (var i = 0; i < _count; i++)
            {
                ref readonly var r = ref _items[Index(i)];
                if (!r.Status.IsOk() || !r.HasRtt) { prev = -1; continue; }
                if (prev >= 0) { total += Math.Abs(r.RttMs - prev); pairs++; }
                prev = r.RttMs;
            }

            return pairs == 0 ? 0 : total / pairs;
        }
    }

    /// <summary>
    /// Copies the last <paramref name="n"/> results in chronological order, for the sparkline.
    /// </summary>
    public ProbeResult[] Recent(int n)
    {
        lock (_gate)
        {
            var take = Math.Min(n, _count);
            var result = new ProbeResult[take];
            for (var i = 0; i < take; i++)
                result[i] = _items[Index(_count - take + i)];
            return result;
        }
    }

    /// <summary>Maps a chronological index (0 = oldest retained) onto the backing array.</summary>
    private int Index(int chronological)
    {
        // Once full, the oldest entry sits at _next; before that the buffer is simply in order.
        var start = _count == _items.Length ? _next : 0;
        return (start + chronological) % _items.Length;
    }
}

/// <param name="Samples">Probes considered, excluding paused/suspended.</param>
/// <param name="OkSamples">Probes that got a reply.</param>
/// <param name="LossPercent">
/// Rolling loss over the window. This is the number worth reading day to day — a lifetime
/// cumulative count is dragged down forever by an outage three days ago.
/// </param>
public readonly record struct RollingStats(
    int Samples,
    int OkSamples,
    double LossPercent,
    int MinMs,
    int MaxMs,
    double AvgMs,
    double JitterMs)
{
    public static readonly RollingStats Empty = new(0, 0, 0, 0, 0, 0, 0);

    public bool HasData => Samples > 0;
}
