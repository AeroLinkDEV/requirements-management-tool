using System.Net;
using System.Net.Http.Json;
using AeroLink.Domain.Identity;
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
public sealed class TestProcedureDocumentApiTests
{
    private const string Member = "document.reader";

    private sealed record Seeded(Guid ProjectId, Guid HighLevelProcedureId);

    private static async Task<Seeded> SeedAsync(AeroLinkApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
        var now = DateTimeOffset.UtcNow;
        var program = new ProgramRecord("Rail Program", "RAIL");
        var project = new ProjectRecord(program.Id, "Flight Software", "Rail Software");
        db.AddRange(program, project);

        var high = new TestProcedure(project.Id, "HLRTP-000501", "Verify flight plan behaviour", "test.engineer", now,
            TestProcedureLevel.HighLevel);
        var low = new TestProcedure(project.Id, "LLRTP-000501", "Verify checksum recovery", "test.engineer", now,
            TestProcedureLevel.LowLevel);
        db.AddRange(high, low);

        var account = new UserAccount(Member, Member, $"{Member}@example.test",
            IdentityService.HashPassword(AeroLinkApiFactory.MemberPassword), now);
        db.Add(account);
        db.Add(new ProgramMembership(account.Id, program.Id, ProgramRole.TestEngineer, "test.setup", now));
        await db.SaveChangesAsync();

        // The bootstrap runs at startup for every project; a project created afterwards is placed explicitly.
        await scope.ServiceProvider.GetRequiredService<TestProcedureDocumentBootstrap>()
            .EnsureForProjectAsync(project.Id);
        await db.SaveChangesAsync();

        return new(project.Id, high.Id);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = Member, password = AeroLinkApiFactory.MemberPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await SecurityBoundaryTests.AuthorizeMutationsAsync(client);
    }

    private sealed record DocumentRow(Guid Id, string DocumentNumber, string Title, string Level,
        string Description, int ProcedureCount, SectionRow[] Sections);
    private sealed record SectionRow(Guid Id, string Heading, int Position, int ProcedureCount);

    [Fact]
    public async Task A_discipline_sees_only_its_own_document()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client);

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
    public async Task Every_document_is_returned_when_no_scope_is_named()
    {
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client);

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
        await using var factory = new AeroLinkApiFactory();
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client);

        var documents = (await (await client.GetAsync(
            $"/api/projects/{seeded.ProjectId}/test-procedure-documents?scope=HighLevelSoftware"))
            .Content.ReadFromJsonAsync<DocumentRow[]>())!;
        var highLevel = Assert.Single(documents);

        var listed = await client.GetFromJsonAsync<ProcedurePage>(
            $"/api/test-procedures?projectId={seeded.ProjectId}&documentId={highLevel.Id}&pageSize=50");

        Assert.NotNull(listed);
        var only = Assert.Single(listed!.Items);
        Assert.StartsWith("HLRTP-000501", only.DisplayNumber);
    }

    /// <summary>
    /// The startup bootstrap cannot help a Project created after it ran, and a Project whose documents appear
    /// only at the next restart is a Project whose Explorer rail is empty for the person who just made it.
    /// </summary>
    [Fact]
    public async Task A_project_created_through_the_api_has_its_documents_immediately()
    {
        await using var factory = new AeroLinkApiFactory();
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
        await using var factory = new AeroLinkApiFactory();
        var first = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await LoginAsync(client);

        Guid secondProjectId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AeroLinkDbContext>();
            var program = await db.Programs.SingleAsync();
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
