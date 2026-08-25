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
    public const string ProjectName = "Configured Ladder Showcase";
    private const string ProjectProduct = "Configured Ladder Software";
    private const string ReleaseVersion = "2.0";
    private const string BaselineNumber = "SW-71.20";
    private const string LegacySystemChangeRequestNumber = "SRCR-71201";
    private const string LegacyLowLevelChangeRequestNumber = "LLRCR-71202";
    private const string LegacySystemRequirementNumber = "SYSR-71201";
    private const string LegacyLowLevelRequirementNumber = "LLR-71202";
    private const string Actor = "showcase.second";
    private const string SystemsAuthor = "systems.author";
    private const string SoftwareAuthor = "software.author";
    private const string SystemsReviewer = "systems.reviewer";
    private const string SoftwareLead = "software.lead";
    private readonly IProjectLadderPolicyResolver resolver = policyResolver ?? new EffectiveProjectLadderPolicyResolver(db);

    public async Task<SecondShowcaseSummary> EnsureSeededAsync(CancellationToken ct = default)
    {
        await EnsureNoFormerReservedWorkspaceAsync(ct);
        var start = new DateTimeOffset(2026, 1, 12, 13, 0, 0, TimeSpan.Zero);
        var workspace = await EnsureWorkspaceAsync(start, ct);
        await EnsureLadderAsync(workspace.ProjectId, start, ct);
        var policy = await resolver.ResolveAsync(workspace.ProjectId, ct);
        EnsureConfiguredPolicy(policy);

        var systemRequest = await EnsureRequestAsync(workspace.ProjectId, workspace.ReleaseId,
            policy, RequirementLevel.System, start.AddDays(1), ct);
        var lowLevelRequest = await EnsureRequestAsync(workspace.ProjectId, workspace.ReleaseId,
            policy, RequirementLevel.LowLevel, start.AddDays(2), ct);
        await EnsureDownstreamAssessmentAsync(systemRequest, start.AddDays(3), ct);
        var baselineId = await EnsureBaselineAsync(workspace.ProjectId, workspace.ReleaseId,
            systemRequest, lowLevelRequest, start.AddDays(4), ct);
        await EnsureDirectTraceAsync(workspace.ProjectId, start.AddDays(5), ct);
        await EnsureControlledDocumentsAsync(workspace.ProjectId, workspace.ReleaseId, baselineId,
            start.AddDays(6), ct);

        // The procedure-document bootstrap is policy-aware and creates only documents supported by the
        // active project ladder. There are no procedures in this deliberately small seed.
        await new TestProcedureDocumentBootstrap(db, policyResolver: resolver)
            .EnsureForProjectAsync(workspace.ProjectId, ct);
        await db.SaveChangesAsync(ct);

        return await SummarizeAsync(workspace.ProgramId, ct);
    }

    private async Task EnsureNoFormerReservedWorkspaceAsync(CancellationToken ct)
    {
        var programId = await db.Programs.AsNoTracking()
            .Where(x => x.Code == ProgramCode).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (programId is null) return;
        var projectIds = await db.Projects.AsNoTracking()
            .Where(x => x.ProgramId == programId.Value).Select(x => x.Id).ToListAsync(ct);
        if (projectIds.Count == 0) return;

        var formerRequest = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId)
                && (x.BaseNumber == LegacySystemChangeRequestNumber || x.BaseNumber == LegacyLowLevelChangeRequestNumber))
            .Select(x => x.DisplayNumber).FirstOrDefaultAsync(ct);
        var formerRequirement = await db.Requirements.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId)
                && (x.BaseNumber == LegacySystemRequirementNumber || x.BaseNumber == LegacyLowLevelRequirementNumber))
            .Select(x => x.BaseNumber).FirstOrDefaultAsync(ct);
        if (formerRequest is null && formerRequirement is null)
        {
            formerRequirement = await (from change in db.RequirementChanges.AsNoTracking()
                                       join request in db.SystemChangeRequests.AsNoTracking()
                                           on change.ChangeRequestId equals request.Id
                                       where projectIds.Contains(request.ProjectId)
                                           && (change.BaseNumber == LegacySystemRequirementNumber
                                               || change.BaseNumber == LegacyLowLevelRequirementNumber)
                                       select change.BaseNumber).FirstOrDefaultAsync(ct);
        }
        if (formerRequest is null && formerRequirement is null) return;

        throw new InvalidOperationException(
            "The LADDERLAB showcase contains reserved identifiers from an earlier incompatible seed "
            + $"({LegacySystemChangeRequestNumber}/{LegacyLowLevelChangeRequestNumber} or "
            + $"{LegacySystemRequirementNumber}/{LegacyLowLevelRequirementNumber}). "
            + "It was not changed; remove or rebuild this dedicated showcase before retrying.");
    }

    private async Task<(Guid ProgramId, Guid ProjectId, Guid ReleaseId)> EnsureWorkspaceAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var program = await db.Programs.SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (program is null)
        {
            program = new ProgramRecord(ProjectName, ProgramCode);
            db.Programs.Add(program);
            await db.SaveChangesAsync(ct);
        }
        else if (program.Name != ProjectName)
            throw new InvalidOperationException($"Program {ProgramCode} exists with unexpected name '{program.Name}'.");

        var projects = await db.Projects.Where(x => x.ProgramId == program.Id).ToListAsync(ct);
        if (projects.Count > 1)
            throw new InvalidOperationException($"Program {ProgramCode} has more than one Project; the second showcase cannot choose one safely.");
        var project = projects.SingleOrDefault();
        if (project is null)
        {
            project = new ProjectRecord(program.Id, ProjectName, ProjectProduct);
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
        }
        else if (project.Name != ProjectName || project.SoftwareProduct != ProjectProduct)
            throw new InvalidOperationException($"Program {ProgramCode} has an unexpected Project identity.");

        var releases = await db.Releases.Where(x => x.ProjectId == project.Id).ToListAsync(ct);
        if (releases.Count > 1)
            throw new InvalidOperationException("Second showcase Project has more than one Release; recovery cannot choose one safely.");
        var release = releases.SingleOrDefault();
        if (release is null)
        {
            release = new SoftwareRelease(project.Id, ReleaseVersion, false);
            db.Releases.Add(release);
            await db.SaveChangesAsync(ct);
        }
        else if (release.Version != ReleaseVersion || release.IsReleased)
            throw new InvalidOperationException("The second showcase Release is not the expected in-work 2.0 Release.");

        var ladders = await db.ProjectLadderConfigurations
            .Where(x => x.ProjectId == project.Id).ToListAsync(ct);
        if (ladders.Count > 1)
            throw new InvalidOperationException("The second showcase Project has more than one ladder configuration.");
        if (ladders.Count == 0)
        {
            // #726: every real creation seam starts from the new-project default ([Case, Procedure] software
            // Draft), and this showcase then deliberately configures the System-to-LowLevel subset before
            // sealing/activation — the same pre-seal removal path an owner uses.
            var initial = NewProjectLadderFactory.Create(project.Id, now);
            db.ProjectLadderConfigurations.Add(initial);
            await db.SaveChangesAsync(ct);
            var edit = await ladderAuthoring.EditAsync(project.Id,
                new ProjectLadderEditCommand(initial.Version,
                    "Show the configured System to LowLevel ladder in the second showcase workspace.",
                    [
                        new(nameof(RequirementLevel.System), 1, LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities),
                        new(nameof(RequirementLevel.LowLevel), 2, LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities),
                    ],
                    [new(nameof(RequirementLevel.System), nameof(RequirementLevel.LowLevel))]),
                Actor, now.AddMinutes(1), ct);
            if (edit.Kind != ProjectLadderEditResultKind.Success || edit.Configuration is null)
                throw new InvalidOperationException(edit.Error ?? "The second showcase ladder could not be authored.");
            await ActivateAsync(project.Id, edit.Configuration.Version, now, ct);
        }

        // Recovery-shaped like the ladder above: the second showcase can be re-run against a Project that
        // already carries a vocabulary, so presence is checked rather than assumed (#701).
        if (!await db.ProjectVerificationVocabularies.AnyAsync(x => x.ProjectId == project.Id, ct))
        {
            db.ProjectVerificationVocabularies.Add(ProjectVerificationVocabulary.Founding(project.Id, now));
            await db.SaveChangesAsync(ct);
        }

        return (program.Id, project.Id, release.Id);
    }

    private async Task EnsureLadderAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var configuration = await db.ProjectLadderConfigurations.AsNoTracking()
            .Include(x => x.Steps).Include(x => x.AllowedUpstream)
            .SingleAsync(x => x.ProjectId == projectId, ct);
        ResolvedProjectLadder resolved;
        try { resolved = ProjectLadderResolver.Resolve(configuration); }
        catch (Domain.Common.DomainException ex)
        {
            throw new InvalidOperationException("The second showcase ladder is malformed and was not changed.", ex);
        }

        if (configuration.Classification == ProjectLadderConfigurationClassification.LegacyDefault
            && configuration.State == ProjectLadderConfigurationState.Stored)
        {
            if (!resolved.AgreesWithLegacyDefault())
                throw new InvalidOperationException("The second showcase legacy ladder does not match the expected default graph.");
            var edit = await ladderAuthoring.EditAsync(projectId,
                new ProjectLadderEditCommand(configuration.Version,
                    "Show the configured System to LowLevel ladder in the second showcase workspace.",
                    [
                        new(nameof(RequirementLevel.System), 1, LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities),
                        new(nameof(RequirementLevel.LowLevel), 2, LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities),
                    ],
                    [new(nameof(RequirementLevel.System), nameof(RequirementLevel.LowLevel))]),
                Actor, now.AddMinutes(1), ct);
            if (edit.Kind != ProjectLadderEditResultKind.Success || edit.Configuration is null)
                throw new InvalidOperationException(edit.Error ?? "The second showcase ladder could not be authored.");
            await ActivateAsync(projectId, edit.Configuration.Version, now, ct);
            return;
        }

        if (configuration.Classification == ProjectLadderConfigurationClassification.NonDefault
            && configuration.State == ProjectLadderConfigurationState.Draft)
        {
            if (!IsConfiguredGraph(resolved))
                throw new InvalidOperationException("The second showcase has a non-default Draft ladder with an unexpected graph.");
            await ActivateAsync(projectId, configuration.Version, now, ct);
            return;
        }

        if (configuration.Classification == ProjectLadderConfigurationClassification.NonDefault
            && configuration.State == ProjectLadderConfigurationState.Active)
        {
            if (!IsConfiguredGraph(resolved))
                throw new InvalidOperationException("The second showcase has an Active ladder with an unexpected graph.");
            return;
        }

        throw new InvalidOperationException(
            $"The second showcase ladder is in unsupported state {configuration.Classification}/{configuration.State}; no data was reset.");
    }

    private async Task ActivateAsync(Guid projectId, long version, DateTimeOffset now, CancellationToken ct)
    {
        var result = await ladderAuthoring.ActivateAsync(projectId,
            new ProjectLadderActivationCommand(version,
                "Activate the authored System to LowLevel showcase ladder."),
            Actor, now.AddMinutes(2), ct);
        if (result.Kind != ProjectLadderActivationResultKind.Success)
            throw new InvalidOperationException(result.Error ?? "The second showcase ladder could not be activated.");
    }

    private static bool IsConfiguredGraph(ResolvedProjectLadder resolved) =>
        resolved.Steps.OrderBy(x => x.Position).Select(x => x.Level)
            .SequenceEqual([RequirementLevel.System, RequirementLevel.LowLevel])
        && resolved.Steps.OrderBy(x => x.Position).Select(x => x.Capabilities)
            .SequenceEqual([
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.System).Capabilities,
                LegacyLadderPolicy.Instance.Definition(RequirementLevel.LowLevel).Capabilities])
        && resolved.AllowedUpstream.Count == 1
        && resolved.AllowedUpstream[0].Parent == RequirementLevel.System
        && resolved.AllowedUpstream[0].Child == RequirementLevel.LowLevel;

    private static void EnsureConfiguredPolicy(ILadderPolicy policy)
    {
        if (!policy.OrderedLevels.SequenceEqual([RequirementLevel.System, RequirementLevel.LowLevel]))
            throw new InvalidOperationException("The second showcase did not activate the required System/LowLevel ladder.");
    }

    private async Task<SystemChangeRequest> EnsureRequestAsync(Guid projectId, Guid releaseId,
        ILadderPolicy policy, RequirementLevel level, DateTimeOffset now, CancellationToken ct)
    {
        var software = level == RequirementLevel.LowLevel;
        var baseNumber = software ? "LLRCR-00001" : "SRCR-00001";
        var request = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.BaseNumber == baseNumber && x.Revision == 0, ct);
        var created = request is null;
        if (request is null)
        {
            request = software
                ? BuildLowLevelRequest(projectId, releaseId, policy, now)
                : BuildSystemRequest(projectId, releaseId, policy, now);
            db.SystemChangeRequests.Add(request);
            await db.SaveChangesAsync(ct);
        }

        var expectedType = software ? ChangeRequestType.Software : ChangeRequestType.System;
        if (request.TargetReleaseId != releaseId || request.Type != expectedType
            || request.SoftwareLevel != (software ? RequirementLevel.LowLevel : null)
            || request.RequirementChanges.Count != 1)
            throw new InvalidOperationException($"The second showcase request {request.DisplayNumber} has unexpected scope or content.");
        var change = request.RequirementChanges.Single();
        var expectedNumber = software ? "LLR-00001" : "SYSR-00001";
        if (change.BaseNumber != expectedNumber || change.Level != level || change.Kind != RequirementChangeKind.Introduce)
            throw new InvalidOperationException($"The second showcase request {request.DisplayNumber} has unexpected requirement content.");

        var approver = software ? SoftwareLead : SystemsReviewer;
        if (request.State == ChangeRequestState.Draft)
        {
            // This deterministic Jan-2026 showcase predates exact parent-or-derived classification and
            // intentionally creates its System and LowLevel requests together, before their materialized
            // revisions exist. Preserve that historical evidence only on the one initial seed submission;
            // an ordinary later submission (including a migrated draft) must use the current v2 contract.
            if (created)
                request.MarkAsLegacyHistoricalPackage(request.AuthorId, now.AddMinutes(1));
            request.SubmitForReview(request.AuthorId, [new(approver, approver)], now.AddHours(1),
                ladderPolicy: policy);
            await db.SaveChangesAsync(ct);
        }
        if (request.State == ChangeRequestState.InReview)
        {
            var active = request.ActiveReviewCycle?.Steps
                .SingleOrDefault(x => x.State == ApprovalStepState.Active);
            if (active is not null && !string.Equals(active.ApproverId, approver, StringComparison.OrdinalIgnoreCase))
            {
                request.CancelAndRestartForWrongApprover(request.AuthorId,
                    "The showcase seed uses the curated demo approver for this discipline.",
                    [new(approver, approver)], now.AddHours(2));
            }
            request.ApproveActiveStage(approver, now.AddHours(2));
            await db.SaveChangesAsync(ct);
        }
        if (request.State is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline)
            EnsureApprovedReviewEvidence(request, approver);
        if (request.State is not (ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline))
            throw new InvalidOperationException($"The second showcase request {request.DisplayNumber} did not reach Approved state.");
        return request;
    }

    private static void EnsureApprovedReviewEvidence(SystemChangeRequest request, string expectedApprover)
    {
        var approvedCycles = request.ReviewCycles
            .Where(x => x.State == ReviewCycleState.Approved)
            .ToList();
        var approvedSteps = approvedCycles.SelectMany(x => x.Steps)
            .Where(x => x.State == ApprovalStepState.Approved)
            .ToList();
        if (approvedCycles.Count != 1 || approvedSteps.Count != 1
            || !string.Equals(approvedSteps[0].ApproverId, expectedApprover, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The already-approved second showcase request {request.DisplayNumber} has review evidence "
                + $"that is not exactly one approval by the curated approver '{expectedApprover}'; it was not changed.");
    }

    private async Task EnsureDownstreamAssessmentAsync(SystemChangeRequest request,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.DownstreamChangeAssessments
            .Where(x => x.SourceChangeRequestId == request.Id).ToListAsync(ct);
        if (existing.Any(x => x.TargetLevel != RequirementLevel.LowLevel))
            throw new InvalidOperationException("The second showcase System change has an unexpected downstream assessment target.");
        if (existing.Any()) return;
        var downstream = new DownstreamImpactService(db, policyResolver: resolver);
        await downstream.RaiseForApprovedChangeRequestAsync(request, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid> EnsureBaselineAsync(Guid projectId, Guid releaseId,
        SystemChangeRequest systemRequest, SystemChangeRequest lowLevelRequest,
        DateTimeOffset now, CancellationToken ct)
    {
        var baseline = await db.CandidateBaselines.Include(x => x.Selections)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.BaseNumber == BaselineNumber
                && x.Revision == 0, ct);
        if (baseline is null)
        {
            baseline = new CandidateBaseline(BaselineNumber, 0, projectId, releaseId, null,
                "Configured System and LowLevel baseline", Actor, now);
            db.CandidateBaselines.Add(baseline);
            await db.SaveChangesAsync(ct);
        }
        if (baseline.ReleaseId != releaseId || baseline.PredecessorBaselineId is not null)
            throw new InvalidOperationException("The second showcase baseline has unexpected release identity.");

        foreach (var request in new[] { systemRequest, lowLevelRequest })
        {
            if (baseline.Selections.Any(x => x.ChangeRequestId == request.Id)) continue;
            if (baseline.State != CandidateBaselineState.Draft || request.State != ChangeRequestState.Approved)
                throw new InvalidOperationException("The second showcase baseline is missing a selection but cannot be edited safely.");
            baseline.Select(request, Actor, now);
            await db.SaveChangesAsync(ct);
        }
        if (baseline.State == CandidateBaselineState.Draft)
        {
            baseline.Freeze(Actor, now);
            await db.SaveChangesAsync(ct);
        }
        if (baseline.RequirementsMaterializedAt is null)
        {
            if (baseline.State != CandidateBaselineState.Frozen)
                throw new InvalidOperationException("The second showcase baseline is not frozen and cannot be materialized.");
            var materializer = new RequirementBaselineMaterializer(db,
                new VerificationImpactService(db, policyResolver: resolver), policyResolver: resolver);
            await materializer.MaterializeLegacyHistoricalSeedAsync(baseline.Id, Actor, now.AddDays(1), ct);
        }
        return baseline.Id;
    }

    private async Task EnsureDirectTraceAsync(Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var endpoints = await (from revision in db.RequirementRevisions
                               join artifact in db.Requirements on revision.ArtifactId equals artifact.Id
                               where artifact.ProjectId == projectId
                               select new { artifact.Level, RevisionId = revision.Id }).ToListAsync(ct);
        var systemRevision = endpoints.Single(x => x.Level == RequirementLevel.System).RevisionId;
        var lowLevelRevision = endpoints.Single(x => x.Level == RequirementLevel.LowLevel).RevisionId;
        var exists = await db.RequirementTraces.AnyAsync(x => x.ProjectId == projectId
            && x.SourceRevisionId == lowLevelRevision && x.TargetRevisionId == systemRevision
            && x.Type == RequirementTraceType.DerivedFrom, ct);
        if (exists) return;
        db.RequirementTraces.Add(new RequirementTraceLink(projectId, lowLevelRevision, systemRevision,
            RequirementTraceType.DerivedFrom,
            "The LowLevel implementation directly derives from the configured System requirement.", now));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureControlledDocumentsAsync(Guid projectId, Guid releaseId, Guid baselineId,
        DateTimeOffset now, CancellationToken ct)
    {
        var hash = Hash($"{baselineId}|configured-ladder-showcase");
        var expected = new[]
        {
            (ControlledDocumentType.Sysrd, "SYSRD-000712", "Configured System Requirements Document"),
            (ControlledDocumentType.SwrdLowLevel, "LLRD-000712", "Configured LowLevel Requirements Document"),
        };
        foreach (var (type, number, title) in expected)
        {
            var documents = await db.ControlledDocuments
                .Where(x => x.ProjectId == projectId && x.ReleaseId == releaseId && x.Type == type)
                .ToListAsync(ct);
            if (documents.Count > 1)
                throw new InvalidOperationException($"The second showcase has duplicate {type} controlled documents.");
            if (documents.Count == 1)
            {
                if (documents[0].DocumentNumber != number || documents[0].BaselineId != baselineId)
                    throw new InvalidOperationException($"The second showcase {type} document has unexpected identity.");
                continue;
            }
            db.ControlledDocuments.Add(new ControlledDocument(projectId, releaseId, baselineId, type,
                number, title, 0, hash, 1, now));
            await db.SaveChangesAsync(ct);
        }
    }

    private static SystemChangeRequest BuildSystemRequest(Guid projectId, Guid releaseId, ILadderPolicy policy,
        DateTimeOffset now)
    {
        var request = new SystemChangeRequest("SRCR-00001", 0, projectId, releaseId,
            "Add configured system scheduling behavior", "The configured workspace needs one system behavior.",
            "The system impact was assessed against the authored two-level ladder.",
            "Introduce the system behavior and its verification basis.", SystemsAuthor, now,
            ChangeRequestType.System, ladderPolicy: policy);
        request.AddRequirementChange(SystemsAuthor, "SYSR-00001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce,
            "The configured system shall schedule approved navigation work.",
            "The second showcase demonstrates a System source change.", "Test", now, ladderPolicy: policy);
        return request;
    }

    private static SystemChangeRequest BuildLowLevelRequest(Guid projectId, Guid releaseId, ILadderPolicy policy,
        DateTimeOffset now)
    {
        var request = new SystemChangeRequest("LLRCR-00001", 0, projectId, releaseId,
            "Implement configured scheduling behavior", "The implementation needs one low-level requirement.",
            "The implementation is directly allocated to the system behavior.",
            "Introduce the low-level implementation behavior.", SoftwareAuthor, now,
            ChangeRequestType.Software, softwareLevel: RequirementLevel.LowLevel, ladderPolicy: policy);
        request.AddRequirementChange(SoftwareAuthor, "LLR-00001", 0, RequirementLevel.LowLevel,
            RequirementChangeKind.Introduce,
            "The configured low-level component shall implement deterministic scheduling.",
            "The second showcase demonstrates a LowLevel child with a direct System trace.", "Test", now,
            ladderPolicy: policy);
        return request;
    }

    private async Task<SecondShowcaseSummary> SummarizeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == ReleaseVersion)
            .Select(x => x.Id).SingleAsync(ct);
        var baselineId = await db.CandidateBaselines
            .Where(x => x.ProjectId == projectId && x.BaseNumber == BaselineNumber && x.Revision == 0)
            .Select(x => x.Id).SingleAsync(ct);
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
