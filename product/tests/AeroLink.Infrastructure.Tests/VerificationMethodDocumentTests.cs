using System.IO.Compression;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #701 acceptance criterion 5: a generated controlled document renders the configured canonical value.
///
/// The document generators read the stored verification method verbatim, which is why refusing a
/// near-miss at submission rather than re-spelling it matters here. What a project configured is what an
/// approver signs and what an auditor filters on; a historical value that predates the vocabulary keeps
/// appearing exactly as stored until a controlled correction changes it.
/// </summary>
public sealed class VerificationMethodDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

    private static ControlledOutputGenerator Generator(AeroLinkDbContext db) =>
        new(db, new RichContentPublisher(db,
            new EvidenceFileStore(Path.Combine(Path.GetTempPath(), $"aerolink-701-evidence-{Guid.NewGuid():N}"))));

    private static async Task<string> DocumentTextAsync(GeneratedOutput output)
    {
        using var archive = new ZipArchive(new MemoryStream(output.Content), ZipArchiveMode.Read);
        var part = archive.GetEntry("word/document.xml");
        Assert.NotNull(part);
        using var reader = new StreamReader(part!.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed record Fixture(AeroLinkDbContext Db, Guid ProjectId, Guid DocumentId, Guid ChangeRequestId);

    private static async Task<Fixture> SeedAsync(string configuredMethod, string declaredMethod)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
        var db = new AeroLinkDbContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();

        var program = new ProgramRecord("Verification Document Program", "VDP");
        var project = new ProjectRecord(program.Id, "Flight Software", "Verification Document Software");
        var release = new SoftwareRelease(project.Id, "1.0", false);
        db.AddRange(program, project, release,
            LegacyDefaultProjectLadderFactory.Create(project.Id, Now),
            ProjectVerificationVocabulary.Declaring(project.Id, [configuredMethod], Now));
        await db.SaveChangesAsync();

        var request = new SystemChangeRequest("SRCR-70110", 0, project.Id, release.Id,
            "Controlled document rendering", "P", "A", "S", "author", Now);
        request.AddRequirementChange("author", "SYSR-701100", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The FMS shall sequence oceanic waypoints.", "New capability",
            declaredMethod, Now);
        request.SubmitForReview("author", [new ApproverSelection("assurance.reviewer", "Assurance Reviewer")], Now,
            ladderPolicy: LegacyLadderPolicy.Instance,
            verificationPolicy: new VerificationMethodPolicy([configuredMethod]));
        request.ApproveActiveStage("assurance.reviewer", Now);

        var baseline = new CandidateBaseline("SW-01.00", 0, project.Id, release.Id, null, "Rendering baseline",
            "author", Now);
        baseline.Select(request, "author", Now);
        baseline.Freeze("author", Now);
        baseline.MarkRequirementsMaterialized("author", new string('a', 64), 1, Now);

        var artifact = new RequirementArtifact(project.Id, "SYSR-701100", RequirementLevel.System, Now);
        var revision = new RequirementRevision(artifact.Id, 0, "The FMS shall sequence oceanic waypoints.",
            "New capability", request.RequirementChanges.Single().VerificationMethod,
            RequirementRevisionState.Active, request.Id, baseline.Id, Now);
        var document = new ControlledDocument(project.Id, release.Id, baseline.Id, ControlledDocumentType.Sysrd,
            "SYSRD-000900", "System Requirements Document", 0, new string('b', 64), 1, Now);
        db.AddRange(request, baseline, artifact, revision, document,
            new BaselineRequirementSelection(baseline.Id, artifact.Id, revision.Id));
        await db.SaveChangesAsync();
        return new(db, project.Id, document.Id, request.Id);
    }

    [Fact]
    public async Task A_controlled_requirements_document_renders_the_configured_canonical_value()
    {
        var fixture = await SeedAsync("Similarity", "Similarity");
        await using var db = fixture.Db;

        var output = await Generator(db).GenerateAsync(fixture.DocumentId, "docx", default);
        Assert.NotNull(output);
        var text = await DocumentTextAsync(output!);

        Assert.Contains("Verification method", text, StringComparison.Ordinal);
        Assert.Contains("Similarity", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_change_request_projection_renders_the_same_configured_value()
    {
        var fixture = await SeedAsync("Similarity", "Similarity");
        await using var db = fixture.Db;

        var request = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges)
            .SingleAsync(x => x.Id == fixture.ChangeRequestId);
        var change = request.RequirementChanges.Single();

        // The change-request document projection reads the same stored value the requirements document does,
        // so one canonical spelling is what an approver signs and what the published record carries.
        Assert.Equal("Similarity", change.VerificationMethod);
    }

    [Fact]
    public async Task A_historical_value_outside_the_vocabulary_still_renders_exactly_as_stored()
    {
        // Written before the project narrowed its vocabulary. It is reported for a deliberate correction and
        // is not relabelled in a document somebody already signed.
        var fixture = await SeedAsync("Test", "Test");
        await using var db = fixture.Db;
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE requirement_revisions SET \"VerificationMethod\" = 'Testing'");

        var output = await Generator(db).GenerateAsync(fixture.DocumentId, "docx", default);
        var text = await DocumentTextAsync(output!);

        Assert.Contains("Testing", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reconciliation_report_names_a_historical_value_without_changing_it()
    {
        var fixture = await SeedAsync("Test", "Test");
        await using var db = fixture.Db;
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE requirement_revisions SET \"VerificationMethod\" = 'Testing'");

        var service = new ProjectVerificationVocabularyService(db,
            new FixedLadderPolicyResolver(LegacyLadderPolicy.Instance));
        var read = await service.ReadAsync(fixture.ProjectId, canManage: true);

        Assert.NotNull(read);
        Assert.Equal(["Test"], read!.Methods);
        var row = Assert.Single(read.NonConforming);
        Assert.Equal("Testing", row.Value);
        Assert.Equal(0, row.ChangeCount);
        Assert.Equal(1, row.RevisionCount);
        Assert.Equal(["SYSR-701100.00"], row.Examples);
        Assert.Equal("Testing", await db.RequirementRevisions.AsNoTracking()
            .Select(x => x.VerificationMethod).SingleAsync());
    }

    private sealed class FixedLadderPolicyResolver(ILadderPolicy policy) : IProjectLadderPolicyResolver
    {
        public Task<ILadderPolicy> ResolveAsync(Guid projectId, CancellationToken ct = default) =>
            Task.FromResult(policy);
    }
}
