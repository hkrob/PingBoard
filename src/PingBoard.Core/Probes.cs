using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingBoard.Core;

/// <summary>Options for a single probe, resolved from global settings plus per-target overrides.</summary>
/// <param name="Host">
/// The address exactly as configured. HTTP needs it even though the caller has already resolved an
/// IP: connecting by address alone sends no SNI and the wrong Host header, so a virtual host
/// answers for the wrong site or the TLS handshake fails outright. The resolved IP still drives
/// the board's IP column.
/// </param>
/// <param name="Path">Request path for HTTP probes.</param>
/// <param name="ExpectStatus">Required status code, or 0 to accept any 2xx/3xx.</param>
public readonly record struct ProbeOptions(
    int TimeoutMs,
    int PayloadBytes,
    int Ttl,
    int Port,
    string Host = "",
    string Path = "/",
    int ExpectStatus = 0);

/// <summary>
/// A probe bound to one target. Instances are per-target and stateful — see
/// <see cref="IcmpProbe"/> for why that matters.
/// </summary>
public interface IProbe : IDisposable
{
    Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken ct);
}

/// <summary>
/// ICMP echo via <see cref="Ping"/>, which uses the OS <c>IcmpSendEcho2</c> path — no raw sockets
/// and no elevation required (verified non-elevated on this machine).
/// <para>
/// <b>One instance per target, never shared.</b> <see cref="Ping"/> is not reentrant: calling
/// <see cref="Ping.SendPingAsync(IPAddress, int, byte[], PingOptions)"/> while a send is already
/// outstanding on the same instance throws <see cref="InvalidOperationException"/>. The scheduler's
/// in-flight guard also protects against this, but owning the instance makes the invariant local.
/// </para>
/// <para>
/// Shelling out to <c>ping.exe</c> would cost a process launch and roughly 5 MB per probe, which
/// defeats the purpose of the tool.
/// </para>
/// </summary>
public sealed class IcmpProbe : IProbe
{
    private readonly Ping _ping = new();
    private byte[] _payload = [];
    private bool _disposed;

    public async Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Reuse the buffer across probes; only reallocate when the configured size changes.
        if (_payload.Length != options.PayloadBytes)
        {
            _payload = new byte[options.PayloadBytes];
            for (var i = 0; i < _payload.Length; i++)
                _payload[i] = (byte)('a' + i % 23);
        }

        var pingOptions = new PingOptions(options.Ttl, dontFragment: false);

        try
        {
            var reply = await _ping.SendPingAsync(address, options.TimeoutMs, _payload, pingOptions)
                                   .WaitAsync(ct)
                                   .ConfigureAwait(false);

            var status = ProbeResult.FromIpStatus(reply.Status);
            var when = DateTimeOffset.Now;
            var tick = Environment.TickCount64;

            // Report the address we probed, never reply.Address.
            //
            // On failure the reply address is not the target: for DestinationHostUnreachable the
            // local stack generates the ICMP error, so it comes back as this machine's own IP, and
            // on the async timeout path it is often 0.0.0.0. Feeding either into the IP column
            // would silently replace the address being monitored with a meaningless one.
            return status.IsOk()
                ? ProbeResult.Ok((int)reply.RoundtripTime, address, tick, when)
                : ProbeResult.Fail(status, tick, when, reply.Status, address);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or SocketException)
        {
            // A PingException generally means the local stack could not send at all. That is not a
            // statement about the target, so record it as a timeout rather than "unreachable".
            return ProbeResult.Fail(TargetStatus.Timeout, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.Unknown, address);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ping.Dispose();
    }
}

/// <summary>
/// TCP connect probe, for the very common case of a host or firewall that silently drops ICMP.
/// A successful three-way handshake proves reachability more strongly than an echo reply does.
/// <para>
/// Every attempt gets a fresh <see cref="TcpClient"/> disposed in a <c>finally</c>. An abandoned
/// half-open connect leaks a socket handle <em>per probe</em>, which at 1 Hz exhausts the process
/// handle table within hours.
/// </para>
/// </summary>
public sealed class TcpProbe : IProbe
{
    public async Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.TimeoutMs);

        // Stopwatch, not Environment.TickCount64. TickCount64 is the right clock for *scheduling*
        // — monotonic and cheap — but its resolution is the system timer, ~15.6 ms by default. A
        // LAN handshake completes well inside one tick, so measuring with it reports every local
        // TCP target as 0 ms or 15 ms and makes avg/min/max/jitter meaningless for exactly the
        // hosts that had to use TCP because they drop ICMP. ICMP is unaffected: the OS supplies
        // reply.RoundtripTime.
        var start = Stopwatch.GetTimestamp();
        TcpClient? client = null;

        try
        {
            client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, options.Port, timeoutCts.Token).ConfigureAwait(false);

            var rtt = (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return ProbeResult.Ok(rtt, address, Environment.TickCount64, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own CancelAfter fired: the connect did not complete in time.
            return ProbeResult.Fail(TargetStatus.Timeout, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.TimedOut, address);
        }
        catch (SocketException ex)
        {
            // A refusal is a meaningfully different result from silence: the host answered, so it
            // is up — the port is simply closed.
            var status = ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => TargetStatus.Refused,
                SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.HostNotFound => TargetStatus.Unreachable,
                SocketError.TimedOut => TargetStatus.Timeout,
                _ => TargetStatus.Timeout,
            };
            return ProbeResult.Fail(status, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.Unknown, address);
        }
        finally
        {
            client?.Dispose();
        }
    }

    public void Dispose() { }
}

