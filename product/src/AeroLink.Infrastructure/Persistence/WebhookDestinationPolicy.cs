using System.Net;
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
        if (endpointUri.Scheme != Uri.UriSchemeHttps && !allowInsecure)
            throw new WebhookDestinationPolicyException("Webhook target is blocked by the outbound target policy: HTTPS is required.");
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

    /// <summary>False only when the address is globally routable unicast space.</summary>
    public static bool IsProhibitedOutboundAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && bytes[0..10].All(x => x == 0) && bytes[10] == 0xff && bytes[11] == 0xff)
            return IsProhibitedOutboundAddress(new IPAddress(bytes[12..]));
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19 || (bytes[1] == 51 && bytes[2] == 100)))
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return true;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo) return true;
        if (bytes.All(x => x == 0)) return true;
        if (bytes[0] == 0xff) return true;
        if ((bytes[0] & 0xfe) == 0xfc) return true;
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return true;
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return true;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] | bytes[3]) == 0) return true;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) return true;
        if (bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes[4..11].All(x => x == 0))
            return IsProhibitedOutboundAddress(new IPAddress(bytes[12..]));
        if (bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4] == 0 && bytes[5] == 1
            && bytes[6..11].All(x => x == 0))
            return IsProhibitedOutboundAddress(new IPAddress(bytes[12..]));
        if (bytes[0..11].All(x => x == 0)) return IsProhibitedOutboundAddress(new IPAddress(bytes[12..]));
        return false;
    }
}

/// <summary>
/// Primary HTTP transport for webhook delivery. Automatic redirects are disabled so a 3xx is returned to the
/// delivery worker as a failed attempt rather than followed. The connect callback pins every socket to an
/// address from the destination policy's approved set carried on the request options; without that set the
/// connection fails closed. The request URI and hostname are untouched, so TLS certificate verification and
/// SNI still validate against the original hostname, never the pinned IP.
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
