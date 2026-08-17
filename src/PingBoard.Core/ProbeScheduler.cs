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

    /// <summary>
    /// The ceiling <see cref="_concurrency"/> was built with. Tracked separately because
    /// <see cref="SemaphoreSlim.CurrentCount"/> reports permits still <em>available</em>, not the
    /// configured maximum — comparing against it would rebuild the semaphore on every settings
    /// apply that happened to catch a probe in flight.
    /// </summary>
    private int _maxConcurrent;

    private Settings _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Outstanding probes, so shutdown can drain rather than abandon them.</summary>
    private int _outstanding;

    /// <summary>
    /// Concurrent traces. Deliberately tiny: when an uplink drops, every target crosses the down
    /// threshold within seconds of the others, and forty simultaneous traces would put a burst of
    /// ICMP on a network that is by definition already in trouble — while measuring mostly our own
    /// queueing. Traces that cannot get a slot are skipped, not queued; the transition has already
    /// been reported and a diagnostic taken minutes late describes a different network.
    /// </summary>
    private readonly SemaphoreSlim _traceSlots = new(2, 2);

    /// <summary>
    /// Concurrent certificate reads, for the same reason as <see cref="_traceSlots"/>: every HTTPS
    /// target comes due together on the first tick after startup, and a board with fifty of them
    /// would open fifty simultaneous TLS handshakes before it had finished its first round of
    /// probes.
    /// <para>
    /// Unlike a trace these <em>wait</em> for a slot rather than skipping. The due time has already
    /// been pushed out by <see cref="PingTarget.TryBeginCertCheck"/> when we get here, so a skipped
    /// read would not be retried for hours — and they drain in seconds.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _certSlots = new(4, 4);

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
        _maxConcurrent = settings.MaxConcurrent;
        _concurrency = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
    }

    /// <summary>Raised when a target crosses the up/down threshold. Never on every failed probe.</summary>
    public event Action<StateTransition>? Transition;

    /// <summary>
    /// Raised when a failure trace finishes. Always well after the <see cref="Transition"/> that
    /// triggered it — a trace takes seconds — so consumers must treat it as a follow-up rather
    /// than as part of the alert.
    /// </summary>
    public event Action<TraceResult>? TraceCompleted;

    /// <summary>Raised when suspend state changes, so the UI can show why probing has stopped.</summary>
    public event Action<bool, string>? SuspendChanged;

    public bool IsSuspended => _suspended;
    public string SuspendReason => _suspendReason;
    public DnsCache Dns => _dns;

    /// <summary>
    /// Probe slots currently free. Exposed so the self-test can assert that the ceiling tracks
    /// settings, since a silently wrong ceiling is invisible from the outside otherwise.
    /// </summary>
    public int AvailableConcurrency => _concurrency.CurrentCount;

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

        if (_maxConcurrent != settings.MaxConcurrent)
        {
            // The outgoing semaphore is deliberately not disposed. Probes already in flight hold
            // permits on it and release the instance they acquired, which may land after this
            // returns; disposing here would race them. SemaphoreSlim only owns a disposable
            // resource once AvailableWaitHandle has been read, which this class never does, so
            // dropping the reference and letting the GC collect it is both safe and complete.
            _maxConcurrent = settings.MaxConcurrent;
            _concurrency = new SemaphoreSlim(_maxConcurrent, _maxConcurrent);
        }

        // The TTL is live rather than fixed at construction, so editing DnsCacheSeconds takes
        // effect on the next lookup instead of at the next restart.
        _dns.SetTtl(settings.DnsCacheSeconds);

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
                t.ForceStatus(t.IsActive ? TargetStatus.Unknown : TargetStatus.Paused);
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

                    // Paused covers both "this host is paused" and "this host's tab is switched
                    // off". Note what is deliberately absent: nothing here asks which tab is on
                    // screen. A target in a background tab is probed exactly like any other.
                    if (!target.IsActive)
                    {
                        target.ForceStatus(TargetStatus.Paused);
                        continue;
                    }

                    // Independent of the probe schedule: a certificate is read every few hours, so
                    // it must not wait on a probe slot and must not be skipped when one is
                    // unavailable.
                    if (target.TryBeginCertCheck(_settings.CertCheckHours))
                        _ = CheckCertificateAsync(target, _settings);

                    if (now < target.NextDueTick) continue;

                    // Skip rather than stack when the previous probe is still outstanding.
                    if (!target.TryBeginProbe())
                    {
                        target.NextDueTick = now + TickMs;
                        continue;
                    }

                    // Skip rather than queue when we're at the concurrency ceiling. The instance is
                    // captured so the release goes back to the semaphore this probe took a permit
                    // from, even if ApplySettings swaps it out while the probe is in flight.
                    var concurrency = _concurrency;
                    if (!concurrency.Wait(0, CancellationToken.None))
                    {
                        target.EndProbe();
                        target.NextDueTick = now + TickMs;
                        continue;
                    }

                    target.NextDueTick = now + target.IntervalFrom(_settings);
                    Interlocked.Increment(ref _outstanding);
                    _ = ProbeOneAsync(target, concurrency, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProbeOneAsync(PingTarget target, SemaphoreSlim concurrency, CancellationToken ct)
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
            concurrency.Release();
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private void RecordAndNotify(PingTarget target, in ProbeResult result, Settings settings)
    {
        // A result that landed while we were going to sleep must not be counted.
        if (_suspended) return;

        // A maintenance window or a muted tab suppresses the alert, never the probe: the board
        // still shows what happened and the history still records it, so you can see afterwards
        // whether the host came back when it was supposed to.
        //
        // Both leave the "already announced" flag clear, so a host still down when the window
        // closes — or when the tab is unmuted — raises the alert then rather than never.
        var quiet = target.TabMuted || target.InMaintenance(result.When);

        var transition = target.Record(
            result,
            target.FailuresBeforeDownFrom(settings),
            !quiet,
            target.ThresholdsFrom(settings),
            out var soft);

        if (transition is { } t)
        {
            Transition?.Invoke(t);

            // Real outages only, and down only. A recovery would trace a path that is working
            // again, and a target that merely became slow is still answering — tracing it would
            // add ICMP to a link already showing strain, to describe a route that is by definition
            // still carrying traffic.
            if (!t.Up && t.Kind == TransitionKind.Hard && settings.TraceOnFailure)
                _ = TraceFailureAsync(target, settings);
        }

        // Raised second, after the recovery it accompanied, so the two read in the order they
        // happened: the host came back, and it came back slow.
        if (soft is { } s) Transition?.Invoke(s);
    }

    /// <summary>
    /// Traces on request, regardless of the target's state or the <c>TraceOnFailure</c> setting.
    /// <para>
    /// Unlike the automatic trace this <em>waits</em> for a slot rather than skipping: a user who
    /// asked for a trace is watching for an answer, and silently doing nothing because two other
    /// traces happened to be running would look like a broken menu item.
    /// </para>
    /// </summary>
    /// <returns>Null when the target has no resolved address — a name that will not resolve has no
    /// path to trace, and the failure is at the DNS layer rather than along the route.</returns>
    public async Task<TraceResult?> TraceNowAsync(PingTarget target, CancellationToken ct = default)
    {
        if (target.ResolvedAddress is not { } address) return null;

        var settings = _settings;
        await _traceSlots.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var options = new TraceOptions(settings.TraceMaxHops, settings.TraceHopTimeoutMs, StopAfterSilentHops: 5);
            var result = await TraceRoute.RunAsync(target.Config.Name, address, options, ct).ConfigureAwait(false);

            target.SetLastTrace(result);
            TraceCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _traceSlots.Release();
        }
    }

    /// <summary>
    /// Captures where the path breaks, at the moment it breaks.
    /// <para>
    /// Fire-and-forget and fully isolated: this runs outside the probe loop, holds no probe slot,
    /// and swallows everything. A diagnostic that could delay or fail a probe would be worse than
    /// no diagnostic at all.
    /// </para>
    /// </summary>
    private async Task TraceFailureAsync(PingTarget target, Settings settings)
    {
        // Skipped rather than queued when both slots are busy — see _traceSlots.
        if (!_traceSlots.Wait(0, CancellationToken.None)) return;

        try
        {
            var ct = _cts?.Token ?? CancellationToken.None;

            // The last address we actually probed. A target whose name stopped resolving has none,
            // and there is nothing to trace to — the failure is at the DNS layer, not on the path.
            if (target.ResolvedAddress is not { } address) return;

            var options = new TraceOptions(settings.TraceMaxHops, settings.TraceHopTimeoutMs, StopAfterSilentHops: 5);
            var result = await TraceRoute.RunAsync(target.Config.Name, address, options, ct).ConfigureAwait(false);

            target.SetLastTrace(result);
            TraceCompleted?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception)
        {
            // Best-effort by definition.
        }
        finally
        {
            _traceSlots.Release();
        }
    }

    /// <summary>
    /// Reads one target's TLS certificate, well away from the probe path.
    /// <para>
    /// Fire-and-forget and fully isolated, on the same contract as the failure trace: it holds no
    /// probe slot and swallows everything. Certificate expiry is useful information, and not
    /// useful enough to be allowed to delay a single probe.
    /// </para>
    /// </summary>
    private async Task CheckCertificateAsync(PingTarget target, Settings settings)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await _certSlots.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Resolve through the same cache the probes use rather than forcing a lookup: an
            // address good enough to probe is good enough to open one more socket to.
            var address = target.ResolvedAddress
                ?? await _dns.ResolveAsync(target.Config.Address, settings.PreferIPv4, false, ct)
                             .ConfigureAwait(false);

            if (address is null)
            {
                target.SetCertificate(
                    CertificateInfo.Failed("unresolved", DateTimeOffset.Now),
                    settings.CertWarnDays,
                    RetryMinutes);
                return;
            }

            var port = target.Config.Port > 0 ? target.Config.Port : 443;

            var info = await CertificateCheck
                .InspectAsync(target.Config.Address, address, port, settings.TimeoutMs * 2, ct)
                .ConfigureAwait(false);

            // A host that was simply unreachable at startup should not go uninspected until this
            // evening, so a failed read asks to be retried in minutes.
            var transition = target.SetCertificate(
                info, settings.CertWarnDays, info.HasCertificate ? 0 : RetryMinutes);

            // Suppressed by the same two conditions as every other alert, and for the same reason:
            // a maintenance window and a muted tab are statements about being left alone.
            if (transition is { } t && !target.TabMuted && !target.InMaintenance(t.When))
                Transition?.Invoke(t);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Not merely best-effort: TryBeginCertCheck already committed the *next* slot, hours
            // away, the moment it let this attempt through — so an exception that lands here rather
            // than inside CertificateCheck.InspectAsync's own narrower catches used to leave the
            // target stranded for the rest of that window: certificate never read, tooltip silent,
            // next attempt not for hours.
            //
            // The gap is real and confirmed, not hypothetical: InspectAsync's catch list is
            // SocketException / IOException / AuthenticationException, and a target address with an
            // out-of-range port throws ArgumentOutOfRangeException straight through it — verified
            // directly rather than assumed. The realistic flaky-link cases (a connection reset mid-
            // handshake, a server that answers but never speaks TLS) are already safely caught
            // inside InspectAsync and return a Failed result; this is the backstop for what isn't,
            // on the same two-layer pattern IcmpProbe/TcpProbe/HttpProbe already use: narrow catches
            // at the probe, a broad one at the scheduler.
            //
            // Recording the failure here, in the same place that would otherwise have swallowed it,
            // means every path through this method now ends in one of the two explicit
            // SetCertificate calls above or this one — never in silence — so a short retry is
            // guaranteed regardless of what actually went wrong.
            try
            {
                target.SetCertificate(
                    CertificateInfo.Failed(Describe(ex), DateTimeOffset.Now),
                    settings.CertWarnDays,
                    RetryMinutes);
            }
            catch (Exception)
            {
                // SetCertificate only ever takes a lock and assigns fields, but this handler exists
                // to be the last line of defence for the certificate path — it must not itself be
                // the thing that takes anything down.
            }
        }
        finally
        {
            _certSlots.Release();
        }
    }

    /// <summary>How soon to retry a certificate read that failed outright.</summary>
    private const int RetryMinutes = 10;

    /// <summary>
    /// A short reason for the tooltip, for the exception types that reach here rather than one of
    /// <see cref="CertificateCheck"/>'s own narrower catches. Deliberately generic — this path
    /// exists to catch what was not anticipated, so it cannot claim to know exactly what happened.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        ObjectDisposedException => "connection closed unexpectedly",
        _ => "unexpected error reading certificate",
    };

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
        _traceSlots.Dispose();
        _certSlots.Dispose();
    }
}
