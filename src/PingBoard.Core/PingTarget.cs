using System.Net;

namespace PingBoard.Core;

/// <summary>Per-target configuration as it appears in a <c>[Target:name]</c> section.</summary>
public sealed class TargetConfig
{
    /// <summary>Section name. Unique, and used as the stable key for persisted counters.</summary>
    public string Name { get; set; } = "";

    /// <summary>Exactly what the user typed: a literal IP or a hostname.</summary>
    public string Address { get; set; } = "";

    public ProbeKind Probe { get; set; } = ProbeKind.Icmp;
    public int Port { get; set; } = 443;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tab this target belongs to. Empty means the default group, and is written out as nothing at
    /// all, so a config that never used tabs stays exactly as it was.
    /// </summary>
    public string Tab { get; set; } = "";

    /// <summary>
    /// Physical site this target belongs to, or empty for none — see <see cref="SiteConfig"/> for
    /// why this is a separate axis from <see cref="Tab"/> rather than a kind of it. Unlike Tab,
    /// empty here means "not tied to a site" rather than falling back to a default group; most
    /// targets have no site worth naming, and there is nothing for them to fall back to.
    /// </summary>
    public string Site { get; set; } = "";

    /// <summary>
    /// Free-form labels, any number of them — unlike <see cref="Tab"/> and <see cref="Site"/>,
    /// which are each exactly one value per target by construction. A tab can filter itself down to
    /// targets carrying a given tag (<see cref="TabConfig.SelectedTags"/>); nothing else reads this
    /// list, so a tag with no tab watching for it costs nothing and needs no cleanup.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Request path for HTTP probes. Ignored by ICMP and TCP.</summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Scheduled quiet hours, e.g. <c>Sat 22:00-02:00</c>. Probing continues; alerts do not fire.
    /// See <see cref="MaintenanceSchedule"/>.
    /// </summary>
    public string Maintenance { get; set; } = "";

    /// <summary>
    /// Status code an HTTP probe must see, or null to accept any 2xx/3xx. Worth setting when a
    /// URL legitimately redirects and you want to know if it ever stops.
    /// </summary>
    public int? ExpectStatus { get; set; }

    // Null means "inherit from [Settings]".
    public int? IntervalMs { get; set; }
    public int? TimeoutMs { get; set; }
    public int? PayloadBytes { get; set; }
    public int? Ttl { get; set; }

    /// <summary>
    /// Average round-trip time above which this target is shown as degraded, or null to inherit.
    /// <para>
    /// This is the override that matters most of the whole set, because the global default is off
    /// and has to be: "too slow" is a statement about one link. Sixty milliseconds is a broken LAN
    /// and a very good path to Frankfurt.
    /// </para>
    /// </summary>
    public int? DegradedLatencyMs { get; set; }

    /// <summary>Loss percentage above which this target is shown as degraded, or null to inherit.</summary>
    public double? DegradedLossPercent { get; set; }

    /// <summary>
    /// Consecutive failures before this target is declared down. Overrides the global setting.
    /// <para>
    /// Worth having per target because the number only means anything relative to that target's
    /// interval, which is itself per target: three failures at a 5 s interval is fifteen seconds
    /// of silence, but at 1 s it is three. A flaky WAN link and a LAN switch need different
    /// answers, and a single global value cannot serve both.
    /// </para>
    /// </summary>
    public int? FailuresBeforeDown { get; set; }

    public TargetConfig Clone() => (TargetConfig)MemberwiseClone();
}

/// <summary>Counters that survive a restart, persisted to the sidecar state file.</summary>
public sealed class TargetCounters
{
    public long OkCount { get; set; }
    public long NokCount { get; set; }
    public DateTimeOffset? LastOk { get; set; }
    public DateTimeOffset? LastNok { get; set; }

    public long Total => OkCount + NokCount;

    /// <summary>Lifetime availability. Informative, but the rolling window is the actionable one.</summary>
    public double UptimePercent => Total == 0 ? 0 : 100d * OkCount / Total;
}

