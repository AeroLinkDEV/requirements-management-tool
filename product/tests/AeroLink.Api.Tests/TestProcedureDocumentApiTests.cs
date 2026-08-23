using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroLink.Api.Tests;

/// <summary>
/// The documents a project's procedures are written into, as the Explorer's rail reads them.
///
/// The Requirements Explorer groups by the requirements document a requirement belongs to. The Test Procedure
/// Explorer had nothing to group by until procedures were given a container; this is what the rail asks for.
/// </summary>
public sealed class TestProcedureDocumentApiTests : IClassFixture<SharedApiHost>
{
    private readonly SharedApiHost _host;

    public TestProcedureDocumentApiTests(SharedApiHost host)
    {
        _host = host;
    }

    private sealed record Seeded(Guid ProjectId, Guid ProgramId, Guid HighLevelProcedureId, string MemberName);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        // Unique per test: user accounts and Program codes are globally unique-constrained, so a shared
        // host/database requires per-test identities. Procedure numbers are project-scoped and stay fixed.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var member = $"document.reader.{tag}";
        var program = new ProgramRecord($"Rail Program {tag}", $"RAIL{tag}");
        var project = new ProjectRecord(program.Id, "Flight Software", "Rail Software");
        db.AddRange(program, project);

        var high = new TestProcedure(project.Id, "HLRTC-000501", "Verify flight plan behaviour", "test.engineer", now,
            TestProcedureLevel.HighLevel);
        var low = new TestProcedure(project.Id, "LLRTC-000501", "Verify checksum recovery", "test.engineer", now,
            TestProcedureLevel.LowLevel);
        db.AddRange(high, low);

        var account = new UserAccount(member, member, $"{member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        // The bootstrap runs at startup for every project; a project created afterwards is placed explicitly.
        await scope.ServiceProvider.GetRequiredService<TestProcedureDocumentBootstrap>()
            .EnsureForProjectAsync(project.Id);
        await db.SaveChangesAsync();

        return new(project.Id, program.Id, high.Id, member);
    }

    private static async Task LoginAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed record DocumentRow(Guid Id, string DocumentNumber, string Title, string Level,
        string Description, int ArtifactCount, int ProcedureCount, SectionRow[] Sections);
    private sealed record SectionRow(Guid Id, string Heading, int Position, int ArtifactCount, int ProcedureCount);

    [Fact]
    public async Task A_discipline_sees_only_its_own_document()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await LoginAsync(client, seeded.MemberName);

        var response = await client.GetAsync(
            $"/api/projects/{seeded.ProjectId}/test-procedure-documents?scope=HighLevelSoftware");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var documents = (await response.Content.ReadFromJsonAsync<DocumentRow[]>())!;

