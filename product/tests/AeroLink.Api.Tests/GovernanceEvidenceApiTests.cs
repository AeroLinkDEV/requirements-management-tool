using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

public sealed class GovernanceEvidenceApiTests
{
    [Fact]
    public async Task Operations_only_claims_production_qualification_when_retained_run_meets_both_targets()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();await BootstrapAsync(client);var projectId=await CreateProjectAsync(factory,"Operations","OPS");var hash=new string('a',64);
        var partial=await Post(client,"/api/operations/qualification-runs",new{projectId,environment="staging",requirementCount=50_000,concurrentUsers=25,durationMinutes=30,resultsJson="{\"allPassed\":true}",reportHash=hash,allPassed=true});Assert.False(partial.GetProperty("meetsProductionTarget").GetBoolean());
        var before=await client.GetFromJsonAsync<JsonElement>($"/api/operations/overview?projectId={projectId}");Assert.False(before.GetProperty("qualification").GetProperty("qualified").GetBoolean());
        var complete=await Post(client,"/api/operations/qualification-runs",new{projectId,environment="production-like",requirementCount=50_000,concurrentUsers=150,durationMinutes=120,resultsJson="{\"allPassed\":true,\"p95Ms\":180}",reportHash=new string('b',64),allPassed=true});Assert.True(complete.GetProperty("meetsProductionTarget").GetBoolean());
        await Post(client,"/api/operations/restore-drills",new{projectId,backupLocation="offsite://vault/backup-001",backupHash=new string('c',64),backupCreatedAt=DateTimeOffset.UtcNow.AddMinutes(-20),offsiteVerifiedAt=DateTimeOffset.UtcNow.AddMinutes(-19),targetRpoMinutes=60,targetRtoMinutes=30,actualRpoMinutes=20,actualRtoMinutes=12,restoreEnvironment="isolated-restore",evidenceHash=new string('d',64)});
        var after=await client.GetFromJsonAsync<JsonElement>($"/api/operations/overview?projectId={projectId}");Assert.True(after.GetProperty("qualification").GetProperty("qualified").GetBoolean());Assert.Equal(150,after.GetProperty("qualification").GetProperty("evidence").GetProperty("concurrentUsers").GetInt32());Assert.Equal("Passed",after.GetProperty("restoreAssurance")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Verification_coverage_metric_never_counts_another_program()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();Guid allowedProjectId;
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var now=DateTimeOffset.UtcNow;var allowedProgram=new ProgramRecord("Allowed","ALW");var deniedProgram=new ProgramRecord("Denied","DNY");var allowedProject=new ProjectRecord(allowedProgram.Id,"Allowed project","FMS");var deniedProject=new ProjectRecord(deniedProgram.Id,"Denied project","FMS");var user=new UserAccount("metric.user","Metric User","metric@example.test",IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword),now);db.AddRange(allowedProgram,deniedProgram,allowedProject,deniedProject,user,new ProgramMembership(user.Id,allowedProgram.Id,ProgramRole.Engineer,"test",now));
            var sequence=0;foreach(var project in new[]{allowedProject,deniedProject}){sequence++;var release=new SoftwareRelease(project.Id,"1.0",false);var baseline=new CandidateBaseline($"SW-09.{sequence}0",0,project.Id,release.Id,null,"Metric test software build","test",now);var scr=new SystemChangeRequest($"SRCR-0900{sequence}",0,project.Id,release.Id,"Metric test change","Problem","Analysis","Solution","test",now);var artifact=new RequirementArtifact(project.Id,$"SYSR-09000{sequence}",RequirementLevel.System,now);var revision=new RequirementRevision(artifact.Id,0,"The system shall remain scoped.","Isolation","Test",RequirementRevisionState.Active,scr.Id,baseline.Id,now);var procedure=new TestProcedure(project.Id,$"SYSTP-09000{sequence}","Scope test","test",now,TestProcedureLevel.System);var procedureRevision=new TestProcedureRevision(procedure.Id,0,"Verify scope.","Prepared","Execute","Pass",TestProcedureState.Approved,"test",now);db.AddRange(release,baseline,scr,artifact,revision,procedure,procedureRevision,new TestRequirementCoverage(procedureRevision.Id,revision.Id));}await db.SaveChangesAsync();allowedProjectId=allowedProject.Id;
        }
        using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="metric.user",password=AeroLinkApiFactory.MemberPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        var response=await client.GetFromJsonAsync<JsonElement>($"/api/quality/metric-contracts?projectId={allowedProjectId}");var coverage=response.GetProperty("contracts").EnumerateArray().Single(x=>x.GetProperty("key").GetString()=="verification_coverage");Assert.Equal(1,coverage.GetProperty("value").GetInt32());Assert.True(response.GetProperty("scope").GetProperty("permissionSafe").GetBoolean());
    }

    [Fact]
    public async Task Quality_portfolio_retains_objectives_attributable_waivers_evidence_and_controlled_export()
    {
        using var factory=new AeroLinkApiFactory();using var client=factory.CreateClient();await BootstrapAsync(client);var projectId=await CreateProjectAsync(factory,"Quality","QLT");var blockerId=Guid.NewGuid();var artifactId=Guid.NewGuid();var evidenceHash=new string('e',64);
        await Post(client,"/api/quality/objectives",new{projectId,code="OBJ-001",title="Requirements verification",targetJson="{\"coveragePercent\":100}",evidenceExpectation="Approved verification evidence for every allocated requirement."});
        await Post(client,"/api/quality/waivers",new{projectId,blockerType="VerificationGap",blockerId,rationale="Independent laboratory evidence is scheduled and release authority accepted the bounded interval.",approvedBy="release.authority",expiresAt=DateTimeOffset.UtcNow.AddDays(14)});
        await Post(client,"/api/quality/evidence-index",new{projectId,objectiveCode="OBJ-001",artifactType="VerificationResult",artifactId,evidenceHash,claimBoundary="Indexed lifecycle evidence only; no certification claim."});
        var portfolio=await client.GetFromJsonAsync<JsonElement>($"/api/quality/portfolio?projectId={projectId}");Assert.Contains("does not make certification claims",portfolio.GetProperty("claimBoundary").GetString());Assert.Equal(1,portfolio.GetProperty("summary").GetProperty("objectives").GetInt32());Assert.Equal(1,portfolio.GetProperty("summary").GetProperty("activeWaivers").GetInt32());Assert.Equal("admin",portfolio.GetProperty("waivers")[0].GetProperty("approvedBy").GetString());Assert.NotEqual("release.authority",portfolio.GetProperty("waivers")[0].GetProperty("approvedBy").GetString());Assert.Equal(evidenceHash,portfolio.GetProperty("evidence")[0].GetProperty("evidenceHash").GetString());
        var export=await Post(client,"/api/quality/exports",new{projectId,idempotencyKey="quality-portfolio-proof"});Assert.Equal(64,export.GetProperty("sha256").GetString()!.Length);using var download=await client.GetAsync(export.GetProperty("downloadUrl").GetString());Assert.Equal(HttpStatusCode.OK,download.StatusCode);Assert.Contains("no certification claim",await download.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quality_authority_requires_the_configuration_manager_position_not_only_its_base_role()
    {
        using var factory = new AeroLinkApiFactory();
        Guid projectId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var now = DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Quality position authority", $"QPA{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Quality Position Authority", "FMS");
            var baseOnly = Member("quality.cm.base", now);
            var primary = Member("quality.cm.primary", now);
            var backup = Member("quality.cm.backup", now);
            db.AddRange(program, project, baseOnly, primary, backup,
                new ProgramMembership(baseOnly.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProgramMembership(primary.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProgramMembership(backup.Id, program.Id, ProgramRole.ConfigurationManager, "test.setup", now),
                new ProjectLeadershipAssignment(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    primary.Id, "test.setup", now),
                new ProjectLeadershipBackup(program.Id, ProjectLeadershipPosition.ConfigurationManager,
                    backup.Id, "test.setup", now));
            await db.SaveChangesAsync();
            projectId = project.Id;
        }

        using var baseClient = factory.CreateClient(); await LoginMemberAsync(baseClient, "quality.cm.base");
        using var primaryClient = factory.CreateClient(); await LoginMemberAsync(primaryClient, "quality.cm.primary");
        using var backupClient = factory.CreateClient(); await LoginMemberAsync(backupClient, "quality.cm.backup");

        using (var refused = await baseClient.PostAsJsonAsync("/api/quality/waivers", new
               {
                   projectId, blockerType = "VerificationGap", blockerId = Guid.NewGuid(),
                   rationale = "A base role is not the accountable position.", expiresAt = DateTimeOffset.UtcNow.AddDays(1)
               }))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        using (var accepted = await primaryClient.PostAsJsonAsync("/api/quality/waivers", new
               {
                   projectId, blockerType = "VerificationGap", blockerId = Guid.NewGuid(),
                   rationale = "The accountable primary approved the bounded interval.", expiresAt = DateTimeOffset.UtcNow.AddDays(1)
               }))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            Assert.Equal("ConfigurationManager",
                (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalAuthority").GetString());
        }

        using (var accepted = await backupClient.PostAsJsonAsync("/api/quality/waivers", new
               {
                   projectId, blockerType = "VerificationGap", blockerId = Guid.NewGuid(),
                   rationale = "The standing backup approved the bounded interval.", expiresAt = DateTimeOffset.UtcNow.AddDays(1)
               }))
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using (var refused = await baseClient.PostAsJsonAsync("/api/quality/objectives", new
               { projectId, code = "BASE-REFUSED", title = "Refused", targetJson = "{}", evidenceExpectation = "None" }))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using (var accepted = await backupClient.PostAsJsonAsync("/api/quality/objectives", new
               { projectId, code = "BACKUP-ACCEPTED", title = "Accepted", targetJson = "{}", evidenceExpectation = "Evidence" }))
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task<Guid> CreateProjectAsync(AeroLinkApiFactory factory,string name,string code){using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();var program=new ProgramRecord(name,code);var project=new ProjectRecord(program.Id,$"{name} Project","FMS");db.AddRange(program,project);await db.SaveChangesAsync();return project.Id;}
    private static async Task BootstrapAsync(HttpClient client){using var request=new HttpRequestMessage(HttpMethod.Post,"/api/setup/bootstrap"){Content=JsonContent.Create(new{displayName="Administrator",email="admin@example.test",password=AeroLinkApiFactory.AdministratorPassword})};request.Headers.Add("X-AeroLink-Bootstrap-Secret",AeroLinkApiFactory.BootstrapSecret);using var created=await client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,created.StatusCode);using var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password=AeroLinkApiFactory.AdministratorPassword});Assert.Equal(HttpStatusCode.OK,login.StatusCode);await SecurityBoundaryTests.AuthorizeMutationsAsync(client);}
    private static async Task<JsonElement> Post(HttpClient client,string url,object body){using var response=await client.PostAsJsonAsync(url,body);var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{url} returned {(int)response.StatusCode}: {text}");return JsonDocument.Parse(text).RootElement.Clone();}
    private static UserAccount Member(string userName,DateTimeOffset now)=>new(userName,userName,$"{userName}@example.test",IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword),now);
    private static async Task LoginMemberAsync(HttpClient client,string userName){using var response=await client.PostAsJsonAsync("/api/auth/login",new{userName,password=AeroLinkApiFactory.MemberPassword});Assert.Equal(HttpStatusCode.OK,response.StatusCode);await SecurityBoundaryTests.AuthorizeMutationsAsync(client);}
}
