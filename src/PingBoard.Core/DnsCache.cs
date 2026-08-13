using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace PingBoard.Core;

/// <summary>
/// Forward and reverse DNS with TTL caching and hard per-lookup timeouts.
/// <para>
/// Resolving on every probe would hammer the resolver — on a network running several of them, at
/// 1 Hz across dozens of targets, that is a self-inflicted denial of service. Names are resolved
/// once and cached; the cache is refreshed on expiry or when a target starts failing, so a DHCP
/// change or a failover still gets picked up.
/// </para>
/// </summary>
public sealed class DnsCache
{
    /// <summary>
    /// The OS resolver can block far longer than any sane probe interval when a DNS server is
    /// unreachable. Every lookup gets its own ceiling.
    /// </summary>
    private const int LookupTimeoutMs = 3000;

    private readonly ConcurrentDictionary<string, ForwardEntry> _forward = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _reverse = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _ttl;

    public DnsCache(int ttlSeconds) => _ttl = TimeSpan.FromSeconds(Math.Max(1, ttlSeconds));

    private sealed record ForwardEntry(IPAddress Address, long ExpiresAtTick);

    /// <summary>
    /// Resolves a host to an address, using the cache when it is still fresh.
    /// Returns <c>null</c> on failure — the caller reports <see cref="TargetStatus.DnsFail"/>,
    /// which is deliberately distinct from a ping timeout.
    /// </summary>
    /// <param name="host">A literal IP or a hostname.</param>
    /// <param name="preferIPv4">Pick an A record over AAAA when both exist.</param>
    /// <param name="forceRefresh">Bypass the cache — used after repeated failures.</param>
    public async Task<IPAddress?> ResolveAsync(
        string host,
        bool preferIPv4,
        bool forceRefresh,
        CancellationToken ct)
    {
        // A literal address needs no resolution and must never be cached or looked up.
        if (IPAddress.TryParse(host, out var literal)) return literal;

        var now = Environment.TickCount64;

        if (!forceRefresh
            && _forward.TryGetValue(host, out var cached)
            && cached.ExpiresAtTick > now)
        {
            return cached.Address;
        }

        var resolved = await LookupAsync(host, preferIPv4, ct).ConfigureAwait(false);

        if (resolved is not null)
        {
            _forward[host] = new ForwardEntry(resolved, now + (long)_ttl.TotalMilliseconds);
            return resolved;
        }

        // Resolution failed. Drop the stale entry so we don't keep probing an address the name no
        // longer points at — silently probing the wrong host is worse than reporting DNS FAIL.
        _forward.TryRemove(host, out _);
        return null;
    }

    private static async Task<IPAddress?> LookupAsync(string host, bool preferIPv4, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LookupTimeoutMs);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            if (addresses.Length == 0) return null;

            var preferred = preferIPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            return Array.Find(addresses, a => a.AddressFamily == preferred) ?? addresses[0];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Best-effort reverse lookup to populate the Hostname column for targets entered by IP.
    /// Resolved once and cached forever (including a null result, so we don't retry a PTR-less
    /// address on every refresh). Never blocks a probe — callers fire this and forget.
    /// </summary>
    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken ct)
    {
        var key = address.ToString();
        if (_reverse.TryGetValue(key, out var cachedName)) return cachedName;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LookupTimeoutMs);

        string? name = null;
        try
        {
            // The (IPAddress, CancellationToken) overload does not exist; the string form takes
            // an address literal happily and is the only cancellable route.
            var entry = await Dns.GetHostEntryAsync(key, cts.Token).ConfigureAwait(false);
            // GetHostEntry echoes the IP back when there is no PTR record; that is not a hostname.
            if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != key)
                name = entry.HostName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            name = null;
        }

        _reverse[key] = name;
        return name;
    }

    /// <summary>Drops all cached entries, so the next probe re-resolves everything.</summary>
    public void Flush()
    {
        _forward.Clear();
        _reverse.Clear();
    }
}
