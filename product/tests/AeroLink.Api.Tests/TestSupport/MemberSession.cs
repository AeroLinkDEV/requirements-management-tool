using System.Net;
using System.Net.Http.Json;

namespace AeroLink.Api.Tests;

/// <summary>
/// Signs a seeded member in through the real login endpoint with the shared member password (#1129).
/// </summary>
internal static class MemberSession
{
    /// <summary>Signs in and attaches the CSRF token that a browser sends with every mutation.</summary>
    internal static async Task SignInAsync(HttpClient client, string userName)
    {
        await SignInForReadsAsync(client, userName);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>
    /// Signs in without fetching a CSRF token. This alone does not make mutations fail: the API demands the
    /// token only from requests that carry a browser <c>Origin</c> or <c>Sec-Fetch-Site</c> header, and a test
    /// client sends neither. A test that proves the CSRF refusal must send one of those headers itself.
    /// </summary>
    internal static async Task SignInForReadsAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
