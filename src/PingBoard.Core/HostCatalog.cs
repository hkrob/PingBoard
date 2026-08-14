using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PingBoard.Core;

/// <summary>One host offered by the catalogue.</summary>
public readonly record struct CatalogEntry(string Name, string Address, ProbeKind Probe);

/// <summary>A named group of hosts, which becomes a tab when added.</summary>
public sealed record HostCategory(string Name, string Description, IReadOnlyList<CatalogEntry> Entries);

/// <summary>
/// Ready-made sets of hosts, so a new board is useful in one click rather than after twenty
/// minutes of typing addresses.
/// <para>
/// <b>The probe kind is chosen per category, and it matters.</b> Public resolvers answer ICMP and
/// that is the lightest possible check. Large websites are the opposite: ICMP to a name like
/// <c>google.com</c> lands on whichever anycast edge node is nearest, which proves the internet
/// works and says nothing about the service — and plenty of them drop ping entirely. Those get an
/// HTTPS probe, which is the check anyone actually means by "is the site up".
/// </para>
/// <para>
/// The lists are deliberately short. A category with forty entries is not a starting point, it is
/// a chore to prune — and every extra target is real traffic every interval, forever.
/// </para>
/// </summary>
public static class HostCatalog
{
    public const string LocalNetworkCategory = "Local network";

    /// <summary>Static categories. The local network is discovered separately, per machine.</summary>
    public static IReadOnlyList<HostCategory> Categories { get; } =
    [
        new("Public DNS",
            "Resolvers that answer ICMP. The quickest way to tell a local fault from an internet one.",
            [
                new("cloudflare-dns", "1.1.1.1", ProbeKind.Icmp),
                new("google-dns", "8.8.8.8", ProbeKind.Icmp),
                new("quad9-dns", "9.9.9.9", ProbeKind.Icmp),
                new("opendns", "208.67.222.222", ProbeKind.Icmp),
                new("adguard-dns", "94.140.14.14", ProbeKind.Icmp),
            ]),

        new("Large websites",
            "Checked over HTTPS rather than ICMP, because a ping to these lands on an anycast edge and proves nothing about the site.",
            [
                new("google", "www.google.com", ProbeKind.Https),
                new("amazon", "www.amazon.com", ProbeKind.Https),
                new("wikipedia", "www.wikipedia.org", ProbeKind.Https),
                new("microsoft", "www.microsoft.com", ProbeKind.Https),
                new("apple", "www.apple.com", ProbeKind.Https),
            ]),

        new("Social media",
            "The sites people notice first when a connection degrades.",
            [
                new("facebook", "www.facebook.com", ProbeKind.Https),
                new("instagram", "www.instagram.com", ProbeKind.Https),
                new("reddit", "www.reddit.com", ProbeKind.Https),
                new("linkedin", "www.linkedin.com", ProbeKind.Https),
                new("x-twitter", "x.com", ProbeKind.Https),
            ]),

        new("Cloud platforms",
            "Provider front doors. Useful for telling a provider outage from your own.",
            [
                new("aws", "aws.amazon.com", ProbeKind.Https),
                new("azure", "portal.azure.com", ProbeKind.Https),
                new("google-cloud", "cloud.google.com", ProbeKind.Https),
                new("cloudflare", "www.cloudflare.com", ProbeKind.Https),
            ]),

        new("Developer services",
            "The things a build breaks without.",
            [
                new("github", "github.com", ProbeKind.Https),
                new("stackoverflow", "stackoverflow.com", ProbeKind.Https),
                new("npm", "registry.npmjs.org", ProbeKind.Https),
                new("nuget", "www.nuget.org", ProbeKind.Https),
                new("docker-hub", "hub.docker.com", ProbeKind.Https),
            ]),

        new("Streaming and gaming",
            "Latency-sensitive services, where jitter matters more than loss.",
            [
                new("netflix", "www.netflix.com", ProbeKind.Https),
                new("youtube", "www.youtube.com", ProbeKind.Https),
                new("twitch", "www.twitch.tv", ProbeKind.Https),
                new("spotify", "open.spotify.com", ProbeKind.Https),
                new("steam", "store.steampowered.com", ProbeKind.Https),
            ]),

        new("Email providers",
            "Webmail front doors, for when the complaint is that mail has stopped.",
            [
                new("gmail", "mail.google.com", ProbeKind.Https),
                new("outlook", "outlook.office.com", ProbeKind.Https),
                new("proton-mail", "mail.proton.me", ProbeKind.Https),
            ]),
    ];

    /// <summary>
    /// This machine's own gateway and resolvers, discovered rather than guessed.
    /// <para>
    /// The most useful category by far, and the only one that cannot be a fixed list. When
    /// something breaks, the first question is whether it is the local network or everything past
    /// it, and that needs the addresses this machine is actually using.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CatalogEntry> DetectLocalNetwork()
    {
        var entries = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gateways = 0;
        var resolvers = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var properties = nic.GetIPProperties();

            foreach (var gateway in properties.GatewayAddresses)
                if (IsUsable(gateway.Address) && seen.Add(gateway.Address.ToString()))
                    entries.Add(new CatalogEntry(Name("gateway", ++gateways), gateway.Address.ToString(), ProbeKind.Icmp));

            foreach (var dns in properties.DnsAddresses)
                if (IsUsable(dns) && seen.Add(dns.ToString()))
                    entries.Add(new CatalogEntry(Name("dns", ++resolvers), dns.ToString(), ProbeKind.Icmp));
        }

        return entries;

        // The first of each kind gets the bare name; only a second one needs disambiguating.
        static string Name(string stem, int index) => index == 1 ? stem : $"{stem}-{index}";
    }

    /// <summary>
    /// Rejects addresses that are real but pointless to probe.
    /// <para>
    /// Loopback and unspecified are obvious. The two that matter are IPv6: link-local is
    /// per-interface and tells a ping nothing useful, and <b>site-local</b> — the deprecated
    /// <c>fec0::/10</c> range — appears on essentially every Windows machine as the placeholder
    /// DNS entries <c>fec0:0:0:ffff::1</c> through <c>::3</c>. Those are not resolvers anyone is
    /// using. Importing them would hand the user three permanently red rows on a perfectly healthy
    /// machine, which is exactly the sort of false alarm this application exists to avoid.
    /// </para>
    /// </summary>
    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6
            && (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal))
        {
            return false;
        }

        return true;
    }
}