/// <summary>
/// An immutable snapshot of a target's state, produced under lock and handed to the UI thread.
/// The UI never touches live state directly — it renders snapshots taken at 4 Hz.
/// </summary>
public readonly record struct TargetSnapshot(
    string Name,
    string Address,
    string DisplayIp,
    string DisplayHostname,
    ProbeKind Probe,
    int Port,
    bool Enabled,
    TargetStatus Status,
    System.Net.NetworkInformation.IPStatus IcmpStatus,
    int LastRttMs,
    DateTimeOffset? LastOk,
    DateTimeOffset? LastNok,
    long OkCount,
    long NokCount,
    int ConsecutiveFailures,
    RollingStats Stats,
    TimeSpan? DownFor,
    CertificateInfo? Certificate = null);

/// <summary>
/// One monitored host: its configuration, its live probe state, and its history ring.
/// <para>
/// All mutable state is guarded by <see cref="_gate"/>. Probes mutate it from threadpool threads;
/// the UI reads it via <see cref="Snapshot"/>.
/// </para>
/// </summary>
public sealed class PingTarget : IDisposable
{
    private readonly Lock _gate = new();
    private readonly RingBuffer _history;
    private IProbe _probe;

    // Set to 1 while a probe is outstanding. Guards against the failure mode where a dead host
    // with a 4s timeout on a 2s interval accumulates probes until handles run out.
    private int _inFlight;

    private TargetStatus _status = TargetStatus.Unknown;
    private System.Net.NetworkInformation.IPStatus _icmpStatus = System.Net.NetworkInformation.IPStatus.Unknown;
    private int _lastRttMs = ProbeResult.NoRtt;
    private int _consecutiveFailures;
    private long? _downSinceTick;

    /// <summary>
    /// True once the down transition has been raised for the current outage. This is what makes
    /// "fire exactly once" hold, rather than testing the streak for exact equality with the
    /// threshold: the threshold can be lowered mid-outage from the settings dialog, and a streak
    /// that has already passed the new value would then never equal it — no down notification
    /// would fire, yet the recovery one still would.
    /// </summary>
    private bool _downFired;

    /// <summary>
    /// Mirrors <see cref="_downFired"/> for the soft state: set once on entering degraded so the
    /// notification fires on the crossing rather than on every slow probe thereafter.
    /// </summary>
    private bool _degradedFired;

    private long? _degradedSinceTick;

    /// <summary>
    /// Probes recorded since this process started, as opposed to those restored from the previous
    /// run's history file.
    /// <para>
    /// The degraded assessment reads a window of recent samples, and after a restart that window is
    /// full of samples from a run that may have ended a week ago. Judging on those means a target
    /// can be declared degraded on startup because of how it behaved last Tuesday — and, worse,
    /// alert about it. The displayed statistics are expected to span restarts and still do; only
    /// the live judgement waits for evidence from this run.
    /// </para>
    /// </summary>
    private int _samplesThisRun;
    private IPAddress? _resolved;
    private string? _reverseName;

    /// <summary>Last TLS certificate seen for an HTTPS target.</summary>
    private CertificateInfo? _certificate;

    /// <summary>
    /// Monotonic tick at which the certificate may be read again; null means "now". Stored as the
    /// next due time rather than the last check so a failed read can ask to be retried sooner
    /// without the scheduler having to remember anything about it.
    /// </summary>
    private long? _certNextTick;

    /// <summary>Set once the expiry warning has been raised, so it fires on the crossing only.</summary>
    private bool _certWarned;

    /// <summary>Most recent path trace, captured when this target was last declared down.</summary>
    private TraceResult? _lastTrace;

    private MaintenanceSchedule _maintenance = MaintenanceSchedule.None;

    /// <summary>Availability over hours and days, as opposed to the last few minutes.</summary>
    public AvailabilityLog Availability { get; private set; } = new();

    /// <summary>True when the target is inside a scheduled maintenance window right now.</summary>
    public bool InMaintenance(DateTimeOffset now) => _maintenance.Contains(now);

