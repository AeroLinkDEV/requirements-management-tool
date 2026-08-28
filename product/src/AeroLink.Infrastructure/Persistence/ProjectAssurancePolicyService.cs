using AeroLink.Domain.Assurance;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record AssuranceSelectionDraft(AssurancePolicyLever Lever, AssuranceLeverValue Value);

/// <summary>What the project supplies to justify one relaxation: why, where it applies, and who approves it.</summary>
public sealed record AssuranceDeviationDraft(
    AssurancePolicyLever Lever, string Scope, string Rationale, bool AirworthinessDesignated, string ApproverUserName);

public sealed record AssurancePolicyDraft(
    int ExpectedVersion,
    AssuranceLevel DeclaredLevel,
    string Reason,
    IReadOnlyList<AssuranceSelectionDraft> Selections,
    IReadOnlyList<AssuranceDeviationDraft> Deviations);

public enum AssurancePolicyResultKind { Success, NotFound, Conflict, Invalid, Refused }

public sealed record AssurancePolicyResult(AssurancePolicyResultKind Kind, string? Error, AssurancePolicyView? Policy);

/// <summary>One lever as the configuration screen reads it: what is set, what is recommended, and why.</summary>
public sealed record AssuranceLeverView(
    string Lever, string Name, string Description, string EnforcementPoint,
    string Selected, string SelectedName, string SelectedEffect,
    string Recommended, string RecommendedName, string RecommendationBasis, string BasisKind,
    string DeviationClass, string ReleaseEffect, bool IsRelaxation,
    IReadOnlyList<AssuranceLeverOptionView> Options);

public sealed record AssuranceLeverOptionView(string Value, string Name, string Effect, bool IsRelaxation);

public sealed record AssuranceDeviationView(
    Guid Id, string Lever, string LeverName, string Scope, string Recommended, string RecommendationBasis,
    string BasisKind, string Selected, string Rationale, string DeviationClass, bool AirworthinessDesignated,
    string ProposedBy, DateTimeOffset ProposedAt, string ApprovedBy, string ApprovalAuthority,
    string ApprovalAuthoritySource, int AuthorityPolicyVersion, DateTimeOffset EffectiveFrom,
    DateTimeOffset? SupersededAt, string SupersededBy, string SupersededReason, string ReleaseEffect,
    string RecordHash, bool RecordVerified);

public sealed record AssurancePolicyVersionView(
    int Version, string DeclaredLevel, string Reason, string CreatedBy, DateTimeOffset EffectiveFrom,
    DateTimeOffset? SupersededAt, string SupersededBy, string SnapshotHash, string SelectionsSnapshot);

public sealed record AssuranceAuthorityRuleView(
    string DeviationClass, IReadOnlyList<string> ApprovingRoles, int MinimumApprovals,
    bool DelegationAllowed, bool SelfApprovalAllowed);

public sealed record AssurancePolicyView(
    Guid ProjectId, int Version, string DeclaredLevel, int AuthorityPolicyVersion, bool CanManage,
    string MappingNotice, string ClaimBoundary,
    IReadOnlyList<AssuranceLeverView> Levers,
    IReadOnlyList<AssuranceDeviationView> Deviations,
    IReadOnlyList<AssurancePolicyVersionView> History,
    IReadOnlyList<AssuranceAuthorityRuleView> AuthorityRules);

/// <summary>
/// Records and reads a project's declared assurance policy.
///
/// Everything a policy change has to be true about is enforced here rather than at the endpoint: the
/// selections are controlled values, a relaxation carries a rationale and an approval the shared authority
/// resolver permitted, and a new version supersedes the old rather than overwriting it. The endpoint's job
/// is authentication, authorisation to *record*, and translating the result to a status code.
/// </summary>
public sealed class ProjectAssurancePolicyService(AeroLinkDbContext db)
{
    /// <summary>
    /// The notice #711 requires every assurance screen to carry. Kept beside the data it qualifies so an
    /// API consumer that is not the AeroLink client cannot render the settings without it.
    /// </summary>
    public const string MappingNotice =
        "No certification-derived recommendation mapping has been approved for this installation. "
        + "The settings below are AeroLink project-policy defaults.";

