using System.Net;
using System.Net.NetworkInformation;

namespace PingBoard.Core;

/// <summary>
/// One completed probe. Immutable so it can be handed between the probe threadpool thread and
/// the UI thread without locking.
/// </summary>
/// <param name="Status">Interpreted outcome.</param>
/// <param name="RttMs">Round-trip time in ms, or -1 when there was no reply.</param>
/// <param name="TickMs">
/// Monotonic timestamp from <see cref="Environment.TickCount64"/>. All duration arithmetic uses
/// this. Never use <see cref="When"/> for durations — an NTP correction or a DST rollover would
/// silently corrupt every elapsed time in the app.
/// </param>
/// <param name="When">Wall-clock timestamp. Display only.</param>
/// <param name="IcmpStatus">
/// Raw <see cref="IPStatus"/> from the ICMP reply, surfaced in the tooltip.
/// <c>DestinationHostUnreachable</c> tells you something that "NOT OK" does not.
/// </param>
/// <param name="Address">The address actually probed, after DNS resolution.</param>
public readonly record struct ProbeResult(
    TargetStatus Status,
    int RttMs,
    long TickMs,
    DateTimeOffset When,
    IPStatus IcmpStatus,
    IPAddress? Address)
{
    /// <summary>Sentinel for "no reply, so no round-trip time".</summary>
    public const int NoRtt = -1;

    public bool HasRtt => RttMs >= 0;

    public static ProbeResult Ok(int rttMs, IPAddress address, long tickMs, DateTimeOffset when) =>
        new(TargetStatus.Ok, rttMs, tickMs, when, IPStatus.Success, address);

    public static ProbeResult Fail(
        TargetStatus status,
        long tickMs,
        DateTimeOffset when,
        IPStatus icmpStatus = IPStatus.Unknown,
        IPAddress? address = null) =>
        new(status, NoRtt, tickMs, when, icmpStatus, address);

    /// <summary>
    /// Maps a raw <see cref="IPStatus"/> onto our coarser status.
    /// <para>
    /// Note the subtlety this exists to capture: an unreachable destination arrives as a
    /// <em>successful reply from a router</em>, not as a timeout. Treating everything that isn't
    /// <see cref="IPStatus.Success"/> as a timeout would throw that distinction away.
    /// </para>
    /// </summary>
    public static TargetStatus FromIpStatus(IPStatus status) => status switch
    {
        IPStatus.Success => TargetStatus.Ok,

        IPStatus.TimedOut => TargetStatus.Timeout,

        IPStatus.DestinationHostUnreachable
            or IPStatus.DestinationNetworkUnreachable
            or IPStatus.DestinationProtocolUnreachable
            or IPStatus.DestinationPortUnreachable
            or IPStatus.DestinationUnreachable
            or IPStatus.DestinationProhibited
            or IPStatus.DestinationScopeMismatch
            or IPStatus.BadRoute
            or IPStatus.BadDestination
            or IPStatus.TtlExpired
            or IPStatus.TimeExceeded
            or IPStatus.TtlReassemblyTimeExceeded => TargetStatus.Unreachable,

        // NoResources / HardwareError / PacketTooBig / BadHeader and friends are local or
        // transport faults rather than a statement about the target. Treat as a timeout so a
        // transient local hiccup doesn't get reported as "the host is unreachable".
        _ => TargetStatus.Timeout,
    };
}
