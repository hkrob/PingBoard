namespace PingBoard.Core;

/// <summary>
/// Drives every target from a single timer.
/// <para>
/// The design constraints that matter here, in order of how badly they bite:
/// </para>
/// <list type="number">
/// <item>
/// <b>One timer, not N.</b> Forty targets on forty timers is forty wakeups competing to fire on
/// the same millisecond. One tick source, with each target given a phase offset across its
/// interval, spreads the load smoothly over both the NIC and the UI.
/// </item>
/// <item>
/// <b>Never queue behind a slow probe.</b> If a probe is still outstanding when the next one is
/// due, the tick is skipped rather than stacked. Without this, a dead host with a timeout longer
/// than its interval accumulates probes forever.
/// </item>
/// <item>
/// <b>A hard concurrency ceiling.</b> When the semaphore is saturated the tick is skipped, not
/// queued — a backlog of probes would report latency that reflects our own queue rather than the
/// network.
/// </item>
/// <item>
/// <b>Monotonic time throughout.</b> Scheduling uses <see cref="Environment.TickCount64"/>, never
/// wall clock, so an NTP correction or DST rollover cannot stall or stampede the loop.
/// </item>
/// </list>
/// </summary>
public sealed class ProbeScheduler : IAsyncDisposable
{
    /// <summary>
    /// Tick granularity. Fine enough that a 250 ms minimum interval is honoured, coarse enough
    /// that an idle board costs nothing measurable.
    /// </summary>
    private const int TickMs = 250;

    private readonly List<PingTarget> _targets = [];
    private readonly Lock _targetsGate = new();
    private readonly DnsCache _dns;

    private SemaphoreSlim _concurrency;
    private Settings _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Outstanding probes, so shutdown can drain rather than abandon them.</summary>
    private int _outstanding;

    /// <summary>
    /// Set while the machine is asleep or the local NIC is down. Probing halts and every target
    /// reads Suspended — the single most important guard against garbage data, because without it
    /// closing a laptop lid manufactures thousands of failures and an alert storm on wake.
    /// </summary>
    private volatile bool _suspended;

    private volatile string _suspendReason = "";

    public ProbeScheduler(Settings settings)
    {
        _settings = settings;
        _dns = new DnsCache(settings.DnsCacheSeconds);
        _concurrency = new SemaphoreSlim(settings.MaxConcurrent, settings.MaxConcurrent);
    }

    /// <summary>Raised when a target crosses the up/down threshold. Never on every failed probe.</summary>
    public event Action<StateTransition>? Transition;

    /// <summary>Raised when suspend state changes, so the UI can show why probing has stopped.</summary>
    public event Action<bool, string>? SuspendChanged;

    public bool IsSuspended => _suspended;
    public string SuspendReason => _suspendReason;
    public DnsCache Dns => _dns;

