using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AeroLink.Api.Tests;

/// <summary>
/// Findings from a security review, kept as tests so they cannot come back quietly.
///
/// Each of these was a live defect: a link promised in every notification email that answered 401, responses
/// carrying no protection headers at all, and an upload that believed whatever content type it was told.
/// </summary>
public sealed class SecurityHardeningTests
{
    /// <summary>
    /// The unsubscribe link has to work from a mail client, where the reader has no session.
    ///
    /// It did not. The authentication middleware runs before endpoint routing, so it decides reachability
    /// from a hardcoded path list and cannot see `.AllowAnonymous()` on the endpoint — which the endpoint
    /// had. Every unsubscribe link in every email answered 401 to whoever clicked it.
    /// </summary>
    [Fact]
    public async Task The_unsubscribe_link_is_reachable_without_a_session()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/notifications/unsubscribe?recipient=someone&token=whatever");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("authentication_required", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A forged link must change nothing — and must not reveal whether the account exists, which is why the
    /// answer is worded identically either way.
    /// </summary>
    [Fact]
    public async Task A_forged_unsubscribe_link_changes_nothing_and_reveals_nothing()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();

        using var forged = await client.GetAsync("/api/notifications/unsubscribe?recipient=admin&token=deadbeef");
        using var absent = await client.GetAsync("/api/notifications/unsubscribe?recipient=nobody-at-all&token=deadbeef");

        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        Assert.Equal(await forged.Content.ReadAsStringAsync(), await absent.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Every API response carries protection headers. They matter most on the endpoints that stream a stored
    /// file, where the content type was chosen by whoever uploaded the bytes.
    /// </summary>
    [Fact]
    public async Task Every_response_carries_protection_headers()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");

        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        // An API returns data, never a document; nothing it serves should be able to load or run anything.
        Assert.Contains("default-src 'none'", Single(response, "Content-Security-Policy"));
    }

    /// <summary>
    /// An upload's content type is a claim by whoever sent it, not a fact.
    ///
    /// An inline image is streamed back from this deployment's own origin and referenced from a controlled
    /// requirement, so a file that says PNG and contains markup would be stored, approved, and served to an
    /// approver by us. The signature is checked against the claim.
    /// </summary>
    [Fact]
    public async Task An_upload_that_is_not_the_image_it_claims_to_be_is_refused()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await BootstrapWorkspaceAsync(client);

        using var content = new MultipartFormDataContent { { new StringContent(projectId.ToString()), "projectId" } };
        var payload = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        payload.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(payload, "file", "diagram.png");

        using var response = await client.PostAsync("/api/content/images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not the image type it claims to be", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A real PNG is accepted, so the check refuses forgeries rather than refusing images.</summary>
    [Fact]
    public async Task A_real_image_is_still_accepted()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var projectId = await BootstrapWorkspaceAsync(client);

        using var content = new MultipartFormDataContent { { new StringContent(projectId.ToString()), "projectId" } };
        var payload = new ByteArrayContent(OnePixelPng());
        payload.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(payload, "file", "diagram.png");

        using var response = await client.PostAsync("/api/content/images", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string Single(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.First() : "";

    private static async Task<Guid> BootstrapWorkspaceAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Administrator",
                email = "admin@example.test",
                password = AeroLinkApiFactory.AdministratorPassword,
            }),
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", AeroLinkApiFactory.BootstrapSecret);
        using var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        using var workspace = await client.PostAsJsonAsync("/api/workspaces", new
        {
            programName = "Header Program",
            programCode = "HDR",
            projectName = "Header Project",
            softwareProduct = "Header Product",
            initialRelease = "1.0",
            initialReleaseIsReleased = false,
        });
        Assert.True(workspace.IsSuccessStatusCode, await workspace.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await workspace.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("project").GetProperty("id").GetGuid();
    }

    /// <summary>The smallest valid PNG: signature, IHDR, one IDAT, IEND.</summary>
    private static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00,
        0x90, 0x77, 0x53, 0xDE,
        0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54,
        0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x03, 0x01, 0x01, 0x00,
        0x18, 0xDD, 0x8D, 0xB0,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];
}
