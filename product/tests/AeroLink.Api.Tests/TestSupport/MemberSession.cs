using System.Net;
using System.Net.Http.Json;

namespace AeroLink.Api.Tests;

/// <summary>
/// Signs a seeded member in through the real login endpoint with the shared member password (#1129).
/// </summary>
internal static class MemberSession
{
    /// <summary>Signs in and attaches the CSRF token, so the client can also mutate.</summary>
    internal static async Task SignInAsync(HttpClient client, string userName)
    {
        await SignInForReadsAsync(client, userName);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    /// <summary>Signs in without a CSRF token: the client can read, and its mutations are refused.</summary>
    internal static async Task SignInForReadsAsync(HttpClient client, string userName)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