    public const string ClaimBoundary =
        "This records the project's declared policy. AeroLink has not assessed conformity to any certification standard.";

    public async Task<AssurancePolicyView?> ReadAsync(Guid projectId, bool canManage, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == projectId, ct)) return null;
        var versions = await db.ProjectAssurancePolicies.AsNoTracking()
            .Where(x => x.ProjectId == projectId).OrderByDescending(x => x.Version).ToListAsync(ct);
        var effective = versions.FirstOrDefault(x => x.SupersededAt is null);
        var resolved = EffectiveProjectAssurancePolicyResolver.Project(effective);
        var deviations = await db.AssurancePolicyDeviations.AsNoTracking()
            .Where(x => x.ProjectId == projectId).ToListAsync(ct);
        return View(projectId, resolved, canManage, versions, deviations);
    }

    public async Task<AssurancePolicyResult> RecordAsync(Guid projectId, AssurancePolicyDraft draft,
        Guid actorAccountId, string actorUserName, DateTimeOffset now, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId, ct);
        if (project is null) return new(AssurancePolicyResultKind.NotFound, "The project does not exist.", null);

        var versions = await db.ProjectAssurancePolicies.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var effective = versions.SingleOrDefault(x => x.SupersededAt is null);
        var currentVersion = effective?.Version ?? 0;
        if (draft.ExpectedVersion != currentVersion)
            return new(AssurancePolicyResultKind.Conflict,
                $"The assurance policy has moved on: this edit expected version {draft.ExpectedVersion} and the project is at {currentVersion}.", null);

        if (string.IsNullOrWhiteSpace(draft.Reason))
            return new(AssurancePolicyResultKind.Invalid, "A meaningful reason is required for every assurance policy change.", null);

        var current = EffectiveProjectAssurancePolicyResolver.Project(effective);
        var selections = new Dictionary<AssurancePolicyLever, AssuranceLeverValue>(current.Selections);
        foreach (var selection in draft.Selections ?? [])
        {
            var definition = AssurancePolicyCatalogue.Definition(selection.Lever);
            if (!definition.Accepts(selection.Value))
                return new(AssurancePolicyResultKind.Invalid,
                    $"{selection.Value} is not a supported setting for the {definition.Name} policy lever.", null);
            selections[selection.Lever] = selection.Value;
        }

        // Which levers become relaxations that were not relaxations before. A relaxation already carried by
        // an effective deviation is not re-approved: the decision was taken, and asking for it again on every
        // unrelated policy edit would bury the record that matters under identical copies of itself.
        var effectiveDeviations = await db.AssurancePolicyDeviations
            .Where(x => x.ProjectId == projectId && x.SupersededAt == null).ToListAsync(ct);
        var newRelaxations = new List<AssuranceLeverDefinition>();
        foreach (var definition in AssurancePolicyCatalogue.All)
        {
            var selected = selections[definition.Lever];
            var carried = effectiveDeviations.SingleOrDefault(x => x.Lever == definition.Lever);
            if (definition.IsRelaxation(selected) && (carried is null || carried.SelectedValue != selected))
                newRelaxations.Add(definition);
        }

        var duplicate = (draft.Deviations ?? []).GroupBy(x => x.Lever).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            return new(AssurancePolicyResultKind.Invalid,
                $"The {AssurancePolicyCatalogue.Definition(duplicate.Key).Name} lever carries more than one deviation in this request. "
                + "One relaxation is one decision.", null);
        var supplied = (draft.Deviations ?? []).ToDictionary(x => x.Lever);
        foreach (var definition in newRelaxations)
            if (!supplied.ContainsKey(definition.Lever))
                return new(AssurancePolicyResultKind.Invalid,
                    $"Selecting '{definition.Option(selections[definition.Lever]).Name}' for {definition.Name} is looser than the AeroLink recommendation, "
                    + "so it requires a recorded deviation with its rationale and an authorised approver.", null);
        foreach (var lever in supplied.Keys)
            if (newRelaxations.All(x => x.Lever != lever))
                return new(AssurancePolicyResultKind.Invalid,
                    $"A deviation was supplied for {AssurancePolicyCatalogue.Definition(lever).Name}, but that selection is not a relaxation of the AeroLink recommendation.", null);

        // Resolve every approval before writing anything, so a refused approver cannot leave a half-recorded
        // policy behind.
        var approved = new List<(AssuranceLeverDefinition Definition, AssuranceDeviationDraft Draft,
            UserAccount Approver, AssuranceDeviationClass Class, AssuranceAuthorityDecision Decision)>();
        foreach (var definition in newRelaxations)
        {
            var deviation = supplied[definition.Lever];
            if (string.IsNullOrWhiteSpace(deviation.Rationale))
                return new(AssurancePolicyResultKind.Invalid,
                    $"A rationale is required for the {definition.Name} deviation.", null);
            if (string.IsNullOrWhiteSpace(deviation.ApproverUserName))
                return new(AssurancePolicyResultKind.Invalid,
                    $"An approving authority is required for the {definition.Name} deviation.", null);

            var approver = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(
                x => x.UserName == deviation.ApproverUserName && x.State == AccountState.Active, ct);
            if (approver is null)
                return new(AssurancePolicyResultKind.Invalid,
                    $"'{deviation.ApproverUserName}' is not an active AeroLink account, so they cannot approve the {definition.Name} deviation.", null);

            var deviationClass = AssurancePolicyDeviation.ClassOf(definition, deviation.AirworthinessDesignated);
            var facts = await ApproverFactsAsync(project.ProgramId, approver, ct);
            var decision = AssuranceDeviationAuthority.Decide(deviationClass, project.ProgramId, actorAccountId, facts, now);
            if (!decision.Permitted) return new(AssurancePolicyResultKind.Refused, decision.Reason, null);
            approved.Add((definition, deviation, approver, deviationClass, decision));
        }

        var version = ProjectAssurancePolicy.Record(projectId, currentVersion + 1, draft.DeclaredLevel,
            selections, draft.Reason, actorUserName, now);
        effective?.Supersede(actorUserName, now);
        db.ProjectAssurancePolicies.Add(version);

        // A carried deviation ends when its lever's selection changes — including when it returns to the
        // recommendation. Superseding rather than deleting keeps the record of what was relaxed, and when it
        // stopped being relaxed, which is the question a later reader actually asks.
        foreach (var carried in effectiveDeviations)
        {
            var selected = selections[carried.Lever];
            if (carried.SelectedValue == selected) continue;
            var definition = AssurancePolicyCatalogue.Definition(carried.Lever);
            carried.Supersede(actorUserName,
                definition.IsRelaxation(selected)
                    ? $"Superseded by assurance policy version {version.Version}, which selected {definition.Option(selected).Name}."
                    : $"Superseded by assurance policy version {version.Version}, which returned {definition.Name} to the AeroLink recommendation.",
                now);
        }

        foreach (var (definition, deviationDraft, approver, deviationClass, decision) in approved)
            db.AssurancePolicyDeviations.Add(AssurancePolicyDeviation.Approve(projectId, version.Id, version.Version,
                definition, string.IsNullOrWhiteSpace(deviationDraft.Scope) ? "Project" : deviationDraft.Scope,
                selections[definition.Lever], deviationDraft.Rationale, deviationClass,
                deviationDraft.AirworthinessDesignated, actorAccountId, actorUserName, approver.Id,
                approver.UserName, decision, now));

        await db.SaveChangesAsync(ct);
        return new(AssurancePolicyResultKind.Success, null, await ReadAsync(projectId, true, ct));
    }

    /// <summary>
    /// The facts the shared resolver decides on.
    ///
    /// Roles come from live memberships only. The ordinary project role check answers true for any
    /// administrator, and reusing it here would silently hand assurance authority to technical
    /// administration — which the product-owner decision refuses outright. Standing role backups are also
    /// deliberately excluded: a backup carries no interval, and the decision accepts delegated authority only
    /// where an explicit recorded delegation states its scope, effective date and expiry.
    /// </summary>
    private async Task<AssuranceApproverFacts> ApproverFactsAsync(Guid programId, UserAccount approver, CancellationToken ct)
    {
        var roles = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.UserId == approver.Id && x.EndedAt == null)
            .Select(x => x.Role).ToListAsync(ct);
        var delegations = await db.RoleDelegations.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.DelegateUserId == approver.Id)
            .Select(x => new { x.Role, x.ProgramId, x.StartsAt, x.EndsAt, x.RevokedAt })
            .ToListAsync(ct);
        // The position-governed demands this approver actually answers. Resolved through the shared resolver
        // so an assurance approval and a document signature agree about who holds a position, and so losing
        // the eligibility retires the assurance authority at the same moment it retires the rest.
        var resolver = new ProjectAuthorityResolver(db);
        var leadershipAuthorities = new List<ProgramRole>();
        foreach (var role in AssuranceAuthorityPolicy.Version(AssuranceAuthorityPolicy.CurrentVersion)
                     .SelectMany(rule => rule.ApprovingRoles).Distinct())
            if ((await resolver.ResolveAnyLeadershipSatisfyingAsync(approver.Id, programId, role, ct)).Granted)
                leadershipAuthorities.Add(role);

        return new(approver.Id, approver.UserName, roles,
            delegations.Select(x => new AssuranceDelegationFact(x.Role, x.ProgramId, x.StartsAt, x.EndsAt, x.RevokedAt is not null)).ToList(),
            approver.UserName == IdentityService.SystemAdministratorUserName || roles.Contains(ProgramRole.Administrator),
            leadershipAuthorities);
    }

    private static AssurancePolicyView View(Guid projectId, ResolvedAssurancePolicy resolved, bool canManage,
        IReadOnlyList<ProjectAssurancePolicy> versions, IReadOnlyList<AssurancePolicyDeviation> deviations) => new(
        projectId,
        resolved.Version,
        resolved.DeclaredLevel.ToString(),
        AssuranceAuthorityPolicy.CurrentVersion,
        canManage,
        MappingNotice,
        ClaimBoundary,
        AssurancePolicyCatalogue.All.Select(definition =>
        {
            var selected = resolved.Value(definition.Lever);
            return new AssuranceLeverView(
                definition.Lever.ToString(), definition.Name, definition.Description, definition.EnforcementPoint,
                selected.ToString(), definition.Option(selected).Name, definition.Option(selected).Effect,
                definition.RecommendedValue.ToString(), definition.Option(definition.RecommendedValue).Name,
                definition.RecommendationBasis, definition.BasisKind.ToString(),
                definition.DeviationClass.ToString(), definition.ReleaseEffect,
                definition.IsRelaxation(selected),
                definition.Options.Select(option => new AssuranceLeverOptionView(
                    option.Value.ToString(), option.Name, option.Effect, definition.IsRelaxation(option.Value))).ToList());
        }).ToList(),
        deviations.OrderByDescending(x => x.EffectiveFrom).ThenBy(x => x.Lever.ToString(), StringComparer.Ordinal)
            .Select(x => new AssuranceDeviationView(
                x.Id, x.Lever.ToString(), AssurancePolicyCatalogue.Definition(x.Lever).Name, x.Scope,
                x.RecommendedValue.ToString(), x.RecommendationBasis, x.BasisKind.ToString(),
                x.SelectedValue.ToString(), x.Rationale, x.DeviationClass.ToString(), x.AirworthinessDesignated,
                x.ProposedBy, x.ProposedAt, x.ApprovedBy,
                AssuranceDeviationAuthority.Readable(x.ApprovalAuthority), x.ApprovalAuthoritySource.ToString(),
                x.AuthorityPolicyVersion, x.EffectiveFrom, x.SupersededAt, x.SupersededBy, x.SupersededReason,
                x.ReleaseEffect, x.RecordHash, x.VerifyRecord())).ToList(),
        versions.OrderByDescending(x => x.Version).Select(x => new AssurancePolicyVersionView(
            x.Version, x.DeclaredLevel.ToString(), x.Reason, x.CreatedBy, x.EffectiveFrom, x.SupersededAt,
            x.SupersededBy, x.SnapshotHash, x.SelectionsSnapshot)).ToList(),
        AssuranceAuthorityPolicy.Version(AssuranceAuthorityPolicy.CurrentVersion)
            .Select(rule => new AssuranceAuthorityRuleView(
                rule.Class.ToString(), rule.ApprovingRoles.Select(AssuranceDeviationAuthority.Readable).ToList(),
                rule.MinimumApprovals, rule.DelegationAllowed, rule.SelfApprovalAllowed)).ToList());
}
