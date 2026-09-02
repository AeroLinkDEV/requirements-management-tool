using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace AeroLink.Infrastructure.Persistence;

public interface IWebhookDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemWebhookDnsResolver : IWebhookDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class WebhookDestinationPolicyException(string message) : InvalidOperationException(message);

/// <summary>
/// A destination whose every reachable address has been resolved and classified for the current delivery.
/// The address list is the only set of endpoints the delivery's connection may attempt.
/// </summary>
public sealed record ApprovedWebhookDestination(Uri EndpointUri, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Outbound webhook destination policy (#849 Finding 1). A delivery may only reach an address that was
/// resolved, classified, and approved for that request; hostnames are resolved to ALL A/AAAA answers and any
/// prohibited answer fails closed. With <c>AllowPrivateWebhookTargets</c>, development explicitly admits
/// non-global addresses, but resolution still must return at least one connectable address and the scheme
/// rule remains governed only by <c>AllowInsecureWebhookTargets</c>. Redirects are disabled on the transport.
/// </summary>
public sealed class WebhookDestinationPolicy(IWebhookDnsResolver resolver)
{
    public async Task<ApprovedWebhookDestination> ValidateAsync(Uri endpointUri, bool allowInsecure, bool allowPrivate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpointUri);
        if (endpointUri.Scheme == Uri.UriSchemeHttp && !allowInsecure)
            throw new WebhookDestinationPolicyException("Webhook target is blocked by the outbound target policy: HTTPS is required.");
        if (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp)
            throw new WebhookDestinationPolicyException($"Webhook target is blocked by the outbound target policy: the endpoint scheme '{endpointUri.Scheme}' is not supported; only HTTP and HTTPS are permitted.");
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(endpointUri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            var resolved = await resolver.ResolveAsync(endpointUri.Host, cancellationToken);
            addresses = resolved.ToArray();
            if (addresses.Count == 0)
                throw new WebhookDestinationPolicyException("Webhook target is blocked by the outbound target policy: the endpoint hostname resolved to no addresses.");
        }
        if (!allowPrivate && addresses.Any(IsProhibitedOutboundAddress))
            throw new WebhookDestinationPolicyException("Webhook target is blocked by the outbound target policy: the destination resolved to a prohibited network address.");
        return new(endpointUri, addresses);
    }

    /// <summary>
    /// False only when the address is globally routable unicast space. The prefix tables below are a static
    /// snapshot of the IANA IPv4 and IPv6 special-purpose registries, classified with registry semantics: the
    /// most specific (longest) matching row decides, so a globally reachable refinement carved out of a
    /// covering block is permitted while the rest of the covering block fails closed. IPv6 fails closed by
    /// default: outside the global unicast range 2000::/3 — reserved or otherwise unassigned space — nothing
    /// is permitted except through an explicit permitted row, and inside it every registered non-global
    /// special-purpose block fails closed. IPv4 remains permitted unless a registered non-global block
    /// matches. The IPv4-mapped ::ffff:0:0/96 and deprecated IPv4-compatible ::/96 forms fail closed
    /// regardless of the embedded IPv4 address; the deprecated IPv4-compatible mechanism's routing status is
    /// not that of the embedded destination. The well-known NAT64 prefix 64:ff9b::/96 is registered as
    /// globally reachable translation space and is classified through the embedded IPv4 destination it
    /// translates. The local-use translation prefix 64:ff9b:1::/48 is non-global in its entirety and is
    /// table-driven. The IETF-protocol-assignments block 2001::/23 is refused except for the registry's
    /// globally reachable refinements inside it (2001:1::1, 2001:1::2, 2001:1::3 protocol anycasts,
    /// 2001:3::/32 AMT, 2001:4:112::/48 AS112-v6, 2001:20::/28 ORCHIDv2, 2001:30::/28 DETs).
    /// </summary>
    public static bool IsProhibitedOutboundAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (bytes[0..10].All(x => x == 0) && bytes[10] == 0xff && bytes[11] == 0xff)
                return true;
            if (bytes[0..12].All(x => x == 0))
                return true;
            if (bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
                && bytes[4..12].All(x => x == 0))
                return IsProhibitedOutboundAddress(new IPAddress(bytes[12..]));
            return ClassifyByLongestPrefix(bytes, Ipv6PrefixRules, defaultProhibited: true);
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return ClassifyByLongestPrefix(bytes, Ipv4PrefixRules, defaultProhibited: false);
        return true;
    }

    private static bool ClassifyByLongestPrefix(ReadOnlySpan<byte> address, (byte[] Prefix, int Bits, bool Prohibited)[] rules, bool defaultProhibited)
    {
        var bestBits = -1;
        var prohibited = defaultProhibited;
        foreach (var (prefix, bits, isProhibited) in rules)
        {
            if (bits <= bestBits || !MatchesPrefix(address, prefix, bits)) continue;
            bestBits = bits;
            prohibited = isProhibited;
        }
        return prohibited;
    }

    private static bool MatchesPrefix(ReadOnlySpan<byte> address, ReadOnlySpan<byte> prefix, int prefixBits)
    {
        var fullBytes = prefixBits / 8;
        if (!address[..fullBytes].SequenceEqual(prefix[..fullBytes])) return false;
        var remainderBits = prefixBits % 8;
        if (remainderBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainderBits));
        return (address[fullBytes] & mask) == (prefix[fullBytes] & mask);
    }

    // Static snapshot of the IANA IPv4 special-purpose registry plus multicast. IPv4 classification is
    // permitted by default: the registry rows are the complete set of non-global blocks. Blocks the registry
    // marks globally reachable (AS112-v4 192.31.196.0/24, AMT 192.52.193.0/24, direct-delegation AS112
    // 192.175.48.0/24) match no rule and are permitted; the PCP and TURN anycast /32s inside the IETF
    // protocol-assignments /24 are explicit permitted refinements, as the registry classifies them.
    private static readonly (byte[] Prefix, int Bits, bool Prohibited)[] Ipv4PrefixRules =
    [
        (PrefixBytes("0.0.0.0"), 8, true),        // "This network"
        (PrefixBytes("10.0.0.0"), 8, true),       // Private-use
        (PrefixBytes("100.64.0.0"), 10, true),    // Shared address space (carrier-grade NAT)
        (PrefixBytes("127.0.0.0"), 8, true),      // Loopback
        (PrefixBytes("169.254.0.0"), 16, true),   // Link-local (includes cloud metadata services)
        (PrefixBytes("172.16.0.0"), 12, true),    // Private-use
        (PrefixBytes("192.0.0.0"), 24, true),     // IETF protocol assignments
        (PrefixBytes("192.0.0.9"), 32, false),    // Port Control Protocol anycast (globally reachable refinement)
        (PrefixBytes("192.0.0.10"), 32, false),   // TURN anycast (globally reachable refinement)
        (PrefixBytes("192.0.2.0"), 24, true),     // Documentation (TEST-NET-1)
        (PrefixBytes("192.88.99.0"), 24, true),   // Deprecated (previously 6to4 relay anycast)
        (PrefixBytes("192.168.0.0"), 16, true),   // Private-use
        (PrefixBytes("198.18.0.0"), 15, true),    // Benchmarking
        (PrefixBytes("198.51.100.0"), 24, true),  // Documentation (TEST-NET-2)
        (PrefixBytes("203.0.113.0"), 24, true),   // Documentation (TEST-NET-3)
        (PrefixBytes("224.0.0.0"), 4, true),      // Multicast
        (PrefixBytes("240.0.0.0"), 4, true),      // Reserved (includes limited broadcast)
    ];

    // Static snapshot of the IANA IPv6 special-purpose registry plus multicast, with the registry's globally
    // reachable refinements inside the covering IETF-protocol-assignments /23 recorded as permitted. IPv6
    // classification fails closed by default: the 2000::/3 global unicast base is the ordinary permitted
    // category, and rows are the registered exceptions to it. Rows outside that base (unique-local,
    // link-local, multicast, deprecated translation space) are redundant with the fail-closed default but are
    // kept to document their registry status explicitly. The deprecated 6to4 prefix 2002::/16 is refused
    // outright rather than recursing into its embedded IPv4 destination.
    private static readonly (byte[] Prefix, int Bits, bool Prohibited)[] Ipv6PrefixRules =
    [
        (PrefixBytes("2000::"), 3, false),        // Global unicast base: the ordinary permitted category
        (PrefixBytes("::"), 128, true),           // Unspecified
        (PrefixBytes("::1"), 128, true),          // Loopback
        (PrefixBytes("64:ff9b:1::"), 48, true),   // Local-use IPv4/IPv6 translation
        (PrefixBytes("100::"), 64, true),         // Discard-only
        (PrefixBytes("100:0:0:1::"), 64, true),   // Dummy IPv6 prefix
        (PrefixBytes("2001::"), 23, true),        // IETF protocol assignments (Teredo, unlisted assignments)
        (PrefixBytes("2001:1::1"), 128, false),   // Port Control Protocol anycast (globally reachable refinement)
        (PrefixBytes("2001:1::2"), 128, false),   // TURN anycast (globally reachable refinement)
        (PrefixBytes("2001:1::3"), 128, false),   // DNS-SD service registration anycast (globally reachable refinement)
        (PrefixBytes("2001:2::"), 48, true),      // Benchmarking
        (PrefixBytes("2001:3::"), 32, false),     // AMT (globally reachable refinement)
        (PrefixBytes("2001:4:112::"), 48, false), // AS112-v6 (globally reachable refinement)
        (PrefixBytes("2001:10::"), 28, true),     // ORCHID (deprecated)
        (PrefixBytes("2001:20::"), 28, false),    // ORCHIDv2 (globally reachable refinement)
        (PrefixBytes("2001:30::"), 28, false),    // Drone Remote ID Protocol DETs (globally reachable refinement)
        (PrefixBytes("2001:db8::"), 32, true),    // Documentation
        (PrefixBytes("2002::"), 16, true),        // 6to4 (deprecated)
        (PrefixBytes("3fff::"), 20, true),        // Documentation
        (PrefixBytes("5f00::"), 16, true),        // Segment Routing (SRv6) SIDs
        (PrefixBytes("fc00::"), 7, true),         // Unique-local
        (PrefixBytes("fec0::"), 10, true),        // Site-local (deprecated, refused conservatively)
        (PrefixBytes("fe80::"), 10, true),        // Link-local
        (PrefixBytes("ff00::"), 8, true),         // Multicast
    ];

    private static byte[] PrefixBytes(string prefix) => IPAddress.Parse(prefix).GetAddressBytes();
}

