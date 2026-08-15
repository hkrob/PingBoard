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

    /// <summary>
    /// Average round-trip time, over the last <see cref="DegradedSamples"/> probes, above which a
    /// target that is still replying is shown as degraded. Zero disables it.
    /// <para>
    /// <b>Off by default, and deliberately so.</b> Any non-zero global default here is a guess
    /// about somebody else's network: 80 ms is a failing LAN and an excellent link to the other
    /// side of the world, and the same number cannot be right for both. A board that turned amber
    /// on first launch because a host in Sydney behaved exactly as expected would teach the user to
    /// ignore the colour, which costs more than the feature is worth. Set it per target, where the
    /// number means something.
    /// </para>
    /// </summary>
    public int DegradedLatencyMs { get; set; }

    /// <summary>
    /// Packet loss percentage, over the same window, above which a target is shown as degraded.
    /// Zero disables it. Off by default for the same reason as <see cref="DegradedLatencyMs"/> —
    /// plenty of routers deprioritise ICMP and answer nine echoes in ten while forwarding traffic
    /// perfectly.
    /// </summary>
    public double DegradedLossPercent { get; set; }

    /// <summary>
    /// How many recent probes the degraded assessment averages over.
    /// <para>
    /// Short on purpose. The rolling window is 300 samples — ten minutes at the default interval —
    /// which is the right span for statistics you read, and far too slow for a state you watch: a
    /// link that goes bad would take five minutes to turn amber and five more to turn back. Twenty
    /// samples reacts inside a minute while still ignoring a single slow reply.
    /// </para>
    /// </summary>
    public int DegradedSamples { get; set; } = 20;

    /// <summary>
    /// Raise a notification when a target enters or leaves the degraded state. Off by default:
    /// degradation is a condition you look at, not an emergency you are woken for, and the board
    /// and the outage log both record it either way.
    /// </summary>
    public bool NotifyOnDegraded { get; set; }

    /// <summary>Record up/down transitions to the outage log, so they survive a restart.</summary>
    public bool OutageLogEnabled { get; set; } = true;

    /// <summary>
    /// How often to re-read the TLS certificate of each HTTPS target.
    /// <para>
    /// Hours rather than seconds because a certificate is a fact that changes a handful of times a
    /// year. Checking it on the probe interval would open an extra TLS handshake every two seconds
    /// per target to re-read a value that has not moved since the last one.
    /// </para>
    /// </summary>
    public int CertCheckHours { get; set; } = 6;

    /// <summary>Days of remaining certificate validity below which the target is flagged.</summary>
    public int CertWarnDays { get; set; } = 14;

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

        // Zero stays zero — it is the "off" switch, not a small threshold — so clamp only the
        // range above it.
        if (DegradedLatencyMs != 0) DegradedLatencyMs = Math.Clamp(DegradedLatencyMs, 1, 600_000);
        if (DegradedLossPercent != 0) DegradedLossPercent = Math.Clamp(DegradedLossPercent, 0.1, 100);
        DegradedSamples = Math.Clamp(DegradedSamples, 3, 1000);
        CertCheckHours = Math.Clamp(CertCheckHours, 1, 720);
        CertWarnDays = Math.Clamp(CertWarnDays, 1, 365);

        // A timeout longer than the interval guarantees permanently skipped ticks against a dead
        // host. Allowed, but pull it back to something coherent.
        if (TimeoutMs > IntervalMs) TimeoutMs = IntervalMs;
    }

    public Settings Clone() => (Settings)MemberwiseClone();
}
