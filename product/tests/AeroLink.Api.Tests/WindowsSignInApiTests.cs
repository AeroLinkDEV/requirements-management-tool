using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AeroLink.Api.Tests;

/// <summary>
/// Windows sign-in (#1615) through the real endpoint, with Negotiate replaced by a test scheme that reads the
/// identity from request headers and the domain controller replaced by an in-memory directory. Everything
/// after authentication — the SID policy, provisioning, default access, the link and the session — is the
/// production path. Real Kerberos/NTLM is qualified manually on a domain-joined host (see the issue).
/// </summary>
public sealed class WindowsSignInApiTests
{
    private const string Domain = "S-1-5-21-1111-2222-3333";
    private const string Scheme = "WindowsTest";

    [Fact]
    public async Task First_sign_in_provisions_on_the_SID_with_Engineer_everywhere_and_later_Programs_and_grants_follow()
    {
        var directory = new FakeDirectory();
        using var host = new WindowsHost(directory);
        Guid existingProgram;
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Existing Program", "EXP"); db.Programs.Add(program); await db.SaveChangesAsync(); existingProgram = program.Id;
        }

        // No Windows identity yet: the endpoint challenges with an untouched Negotiate 401.
        using (var anonymous = host.Client())
        using (var challenge = await anonymous.GetAsync("/api/auth/windows"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.Contains("Negotiate", challenge.Headers.WwwAuthenticate.ToString());
            // A browser that cannot finish Negotiate shows this body: the way to the password break-glass.
            Assert.Contains("href=\"/?windows=password\"", await challenge.Content.ReadAsStringAsync());
        }

        var sid = Domain + "-1104";
        directory.Entries[sid] = new("Jane Doe", "jane.doe@cmc.test");
        using var jane = host.Client();
        // Until first-install setup has created the administrator, nobody is provisioned: an account created now
        // would close the bootstrap with nobody able to administer the installation.
        Assert.Equal("/?windows=refused&code=setup_incomplete", await SignInAsync(jane, sid, @"CMC\jdoe", "/projects"));
        using var admin = host.Client();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);