/// <summary>
/// Primary HTTP transport for webhook delivery. Automatic redirects are disabled so a 3xx is returned to the
/// delivery worker as a failed attempt rather than followed. Connection reuse is disabled: a pooled connection
/// is handed to a later request to the same origin without running the connect callback again, so reuse could
/// put a delivery on a socket whose peer address was approved for a different delivery. With a zero pooled
/// connection lifetime every connection is disposed when its response completes instead of pooled, so each
/// delivery's socket is established by the connect callback against that delivery's own approved address set.
/// TLS ALPN is restricted to HTTP/1.1 because an HTTP/2 connection stays available to other requests while in
/// use and is only gated on a coarse age check, which leaves a same-millisecond multiplexing path open; webhook
/// delivery needs no HTTP/2 features. The connect callback pins every socket to an address from the
/// destination policy's approved set carried on the request options; without that set the connection fails
/// closed. The request URI and hostname are untouched, so TLS certificate verification and SNI still validate
/// against the original hostname, never the pinned IP.
/// </summary>
public static class WebhookConnectionTransport
{
    public static readonly HttpRequestOptionsKey<IReadOnlyList<IPAddress>> ApprovedAddressesOption = new("AeroLink.Webhooks.ApprovedAddresses");

    public static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = connectTimeout,
        ConnectCallback = ConnectToApprovedDestinationAsync,
        PooledConnectionLifetime = TimeSpan.Zero,
        SslOptions = new SslClientAuthenticationOptions { ApplicationProtocols = [SslApplicationProtocol.Http11] },
    };

    private static async ValueTask<Stream> ConnectToApprovedDestinationAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(ApprovedAddressesOption, out var approved) || approved.Count == 0)
            throw new HttpRequestException("Webhook connection refused: the request carries no approved destination.");
        var port = context.DnsEndPoint.Port;
        Exception? lastFailure = null;
        foreach (var address in approved)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                socket.Dispose();
                lastFailure = failure;
            }
        }
        throw new HttpRequestException("Webhook connection failed to every approved destination address.", lastFailure);
    }
}