        // The HLR Explorer speaks for one level, exactly as the requirements side does. The number runs
        // across the installation rather than within the project, so the acronym is the assertion — a
        // project's document is not necessarily the first one of its kind.
        var document = Assert.Single(documents);
        Assert.StartsWith("HLRTD-", document.DocumentNumber);
        Assert.Equal("HighLevel", document.Level);
        Assert.Equal(1, document.ProcedureCount);
        var section = Assert.Single(document.Sections);
        Assert.Equal(1, section.ProcedureCount);
    }

    [Fact]
    public async Task Canonical_case_documents_route_is_software_only_and_uses_artifact_counts()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await LoginAsync(client, seeded.MemberName);

        var documents = await client.GetFromJsonAsync<DocumentRow[]>(
            $"/api/projects/{seeded.ProjectId}/test-case-documents");

        Assert.NotNull(documents);
        Assert.Equal(2, documents!.Length);
        Assert.DoesNotContain(documents, document => document.Level == "System");
        Assert.All(documents, document => Assert.Equal(document.ProcedureCount, document.ArtifactCount));
        Assert.All(documents.SelectMany(document => document.Sections), section =>
            Assert.Equal(section.ProcedureCount, section.ArtifactCount));
    }

    [Fact]
    public async Task Every_document_is_returned_when_no_scope_is_named()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await LoginAsync(client, seeded.MemberName);

        var documents = (await (await client.GetAsync(
            $"/api/projects/{seeded.ProjectId}/test-procedure-documents"))
            .Content.ReadFromJsonAsync<DocumentRow[]>())!;

        Assert.Equal(3, documents.Length);
        Assert.Contains(documents, x => x.DocumentNumber.StartsWith("SYSTD-"));
        Assert.Contains(documents, x => x.DocumentNumber.StartsWith("HLRTD-"));
        Assert.Contains(documents, x => x.DocumentNumber.StartsWith("LLRTD-"));
    }

    /// <summary>Picking a document narrows the list, which is the whole point of the rail.</summary>
    [Fact]
    public async Task Filtering_procedures_by_document_returns_only_that_documents_procedures()
    {
        var seeded = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await LoginAsync(client, seeded.MemberName);

        var documents = (await (await client.GetAsync(
            $"/api/projects/{seeded.ProjectId}/test-procedure-documents?scope=HighLevelSoftware"))
            .Content.ReadFromJsonAsync<DocumentRow[]>())!;
        var highLevel = Assert.Single(documents);

        var listed = await client.GetFromJsonAsync<ProcedurePage>(
            $"/api/test-procedures?projectId={seeded.ProjectId}&documentId={highLevel.Id}&pageSize=50");

        Assert.NotNull(listed);
        var only = Assert.Single(listed!.Items);
        Assert.StartsWith("HLRTC-000501", only.DisplayNumber);
    }

    /// <summary>
    /// The startup bootstrap cannot help a Project created after it ran, and a Project whose documents appear
    /// only at the next restart is a Project whose Explorer rail is empty for the person who just made it.
    /// </summary>
    [Fact]
    public async Task A_project_created_through_the_api_has_its_documents_immediately()
    {
        // First-install bootstrap requires a database with no user accounts yet, so this test keeps its own
        // fresh factory: the shared host's database already contains the other tests' seeded users.
        await using var factory = new AeroLinkApiFactory(attachProjectLadders: false);
        using var client = factory.CreateClient();
        await SecurityBoundaryTests.BootstrapAndLoginAdministratorAsync(client);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);

        var created = await client.PostAsJsonAsync("/api/workspaces", new
        {
            programName = "Late Program",
            programCode = "LATE",
            projectName = "Late Project",
            softwareProduct = "Late Software",
            initialRelease = "1.0",
            initialReleaseIsReleased = false,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var workspace = await created.Content.ReadFromJsonAsync<CreatedWorkspace>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var ladder = await db.ProjectLadderConfigurations
                .Include(x => x.Steps).Include(x => x.AllowedUpstream)
                .SingleAsync(x => x.ProjectId == workspace!.Project.Id);
            var resolved = ProjectLadderResolver.Resolve(ladder);
            Assert.True(resolved.AgreesWithLegacyDefault());
            Assert.Equal(ProjectLadderConfigurationClassification.LegacyDefault, ladder.Classification);
            Assert.Equal(ProjectLadderConfigurationState.Stored, ladder.State);
            Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel],
                resolved.Steps.Select(x => x.Level));
            Assert.Equal([7, 7, 15], resolved.Steps.Select(x => (int)x.Capabilities));
            Assert.Equal(2, ladder.AllowedUpstream.Count);
            Assert.DoesNotContain(await db.ProjectLadderConfigurations.AsNoTracking()
                .Where(x => x.ProjectId == workspace!.Project.Id)
                .Select(x => new { x.Classification, x.State })
                .ToListAsync(), x => x.Classification != ProjectLadderConfigurationClassification.LegacyDefault
                    || x.State != ProjectLadderConfigurationState.Stored);
        }

        var documents = (await (await client.GetAsync(
            $"/api/projects/{workspace!.Project.Id}/test-procedure-documents"))
            .Content.ReadFromJsonAsync<DocumentRow[]>())!;

        // No restart in between.
        Assert.Equal(3, documents.Length);
        Assert.All(documents, document => Assert.Single(document.Sections));
    }

    /// <summary>
    /// Two documents answering to the same number would make a reference ambiguous, so the number runs
    /// across the installation. A second project's HLR document is HLRTD-000002, not a second HLRTD-000001.
    /// </summary>
    [Fact]
    public async Task Document_numbers_do_not_repeat_across_projects()
    {
        var first = await SeedAsync(_host.Factory);
        using var client = _host.CreateClient();
        await LoginAsync(client, first.MemberName);

        Guid secondProjectId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = await db.Programs.SingleAsync(x => x.Id == first.ProgramId);
            var second = new ProjectRecord(program.Id, "Second Project", "Second Software");
            db.Add(second);
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<TestProcedureDocumentBootstrap>()
                .EnsureForProjectAsync(second.Id);
            await db.SaveChangesAsync();
            secondProjectId = second.Id;
        }

        var numbersOf = async (Guid projectId) => (await (await client.GetAsync(
            $"/api/projects/{projectId}/test-procedure-documents")).Content.ReadFromJsonAsync<DocumentRow[]>())!
            .Select(x => x.DocumentNumber).ToArray();

        var firstNumbers = await numbersOf(first.ProjectId);
        var secondNumbers = await numbersOf(secondProjectId);

        Assert.Empty(firstNumbers.Intersect(secondNumbers));
        Assert.Equal(6, firstNumbers.Concat(secondNumbers).Distinct().Count());
    }

    private sealed record CreatedWorkspace(ProjectRef Project);
    private sealed record ProjectRef(Guid Id);

    private sealed record ProcedurePage(int Page, int PageSize, int TotalCount, int TotalPages, ProcedureRow[] Items);
    private sealed record ProcedureRow(Guid Id, string DisplayNumber, string Title, string Level);
}
