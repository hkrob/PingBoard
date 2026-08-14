using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace PingBoard.Core;

/// <summary>One hop in a trace.</summary>
/// <param name="Ttl">Hop number, counting from 1.</param>
/// <param name="Address">The router that answered, or null when nothing did.</param>
/// <param name="RttMs">Round-trip to that hop, or <see cref="ProbeResult.NoRtt"/> on silence.</param>
/// <param name="Status">
/// Raw status for this hop. <see cref="IPStatus.TtlExpired"/> is the <em>successful</em> case for
/// an intermediate hop — a router reporting that it decremented the TTL to zero is exactly what a
/// traceroute is asking for, and reading it as a failure is the classic way to get this wrong.
/// </param>
public readonly record struct TraceHop(int Ttl, IPAddress? Address, int RttMs, IPStatus Status)
{
    public bool Answered => Address is not null;

    /// <summary>Traditional traceroute rendering: <c>7  10.1.10.1  4 ms</c>, or <c>*</c> for silence.</summary>
    public override string ToString() => Answered
        ? $"{Ttl,2}  {Address}  {(RttMs >= 0 ? RttMs + " ms" : "")}".TrimEnd()
        : $"{Ttl,2}  *";
}

/// <summary>The result of one trace.</summary>
/// <param name="Reached">True when the final hop was the target itself.</param>
public readonly record struct TraceResult(
    string TargetName,
    IPAddress Destination,
    IReadOnlyList<TraceHop> Hops,
    bool Reached,
    DateTimeOffset When)
{
    /// <summary>
    /// The last hop that answered. This is the point of the whole exercise: when a target goes
    /// dark, the interesting question is not "is it down" — the board already said so — but how
    /// far the packets got before they stopped, which is what tells you whose problem it is.
    /// </summary>
    public TraceHop? LastResponding
    {
        get
        {
            for (var i = Hops.Count - 1; i >= 0; i--)
                if (Hops[i].Answered) return Hops[i];
            return null;
        }
    }

    /// <summary>One line for an alert or a log: where the path stopped getting through.</summary>
    public string Summary()
    {
        if (Reached) return $"path intact, {Hops.Count} hops to {Destination}";

        return LastResponding is { } last
            ? $"path breaks after hop {last.Ttl} ({last.Address}), {Hops.Count} probed"
            : "no hop responded at all — the local network or first gateway is not forwarding";
    }

    public string ToText() => string.Join(Environment.NewLine, Hops);
}

/// <summary>
/// ICMP traceroute, run when a target is declared down.
/// <para>
/// "Down" is a fact the board already gives you. What it cannot tell you is <em>where</em>: a host
/// that stopped answering, an ISP link that dropped, and a local gateway that fell over all render
/// identically as a red row. A trace taken at the moment of failure is the difference between
/// knowing something broke and knowing whose problem it is — and it has to be taken then, because
/// by the time you get to the machine the path has usually healed.
/// </para>
/// <para>
/// Built on <see cref="Ping"/> with an increasing TTL rather than raw sockets, for the same reason
/// the probes are: no elevation required. Hops are walked strictly sequentially — a single
/// <see cref="Ping"/> is not reentrant, and firing all thirty TTLs at once would also put a burst
/// of ICMP on a network that is, by hypothesis, already unhealthy.
/// </para>
/// </summary>
public sealed class TraceRoute
{
    /// <summary>Payload size. Small: this is a diagnostic, not a load test.</summary>
    private static readonly byte[] Payload = new byte[32];

    /// <summary>
    /// Runs a trace to <paramref name="destination"/>.
    /// <para>
    /// Never throws for network reasons — a trace is a best-effort diagnostic attached to a failure
    /// that has already been reported, and it must not become a second failure of its own.
    /// </para>
    /// </summary>
    public static async Task<TraceResult> RunAsync(
        string targetName,
        IPAddress destination,
        TraceOptions options,
        CancellationToken ct)
    {
        var hops = new List<TraceHop>(options.MaxHops);
        var reached = false;

        using var ping = new Ping();

        for (var ttl = 1; ttl <= options.MaxHops && !ct.IsCancellationRequested; ttl++)
        {
            var hop = await OneHopAsync(ping, destination, ttl, options, ct).ConfigureAwait(false);
            hops.Add(hop);

            if (hop.Status == IPStatus.Success)
            {
                reached = true;
                break;
            }

            // An administrative block is the end of the useful trace: everything past it is silence
            // we already know the reason for, and walking the remaining TTLs just costs time.
            if (hop.Status is IPStatus.DestinationProhibited or IPStatus.BadRoute) break;

            if (options.StopAfterSilentHops > 0
                && CountTrailingSilence(hops) >= options.StopAfterSilentHops)
            {
                break;
            }
        }

        return new TraceResult(targetName, destination, hops, reached, DateTimeOffset.Now);
    }

    private static async Task<TraceHop> OneHopAsync(
        Ping ping, IPAddress destination, int ttl, TraceOptions options, CancellationToken ct)
    {
        var start = Stopwatch.GetTimestamp();

        try
        {
            var reply = await ping
                .SendPingAsync(destination, options.HopTimeoutMs, Payload, new PingOptions(ttl, false))
                .WaitAsync(ct)
                .ConfigureAwait(false);

            // reply.RoundtripTime is unreliable for a TtlExpired reply — several Windows versions
            // report 0 there — so time it here instead.
            var rtt = (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds);

            // Unlike a probe, the responder's address is exactly what we want here: for an
            // intermediate hop it *is* the answer.
            return reply.Status switch
            {
                IPStatus.TtlExpired or IPStatus.TimeExceeded =>
                    new TraceHop(ttl, reply.Address, rtt, IPStatus.TtlExpired),

                IPStatus.Success => new TraceHop(ttl, reply.Address, rtt, IPStatus.Success),

                IPStatus.TimedOut => new TraceHop(ttl, null, ProbeResult.NoRtt, IPStatus.TimedOut),

                _ => new TraceHop(ttl, reply.Address, rtt, reply.Status),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return new TraceHop(ttl, null, ProbeResult.NoRtt, IPStatus.Unknown);
        }
    }

    /// <summary>
    /// How many hops at the end of the list went unanswered. Many routers simply do not generate
    /// TTL-expired messages, so a couple of gaps mid-path are normal and must not end the trace —
    /// but a long unbroken run of them means we are past the break and only burning timeouts.
    /// </summary>
    private static int CountTrailingSilence(List<TraceHop> hops)
    {
        var count = 0;
        for (var i = hops.Count - 1; i >= 0 && !hops[i].Answered; i--) count++;
        return count;
    }
}

/// <param name="MaxHops">TTL ceiling. 30 is the traditional traceroute default.</param>
/// <param name="HopTimeoutMs">Per-hop wait. Deliberately shorter than a probe timeout: thirty hops
/// at two seconds each is a minute of waiting for a diagnostic nobody is watching in real time.</param>
/// <param name="StopAfterSilentHops">Give up after this many consecutive silent hops. Zero walks
/// the full range regardless.</param>
public readonly record struct TraceOptions(int MaxHops, int HopTimeoutMs, int StopAfterSilentHops)
{
    public static TraceOptions Default => new(30, 1000, 5);
}