/// <summary>
/// HTTP(S) request, judged on the status code.
/// <para>
/// The probe that can tell "the socket opens" from "the service works". A TCP connect to 443
/// succeeds against a wedged application server that returns 500 to every request, and the board
/// would show it green indefinitely — which is the failure mode a monitor exists to catch.
/// </para>
/// <para>
/// Three deliberate choices. <b>Redirects are not followed:</b> a 301 is a real answer about this
/// URL, and following it silently measures a different endpoint than the one configured. <b>The
/// response body is never read:</b> only the headers are needed, so a target serving a large file
/// costs nothing. <b>A non-success status is its own failure kind</b> rather than a timeout,
/// because everything below the application layer worked and reporting it as a network fault
/// sends you to look in the wrong place.
/// </para>
/// </summary>
public sealed class HttpProbe(bool useTls) : IProbe
{
    /// <summary>
    /// Shared across every HTTP target. One handler pools connections; a client per probe would
    /// open a fresh TCP connection — and a fresh TLS handshake — on every single request, which
    /// would measure our own setup cost rather than the server's response time.
    /// </summary>
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// Identifies itself, which is both good manners for something that will hit a third-party
    /// server every few seconds for months and a correctness fix.
    /// <para>
    /// A request with no <c>User-Agent</c> is rejected outright by a number of large sites —
    /// Wikipedia answers 403 — so probing one showed a permanently red row for a service that was
    /// working perfectly. That is the worst kind of monitoring failure: not a missed outage but a
    /// manufactured one, which teaches the user to disbelieve the board.
    /// </para>
    /// </summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        })
        {
            // Per-request cancellation supplies the real timeout; this only stops a stuck request
            // living forever if that is ever missed.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// The product, its version, and where to complain — the three things an operator wants when
    /// an unfamiliar agent turns up in their access log.
    /// </summary>
    internal static string UserAgent
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            var number = version is null ? "1" : $"{version.Major}.{version.Minor}.{version.Build}";
            return $"PingBoard/{number} (+https://github.com/hkrob/PingBoard)";
        }
    }

    private readonly bool _useTls = useTls;

    public async Task<ProbeResult> ProbeAsync(IPAddress address, ProbeOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.TimeoutMs);

        var start = Stopwatch.GetTimestamp();

        try
        {
            var uri = BuildUri(address, options);

            // HEAD first would be cheaper, but plenty of servers answer it with 405 while serving
            // GET perfectly well, which would report a healthy site as broken.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var rtt = (int)Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            var code = (int)response.StatusCode;

            var ok = options.ExpectStatus > 0
                ? code == options.ExpectStatus
                : code is >= 200 and < 400;

            return ok
                ? ProbeResult.Ok(rtt, address, Environment.TickCount64, DateTimeOffset.Now)
                : ProbeResult.Fail(TargetStatus.HttpError, Environment.TickCount64, DateTimeOffset.Now,
                                   IPStatus.Success, address);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Fail(TargetStatus.Timeout, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.TimedOut, address);
        }
        catch (HttpRequestException ex)
        {
            // Connection-level trouble: refused, unresolvable, or a TLS handshake that failed.
            var status = ex.InnerException is SocketException socket
                ? socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => TargetStatus.Refused,
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable => TargetStatus.Unreachable,
                    _ => TargetStatus.Timeout,
                }
                : TargetStatus.Unreachable;

            return ProbeResult.Fail(status, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.Unknown, address);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            return ProbeResult.Fail(TargetStatus.Unreachable, Environment.TickCount64, DateTimeOffset.Now,
                                    IPStatus.BadDestination, address);
        }
    }

    /// <summary>
    /// Builds the request URL from the configured host, falling back to the resolved address when
    /// no host was recorded. A bracketed literal is required for IPv6 or the port parses wrongly.
    /// </summary>
    private Uri BuildUri(IPAddress address, ProbeOptions options)
    {
        var host = options.Host.Length > 0 ? options.Host : address.ToString();

        if (IPAddress.TryParse(host, out var literal)
            && literal.AddressFamily == AddressFamily.InterNetworkV6)
        {
            host = "[" + host + "]";
        }

        var scheme = _useTls ? "https" : "http";
        var path = options.Path.Length == 0 ? "/" : options.Path;
        if (!path.StartsWith('/')) path = "/" + path;

        var defaultPort = _useTls ? 443 : 80;
        var authority = options.Port == defaultPort ? host : $"{host}:{options.Port}";

        return new Uri($"{scheme}://{authority}{path}");
    }

    public void Dispose() { }
}
