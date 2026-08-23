using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class VerificationProcedureConcurrencyException(string message) : InvalidOperationException(message);

/// <summary>Procedure-only body used by the dormant shared authoring seam.</summary>
public sealed record VerificationProcedureContent(
    string EnvironmentSetup,
    string TestData,
    string OrderedSteps,
    string ExpectedObservations,
    string Cleanup,
    string ToolingAutomation,
    string Objective = "Procedure execution",
    string Preconditions = "");

/// <summary>
/// Creates and revises software Procedure artifacts without creating a Procedure TCR or activating runtime
/// behavior.  The service deliberately reuses TestProcedure/TestProcedureRevision: identity, history,
/// provenance and the eventual #725 review seam stay in one aggregate.
/// </summary>
public sealed class VerificationProcedureAuthoringService(AeroLinkDbContext db,
    IProjectLadderPolicyResolver? policyResolver = null)
{
    public async Task<(TestProcedure Artifact, TestProcedureRevision Revision)> CreateAsync(Guid projectId,
        TestProcedureLevel level, string title, string actorId, VerificationProcedureContent content,
        VerificationProcedureParentKind parentKind, IReadOnlyCollection<Guid>? caseRevisionIds,
        string? derivedRationale, DateTimeOffset now, CancellationToken ct)
    {
        if (level == TestProcedureLevel.System)
            throw new DomainException("This dormant authoring seam is for software Procedures; System remains Procedure-only.");
        VerificationProcedureParentPolicy.Validate(parentKind, caseRevisionIds, derivedRationale);
        var policy = policyResolver is null ? LegacyLadderPolicy.Instance : await policyResolver.ResolveAsync(projectId, ct);
        var baseNumber = await IdentifierAllocator.NextTestProcedureAsync(db, level,
            VerificationArtifactKind.Procedure, ct, policy);
        var artifact = new TestProcedure(projectId, baseNumber, title, actorId, now, level, policy,
            VerificationArtifactKind.Procedure, parentKind);
        var revision = NewRevision(artifact.Id, 0, actorId, content, parentKind, derivedRationale, now);
        db.TestProcedures.Add(artifact);
        db.TestProcedureRevisions.Add(revision);
        AddParents(revision.Id, parentKind, caseRevisionIds);
        await db.SaveChangesAsync(ct);
        return (artifact, revision);
    }

    public async Task<TestProcedureRevision> ReviseAsync(Guid artifactId, string actorId,
        VerificationProcedureContent content, VerificationProcedureParentKind parentKind,
        IReadOnlyCollection<Guid>? caseRevisionIds, string? derivedRationale, DateTimeOffset now,
        CancellationToken ct, long expectedVersion)
    {
        var artifact = await db.TestProcedures.SingleOrDefaultAsync(x => x.Id == artifactId, ct)
            ?? throw new DomainException("The verification artifact does not exist.");
        if (artifact.ArtifactKind != VerificationArtifactKind.Procedure || artifact.Level == TestProcedureLevel.System)
            throw new DomainException("Only a dormant software Procedure can use this authoring seam.");
        if (artifact.Version != expectedVersion)
            throw new VerificationProcedureConcurrencyException("The Procedure changed after it was opened. Refresh before revising.");
        VerificationProcedureParentPolicy.Validate(parentKind, caseRevisionIds, derivedRationale);
        var prior = await db.TestProcedureRevisions.AsNoTracking().Where(x => x.ProcedureId == artifactId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct)
            ?? throw new DomainException("The verification artifact has no revision to advance.");
        if (prior.State == TestProcedureState.Retired)
            throw new DomainException("A retired Procedure cannot be revised outside governed reactivation.");
        var revision = NewRevision(artifactId, prior.Revision + 1, actorId, content, parentKind, derivedRationale, now);
        db.TestProcedureRevisions.Add(revision);
        AddParents(revision.Id, parentKind, caseRevisionIds);
        artifact.UpdateDraft(artifact.Title, artifact.OwnerId, now);
        await db.SaveChangesAsync(ct);
        return revision;
    }

    public async Task<TestProcedureRevision> RetireAsync(Guid artifactId, string actorId,
        string rationale, DateTimeOffset now, CancellationToken ct, long expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(rationale)) throw new DomainException("A Procedure retirement rationale is required.");
        var artifact = await db.TestProcedures.SingleOrDefaultAsync(x => x.Id == artifactId, ct)
            ?? throw new DomainException("The verification artifact does not exist.");
        if (artifact.ArtifactKind != VerificationArtifactKind.Procedure || artifact.Level == TestProcedureLevel.System)
            throw new DomainException("Only a dormant software Procedure can use this authoring seam.");
        if (artifact.Version != expectedVersion)
            throw new VerificationProcedureConcurrencyException("The Procedure changed after it was opened. Refresh before retiring.");
        var prior = await db.TestProcedureRevisions.AsNoTracking().Where(x => x.ProcedureId == artifactId)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct)
            ?? throw new DomainException("The verification artifact has no revision to retire.");
        var revision = new TestProcedureRevision(artifactId, prior.Revision + 1, "", "", "", "",
            TestProcedureState.Retired, actorId, now, sourceChangeRequestsJson: "[]",
            retirementRationale: rationale);
        db.TestProcedureRevisions.Add(revision);
        // Retiring is an aggregate mutation too: advance the artifact version so a second tab cannot
        // successfully submit a stale revise/retire intent after this immutable successor is written.
        artifact.UpdateDraft(artifact.Title, artifact.OwnerId, now);
        await db.SaveChangesAsync(ct);
        return revision;
    }

    private static TestProcedureRevision NewRevision(Guid artifactId, int number, string actorId,
        VerificationProcedureContent content, VerificationProcedureParentKind parentKind,
        string? rationale, DateTimeOffset now) => new(artifactId, number, content.Objective,
        string.IsNullOrWhiteSpace(content.Preconditions) ? content.EnvironmentSetup : content.Preconditions,
        content.OrderedSteps, content.ExpectedObservations, TestProcedureState.Draft, actorId, now,
        environmentSetup: content.EnvironmentSetup, testData: content.TestData,
        orderedSteps: content.OrderedSteps, expectedObservations: content.ExpectedObservations,
        cleanup: content.Cleanup, toolingAutomation: content.ToolingAutomation,
        parentKind: parentKind, derivedRationale: rationale);

    private void AddParents(Guid procedureRevisionId, VerificationProcedureParentKind kind,
        IReadOnlyCollection<Guid>? caseRevisionIds)
    {
        if (kind != VerificationProcedureParentKind.Allocated) return;
        foreach (var id in (caseRevisionIds ?? []).Distinct())
            db.TestCaseProcedureLinks.Add(new TestCaseProcedureLink(id, procedureRevisionId));
    }
}
