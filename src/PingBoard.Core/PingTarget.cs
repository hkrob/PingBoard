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

    // Null means "inherit from [Settings]".
    public int? IntervalMs { get; set; }
    public int? TimeoutMs { get; set; }
    public int? PayloadBytes { get; set; }
    public int? Ttl { get; set; }

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
    TimeSpan? DownFor);

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
    private IPAddress? _resolved;
    private string? _reverseName;

    public PingTarget(TargetConfig config, Settings settings, TargetCounters? counters = null)
    {
        Config = config;
        Counters = counters ?? new TargetCounters();
        _history = new RingBuffer(settings.RollingWindow);
        _probe = CreateProbe(config.Probe);
        if (!config.Enabled) _status = TargetStatus.Paused;
    }

    public TargetConfig Config { get; private set; }
    public TargetCounters Counters { get; }

    /// <summary>Phase offset within the interval, so targets don't all fire on the same tick.</summary>
    public long NextDueTick { get; set; }

    public bool IsInFlight => Volatile.Read(ref _inFlight) == 1;

    private static IProbe CreateProbe(ProbeKind kind) =>
        kind == ProbeKind.Tcp ? new TcpProbe() : new IcmpProbe();

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
        Port: Config.Port);

    public int IntervalFrom(Settings s) => Config.IntervalMs ?? s.IntervalMs;

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
    public StateTransition? Record(in ProbeResult result, int failuresBeforeDown)
    {
        lock (_gate)
        {
            _history.Add(result);
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

                if (!wasDown) { _downSinceTick = null; return null; }

                var downFor = _downSinceTick is { } since
                    ? TimeSpan.FromMilliseconds(result.TickMs - since)
                    : TimeSpan.Zero;
                _downSinceTick = null;

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
                if (!_downFired && _consecutiveFailures >= failuresBeforeDown)
                {
                    _downFired = true;
                    return new StateTransition(
                        Config.Name, Up: false, result.When, TimeSpan.Zero, result.Status, failuresBeforeDown);
                }
            }

            return null;
        }
    }

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
            }
        }
    }

    public void ResetStats()
    {
        lock (_gate)
        {
            _history.Clear();
            Counters.OkCount = 0;
            Counters.NokCount = 0;
            Counters.LastOk = null;
            Counters.LastNok = null;
            _consecutiveFailures = 0;
            _downSinceTick = null;
            _downFired = false;
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
            }
            else if (_status == TargetStatus.Paused) _status = TargetStatus.Unknown;
        }
    }

    public ProbeResult[] RecentHistory(int n) => _history.Recent(n);

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
                downFor);
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
public readonly record struct StateTransition(
    string TargetName,
    bool Up,
    DateTimeOffset When,
    TimeSpan DownFor,
    TargetStatus Status,
    int Threshold);
