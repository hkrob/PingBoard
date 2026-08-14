namespace PingBoard.Core;

/// <summary>
/// Outcome of a probe, as shown in the Status column.
/// Deliberately richer than OK/NOT-OK: "the name stopped resolving" and "the packet timed out"
/// are different problems with different fixes, and <see cref="Suspended"/> exists so that a
/// laptop lid or a downed NIC never gets recorded as a target failure.
/// </summary>
public enum TargetStatus
{
    /// <summary>No probe has completed yet.</summary>
    Unknown = 0,

    /// <summary>Reply received (ICMP echo reply, or TCP connect succeeded).</summary>
    Ok,

    /// <summary>No reply within the timeout.</summary>
    Timeout,

    /// <summary>
    /// The network actively told us it could not deliver — host/net/protocol unreachable,
    /// administratively prohibited, TTL expired. A router answered; the target did not.
    /// </summary>
    Unreachable,

    /// <summary>Forward DNS resolution failed. Not a ping failure — do not treat it as one.</summary>
    DnsFail,

    /// <summary>TCP connect was actively refused (RST). The host is up; the port is closed.</summary>
    Refused,

    /// <summary>
    /// The server answered with an unacceptable HTTP status. Distinct from every other failure
    /// here because it is the only one where everything below the application layer worked: DNS
    /// resolved, the socket opened, TLS completed, a response came back — and the service is still
    /// broken. Collapsing that into "timeout" would send you to look at the network.
    /// </summary>
    HttpError,

    /// <summary>Probing disabled for this target by the user.</summary>
    Paused,

    /// <summary>
    /// Machine asleep or local network unavailable. Counters are frozen and notifications
    /// suppressed while in this state.
    /// </summary>
    Suspended,
}

/// <summary>How a target is probed.</summary>
public enum ProbeKind
{
    /// <summary>ICMP echo request via <see cref="System.Net.NetworkInformation.Ping"/>.</summary>
    Icmp = 0,

    /// <summary>TCP connect to a port. For hosts and firewalls that silently drop ICMP.</summary>
    Tcp,

    /// <summary>
    /// HTTP request, checking the status code.
    /// <para>
    /// A TCP connect to port 80 or 443 proves a socket opens. It does not prove the service works:
    /// a wedged application server accepts connections and returns 500 forever, and the board
    /// would show it green throughout. This is the probe that tells the difference.
    /// </para>
    /// </summary>
    Http,

    /// <summary>HTTPS request. A separate kind so the scheme is visible in the config file.</summary>
    Https,
}

public static class TargetStatusExtensions
{
    /// <summary>True when the probe reached the target. Only this counts as OK.</summary>
    public static bool IsOk(this TargetStatus s) => s == TargetStatus.Ok;

    /// <summary>
    /// True when this outcome represents a real failure of the target and should increment the
    /// NOK counter. Paused/Suspended/Unknown are explicitly excluded — that exclusion is the
    /// whole point of having those states.
    /// </summary>
    public static bool IsFailure(this TargetStatus s) => s is TargetStatus.Timeout
        or TargetStatus.Unreachable
        or TargetStatus.DnsFail
        or TargetStatus.Refused
        or TargetStatus.HttpError;

    /// <summary>True when the target is not being probed, so counters must not move.</summary>
    public static bool IsInactive(this TargetStatus s) => s is TargetStatus.Paused
        or TargetStatus.Suspended
        or TargetStatus.Unknown;

    /// <summary>Short label for the Status column.</summary>
    public static string Label(this TargetStatus s) => s switch
    {
        TargetStatus.Ok => "OK",
        TargetStatus.Timeout => "TIMEOUT",
        TargetStatus.Unreachable => "UNREACHABLE",
        TargetStatus.DnsFail => "DNS FAIL",
        TargetStatus.Refused => "REFUSED",
        TargetStatus.HttpError => "HTTP ERR",
        TargetStatus.Paused => "PAUSED",
        TargetStatus.Suspended => "SUSPENDED",
        _ => "—",
    };

    /// <summary>
    /// Segoe Fluent Icons glyph. Paired with <see cref="Label"/> and colour so status is never
    /// conveyed by colour alone.
    /// </summary>
    public static string Glyph(this TargetStatus s) => s switch
    {
        TargetStatus.Ok => "",          // CheckMark
        TargetStatus.Timeout => "",     // Warning
        TargetStatus.Unreachable => "", // NetworkOffline
        TargetStatus.DnsFail => "",     // Error
        TargetStatus.Refused => "",     // Cancel
        TargetStatus.HttpError => "",   // Globe
        TargetStatus.Paused => "",      // Pause
        TargetStatus.Suspended => "",   // QuietHours
        _ => "",                        // Unknown
    };
}