        Assert.Equal("/projects", await SignInAsync(jane, sid, @"CMC\jdoe", "/projects"));
        var me = await jane.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(@"cmc\jdoe", me.GetProperty("userName").GetString());
        Assert.Equal("Jane Doe", me.GetProperty("displayName").GetString());
        Guid janeId;
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var account = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.WindowsSid == sid);
            janeId = account.Id;
            Assert.Equal(AccountOrigin.Windows, account.Origin);
            Assert.False(account.HasPassword);
            var grant = await db.ProgramMemberships.AsNoTracking().SingleAsync(x => x.UserId == janeId);
            Assert.Equal((existingProgram, ProgramRole.Engineer, "system.windows-provisioning"), (grant.ProgramId, grant.Role, grant.GrantedBy));
            var login = (await db.SecurityAuditEvents.AsNoTracking().Where(x => x.EventType == "Login" && x.ActorId == @"cmc\jdoe" && x.Outcome == "Success").ToListAsync()).Single();
            Assert.Equal(("Success", "windows", @"CMC\jdoe"), (login.Outcome, login.AuthenticationMethod, login.WindowsPrincipal));
            Assert.Contains("(" + Scheme + ")", login.Detail);
        }
        using (var reset = await admin.PostAsJsonAsync($"/api/admin/users/{janeId}/reset-password", new { temporaryPassword = "Temporary-Pass!2026", reason = "test" }))
            Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
        // An image on another page must not sign anybody in, let alone create their account.
        using (var image = new HttpRequestMessage(HttpMethod.Get, "/api/auth/windows"))
        {
            image.Headers.Add(TestWindowsHandler.SidHeader, Domain + "-1999");
            image.Headers.Add(TestWindowsHandler.NameHeader, @"CMC\viewer");
            image.Headers.Add("Sec-Fetch-Dest", "image");
            using var refused = await jane.SendAsync(image);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // The directory renames her: the SID still finds the same account, refreshed, under its original name.
        directory.Entries[sid] = new("Jane Smith", "jane.smith@cmc.test");
        using (var renamed = host.Client())
        {
            await SignInAsync(renamed, sid, @"CMC\jsmith");
            var again = await renamed.GetFromJsonAsync<JsonElement>("/api/auth/me");
            Assert.Equal(janeId, again.GetProperty("id").GetGuid());
            Assert.Equal(@"cmc\jdoe", again.GetProperty("userName").GetString());
            Assert.Equal(("Jane Smith", "jane.smith@cmc.test"), (again.GetProperty("displayName").GetString(), again.GetProperty("email").GetString()));
        }

        // A Program created later grants her Engineer, and the administrator can grant her more.
        using var workspace = await admin.PostAsJsonAsync("/api/workspaces", new
        { programName = "Later Program", programCode = "LTR", projectName = "Later", softwareProduct = "Later Product", initialRelease = "1.0" });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);
        var laterProgram = (await workspace.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("program").GetProperty("id").GetGuid();
        using var grant2 = await admin.PostAsJsonAsync($"/api/admin/users/{janeId}/memberships", new { programId = laterProgram, role = "Administrator" });
        Assert.Equal(HttpStatusCode.NoContent, grant2.StatusCode);
        var roles = (await jane.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("programs").EnumerateArray()
            .Single(x => x.GetProperty("programId").GetGuid() == laterProgram).GetProperty("roles").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Equal(["Administrator", "Engineer"], roles);
    }

    [Theory]
    [InlineData("S-1-5-21-9999-8888-7777-1104", "jane@other.test", "foreign_domain")]
    [InlineData(Domain + "-500", "admin@cmc.test", "builtin_account")]
    [InlineData("S-1-5-18", "system@cmc.test", "builtin_account")]
    [InlineData(Domain + "-1200", "", "no_email")]
    [InlineData(Domain + "-1300", "host01@cmc.test", "machine_account", @"CMC\HOST01$")]
    public async Task Accounts_outside_the_policy_are_refused_and_no_account_is_created(string sid, string email, string code, string principal = @"CMC\somebody")
    {
        var directory = new FakeDirectory();
        directory.Entries[sid] = new("Somebody", email);
        using var host = new WindowsHost(directory);
        using var client = host.Client();
        Assert.Equal($"/?windows=refused&code={code}", await SignInAsync(client, sid, principal));
        using var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var scope = host.Factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserAccounts.AnyAsync(x => x.WindowsSid != null));
    }

    [Fact]
    public async Task Linking_admin_takes_its_password_and_MFA_once_and_then_Windows_sign_in_needs_neither()
    {
        var directory = new FakeDirectory();
        var sid = Domain + "-1105";
        directory.Entries[sid] = new("Sean McCarthy", "sean@cmc.test");
        using var host = new WindowsHost(directory);
        using var admin = host.Client();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        using var enrolled = await admin.PostAsJsonAsync("/api/auth/mfa/enroll", new { });
        var secret = (await enrolled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        using var confirmed = await admin.PostAsJsonAsync("/api/auth/mfa/confirm", new { code = SecurityBoundaryTests.Totp(secret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        using var linking = host.Client();
        Assert.Equal("/?windows=link", await SignInAsync(linking, sid, @"CMC\smccarthy", intent: "link"));
        using (var noSecondFactor = await linking.PostAsJsonAsync("/api/auth/windows/link", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword }))
            Assert.Equal(HttpStatusCode.Unauthorized, noSecondFactor.StatusCode);
        using (var linked = await linking.PostAsJsonAsync("/api/auth/windows/link", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword, mfaCode = SecurityBoundaryTests.Totp(secret, DateTimeOffset.UtcNow) }))
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        // A Windows sign-in does not clear the password's failure count, so it cannot refill a guesser's attempts.
        using (var guesser = host.Client())
        using (var guess = await guesser.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "wrong-password-1" }))
            Assert.Equal(HttpStatusCode.Unauthorized, guess.StatusCode);
        using var later = host.Client();
        await SignInAsync(later, sid, @"CMC\smccarthy");
        var me = await later.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("admin", me.GetProperty("userName").GetString());
        Assert.True(me.GetProperty("isAdministrator").GetBoolean());
        using var scope = host.Factory.Services.CreateScope();
        var account = await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "admin");
        Assert.Equal((AccountOrigin.Local, sid, @"CMC\smccarthy", 1), (account.Origin, account.WindowsSid, account.WindowsPrincipal, account.FailedLoginCount));
    }

    [Fact]
    public async Task A_link_needs_a_settled_password_and_a_free_SID_and_an_administrator_can_remove_it()
    {
        var directory = new FakeDirectory();
        var sid = Domain + "-1106";
        directory.Entries[sid] = new("Bob Builder", "bob@cmc.test");
        using var host = new WindowsHost(directory);
        using var admin = host.Client();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(admin);
        using (var first = host.Client()) await SignInAsync(first, sid, @"CMC\bbuilder"); // provisioned before linking
        using (var created = await admin.PostAsJsonAsync("/api/admin/users", new { userName = "bob", displayName = "Bob", email = "bob@aerolink.test", temporaryPassword = "Temporary-Pass!2026" }))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var linking = host.Client();
        await SignInAsync(linking, sid, @"CMC\bbuilder", intent: "link");
        Assert.Equal(@"CMC\bbuilder", (await linking.GetFromJsonAsync<JsonElement>("/api/auth/windows/link")).GetProperty("principal").GetString());
        async Task<(HttpStatusCode Status, string? Code)> LinkAsync(string password)
        {
            using var response = await linking.PostAsJsonAsync("/api/auth/windows/link", new { userName = "bob", password });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (response.StatusCode, body.TryGetProperty("code", out var code) ? code.GetString() : null);
        }
        // A temporary password is known to whoever issued it; it cannot bind a Windows logon.
        Assert.Equal((HttpStatusCode.Conflict, "password_change_required"), await LinkAsync("Temporary-Pass!2026"));
        using (var bob = host.Client())
        {
            using var signedIn = await bob.PostAsJsonAsync("/api/auth/login", new { userName = "bob", password = "Temporary-Pass!2026" });
            using var changed = await bob.PostAsJsonAsync("/api/auth/password", new { currentPassword = "Temporary-Pass!2026", newPassword = "Settled-Pass!2026" });
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }
        // The SID still signs in to the account it created; only disabling that account frees it.
        Assert.Equal((HttpStatusCode.Conflict, "windows_already_linked"), await LinkAsync("Settled-Pass!2026"));
        Guid provisioned, bobId;
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            provisioned = (await db.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == @"cmc\bbuilder")).Id;
            bobId = (await db.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "bob")).Id;
        }
        using (var disabled = await admin.PostAsJsonAsync($"/api/admin/users/{provisioned}/state", new { enabled = false }))
            Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
        Assert.Equal((HttpStatusCode.OK, null), await LinkAsync("Settled-Pass!2026"));
        Assert.Equal("bob", (await linking.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("userName").GetString());

        // The administrator sees the link and removes it; the sessions it opened end with it.
        var roster = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.Equal(@"CMC\bbuilder", roster.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == bobId).GetProperty("windowsPrincipal").GetString());
        using (var unlinked = await admin.PostAsync($"/api/admin/users/{bobId}/windows/unlink", null))
            Assert.Equal(HttpStatusCode.NoContent, unlinked.StatusCode);
        using (var after = await linking.GetAsync("/api/auth/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [InlineData("/projects?x=1", "/projects?x=1")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example/", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData(null, "/")]
    public void Sign_in_returns_only_to_a_path_on_this_site(string? returnTo, string expected) =>
        Assert.Equal(expected, WindowsSignInEndpoints.SafeReturnTo(returnTo));

    [Fact]
    public void Production_refuses_to_start_Windows_sign_in_on_a_test_scheme()
    {
        using var host = new WindowsHost(new FakeDirectory(), environment: "Production");
        var refused = Assert.Throws<InvalidOperationException>(() => host.Factory.CreateClient());
        Assert.Contains("Negotiate", refused.Message);
    }

    /// <summary>Signs in through the endpoint as the given Windows identity; returns where it redirected.</summary>
    private static async Task<string> SignInAsync(HttpClient client, string sid, string principal, string? returnTo = null, string? intent = null)
    {
        var query = intent is not null ? $"?intent={intent}" : returnTo is not null ? $"?returnTo={Uri.EscapeDataString(returnTo)}" : "";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/windows" + query);
        request.Headers.Add(TestWindowsHandler.SidHeader, sid);
        request.Headers.Add(TestWindowsHandler.NameHeader, principal);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private sealed class WindowsHost : IDisposable
    {
        private readonly AeroLinkApiFactory _base = new();
        public WebApplicationFactory<Program> Factory { get; }

        public WindowsHost(FakeDirectory directory, string environment = "Testing")
        {
            Factory = _base.WithWebHostBuilder(builder =>
            {
                // A test scheme is only permitted outside Production; that refusal is itself under test.
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Identity:Windows:Enabled"] = "true",
                    ["Identity:Windows:Scheme"] = Scheme,
                    ["Identity:Windows:AllowedDomainSids:0"] = Domain,
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestWindowsHandler>(Scheme, _ => { });
                    services.RemoveAll<IWindowsDirectory>();
                    services.AddSingleton<IWindowsDirectory>(directory);
                });
            });
        }

        public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        public void Dispose() { Factory.Dispose(); _base.Dispose(); }
    }

    internal sealed class FakeDirectory : IWindowsDirectory
    {
        public Dictionary<string, WindowsDirectoryEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<WindowsDirectoryEntry?> FindBySidAsync(string domain, string sid, CancellationToken ct) =>
            Task.FromResult(Entries.GetValueOrDefault(sid));
    }

    /// <summary>Stands in for Negotiate: the identity Windows would have proved arrives in two headers.</summary>
    private sealed class TestWindowsHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SidHeader = "X-Test-Windows-Sid";
        public const string NameHeader = "X-Test-Windows-Name";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sid = Request.Headers[SidHeader].ToString();
            if (sid.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, Request.Headers[NameHeader].ToString()), new Claim(ClaimTypes.PrimarySid, sid)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Negotiate";
            return Task.CompletedTask;
        }
    }
}
