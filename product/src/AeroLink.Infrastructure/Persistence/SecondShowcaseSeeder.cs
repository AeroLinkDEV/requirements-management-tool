using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The small, opt-in showcase that makes a project-owned ladder visible without changing FMSLIVE.
///
/// The project starts with the ordinary persisted legacy ladder because that is the same creation seam used by
/// the workspace endpoint. It is then edited and activated through the application authority, exactly as a
/// Configuration Manager would do it. Nothing in this seeder writes an active lifecycle value directly.
/// </summary>
public sealed record SecondShowcaseSummary(
    Guid ProgramId,
    Guid ProjectId,
    Guid ReleaseId,
    Guid BaselineId,
    int SystemRequirements,
    int LowLevelRequirements,
    int DownstreamAssessments,
    int TraceLinks,
    int ControlledDocuments,
    int TestProcedureDocuments);

public sealed class SecondShowcaseSeeder(
    AeroLinkDbContext db,
    ProjectLadderAuthoringService ladderAuthoring,
    IProjectLadderPolicyResolver? policyResolver = null)
{
    public const string ProgramCode = "LADDERLAB";
    public const string ProjectCode = "LADDER-SECOND";
    private const string Actor = "showcase.second";
    private readonly IProjectLadderPolicyResolver resolver = policyResolver ?? new EffectiveProjectLadderPolicyResolver(db);

    public async Task<SecondShowcaseSummary> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (existing is not null)
            return await SummarizeAsync(existing.Id, ct);

        var start = new DateTimeOffset(2026, 1, 12, 13, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Configured Ladder Showcase", ProgramCode);
        var project = new ProjectRecord(program.Id, ProjectCode, "Configured Ladder Software");
        var release = new SoftwareRelease(project.Id, "2.0", false);
        var legacyLadder = LegacyDefaultProjectLadderFactory.Create(project.Id, start);
        db.AddRange(program, project, release, legacyLadder);
        await db.SaveChangesAsync(ct);

        // The initial project row is the normal legacy/default shape. EditAsync creates the governed NonDefault
        // Draft and records its immutable authored snapshot; ActivateAsync is the sole path that may make it
        // runtime authority. Keeping both calls here is deliberate: a seed must not become a privileged second
        // activation mechanism merely because it runs at startup.
        var edit = await ladderAuthoring.EditAsync(project.Id,
            new ProjectLadderEditCommand(
                legacyLadder.Version,
                "Show the configured System to LowLevel ladder in the second showcase workspace.",
                [
                    new(nameof(RequirementLevel.System), 1, LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities),
                    new(nameof(RequirementLevel.LowLevel), 2, LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities),
                ],
                [new(nameof(RequirementLevel.System), nameof(RequirementLevel.LowLevel))]),
            Actor, start.AddMinutes(1), ct);
        if (edit.Kind != ProjectLadderEditResultKind.Success || edit.Configuration is null)
            throw new InvalidOperationException(edit.Error ?? "The second showcase ladder could not be authored.");

        var activation = await ladderAuthoring.ActivateAsync(project.Id,
            new ProjectLadderActivationCommand(edit.Configuration.Version,
                "Activate the authored System to LowLevel showcase ladder."),
            Actor, start.AddMinutes(2), ct);
        if (activation.Kind != ProjectLadderActivationResultKind.Success)
            throw new InvalidOperationException(activation.Error ?? "The second showcase ladder could not be activated.");

        var policy = await resolver.ResolveAsync(project.Id, ct);
        if (!policy.OrderedLevels.SequenceEqual([RequirementLevel.System, RequirementLevel.LowLevel]))
            throw new InvalidOperationException("The second showcase did not activate the required System/LowLevel ladder.");

        var systemRequest = BuildSystemRequest(project.Id, release.Id, policy, start.AddDays(1));
        var lowLevelRequest = BuildLowLevelRequest(project.Id, release.Id, policy, start.AddDays(2));
        db.SystemChangeRequests.AddRange(systemRequest, lowLevelRequest);
        await db.SaveChangesAsync(ct);

        // This is the same application service the approval endpoint uses. The configured policy, not an
        // assumed enum successor, creates exactly one LowLevel assessment for the approved System change.
        var downstream = new DownstreamImpactService(db, policyResolver: resolver);
        await downstream.RaiseForApprovedChangeRequestAsync(systemRequest, start.AddDays(3), ct);
        await db.SaveChangesAsync(ct);

        var baseline = new CandidateBaseline("SW-71.20", 0, project.Id, release.Id, null,
            "Configured System and LowLevel baseline", Actor, start.AddDays(4));
        baseline.Select(systemRequest, Actor, start.AddDays(4));
        baseline.Select(lowLevelRequest, Actor, start.AddDays(4));
        baseline.Freeze(Actor, start.AddDays(4));
        db.CandidateBaselines.Add(baseline);
        await db.SaveChangesAsync(ct);

        // Materialisation is the supported route from approved change content to durable requirement revisions.
        // It also synchronizes only the active System and LowLevel enterprise projections for this project.
        var materializer = new RequirementBaselineMaterializer(db,
            new VerificationImpactService(db, policyResolver: resolver), policyResolver: resolver);
        await materializer.MaterializeAsync(baseline.Id, Actor, start.AddDays(5), ct);

        var systemRevision = await (from revision in db.RequirementRevisions
                                    join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                    where artifact.ProjectId == project.Id && artifact.Level == RequirementLevel.System
                                    select revision).SingleAsync(ct);
        var lowLevelRevision = await (from revision in db.RequirementRevisions
                                      join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                                      where artifact.ProjectId == project.Id && artifact.Level == RequirementLevel.LowLevel
                                      select revision).SingleAsync(ct);
        // The active project policy enforces this direct edge at the persistence boundary. It is intentionally
        // not an inferred System -> HighLevel -> LowLevel path.
        db.RequirementTraces.Add(new RequirementTraceLink(project.Id, lowLevelRevision.Id, systemRevision.Id,
            RequirementTraceType.DerivedFrom,
            "The LowLevel implementation directly derives from the configured System requirement.",
            start.AddDays(5)));

        var hash = Hash($"{baseline.Id}|configured-ladder-showcase");
        db.ControlledDocuments.AddRange(
            new ControlledDocument(project.Id, release.Id, baseline.Id, ControlledDocumentType.Sysrd,
                "SYSRD-000712", "Configured System Requirements Document", 0, hash, 1, start.AddDays(6)),
            new ControlledDocument(project.Id, release.Id, baseline.Id, ControlledDocumentType.SwrdLowLevel,
                "LLRD-000712", "Configured LowLevel Requirements Document", 0, hash, 1, start.AddDays(6)));
        await db.SaveChangesAsync(ct);

        // The procedure-document bootstrap is policy-aware and creates only the configured procedure
        // documents. There are no procedures in this deliberately small seed, but the documents are useful
        // durable proof that the HLR document/queue is not merely empty by accident.
        await new TestProcedureDocumentBootstrap(db, policyResolver: resolver)
            .EnsureForProjectAsync(project.Id, ct);
        await db.SaveChangesAsync(ct);

        return await SummarizeAsync(program.Id, ct);
    }

    private static SystemChangeRequest BuildSystemRequest(Guid projectId, Guid releaseId, ILadderPolicy policy,
        DateTimeOffset now)
    {
        var request = new SystemChangeRequest("SRCR-71201", 0, projectId, releaseId,
            "Add configured system scheduling behavior", "The configured workspace needs one system behavior.",
            "The system impact was assessed against the authored two-level ladder.",
            "Introduce the system behavior and its verification basis.", Actor, now,
            ChangeRequestType.System, ladderPolicy: policy);
        request.AddRequirementChange(Actor, "SYSR-71201", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce,
            "The configured system shall schedule approved navigation work.",
            "The second showcase demonstrates a System source change.", "Test", now, ladderPolicy: policy);
        request.SubmitForReview(Actor, [new("showcase.approver", "Showcase Approver")], now.AddHours(1), ladderPolicy: policy);
        request.ApproveActiveStage("showcase.approver", now.AddHours(2));
        return request;
    }

    private static SystemChangeRequest BuildLowLevelRequest(Guid projectId, Guid releaseId, ILadderPolicy policy,
        DateTimeOffset now)
    {
        var request = new SystemChangeRequest("LLRCR-71202", 0, projectId, releaseId,
            "Implement configured scheduling behavior", "The implementation needs one low-level requirement.",
            "The implementation is directly allocated to the system behavior.",
            "Introduce the low-level implementation behavior.", Actor, now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel, ladderPolicy: policy);
        request.AddRequirementChange(Actor, "LLR-71202", 0, RequirementLevel.LowLevel,
            RequirementChangeKind.Introduce,
            "The configured low-level component shall implement deterministic scheduling.",
            "The second showcase demonstrates a LowLevel child with a direct System trace.", "Test", now,
            ladderPolicy: policy);
        request.SubmitForReview(Actor, [new("showcase.approver", "Showcase Approver")], now.AddHours(1), ladderPolicy: policy);
        request.ApproveActiveStage("showcase.approver", now.AddHours(2));
        return request;
    }

    private async Task<SecondShowcaseSummary> SummarizeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync(ct);
        var baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId).Select(x => x.Id).SingleAsync(ct);
        return new(programId, projectId, releaseId, baselineId,
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.System, ct),
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.LowLevel, ct),
            await db.DownstreamChangeAssessments.CountAsync(x => x.ProjectId == projectId, ct),
            await db.RequirementTraces.CountAsync(x => x.ProjectId == projectId, ct),
            await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct),
            await db.TestProcedureDocuments.CountAsync(x => x.ProjectId == projectId, ct));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