    /// <summary>Replaces the availability history, for restoring a previous run's figures.</summary>
    public void RestoreAvailability(AvailabilityLog log) => Availability = log;

    public PingTarget(TargetConfig config, Settings settings, TargetCounters? counters = null)
    {
        Config = config;
        Counters = counters ?? new TargetCounters();
        _maintenance = MaintenanceSchedule.Parse(config.Maintenance);
        _history = new RingBuffer(settings.RollingWindow);
        _probe = CreateProbe(config.Probe);
        if (!config.Enabled) _status = TargetStatus.Paused;
    }

    public TargetConfig Config { get; private set; }
    public TargetCounters Counters { get; }

    /// <summary>Phase offset within the interval, so targets don't all fire on the same tick.</summary>
    public long NextDueTick { get; set; }

    /// <summary>
    /// False when this target's tab has been disabled. Kept separate from
    /// <see cref="TargetConfig.Enabled"/> rather than folded into it: they are different
    /// statements — "I paused this host" versus "I switched off this whole group" — and merging
    /// them would mean re-enabling a tab silently un-pausing hosts the user had paused by hand.
    /// </summary>
    public bool TabEnabled { get; set; } = true;

    /// <summary>
    /// True when this target's tab is muted. Probing and recording continue exactly as normal;
    /// only the notification is withheld — the same contract as a maintenance window.
    /// </summary>
    public bool TabMuted { get; set; }

    /// <summary>Probed only when both the target and its tab are enabled.</summary>
    public bool IsActive => Config.Enabled && TabEnabled;

    public bool IsInFlight => Volatile.Read(ref _inFlight) == 1;

    private static IProbe CreateProbe(ProbeKind kind) => kind switch
    {
        ProbeKind.Tcp => new TcpProbe(),
        ProbeKind.Http => new HttpProbe(useTls: false),
        ProbeKind.Https => new HttpProbe(useTls: true),
        _ => new IcmpProbe(),
    };

    /// <summary>Attempts to claim the in-flight slot. False means a probe is already outstanding.</summary>
    public bool TryBeginProbe() => Interlocked.CompareExchange(ref _inFlight, 1, 0) == 0;

    public void EndProbe() => Volatile.Write(ref _inFlight, 0);

    public IProbe Probe => _probe;

    /// <summary>
    /// True when the name should be re-resolved before the next probe: we have no address yet, or
    /// the target has failed enough times that a stale address is a plausible cause.
    /// </summary>
    public bool NeedsReresolve(int failuresBeforeReresolve)
    {
        lock (_gate)
            return _resolved is null || _consecutiveFailures >= failuresBeforeReresolve;
    }

    public IPAddress? ResolvedAddress
    {
        get { lock (_gate) return _resolved; }
    }

    public void SetResolved(IPAddress? address)
    {
        lock (_gate) _resolved = address;
    }

    public void SetReverseName(string? name)
    {
        lock (_gate) _reverseName = name;
    }

    public bool HasReverseName
    {
        get { lock (_gate) return _reverseName is not null; }
    }

    public ProbeOptions OptionsFrom(Settings s) => new(
        TimeoutMs: Config.TimeoutMs ?? s.TimeoutMs,
        PayloadBytes: Config.PayloadBytes ?? s.PayloadBytes,
        Ttl: Config.Ttl ?? s.Ttl,
        Port: Config.Port,
        Host: Config.Address,
        Path: Config.Path,
        ExpectStatus: Config.ExpectStatus ?? 0);

    public int IntervalFrom(Settings s) => Config.IntervalMs ?? s.IntervalMs;

    /// <summary>
    /// Effective per-probe timeout: the per-target override, else the global setting. Same value
    /// <see cref="OptionsFrom"/> hands the probe — exposed separately so the UI can state it
    /// without reaching into <see cref="ProbeOptions"/> for one field.
    /// </summary>
    public int TimeoutMsFrom(Settings s) => Config.TimeoutMs ?? s.TimeoutMs;

