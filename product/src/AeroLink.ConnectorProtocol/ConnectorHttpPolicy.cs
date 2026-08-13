namespace AeroLink.ConnectorProtocol;

public static class ConnectorHttpPolicy
{
    public static HttpClient CreateClient(Uri enrolledOrigin, TimeSpan timeout)
    {
        var normalized = new Uri(ConnectorLaunchProtocol.NormalizeOrigin(enrolledOrigin.GetLeftPart(UriPartial.Authority), allowInsecureLoopback: true));
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true)
        {
            BaseAddress = normalized,
            Timeout = timeout
        };
    }

    public static void ValidateResponse(HttpResponseMessage response, Uri enrolledOrigin)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new ConnectorProtocolException("connector_redirect_refused", "AeroLink connector endpoints may not redirect.");
        var final = response.RequestMessage?.RequestUri;
        if (final is null) return;
        var expected = ConnectorLaunchProtocol.NormalizeOrigin(enrolledOrigin.GetLeftPart(UriPartial.Authority), allowInsecureLoopback: true);
        var actual = ConnectorLaunchProtocol.NormalizeOrigin(final.GetLeftPart(UriPartial.Authority), allowInsecureLoopback: true);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new ConnectorProtocolException("connector_origin_mismatch", "The connector response came from a different origin.");
    }
}