    public IReadOnlyList<PingTarget> Targets
    {
        get { lock (_targetsGate) return _targets.ToArray(); }
    }

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);

        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }

        // Give outstanding probes a moment to finish so their sockets close cleanly.
        var deadline = Environment.TickCount64 + 3000;
        while (Volatile.Read(ref _outstanding) > 0 && Environment.TickCount64 < deadline)
            await Task.Delay(50).ConfigureAwait(false);

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public void AddTarget(PingTarget target)
    {
        lock (_targetsGate)
        {
            _targets.Add(target);
            RestaggerLocked();
        }
    }

    public void RemoveTarget(PingTarget target)
    {
        lock (_targetsGate)
        {
            _targets.Remove(target);
            RestaggerLocked();
        }
        target.Dispose();
    }

    public void SetTargets(IEnumerable<PingTarget> targets)
    {
        PingTarget[] old;
        lock (_targetsGate)
        {
            old = [.. _targets];
            _targets.Clear();
            _targets.AddRange(targets);
            RestaggerLocked();
        }
        foreach (var t in old) t.Dispose();
    }

    public void ApplySettings(Settings settings)
    {
        _settings = settings;

        if (_concurrency.CurrentCount != settings.MaxConcurrent)
        {
            var old = _concurrency;
            _concurrency = new SemaphoreSlim(settings.MaxConcurrent, settings.MaxConcurrent);
            old.Dispose();
        }

        lock (_targetsGate) RestaggerLocked();
    }

    /// <summary>
    /// Spreads first-probe times evenly across the interval. Called whenever the target set
    /// changes so the spread stays even rather than degrading as targets are added.
    /// </summary>
    private void RestaggerLocked()
    {
        var now = Environment.TickCount64;
        var count = Math.Max(1, _targets.Count);

        for (var i = 0; i < _targets.Count; i++)
        {
            var interval = _targets[i].IntervalFrom(_settings);
            _targets[i].NextDueTick = now + (long)i * interval / count;
        }
    }

    /// <summary>
    /// Halts or resumes probing wholesale. Called on sleep/resume and on NIC up/down.
    /// While suspended, counters are frozen and no notifications fire.
    /// </summary>
    public void SetSuspended(bool suspended, string reason)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        _suspendReason = suspended ? reason : "";

        if (suspended)
        {
            foreach (var t in Targets)
                t.ForceStatus(TargetStatus.Suspended);
        }
        else
        {
            // Everything is due immediately on resume, but staggered so the whole board doesn't
            // fire on one tick after a wake.
            lock (_targetsGate) RestaggerLocked();

            // A disabled target goes back to Paused, not Unknown — otherwise it would sit reading
            // "Suspended" until the next tick corrected it.
            foreach (var t in Targets)
                t.ForceStatus(t.Config.Enabled ? TargetStatus.Unknown : TargetStatus.Paused);
        }

        SuspendChanged?.Invoke(suspended, _suspendReason);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_suspended) continue;

                var now = Environment.TickCount64;
                foreach (var target in Targets)
                {
                    if (ct.IsCancellationRequested) break;

                    if (!target.Config.Enabled)
                    {
                        target.ForceStatus(TargetStatus.Paused);
                        continue;
                    }

                    if (now < target.NextDueTick) continue;

                    // Skip rather than stack when the previous probe is still outstanding.
                    if (!target.TryBeginProbe())
                    {
                        target.NextDueTick = now + TickMs;
                        continue;
                    }

                    // Skip rather than queue when we're at the concurrency ceiling.
                    if (!_concurrency.Wait(0, CancellationToken.None))
                    {
                        target.EndProbe();
                        target.NextDueTick = now + TickMs;
                        continue;
                    }

                    target.NextDueTick = now + target.IntervalFrom(_settings);
                    Interlocked.Increment(ref _outstanding);
                    _ = ProbeOneAsync(target, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProbeOneAsync(PingTarget target, CancellationToken ct)
    {
        var settings = _settings;

        try
        {
            var address = target.ResolvedAddress;

            if (target.NeedsReresolve(settings.FailuresBeforeReresolve))
            {
                address = await _dns.ResolveAsync(
                    target.Config.Address,
                    settings.PreferIPv4,
                    forceRefresh: address is not null,
                    ct).ConfigureAwait(false);

                target.SetResolved(address);
            }

            if (address is null)
            {
                // A name that stopped resolving is not a ping timeout, and conflating them sends
                // you looking at the wrong layer.
                var dnsFail = ProbeResult.Fail(
                    TargetStatus.DnsFail, Environment.TickCount64, DateTimeOffset.Now);
                RecordAndNotify(target, dnsFail, settings);
                return;
            }

            var result = await target.Probe
                .ProbeAsync(address, target.OptionsFrom(settings), ct)
                .ConfigureAwait(false);

            RecordAndNotify(target, result, settings);

            // Populate the Hostname column for targets entered by IP. Fire-and-forget by design:
            // a reverse lookup must never delay or fail a probe.
            if (!target.HasReverseName && !ct.IsCancellationRequested)
                _ = ReverseLookupAsync(target, address, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception)
        {
            // A probe must never take the loop down with it.
            var failure = ProbeResult.Fail(
                TargetStatus.Timeout, Environment.TickCount64, DateTimeOffset.Now);
            RecordAndNotify(target, failure, settings);
        }
        finally
        {
            target.EndProbe();
            _concurrency.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private void RecordAndNotify(PingTarget target, in ProbeResult result, Settings settings)
    {
        // A result that landed while we were going to sleep must not be counted.
        if (_suspended) return;

        var transition = target.Record(result, target.FailuresBeforeDownFrom(settings));
        if (transition is { } t) Transition?.Invoke(t);
    }

    private async Task ReverseLookupAsync(PingTarget target, System.Net.IPAddress address, CancellationToken ct)
    {
        try
        {
            var name = await _dns.ReverseAsync(address, ct).ConfigureAwait(false);
            target.SetReverseName(name ?? "");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception) { /* best-effort only */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var t in Targets) t.Dispose();
        _concurrency.Dispose();
    }
}
