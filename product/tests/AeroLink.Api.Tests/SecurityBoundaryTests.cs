using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroLink.Api.Tests;

public sealed class SecurityBoundaryTests
{
    [Fact]
    public async Task Mfa_enrollment_returns_interoperable_uri_protects_secret_and_cannot_downgrade_confirmed_factor()
    {
        using var factory = new AeroLinkApiFactory(); using var client = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(client);
        using var enrolled = await client.PostAsJsonAsync("/api/auth/mfa/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode); var payload = await enrolled.Content.ReadFromJsonAsync<JsonElement>();
        var secret = payload.GetProperty("secret").GetString()!; var uri = payload.GetProperty("otpauthUri").GetString()!;
        Assert.Equal(32, secret.Length); Assert.StartsWith("otpauth://totp/AeroLink%3Aadmin?secret=", uri); Assert.Contains("issuer=AeroLink", uri);
        using (var scope=factory.Services.CreateScope())
        {
            var stored=await scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>().UserMfaEnrollments.AsNoTracking().SingleAsync();
            Assert.StartsWith("dp:v1:",stored.Secret); Assert.DoesNotContain(secret,stored.Secret);
        }
        var code=Totp(secret,DateTimeOffset.UtcNow);using var confirmed=await client.PostAsJsonAsync("/api/auth/mfa/confirm",new{code});Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        var recovery=(await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes");Assert.Equal(10,recovery.GetArrayLength());
        var status=await client.GetFromJsonAsync<JsonElement>("/api/auth/security");Assert.True(status.GetProperty("mfaEnabled").GetBoolean());Assert.Equal(10,status.GetProperty("recoveryCodesRemaining").GetInt32());
        using var repeated=await client.PostAsJsonAsync("/api/auth/mfa/enroll",new{});Assert.Equal(HttpStatusCode.Conflict,repeated.StatusCode);
        using var badDisable=await client.PostAsJsonAsync("/api/auth/mfa/disable",new{password=AeroLinkApiFactory.AdministratorPassword,code="000000"});Assert.Equal(HttpStatusCode.Unauthorized,badDisable.StatusCode);
        using var disabled=await client.PostAsJsonAsync("/api/auth/mfa/disable",new{password=AeroLinkApiFactory.AdministratorPassword,code=Totp(secret,DateTimeOffset.UtcNow)});Assert.Equal(HttpStatusCode.NoContent,disabled.StatusCode);
        status=await client.GetFromJsonAsync<JsonElement>("/api/auth/security");Assert.False(status.GetProperty("mfaEnabled").GetBoolean());Assert.Equal(0,status.GetProperty("recoveryCodesRemaining").GetInt32());
    }

    [Fact]
    public async Task Empty_database_bootstrap_requires_secret_runs_once_and_creates_login()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(status.GetProperty("bootstrapRequired").GetBoolean());
        Assert.True(status.GetProperty("bootstrapEnabled").GetBoolean());

        using var denied = await BootstrapAsync(client, "incorrect-bootstrap-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var weakPassword = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret, "too-weak");
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);

        using var created = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var administrator = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(IdentityService.SystemAdministratorUserName, administrator.GetProperty("userName").GetString());

        using var repeated = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var completed = await client.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.False(completed.GetProperty("bootstrapRequired").GetBoolean());
        Assert.False(completed.GetProperty("bootstrapEnabled").GetBoolean());
    }

    [Fact]
    public void Production_rejects_demo_identity_seeding_without_explicit_override()
    {
        using var factory = new AeroLinkApiFactory(seedDemoAccounts: true);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Demo identity seeding is disabled outside Development", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Program_member_without_control_roles_cannot_mutate_baseline_or_release_package()
    {
        using var factory = new AeroLinkApiFactory();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        Guid baselineId;
        Guid campaignId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Security Boundary Program", "SBP");
            var project = new ProjectRecord(program.Id, "Security Boundary Project", "Boundary Product");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var baseline = new CandidateBaseline("SW-90.10", 0, project.Id, release.Id, null, "Security candidate", "configuration.manager", now);
            var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "Security campaign", "program.manager", now);
            var member = new UserAccount("program.engineer", "Program Engineer", "program.engineer@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            db.AddRange(program, project, release, baseline, campaign, member, new ProgramMembership(member.Id, program.Id, ProgramRole.Engineer, "test.setup", now));
            await db.SaveChangesAsync();
            baselineId = baseline.Id;
            campaignId = campaign.Id;
        }

        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "program.engineer", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AuthorizeMutationsAsync(client);

        var responses = new List<HttpResponseMessage>
        {
            await client.DeleteAsync($"/api/baselines/{baselineId}/selections/{Guid.NewGuid()}"),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/start-verification", new { }),
            await client.PutAsJsonAsync($"/api/release-campaigns/{campaignId}/impact-dispositions", new { scrId = (Guid?)null, state = "Addressed", rationale = "Unauthorized.", actorId = "ignored" }),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/reconcile-lifecycle-links", new { actorId = "ignored" }),
            await client.PostAsync($"/api/release-campaigns/{campaignId}/verification-package", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
            await client.PostAsJsonAsync($"/api/release-campaigns/{campaignId}/verification-build", new { softwareBuildId = Guid.NewGuid(), actorId = "ignored" })
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Disabling_account_revokes_session_so_reenable_does_not_resurrect_cookie()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);

        using var created = await administrator.PostAsJsonAsync("/api/admin/users", new
        {
            userName = "session.user",
            displayName = "Session User",
            email = "session.user@example.test",
            temporaryPassword = AeroLinkApiFactory.MemberPassword
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var userId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var user = factory.CreateClient();
        using var login = await user.PostAsJsonAsync("/api/auth/login", new { userName = "session.user", password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync($"/api/admin/users/{userId}/state", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await administrator.PostAsJsonAsync($"/api/admin/users/{userId}/state", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Administrator_can_revoke_membership_and_delegation_with_audit_state_retained()
    {
        using var factory = new AeroLinkApiFactory();
        using var administrator = factory.CreateClient();
        await BootstrapAndLoginAdministratorAsync(administrator);
        var now = DateTimeOffset.UtcNow;
        Guid programId;
        Guid delegatorId;
        Guid delegateId;
        Guid delegationId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = new ProgramRecord("Authority Revocation Program", "ARP");
            var delegator = new UserAccount("delegator.user", "Delegator User", "delegator@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegateUser = new UserAccount("delegate.user", "Delegate User", "delegate@example.test", IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
            var delegatorMembership = new ProgramMembership(delegator.Id, program.Id, ProgramRole.Engineer, "test.setup", now);
            var delegateMembership = new ProgramMembership(delegateUser.Id, program.Id, ProgramRole.Engineer, "test.setup", now);
            var delegation = new RoleDelegation(program.Id, delegator.Id, delegateUser.Id, ProgramRole.Engineer, now.AddMinutes(-1), now.AddHours(1), "Temporary engineering coverage.", "test.setup", now);
            db.AddRange(program, delegator, delegateUser, delegatorMembership, delegateMembership, delegation);
            await db.SaveChangesAsync();
            programId = program.Id;
            delegatorId = delegator.Id;
            delegateId = delegateUser.Id;
            delegationId = delegation.Id;
        }

        using var membership = await administrator.DeleteAsync($"/api/admin/users/{delegateId}/memberships/{programId}/Engineer");
        Assert.Equal(HttpStatusCode.NoContent, membership.StatusCode);
        using var delegationResponse = await administrator.DeleteAsync($"/api/delegations/{delegationId}");
        Assert.Equal(HttpStatusCode.NoContent, delegationResponse.StatusCode);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        Assert.False(await verificationDb.ProgramMemberships.AnyAsync(x => x.UserId == delegateId && x.ProgramId == programId && x.Role == ProgramRole.Engineer));
        Assert.NotNull((await verificationDb.RoleDelegations.AsNoTracking().SingleAsync(x => x.Id == delegationId)).RevokedAt);
        Assert.True(await verificationDb.SecurityAuditEvents.CountAsync(x => x.EventType == "RoleRevoked" || x.EventType == "DelegationRevoked") >= 2);
        Assert.NotEqual(Guid.Empty, delegatorId);
    }

    private static async Task<HttpResponseMessage> BootstrapAsync(HttpClient client, string secret, string password = AeroLinkApiFactory.AdministratorPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/setup/bootstrap")
        {
            Content = JsonContent.Create(new
            {
                displayName = "AeroLink Administrator",
                email = "admin@example.test",
                password
            })
        };
        request.Headers.Add("X-AeroLink-Bootstrap-Secret", secret);
        return await client.SendAsync(request);
    }

    internal static async Task BootstrapAndLoginAdministratorAsync(HttpClient client)
    {
        using var bootstrap = await BootstrapAsync(client, AeroLinkApiFactory.BootstrapSecret);
        Assert.Equal(HttpStatusCode.Created, bootstrap.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AeroLinkApiFactory.AdministratorPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await AuthorizeMutationsAsync(client);
    }

    internal static async Task AuthorizeMutationsAsync(HttpClient client)
    {
        var response=await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-AeroLink-CSRF");
        client.DefaultRequestHeaders.Add("X-AeroLink-CSRF",response.GetProperty("token").GetString());
    }

    private static string Totp(string secret,DateTimeOffset now)
    {
        const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var bytes=new List<byte>();var buffer=0;var bits=0;
        foreach(var ch in secret){buffer=(buffer<<5)|alphabet.IndexOf(ch);bits+=5;if(bits>=8){bits-=8;bytes.Add((byte)((buffer>>bits)&255));}}
        var counter=BitConverter.GetBytes(now.ToUnixTimeSeconds()/30);if(BitConverter.IsLittleEndian)Array.Reverse(counter);using var hmac=new System.Security.Cryptography.HMACSHA1(bytes.ToArray());var hash=hmac.ComputeHash(counter);var offset=hash[^1]&15;var value=((hash[offset]&127)<<24)|(hash[offset+1]<<16)|(hash[offset+2]<<8)|hash[offset+3];return(value%1_000_000).ToString("D6");
    }
}

internal sealed class AeroLinkApiFactory(bool seedDemoAccounts = false, bool allowDemoAccounts = false,
    string? showcaseTemplate = null) : WebApplicationFactory<Program>
{
    public const string BootstrapSecret = "test-bootstrap-secret-0123456789-abcdef";
    public const string AdministratorPassword = "Bootstrap-Admin!2026";
    public const string MemberPassword = "Program-Member!2026";
    private readonly string _databasePath = NewDatabase(showcaseTemplate);

    /// <summary>
    /// A private database file, optionally starting as a copy of an already-seeded showcase.
    ///
    /// Three tests in this assembly seeded the FMS showcase inside their own factory, which is 40 to 60 seconds
    /// each and was 177 of the assembly's 552 CPU-seconds for a dataset identical all three times. The copy
    /// happens before the host starts, so the API opens a database that is already populated and its startup
    /// EnsureCreated finds nothing to do.
    /// </summary>
    private static string NewDatabase(string? template)
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-api-tests-{Guid.NewGuid():N}.db");
        if (template is not null) File.Copy(template, path);
        return path;
    }

    private readonly string _evidenceRoot = Path.Combine(Path.GetTempPath(), $"aerolink-api-evidence-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseContentRoot(FindApiContentRoot());
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:AeroLink"] = $"Data Source={_databasePath}",
            ["Evidence:Root"] = _evidenceRoot,
            ["DemoData:Enabled"] = "false",
            ["Identity:SeedDemoAccounts"] = seedDemoAccounts.ToString(),
            ["Identity:AllowDemoAccounts"] = allowDemoAccounts.ToString(),
            ["Identity:BootstrapSecret"] = BootstrapSecret,
            ["Identity:CookieSecure"] = "false",
            ["Identity:LoginRateLimitPerMinute"] = "500",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AeroLinkDbContext>();
            services.RemoveAll<DbContextOptions<AeroLinkDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AeroLinkDbContext>>();
            services.AddDbContext<AeroLinkDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Pooling=False"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        DeleteIfPresent(_databasePath);
        DeleteIfPresent(_databasePath + "-shm");
        DeleteIfPresent(_databasePath + "-wal");
        if (Directory.Exists(_evidenceRoot)) Directory.Delete(_evidenceRoot, true);
    }

    private static void DeleteIfPresent(string path) { if (File.Exists(path)) File.Delete(path); }

    private static string FindApiContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx"))) current = current.Parent;
        if (current is null) throw new InvalidOperationException("Could not locate the product solution root for API tests.");
        return Path.Combine(current.FullName, "src", "AeroLink.Api");
    }
}