    /// <summary>Effective degraded thresholds: per-target overrides, else the global defaults.</summary>
    public DegradeThresholds ThresholdsFrom(Settings s) => new(
        Config.DegradedLatencyMs ?? s.DegradedLatencyMs,
        Config.DegradedLossPercent ?? s.DegradedLossPercent,
        s.DegradedSamples);

    /// <summary>
    /// The certificate last read from this target, or null if it is not an HTTPS target or has not
    /// been read yet.
    /// </summary>
    public CertificateInfo? Certificate
    {
        get { lock (_gate) return _certificate; }
    }

    /// <summary>
    /// Claims the right to read this target's certificate now, deferring the next read by
    /// <paramref name="everyHours"/>.
    /// <para>
    /// The slot is claimed up front rather than on completion, which is what stops the scheduler —
    /// which asks four times a second — from launching a second handshake while the first is still
    /// running.
    /// </para>
    /// </summary>
    public bool TryBeginCertCheck(int everyHours)
    {
        if (Config.Probe != ProbeKind.Https) return false;

        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_certNextTick is { } due && now < due) return false;
            _certNextTick = now + (long)everyHours * 3_600_000;
            return true;
        }
    }

    /// <summary>
    /// Stores a certificate reading, and reports the first crossing into "expiring soon".
    /// </summary>
    /// <param name="retryMinutes">
    /// When positive, overrides the next due time — used after a failed read so a host that
    /// happened to be down at startup is retried in minutes rather than in hours.
    /// </param>
    /// <returns>A transition the first time expiry is within <paramref name="warnDays"/>, else null.</returns>
    public StateTransition? SetCertificate(CertificateInfo info, int warnDays, int retryMinutes = 0)
    {
        lock (_gate)
        {
            _certificate = info;

            if (retryMinutes > 0)
                _certNextTick = Environment.TickCount64 + (long)retryMinutes * 60_000;

            if (!info.HasCertificate) return null;

            if (!info.IsExpiring(info.CheckedAt, warnDays))
            {
                // Renewed. Re-arm so the next approach to expiry is announced too.
                _certWarned = false;
                return null;
            }

            if (_certWarned) return null;
            _certWarned = true;

            // DownFor carries time remaining rather than time elapsed for this kind — negative
            // once the certificate has already expired.
            return new StateTransition(
                Config.Name, Up: false, info.CheckedAt, info.NotAfter - info.CheckedAt,
                TargetStatus.Degraded, warnDays, TransitionKind.Certificate);
        }
    }

    /// <summary>Effective alert threshold: the per-target override, else the global setting.</summary>
    public int FailuresBeforeDownFrom(Settings s) => Config.FailuresBeforeDown ?? s.FailuresBeforeDown;

    /// <summary>
    /// Records a completed probe: updates status, counters, streaks and history.
    /// </summary>
    /// <returns>
    /// A transition when the target crossed the up/down threshold, otherwise null. The caller
    /// turns that into a notification and a log line — which is why notifications fire on state
    /// change rather than on every failed probe.
    /// </returns>
    /// <param name="raiseTransitions">
    /// False during a maintenance window. The sample, the counters and the failure streak are all
    /// updated exactly as usual — only the notification is withheld, and crucially the
    /// "already announced" flag is left alone.
    /// <para>
    /// That last part is the whole subtlety. If a window simply swallowed the transition, a host
    /// that went down during maintenance and never came back would be marked as announced and you
    /// would never be told. Leaving the flag clear means the first failing probe after the window
    /// closes raises the alert — quiet while you expected the outage, loud the moment you did not.
    /// </para>
    /// </param>
    /// <param name="thresholds">
    /// Latency and loss limits for the degraded state. Defaults to <c>default</c>, which is off —
    /// so every existing caller keeps the exact behaviour it had before the state existed.
    /// </param>
    public StateTransition? Record(
        in ProbeResult result,
        int failuresBeforeDown,
        bool raiseTransitions = true,
        DegradeThresholds thresholds = default)
        => Record(result, failuresBeforeDown, raiseTransitions, thresholds, out _);

    /// <summary>
    /// As above, but also reports a soft transition that occurred on the same probe.
    /// </summary>
    /// <param name="soft">
    /// The degraded transition raised alongside a real recovery, or null.
    /// <para>
    /// One probe can genuinely be two pieces of news: a host that comes back from an outage and is
    /// slow the moment it returns has both recovered and become degraded. There is one return
    /// value and the hard transition has to win it, so this used to be dropped on the floor — the
    /// log then said the host simply came back, and because the latch had already been set it
    /// would never mention the degradation at all. An overnight soak made that visible: three
    /// outages on a permanently degraded host produced exactly one degraded event between them.
    /// </para>
    /// </param>
    public StateTransition? Record(
        in ProbeResult result,
        int failuresBeforeDown,
        bool raiseTransitions,
        DegradeThresholds thresholds,
        out StateTransition? soft)
    {
        soft = null;

        lock (_gate)
        {
            Availability.Record(result.Status, result.When);
            _history.Add(result);
            if (_samplesThisRun < int.MaxValue) _samplesThisRun++;
            _status = result.Status;
            _icmpStatus = result.IcmpStatus;
            _lastRttMs = result.RttMs;
            if (result.Address is not null) _resolved = result.Address;

            if (result.Status.IsOk())
            {
                Counters.OkCount++;
                Counters.LastOk = result.When;

                var wasDown = _downFired;
                _consecutiveFailures = 0;
                _downFired = false;

                // Assessed before the recovery return, so a host that comes back slow reads amber
                // from that first reply rather than green until the probe after it.
                var degraded = EvaluateDegradedLocked(result, thresholds, raiseTransitions);

                if (!wasDown || !raiseTransitions) { _downSinceTick = null; return degraded; }

                var downFor = _downSinceTick is { } since
                    ? TimeSpan.FromMilliseconds(result.TickMs - since)
                    : TimeSpan.Zero;
                _downSinceTick = null;

                // The hard transition wins the return value — "it came back" is the headline — but
                // the degraded one is handed back rather than dropped, so the log records both
                // things that actually happened.
                soft = degraded;

                return new StateTransition(
                    Config.Name, Up: true, result.When, downFor, result.Status, failuresBeforeDown);
            }

            if (result.Status.IsFailure())
            {
                Counters.NokCount++;
                Counters.LastNok = result.When;
                _consecutiveFailures++;
                _downSinceTick ??= result.TickMs;

                // Fire exactly once per outage, on the first probe at or past the threshold.
                // Suppressed during maintenance without setting the flag, so the alert is still
                // waiting to be raised when the window closes.
                if (raiseTransitions && !_downFired && _consecutiveFailures >= failuresBeforeDown)
                {
                    _downFired = true;

                    // A target that has gone hard down is no longer merely degraded, and the next
                    // recovery should be free to announce degradation afresh.
                    _degradedFired = false;
                    _degradedSinceTick = null;

                    return new StateTransition(
                        Config.Name, Up: false, result.When, TimeSpan.Zero, result.Status, failuresBeforeDown);
                }

                // Note what does not happen on an ordinary failed probe: the degraded latch is
                // left exactly as it was. Clearing it would mean an intermittently dropping link
                // re-announced its degradation on every recovery, which is the noisiest possible
                // reading of a target that never actually changed condition.
            }

            return null;
        }
    }

    /// <summary>
    /// Decides whether this target is currently degraded, updates the displayed status, and
    /// returns a soft transition on the crossing in either direction.
    /// <para>
    /// The sample itself stays <see cref="TargetStatus.Ok"/> in history and in the availability
    /// buckets — degradation is a property of the recent window, not of one probe, and writing it
    /// into the record would make it impossible to say afterwards what the actual reply was.
    /// </para>
    /// </summary>
    private StateTransition? EvaluateDegradedLocked(
        in ProbeResult result, DegradeThresholds thresholds, bool raiseTransitions)
    {
        if (thresholds.IsOff)
        {
            _degradedSinceTick = null;
            _degradedFired = false;
            return null;
        }

        // Wait until the window can be filled from this run before judging anything. Restored
        // history is the right basis for the statistics on the board and the wrong basis for a
        // live verdict — see _samplesThisRun.
        if (_samplesThisRun < thresholds.Samples) return null;

        var window = _history.RecentStats(thresholds.Samples);

        // Refuse to judge on too little evidence. Without this a single slow first reply after a
        // restart would turn the whole board amber.
        if (window.Samples < Math.Min(MinDegradeSamples, thresholds.Samples)) return null;

        var breached = (thresholds.LatencyMs > 0 && window.AvgMs > thresholds.LatencyMs)
                    || (thresholds.LossPercent > 0 && window.LossPercent > thresholds.LossPercent);

        if (breached)
        {
            _status = TargetStatus.Degraded;
            _degradedSinceTick ??= result.TickMs;

            if (_degradedFired || !raiseTransitions) return null;
            _degradedFired = true;

            return new StateTransition(
                Config.Name, Up: false, result.When, TimeSpan.Zero,
                TargetStatus.Degraded, 0, TransitionKind.Degraded);
        }

        if (!_degradedFired) { _degradedSinceTick = null; return null; }

        var lasted = _degradedSinceTick is { } since
            ? TimeSpan.FromMilliseconds(result.TickMs - since)
            : TimeSpan.Zero;

        _degradedSinceTick = null;
        _degradedFired = false;

        return raiseTransitions
            ? new StateTransition(
                Config.Name, Up: true, result.When, lasted, TargetStatus.Ok, 0, TransitionKind.Degraded)
            : null;
    }

    /// <summary>Minimum samples in the window before the degraded state may be entered at all.</summary>
    private const int MinDegradeSamples = 5;

    /// <summary>
    /// Forces a status without touching counters or history. Used for Paused, and for Suspended
    /// during sleep or local network loss — the states that exist precisely so those conditions
    /// are never recorded as target failures.
    /// </summary>
    public void ForceStatus(TargetStatus status)
    {
        lock (_gate)
        {
            _status = status;
            if (status is TargetStatus.Suspended or TargetStatus.Paused)
            {
                _consecutiveFailures = 0;
                _downSinceTick = null;
                _downFired = false;
                _degradedSinceTick = null;
                _degradedFired = false;
            }
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _history.Clear();
            Availability.Clear();
            Counters.OkCount = 0;
            Counters.NokCount = 0;
            Counters.LastOk = null;
            Counters.LastNok = null;
            _consecutiveFailures = 0;
            _downSinceTick = null;
            _downFired = false;
            _degradedSinceTick = null;
            _degradedFired = false;
        }
    }

    /// <summary>Applies edited configuration, rebuilding the probe if the kind changed.</summary>
    public void UpdateConfig(TargetConfig config)
    {
        lock (_gate)
        {
            var kindChanged = config.Probe != Config.Probe;
            var addressChanged = !string.Equals(config.Address, Config.Address, StringComparison.OrdinalIgnoreCase);
            Config = config;
            _maintenance = MaintenanceSchedule.Parse(config.Maintenance);

            if (kindChanged)
            {
                _probe.Dispose();
                _probe = CreateProbe(config.Probe);
            }

            if (addressChanged)
            {
                _resolved = null;
                _reverseName = null;
            }

            if (!config.Enabled)
            {
                _status = TargetStatus.Paused;
                _consecutiveFailures = 0;
                _downSinceTick = null;
                _downFired = false;
                _degradedSinceTick = null;
                _degradedFired = false;
            }
            else if (_status == TargetStatus.Paused) _status = TargetStatus.Unknown;
        }
    }

    public ProbeResult[] RecentHistory(int n) => _history.Recent(n);

    /// <summary>Retained history, oldest first, for the state sidecar.</summary>
    public ProbeResult[] HistorySnapshot() => _history.Snapshot();

    /// <summary>Restores history saved by a previous run.</summary>
    public void RestoreHistory(IReadOnlyList<ProbeResult> samples) => _history.Restore(samples);

    /// <summary>
    /// The trace taken at the last failure, or null if there has not been one. Kept on the target
    /// rather than only logged, so the UI can show where the path broke while it is still broken.
    /// </summary>
    public TraceResult? LastTrace
    {
        get { lock (_gate) return _lastTrace; }
    }

    public void SetLastTrace(TraceResult trace)
    {
        lock (_gate) _lastTrace = trace;
    }

    public TargetSnapshot Snapshot()
    {
        lock (_gate)
        {
            var ip = _resolved?.ToString() ?? "";
            var isLiteral = IPAddress.TryParse(Config.Address, out _);

            // Hostname column: the name the user typed if they typed one, otherwise whatever the
            // reverse lookup found. IP column: the address actually being probed right now, so a
            // DHCP or round-robin change is visible rather than hidden behind the name.
            var hostname = isLiteral ? _reverseName ?? "" : Config.Address;
            if (ip.Length == 0 && isLiteral) ip = Config.Address;

            var downFor = _downSinceTick is { } since
                ? TimeSpan.FromMilliseconds(Environment.TickCount64 - since)
                : (TimeSpan?)null;

            return new TargetSnapshot(
                Config.Name,
                Config.Address,
                ip,
                hostname,
                Config.Probe,
                Config.Port,
                Config.Enabled,
                _status,
                _icmpStatus,
                _lastRttMs,
                Counters.LastOk,
                Counters.LastNok,
                Counters.OkCount,
                Counters.NokCount,
                _consecutiveFailures,
                _history.Stats(),
                downFor,
                _certificate);
        }
    }

    public void Dispose() => _probe.Dispose();
}

