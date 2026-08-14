namespace PingBoard.Core;

/// <summary>
/// Global defaults from the <c>[Settings]</c> section. Every numeric value here can be overridden
/// per target.
/// </summary>
public sealed class Settings
{
    public int IntervalMs { get; set; } = 2000;
    public int TimeoutMs { get; set; } = 2000;
    public int PayloadBytes { get; set; } = 32;
    public int Ttl { get; set; } = 64;

    /// <summary>Samples retained per target for rolling loss, min/avg/max and the sparkline.</summary>
    public int RollingWindow { get; set; } = 300;

    public bool PreferIPv4 { get; set; } = true;
    public int DnsCacheSeconds { get; set; } = 300;

    /// <summary>
    /// Ceiling on simultaneous in-flight probes. Without this, a few hundred targets would open a
    /// few hundred concurrent ICMP operations on the same tick.
    /// </summary>
    public int MaxConcurrent { get; set; } = 32;

    public bool NotifyOnChange { get; set; } = true;

    /// <summary>
    /// Consecutive failures before a target is declared down and a notification fires. Stops a
    /// single dropped packet from generating an alert.
    /// </summary>
    public int FailuresBeforeDown { get; set; } = 3;

    /// <summary>
    /// Consecutive failures before the name is re-resolved, so a failover or DHCP change is
    /// picked up without waiting out the DNS TTL.
    /// </summary>
    public int FailuresBeforeReresolve { get; set; } = 3;

    public string LogPath { get; set; } = "pingboard-events.csv";
    public bool LogEnabled { get; set; } = true;

    /// <summary>Grace period after wake before probing resumes, letting the network stack settle.</summary>
    public int ResumeSettleMs { get; set; } = 5000;

    /// <summary>
    /// Trace the path when a target is declared down.
    /// <para>
    /// On by default, because the trace is only worth anything if it is taken at the moment of
    /// failure — by the time anyone reaches the machine to run one by hand, the path has usually
    /// healed and the evidence is gone. It fires on the down transition only, never per failed
    /// probe, so a permanently dead host costs one trace rather than one per second.
    /// </para>
    /// </summary>
    public bool TraceOnFailure { get; set; } = true;

    public int TraceMaxHops { get; set; } = 30;

    /// <summary>Per-hop wait. Short on purpose: 30 hops at a probe-length timeout is a minute.</summary>
    public int TraceHopTimeoutMs { get; set; } = 1000;

    /// <summary>Clamps every value into a sane range. Applied after loading a hand-edited file.</summary>
    public void Validate()
    {
        IntervalMs = Math.Clamp(IntervalMs, 250, 3_600_000);
        TimeoutMs = Math.Clamp(TimeoutMs, 100, 60_000);
        PayloadBytes = Math.Clamp(PayloadBytes, 0, 65_500);
        Ttl = Math.Clamp(Ttl, 1, 255);
        RollingWindow = Math.Clamp(RollingWindow, 10, 10_000);
        DnsCacheSeconds = Math.Clamp(DnsCacheSeconds, 1, 86_400);
        MaxConcurrent = Math.Clamp(MaxConcurrent, 1, 512);
        FailuresBeforeDown = Math.Clamp(FailuresBeforeDown, 1, 100);
        FailuresBeforeReresolve = Math.Clamp(FailuresBeforeReresolve, 1, 100);
        ResumeSettleMs = Math.Clamp(ResumeSettleMs, 0, 120_000);
        TraceMaxHops = Math.Clamp(TraceMaxHops, 1, 64);
        TraceHopTimeoutMs = Math.Clamp(TraceHopTimeoutMs, 100, 10_000);

        // A timeout longer than the interval guarantees permanently skipped ticks against a dead
        // host. Allowed, but pull it back to something coherent.
        if (TimeoutMs > IntervalMs) TimeoutMs = IntervalMs;
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}
