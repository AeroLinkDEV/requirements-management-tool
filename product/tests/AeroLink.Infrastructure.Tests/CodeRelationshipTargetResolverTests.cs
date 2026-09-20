using System.Text.Json.Nodes;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class CodeRelationshipTargetResolverTests
{
    [Fact]
    public async Task Inherited_requirement_resolves_exact_revision_without_a_source_change_request()
    {
        await using var f = await Fixture.CreateAsync();
        var baseline = new CandidateBaseline("BL-00001", 0, f.Project.Id, f.Change.TargetReleaseId,
            null, "Inherited", "author", f.Now);
        var artifact = new RequirementArtifact(f.Project.Id, "SYS-00001", RequirementLevel.System, f.Now);
        var revision = RequirementRevision.FromAeroLinkBaseline(artifact.Id, 2, "Historical system behavior.",
            "Test", RequirementRevisionState.Superseded, baseline.Id, baseline.Id, f.Now, "SYS-00001.01");
        f.Db.AddRange(baseline, artifact, revision);
        await f.Db.SaveChangesAsync();
        var target = await f.Resolve(CodeRelationshipTargetKind.RequirementRevision, revision.Id);
        Assert.Equal(revision.Id, target!.ExactIdentityId);
        Assert.Equal(artifact.Id, target.OwningIdentityId);
        Assert.Equal(2, target.RevisionNumber);
        Assert.Equal("SYS-00001.02", target.DisplaySnapshot);
        Assert.Null(await f.Resolve(target.Kind, artifact.Id));
        Assert.Null(await CodeRelationshipTargetResolver.ResolveAsync(f.Db, Guid.NewGuid(), target.Kind, revision.Id, default));
        Assert.Null(await f.Resolve((CodeRelationshipTargetKind)999, revision.Id));
    }

    [Fact]
    public async Task Proposal_identity_is_not_its_result_revision_or_replacement()
    {
        await using var f = await Fixture.CreateAsync();
        var proposal = f.Change.AddRequirementChange("author", "", 3, RequirementLevel.System,
            RequirementChangeKind.Introduce, "", "", "", f.Now, allowIncomplete: true);
        await f.Db.SaveChangesAsync();
        var resolved = await f.Resolve(CodeRelationshipTargetKind.RequirementProposal, proposal.Id);
        Assert.NotNull(resolved);
        Assert.Equal(f.Change.Id, resolved.OwningIdentityId);
        Assert.Null(resolved.RevisionNumber);
        Assert.Contains("Unnamed requirement proposal", resolved.DisplaySnapshot);
        Assert.Null(await CodeRelationshipTargetResolver.ResolveAsync(f.Db, Guid.NewGuid(), resolved.Kind, proposal.Id, default));
        f.Change.RemoveRequirementChange("author", proposal.Id, f.Now);
        f.Change.AddRequirementChange("author", "", 3, RequirementLevel.System,
            RequirementChangeKind.Introduce, "", "", "", f.Now, allowIncomplete: true);
        await f.Db.SaveChangesAsync();
        Assert.Null(await f.Resolve(resolved.Kind, proposal.Id));
        var change = await f.Resolve(CodeRelationshipTargetKind.ChangeRequestRevision, f.Change.Id);
        Assert.Equal(f.Change.Id, change!.ExactIdentityId);
        Assert.Null(change.OwningIdentityId);
        Assert.Equal(f.Change.DisplayNumber, change.DisplaySnapshot);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("digest")]
    [InlineData("report")]
    [InlineData("project")]
    [InlineData("revision")]
    [InlineData("display")]
    [InlineData("malformed")]
    [InlineData("schema")]
    public async Task Snapshot_resolution_checks_original_envelope_and_project(string variant)
    {
        await using var f = await Fixture.CreateAsync();
        var report = new ProblemReport(f.Project.Id, "PR-000321", "Historical report", "Problem.",
            "Analysis", "author", f.Now, category: ProblemReportCategory.CodeFunctional);
        var json = ProblemReportClosureCandidateService.ReportSnapshotForSchema(report, ProblemReportEvidenceContract.SchemaVersion);
        var payload = JsonNode.Parse(json)!.AsObject();
        if (variant == "report") payload["id"] = Guid.NewGuid();
        if (variant == "project") payload["projectId"] = Guid.NewGuid();
        if (variant == "revision") payload["revision"] = report.Revision + 1;
        if (variant == "display") payload["displayNumber"] = "";
        json = variant == "malformed" ? "{" : payload.ToJsonString();
        var snapshot = new ProblemReportRevision(report.Id, report.Revision, "Created", "author",
            variant == "digest" ? new string('0', 64) : ProblemReportEvidenceContract.Hash(json), json, f.Now,
            variant == "schema" ? 999 : ProblemReportEvidenceContract.SchemaVersion);
        var other = new ProblemReportRevision(report.Id, report.Revision, "Observed", "author",
            snapshot.SnapshotHash, json, f.Now);
        f.Db.AddRange(report, snapshot, other);
        await f.Db.SaveChangesAsync();
        var resolved = await f.Resolve(CodeRelationshipTargetKind.ProblemReportRevision, snapshot.Id);
        if (variant != "valid") Assert.Null(resolved);
        else
        {
            Assert.Equal(snapshot.Id, resolved!.ExactIdentityId);
            Assert.Equal(report.Id, resolved.OwningIdentityId);
            Assert.Equal(report.DisplayNumber, resolved.DisplaySnapshot);
            Assert.Equal(other.Id, (await f.Resolve(resolved.Kind, other.Id))!.ExactIdentityId);
        }
        Assert.Null(await CodeRelationshipTargetResolver.ResolveAsync(f.Db, Guid.NewGuid(),
            CodeRelationshipTargetKind.ProblemReportRevision, snapshot.Id, default));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public AeroLinkDbContext Db { get; private set; } = null!;
        public ProjectRecord Project { get; private set; } = null!;
        public SystemChangeRequest Change { get; private set; } = null!;
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.connection.OpenAsync();
            f.Db = new(new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync();
            var program = new ProgramRecord("Target resolver", "CTR");
            f.Project = new(program.Id, "Resolver", "Exact targets");
            var release = new SoftwareRelease(f.Project.Id, "1.0", true);
            f.Change = new("SRCR-00001", 0, f.Project.Id, release.Id, "Proposal", "Problem", "Analysis", "Solution", "author", f.Now);
            f.Db.AddRange(program, f.Project, release, f.Change);
            await f.Db.SaveChangesAsync();
            return f;
        }
        public Task<CodeRelationshipTarget?> Resolve(CodeRelationshipTargetKind kind, Guid id) =>
            CodeRelationshipTargetResolver.ResolveAsync(Db, Project.Id, kind, id, default);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