/// <param name="Up">True when the target recovered, false when it was declared down.</param>
/// <param name="DownFor">How long the outage lasted. Only meaningful when <paramref name="Up"/>.</param>
/// <param name="Threshold">
/// The consecutive-failure count that actually triggered this transition. Carried on the
/// transition rather than read back from global settings, because a target may override it — the
/// notification would otherwise quote a number that was never applied to this host.
/// </param>
/// <param name="Kind">
/// Whether this is a real up/down transition or a soft one. Defaulted so that every call site
/// written before soft transitions existed still means exactly what it did then.
/// </param>
public readonly record struct StateTransition(
    string TargetName,
    bool Up,
    DateTimeOffset When,
    TimeSpan DownFor,
    TargetStatus Status,
    int Threshold,
    TransitionKind Kind = TransitionKind.Hard);

/// <summary>What sort of change a <see cref="StateTransition"/> describes.</summary>
public enum TransitionKind
{
    /// <summary>The target went down, or came back. The only kind that is an outage.</summary>
    Hard = 0,

    /// <summary>
    /// The target crossed a latency or loss threshold while still replying. <c>Up: false</c> means
    /// it became degraded; <c>Up: true</c> means it stopped being.
    /// </summary>
    Degraded,

    /// <summary>
    /// A TLS certificate is near expiry or already expired. <see cref="StateTransition.DownFor"/>
    /// carries the time remaining rather than a duration already elapsed — negative once expired.
    /// </summary>
    Certificate,
}

/// <summary>
/// Latency and loss limits that define the degraded state for one target, already resolved from
/// per-target overrides and global defaults.
/// </summary>
/// <param name="LatencyMs">Average round-trip ceiling, or 0 for no latency limit.</param>
/// <param name="LossPercent">Loss ceiling, or 0 for no loss limit.</param>
/// <param name="Samples">How many recent probes to average over.</param>
public readonly record struct DegradeThresholds(int LatencyMs, double LossPercent, int Samples)
{
    /// <summary>True when neither limit is set, which is the default and disables the state.</summary>
    public bool IsOff => LatencyMs <= 0 && LossPercent <= 0;
}
