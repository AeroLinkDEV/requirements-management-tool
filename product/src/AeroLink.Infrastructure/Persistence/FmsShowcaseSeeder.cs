using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>One checked expectation about the showcase, and whether it currently holds.</summary>
public sealed record ShowcaseInvariant(string Key, bool Holds, string Detail);

public sealed record ShowcaseUpgradeAuthorityDecision(bool Ready, string Code, string Detail,
    DateTimeOffset? ClosureAt = null);

public sealed record FmsShowcaseSummary(Guid ProgramId, Guid ProjectId, Guid ReleasedBaselineId, Guid ActiveReleaseId,
    int SystemRequirements, int HighLevelRequirements, int LowLevelRequirements, int HistoricalScrs,
    int HistoricalSwcrs, int TraceLinks, int TestProcedures, int TestExecutions, int Documents);

public sealed class FmsShowcaseSeeder(AeroLinkDbContext db, IProjectLadderPolicyResolver? policyResolver = null)
{
    private static readonly SemaphoreSlim UpgradeGate = new(1, 1);
    private readonly IProjectLadderPolicyResolver resolver = policyResolver ?? new EffectiveProjectLadderPolicyResolver(db);
    public const string ProgramCode = "FMSLIVE";
    private const string QualityAnalystUserName = "quality.analyst";
    // The fresh showcase is a historical record. Its SQA authority must therefore pre-date the deterministic
    // Build 1.5 closure evidence below; using UtcNow would make a new seed approve work before the authority
    // that supposedly approved it existed.
    private static readonly DateTimeOffset FreshSqaMembershipGrantedAt = new(2024, 1, 8, 14, 0, 0, TimeSpan.Zero);
    // Scenario ownership is recorded in one immutable upgrade-step row per artifact. Prose markers below
    // are display breadcrumbs only; they are never used to locate or mutate controlled rows.
    private const string InterfaceScenarioMarkerPrefix = "[FMSLIVE showcase scenario: interface-";
    private const string ProblemReportScenarioMarkerPrefix = "[FMSLIVE showcase scenario: problem-report-";
    private const string InterfaceScenarioStepPrefix = "scenario-richness/interface/";
    private const string ProblemReportScenarioStepPrefix = "scenario-richness/problem-report/";
    private const string ProblemReportVerificationExecutionStepPrefix = "scenario-richness/problem-report-verification/";
    private const int PreferredScenarioNumber = 86601;
    private static readonly string[] InterfaceScenarioAuthors = ["systems.author", "software.author"];
    private static readonly string[] ProblemReportOwners =
        ["systems.author", "software.author", "test.engineer", "engineer.demo", "test.author", "test.engineer", "software.author", "systems.author"];
    private static readonly (ProjectLeadershipPosition Position, string UserName, ProgramRole RequiredRole)[]
        ShowcaseLeadershipRoster =
        [
            (ProjectLeadershipPosition.ProjectEngineer, "project.lead", ProgramRole.ProjectEngineer),
            (ProjectLeadershipPosition.ProgramManager, "program.manager", ProgramRole.ProgramManager),
            (ProjectLeadershipPosition.EngineeringManager, "engineering.manager", ProgramRole.EngineeringManager),
            (ProjectLeadershipPosition.ConfigurationManager, "cm.fms", ProgramRole.ConfigurationManager),
            (ProjectLeadershipPosition.SystemEngineeringLead, "systems.lead", ProgramRole.SystemEngineer),
            (ProjectLeadershipPosition.SoftwareEngineeringLead, "software.lead", ProgramRole.SoftwareEngineer),
            (ProjectLeadershipPosition.SystemTestLead, "test.engineer", ProgramRole.SystemTestEngineer),
            (ProjectLeadershipPosition.SoftwareTestLead, "test.author", ProgramRole.SoftwareTestEngineer),
        ];
    private static readonly string[] Topics = ["flight plan", "lateral navigation", "vertical navigation", "performance prediction", "navigation database", "guidance", "radio navigation", "position estimation", "fuel management", "crew interface", "departure procedures", "arrival procedures", "approach management", "airspace constraints", "route sequencing"];

    private static string InterfaceScenarioMarker(int index) => $"{InterfaceScenarioMarkerPrefix}{index:D2}]";
    private static string ProblemReportScenarioMarker(int index) => $"{ProblemReportScenarioMarkerPrefix}{index:D2}]";
    private static string InterfaceScenarioStepKey(int index) => $"{InterfaceScenarioStepPrefix}{index:D2}";
    private static string ProblemReportScenarioStepKey(int index) => $"{ProblemReportScenarioStepPrefix}{index:D2}";
    private static string ProblemReportVerificationExecutionStepKey(int index) =>
        $"{ProblemReportVerificationExecutionStepPrefix}{index:D2}";

    // Only the historical Build 1.5 scenarios carry governed closure evidence. Build 1.6 remains an
    // in-work release and its Problem Reports deliberately stop at an honest active/rejected state.
    private static bool IsHistoricalProblemReportScenario(int index) => index <= 4 || index is 6 or 7;

    public async Task<FmsShowcaseSummary> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (existing is not null)
        {
            // An existing FMS database is operator-owned state. Startup and the showcase seed endpoint are
            // discovery/retry boundaries, not approval to add controlled scenarios, memberships, or closure
            // evidence. The explicit /api/showcase/upgrade command is the backup-confirmed repair path.
            return await SummarizeAsync(existing.Id, ct);
        }

        await UpgradeGate.WaitAsync(ct);
        try
        {
            var isolation = db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
            if (db.Database.IsNpgsql())
                // Serialize first creation across API instances. The second creator rechecks after the lock
                // and observes the committed Program instead of racing a duplicate or a partial dataset.
                await AcquirePostgresAdvisoryLockAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext({"aerolink-showcase-seed"}))", ct);
            existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
            if (existing is not null)
            {
                await transaction.CommitAsync(ct);
                return await SummarizeAsync(existing.Id, ct);
            }

        var start = new DateTimeOffset(2024, 1, 8, 14, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Flight Management System Live Program", ProgramCode);
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var release15 = new SoftwareRelease(project.Id, "1.5", true); var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        var ladder = NewProjectLadderFactory.Create(project.Id, start);
        // The showcase project carries a persisted verification-method vocabulary like any other (#701).
        var vocabulary = ProjectVerificationVocabulary.Founding(project.Id, start);
        db.AddRange(program, project, release15, release16, ladder, vocabulary); await db.SaveChangesAsync(ct);

        var historical = new List<SystemChangeRequest>();
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"SRCR-{i:D5}", ChangeRequestType.System, RequirementLevel.System, 5, (i - 1) * 5, project.Id, release15.Id, start.AddDays(i), "system"));
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"HLRCR-{i:D5}", ChangeRequestType.Software, RequirementLevel.HighLevel, i <= 10 ? 14 : 13, (i - 1) * 13 + Math.Min(i - 1, 10), project.Id, release15.Id, start.AddDays(40 + i), "HLR"));
        for (var i = 1; i <= 45; i++) historical.Add(BuildHistoricalRequest($"LLRCR-{i + 30:D5}", ChangeRequestType.Software, RequirementLevel.LowLevel, i <= 25 ? 16 : 15, (i - 1) * 15 + Math.Min(i - 1, 25), project.Id, release15.Id, start.AddDays(80 + i), "LLR"));
        db.SystemChangeRequests.AddRange(historical); await db.SaveChangesAsync(ct);

        var baseline15 = new CandidateBaseline("SW-01.50", 0, project.Id, release15.Id, null, "FMS 1.5 Released Software Build", "cm.fms", start.AddDays(150));
        foreach (var request in historical) baseline15.Select(request, "cm.fms", start.AddDays(150));
        baseline15.Freeze("cm.fms", start.AddDays(151)); db.CandidateBaselines.Add(baseline15); await db.SaveChangesAsync(ct);
        await new RequirementBaselineMaterializer(db, new VerificationImpactService(db, policyResolver: resolver))
            .MaterializeLegacyHistoricalSeedAsync(baseline15.Id, "cm.fms", start.AddDays(152), ct,
                joinExistingTransaction: true);

        var currentRows = await (from member in db.BaselineRequirements.Where(x => x.BaselineId == baseline15.Id)
                                 join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                                 join revision in db.RequirementRevisions on member.RevisionId equals revision.Id
                                 select new { artifact, revision }).ToListAsync(ct);
        foreach (var row in currentRows.Where(x => x.revision.Revision > 0))
            for (var rev = 0; rev < row.revision.Revision; rev++) db.RequirementRevisions.Add(new RequirementRevision(row.artifact.Id, rev,
                HistoricalStatement(row.artifact.Level, row.artifact.BaseNumber, rev), "Earlier approved wording retained for history.", row.revision.VerificationMethod,
                RequirementRevisionState.Superseded, row.revision.SourceChangeRequestId!.Value, baseline15.Id, start.AddDays(10 + rev)));
        await db.SaveChangesAsync(ct);

        var current = currentRows.ToDictionary(x => x.artifact.BaseNumber, x => new CurrentRequirement(x.artifact, x.revision));
        var systems = current.Values.Where(x => x.Artifact.Level == RequirementLevel.System).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var hlrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.HighLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var llrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.LowLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        db.RequirementTraces.AddRange(hlrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, systems[i % systems.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Allocated software behavior satisfies the parent system requirement.", start.AddDays(153))));
        db.RequirementTraces.AddRange(llrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, hlrs[i % hlrs.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Detailed behavior implements the parent high-level requirement.", start.AddDays(153))));
        await db.SaveChangesAsync(ct);

        var build15 = new SoftwareBuild(project.Id, release15.Id, baseline15.Id, "SW-01.50", "Released operational FMS 1.5 software configuration.", "cm.fms", start.AddDays(160));
        db.SoftwareBuilds.Add(build15); await db.SaveChangesAsync(ct);
        var procedures = new List<(TestProcedure Procedure, TestProcedureRevision Revision, List<Guid> Requirements)>();
        procedures.AddRange(BuildProcedures(project.Id, baseline15.Id, systems.Select(x => x.Revision.Id).ToList(), 75, TestProcedureLevel.System, "SYSTP", start.AddDays(154)));
        procedures.AddRange(BuildProcedures(project.Id, baseline15.Id, hlrs.Select(x => x.Revision.Id).ToList(), 160, TestProcedureLevel.HighLevel, "HLRTC", start.AddDays(155)));
        procedures.AddRange(BuildProcedures(project.Id, baseline15.Id, llrs.Select(x => x.Revision.Id).ToList(), 280, TestProcedureLevel.LowLevel, "LLRTC", start.AddDays(156)));
        db.TestProcedures.AddRange(procedures.Select(x => x.Procedure)); db.TestProcedureRevisions.AddRange(procedures.Select(x => x.Revision));
        db.TestCoverage.AddRange(procedures.SelectMany(x => x.Requirements.Select(req => new TestRequirementCoverage(x.Revision.Id, req)))); await db.SaveChangesAsync(ct);
        // This fresh showcase is created after exact procedure manifests exist, so record Build 1.5's
        // configuration before any Build 1.6 draft revision is introduced. Existing historical databases are
        // deliberately not backfilled by an upgrade step: inferring an unrecorded manifest would fabricate
        // controlled history.
        db.BaselineTestProcedures.AddRange(procedures.Select(x =>
            new BaselineTestProcedureSelection(baseline15.Id, x.Procedure.Id, x.Revision.Id)));
        var procedureManifest = string.Join(";", procedures.OrderBy(x => x.Procedure.BaseNumber)
            .Select(x => $"{x.Procedure.BaseNumber}.{x.Revision.Revision:D2}:{x.Revision.Id}"));
        baseline15.MarkTestProceduresMaterialized("cm.fms", Hash(procedureManifest), procedures.Count,
            start.AddDays(158));
        await db.SaveChangesAsync(ct);
        var executionNumber = 0;
        foreach (var item in procedures)
        {
            executionNumber++; var executed = start.AddDays(157).AddMinutes(executionNumber);
            if (executionNumber % 103 == 0)
            {
                var fail = new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Fail, "test.engineer", "FMS integration rig / build 1.5", "Initial observation did not satisfy the expected result.", $"evidence/fms-1.5/fail-{executionNumber:D4}.json", executed, executed, release15.Id);
                db.TestExecutions.Add(fail); db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, fail.Id, TestOutcome.Pass, "test.engineer", "FMS integration rig / corrected configuration", "Retest successfully verified every linked requirement.", $"evidence/fms-1.5/retest-{executionNumber:D4}.json", executed.AddHours(2), executed.AddHours(2), release15.Id));
            }
            else db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Pass, "test.engineer", "FMS integration rig / build 1.5", "Observed results satisfy the approved expected result and linked requirements.", $"evidence/fms-1.5/pass-{executionNumber:D4}.json", executed, executed, release15.Id));
        }
        await db.SaveChangesAsync(ct);

        var docSpecs = new[] {
            (ControlledDocumentType.Sysrd,"SYSRD-000015","FMS System Requirements Document",150),
            (ControlledDocumentType.SwrdHighLevel,"HLRD-000015","FMS High-Level Software Requirements Document",400),
            (ControlledDocumentType.SwrdLowLevel,"LLRD-000015","FMS Low-Level Software Requirements Document",700),
            (ControlledDocumentType.SystemTestProcedures,"SYSTD-000015","FMS System Test Procedures",75),
            (ControlledDocumentType.HighLevelTestCases,"HLRTD-000015","FMS HLR Test Cases",160),
            (ControlledDocumentType.LowLevelTestCases,"LLRTD-000015","FMS LLR Test Cases",280) };
        foreach (var spec in docSpecs) db.ControlledDocuments.Add(new ControlledDocument(project.Id, release15.Id, baseline15.Id, spec.Item1, spec.Item2, spec.Item3, 0, Hash($"{baseline15.RequirementsHash}|{spec.Item1}|{spec.Item4}"), spec.Item4, start.AddDays(159)));

        var activeRequests = BuildActive16Requests(project.Id, release16.Id, current, start.AddDays(300));
        db.SystemChangeRequests.AddRange(activeRequests); await db.SaveChangesAsync(ct);

        // Approval is what raises verification work, and these change requests were approved directly rather
        // than through the endpoint that normally does it. Without this the showcase presents an empty change
        // impact queue while simultaneously showing approved changes that introduce and modify requirements —
        // the one state the product says is impossible.
        var verificationImpact = new VerificationImpactService(db, policyResolver: resolver);
        var downstreamImpact = new DownstreamImpactService(db, policyResolver: resolver);
        foreach (var request in activeRequests.Where(x => x.State is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline))
        {
            await verificationImpact.RaiseForApprovedChangeRequestAsync(request, start.AddDays(305), ct);
            await downstreamImpact.RaiseForApprovedChangeRequestAsync(request, start.AddDays(305), ct);
        }
        await db.SaveChangesAsync(ct);
        var baseline16 = new CandidateBaseline("SW-01.60", 0, project.Id, release16.Id, baseline15.Id, "FMS 1.6 In-Work Software Build", "cm.fms", start.AddDays(310));
        foreach (var request in activeRequests.Where(x => x.State == ChangeRequestState.Approved).Take(2)) baseline16.Select(request, "cm.fms", start.AddDays(311));
        db.CandidateBaselines.Add(baseline16); await db.SaveChangesAsync(ct);
        // The fresh path runs the same ordered steps, so a database seeded today records them as applied
        // and a later start does not try to reconcile what was just built.
        await EnsureQualityAnalystMembershipAsync(program.Id, FreshSqaMembershipGrantedAt, ct);
        await EnsureFreshControlledActorMembershipsAsync(program.Id, FreshSqaMembershipGrantedAt, ct);
        await EnsureFreshLeadershipRosterAsync(program.Id, FreshSqaMembershipGrantedAt, ct);
        await ApplyUpgradeStepsAsync(program.Id, ct);
        await transaction.CommitAsync(ct);
        return await SummarizeAsync(program.Id, ct);
        }
        finally
        {
            UpgradeGate.Release();
        }
    }

    private async Task<bool> EnsureQualityAnalystMembershipAsync(Guid programId, DateTimeOffset grantedAt, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserName == QualityAnalystUserName && x.State == AccountState.Active, ct)
            ?? throw new InvalidOperationException(
                "The seeded quality.analyst account is required before FMS closure scenarios can be frozen.");

        var history = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == account.Id && x.ProgramId == programId
                && x.Role == ProgramRole.SoftwareQualityAnalyst)
            .ToListAsync(ct);
        if (history.Any(x => x.EndedAt is null)) return true;
        // An ended membership is an intentional authority decision. Do not restore it merely because the
        // deterministic showcase happens to contain a closed Problem Report. The enrichment step will keep
        // closure scenarios in their honest in-work state until an operator grants authority again.
        if (history.Count > 0) return false;

        db.ProgramMemberships.Add(new ProgramMembership(account.Id, programId, ProgramRole.SoftwareQualityAnalyst,
            "system.bootstrap", grantedAt));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static readonly (string UserName, ProgramRole[] Roles)[] FreshControlledActors =
    [
        ("systems.author", [ProgramRole.Engineer, ProgramRole.SystemEngineer]),
        ("software.author", [ProgramRole.Engineer, ProgramRole.SoftwareEngineer]),
        ("systems.reviewer", [ProgramRole.SystemEngineer]),
        ("assurance.reviewer", [ProgramRole.SoftwareQualityAnalyst]),
        ("lead.reviewer", [ProgramRole.SoftwareEngineer]),
        ("manager.reviewer", [ProgramRole.ProgramManager]),
        ("cm.fms", [ProgramRole.ConfigurationManager]),
        ("test.author", [ProgramRole.TestEngineer]),
        ("test.engineer", [ProgramRole.TestEngineer]),
        ("engineer.demo", [ProgramRole.Engineer]),
        ("release.manager", [ProgramRole.ConfigurationManager, ProgramRole.ProgramManager]),
        ("program.manager", [ProgramRole.ProgramManager]),
        ("software.lead", [ProgramRole.SoftwareEngineer]),
    ];

    private async Task EnsureFreshControlledActorMembershipsAsync(Guid programId, DateTimeOffset grantedAt,
        CancellationToken ct)
    {
        var names = FreshControlledActors.Select(x => x.UserName).ToArray();
        var accounts = await db.UserAccounts.Where(x => names.Contains(x.UserName)).ToDictionaryAsync(x => x.UserName,
            StringComparer.OrdinalIgnoreCase, ct);
        var existing = (await db.ProgramMemberships.AsNoTracking().Where(x => x.ProgramId == programId).ToListAsync(ct))
            .Select(x => (x.UserId, x.Role)).ToHashSet();
        foreach (var actor in FreshControlledActors)
        {
            if (!accounts.TryGetValue(actor.UserName, out var account) || account.State != AccountState.Active)
                throw new InvalidOperationException($"The seeded {actor.UserName} account is required and must be active before FMS controlled scenarios are created.");
            foreach (var role in actor.Roles)
                if (existing.Add((account.Id, role)))
                    db.ProgramMemberships.Add(new ProgramMembership(account.Id, programId, role, "system.bootstrap", grantedAt));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureFreshLeadershipRosterAsync(Guid programId, DateTimeOffset assignedAt,
        CancellationToken ct)
    {
        var names = ShowcaseLeadershipRoster.Select(x => x.UserName).ToArray();
        var accounts = await db.UserAccounts.Where(x => names.Contains(x.UserName))
            .ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var expected in ShowcaseLeadershipRoster)
            if (!accounts.TryGetValue(expected.UserName, out var account) || account.State != AccountState.Active)
                throw new InvalidOperationException(
                    $"The seeded {expected.UserName} account is required and must be active before the FMS {expected.Position} position is created.");

        var memberships = await db.ProgramMemberships.Where(x => x.ProgramId == programId).ToListAsync(ct);
        var assignments = await db.ProjectLeadershipAssignments.Where(x => x.ProgramId == programId).ToListAsync(ct);
        foreach (var expected in ShowcaseLeadershipRoster)
        {
            var account = accounts[expected.UserName];
            if (!memberships.Any(x => x.UserId == account.Id && x.Role == expected.RequiredRole && x.EndedAt == null))
            {
                var historical = memberships.Any(x => x.UserId == account.Id && x.Role == expected.RequiredRole);
                if (historical)
                    throw new InvalidOperationException(
                        $"The fresh FMS {expected.Position} holder has an ended {expected.RequiredRole} membership that cannot be silently restored.");
                db.ProgramMemberships.Add(new ProgramMembership(account.Id, programId, expected.RequiredRole,
                    "system.bootstrap", assignedAt));
            }

            var history = assignments.Where(x => x.Position == expected.Position).ToList();
            if (history.Count == 0)
                db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(programId, expected.Position,
                    account.Id, "system.bootstrap", assignedAt));
            else if (history.Count != 1 || history[0].EndedAt is not null || history[0].HolderUserId != account.Id)
                throw new InvalidOperationException(
                    $"The fresh FMS {expected.Position} position has conflicting assignment history.");
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly (string UserName, ProgramRole Role)[] InterfaceScenarioActors =
    [
        ("systems.author", ProgramRole.Engineer),
        ("systems.author", ProgramRole.SystemEngineer),
        ("software.author", ProgramRole.Engineer),
        ("software.author", ProgramRole.SoftwareEngineer),
        ("systems.reviewer", ProgramRole.SystemEngineer),
        ("assurance.reviewer", ProgramRole.SoftwareQualityAnalyst),
        ("lead.reviewer", ProgramRole.SoftwareEngineer),
        ("manager.reviewer", ProgramRole.ProgramManager),
        ("cm.fms", ProgramRole.ConfigurationManager),
    ];

    private static readonly (string UserName, ProgramRole Role)[] ProblemReportScenarioActors =
    [
        ("systems.author", ProgramRole.Engineer),
        ("software.author", ProgramRole.Engineer),
        ("test.engineer", ProgramRole.TestEngineer),
        ("engineer.demo", ProgramRole.Engineer),
        ("test.author", ProgramRole.TestEngineer),
        ("project.lead", ProgramRole.ProjectEngineer),
    ];

    private async Task EnsureCurrentProgramAuthorityAsync(Guid programId, string userName, ProgramRole role,
        DateTimeOffset effectiveAt, CancellationToken ct)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserName == userName, ct);
        if (account is null)
            throw new InvalidOperationException($"{userName} is required as an active {role} actor before FMS controlled scenarios are created.");
        if (account.State != AccountState.Active)
            throw new InvalidOperationException($"{userName} cannot act as {role}: the account is not active.");
        var authorityGrants = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == account.Id && x.ProgramId == programId && x.Role == role && x.EndedAt == null)
            .Select(x => x.GrantedAt).ToListAsync(ct);
        if (!authorityGrants.Any(x => x <= effectiveAt))
            throw new InvalidOperationException($"{userName} cannot act as {role}: no current program authority covers {effectiveAt:O}.");
    }

    private async Task<DateTimeOffset> EffectiveInterfaceTimelineAtAsync(Guid programId, DateTimeOffset baselineAt,
        CancellationToken ct)
    {
        var names = InterfaceScenarioActors.Select(x => x.UserName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var actorIds = await db.UserAccounts.AsNoTracking().Where(x => names.Contains(x.UserName))
            .Select(x => x.Id).ToListAsync(ct);
        var grants = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && actorIds.Contains(x.UserId) && x.EndedAt == null)
            .Select(x => x.GrantedAt).ToListAsync(ct);
        var latestGrant = grants.Count == 0 ? (DateTimeOffset?)null : grants.Max();
        return latestGrant is { } grant && grant >= baselineAt ? grant.AddMinutes(1) : baselineAt;
    }

    private async Task<DateTimeOffset> EffectiveProblemReportTimelineAtAsync(Guid programId,
        DateTimeOffset baselineAt, CancellationToken ct)
    {
        var latestGrant = await LatestCurrentProblemReportActorGrantAsync(programId, ct);
        return latestGrant is { } grant && grant >= baselineAt ? grant.AddMinutes(1) : baselineAt;
    }

    private async Task<DateTimeOffset?> LatestCurrentProblemReportActorGrantAsync(Guid programId,
        CancellationToken ct)
    {
        var names = ProblemReportScenarioActors.Select(x => x.UserName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var actorIds = await db.UserAccounts.AsNoTracking().Where(x => names.Contains(x.UserName))
            .Select(x => x.Id).ToListAsync(ct);
        var grants = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && actorIds.Contains(x.UserId) && x.EndedAt == null)
            .Select(x => x.GrantedAt).ToListAsync(ct);
        return grants.Count == 0 ? null : grants.Max();
    }

    private async Task<ShowcaseUpgradeAuthorityDecision?> CheckLeadershipRosterAuthorityAsync(Guid programId,
        CancellationToken ct)
    {
        var expectedNames = ShowcaseLeadershipRoster.Select(x => x.UserName).ToArray();
        var expectedAccounts = await db.UserAccounts.AsNoTracking().Where(x => expectedNames.Contains(x.UserName))
            .ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase, ct);
        var assignments = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == programId).ToListAsync(ct);
        var holderIds = assignments.Select(x => x.HolderUserId)
            .Concat(expectedAccounts.Values.Select(x => x.Id)).Distinct().ToArray();
        var accountsById = await db.UserAccounts.AsNoTracking().Where(x => holderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && holderIds.Contains(x.UserId)).ToListAsync(ct);

        foreach (var expected in ShowcaseLeadershipRoster)
        {
            if (!expectedAccounts.TryGetValue(expected.UserName, out var expectedAccount)
                || expectedAccount.State != AccountState.Active)
                return new(false, "showcase_leadership_account_unavailable",
                    $"The active {expected.UserName} account is required before the FMS {expected.Position} roster can be upgraded.");

            var history = assignments.Where(x => x.Position == expected.Position).ToList();
            var active = history.Where(x => x.EndedAt is null).ToList();
            if (active.Count > 1)
                return new(false, "showcase_leadership_history_conflict",
                    $"The FMS {expected.Position} position has multiple active holders and must be reconciled explicitly.");
            if (active.Count == 1)
            {
                var holder = active[0];
                if (!accountsById.TryGetValue(holder.HolderUserId, out var holderAccount)
                    || holderAccount.State != AccountState.Active
                    || !memberships.Any(x => x.UserId == holder.HolderUserId && x.Role == expected.RequiredRole
                        && x.EndedAt is null))
                    return new(false, "showcase_leadership_holder_ineligible",
                        $"The existing FMS {expected.Position} holder is not an active {expected.RequiredRole} member; the showcase upgrade will not replace operator-owned authority.");
                continue;
            }
            if (history.Count > 0)
                // An ended assignment is attributable operator history and remains a deliberate vacancy.
                continue;
            if (!memberships.Any(x => x.UserId == expectedAccount.Id && x.Role == expected.RequiredRole
                    && x.EndedAt is null))
                return new(false, "showcase_leadership_membership_unavailable",
                    $"The active {expected.UserName} {expected.RequiredRole} membership is required before the FMS {expected.Position} position can be created.");
        }
        return null;
    }

    /// <summary>
    /// Brings an already-seeded showcase Program up to the invariants the current seeder produces.
    ///
    /// A fresh seed builds everything in one pass, so nothing here ever ran against a database created
    /// today — which is exactly why the gap went unnoticed. An installation seeded before verification
    /// impact existed kept two approved FMS 1.6 change requests and an empty impact queue, a state the
    /// product describes as impossible, because the code that raises those items shipped afterwards and the
    /// seeder returned early on every subsequent start.
    ///
    /// Each step is keyed, ordered and idempotent. The explicit upgrade is serialized and committed as one
    /// transaction, so a concurrent or interrupted request cannot expose controlled rows without their step
    /// markers. A step added later applies on its own without renumbering anything.
    /// </summary>
    public async Task<ShowcaseUpgradeAuthorityDecision> CheckUpgradeAuthorityAsync(Guid programId,
        CancellationToken ct = default)
    {
        var account = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserName == QualityAnalystUserName, ct);
        if (account is null)
            return new(false, "quality_analyst_account_missing",
                "The quality.analyst account is required before controlled closure evidence can be upgraded.");
        if (account.State != AccountState.Active)
            return new(false, "quality_analyst_account_inactive",
                "The quality.analyst account is not active; no closure approval may be attributed to it.");

        var memberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == account.Id && x.ProgramId == programId
                && x.Role == ProgramRole.SoftwareQualityAnalyst)
            .ToListAsync(ct);
        if (!memberships.Any(x => x.EndedAt is null))
            return new(false, "quality_analyst_membership_inactive",
                "An unended SoftwareQualityAnalyst membership is required before controlled closure evidence can be upgraded.");

        var closureAt = await HistoricalClosureApprovalAtAsync(programId, memberships, ct);
        if (closureAt is null)
            return new(false, "closure_evidence_unavailable",
                "Two attributable Build 1.5 failed-execution to passing-retest chains are required before closure evidence can be upgraded.");
        if (!memberships.Any(x => x.GrantedAt <= closureAt.Value
                && (x.EndedAt is null || x.EndedAt.Value > closureAt.Value)))
            return new(false, "quality_analyst_membership_does_not_cover_closure",
                $"The quality.analyst membership history does not cover the historical closure approval at {closureAt.Value:O}; an operator-created membership is not backdated.");

        var projectId = await db.Projects.Where(x => x.ProgramId == programId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (projectId is { } project)
        {
            var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == project)
                .ToDictionaryAsync(x => x.Version, ct);
            var interfacePending = false;
            if (releases.TryGetValue("1.6", out var activeRelease))
                for (var index = 1; index <= 8 && !interfacePending; index++)
                    interfacePending = await ResolveInterfaceScenarioAsync(programId, project, activeRelease.Id, index, ct) is null;

            var problemPending = false;
            if (releases.TryGetValue("1.5", out var released)
                && releases.TryGetValue("1.6", out activeRelease))
                for (var index = 1; index <= 8 && !problemPending; index++)
                {
                    var expected = IsHistoricalProblemReportScenario(index) ? released.Id : activeRelease.Id;
                    problemPending = await ResolveProblemReportScenarioAsync(programId, project, index, expected, ct) is null;
                }

            // A durable scenario mapping is only an identity pointer. It does not prove that the mapped
            // Problem Report's verification, candidate and (where applicable) frozen closure evidence is
            // complete. Treat an incomplete mapped scenario as pending too, so the full controlled actor
            // roster is checked before any upgrade step can mutate it.
            if (!problemPending)
                problemPending = !await ScenarioRichnessCompleteAsync(programId, ct);

            if (interfacePending)
            {
                var effectiveAt = await EffectiveInterfaceTimelineAtAsync(programId,
                    new DateTimeOffset(2024, 12, 2, 10, 0, 0, TimeSpan.Zero), ct);
                try
                {
                    foreach (var actor in InterfaceScenarioActors)
                        await EnsureCurrentProgramAuthorityAsync(programId, actor.UserName, actor.Role, effectiveAt, ct);
                }
                catch (InvalidOperationException ex)
                {
                    return new(false, "showcase_actor_authority_unavailable", ex.Message, closureAt);
                }
            }

            if (problemPending)
            {
                var effectiveAt = await EffectiveProblemReportTimelineAtAsync(programId,
                    new DateTimeOffset(2024, 12, 12, 9, 0, 0, TimeSpan.Zero), ct);
                try
                {
                    foreach (var actor in ProblemReportScenarioActors)
                        await EnsureCurrentProgramAuthorityAsync(programId, actor.UserName, actor.Role, effectiveAt, ct);
                }
                catch (InvalidOperationException ex)
                {
                    return new(false, "showcase_actor_authority_unavailable", ex.Message, closureAt);
                }

                // The SQA approval must follow every newly-created controlled PR action. Use the historical
                // closure as the baseline, then move it only when a real actor grant occurred later. Two
                // hours leaves the deterministic draft-to-verification sequence (at most fifty minutes),
                // both governed evidence rows, and their ordering ahead of the approval without inventing
                // authority before the operator's actual grant.
                var authorityCoveredClosureAt = await EffectiveProblemReportTimelineAtAsync(programId,
                    closureAt.Value, ct);
                if (authorityCoveredClosureAt > closureAt.Value)
                    closureAt = authorityCoveredClosureAt.AddHours(2);
                if (!memberships.Any(x => x.GrantedAt <= closureAt.Value
                        && (x.EndedAt is null || x.EndedAt.Value > closureAt.Value)))
                    return new(false, "quality_analyst_membership_does_not_cover_closure",
                        $"The quality.analyst membership history does not cover the controlled evidence timeline at {closureAt.Value:O}; an operator-created membership is not backdated.");
            }
        }

        // Report the authority gap for a pending controlled scenario before the independent leadership
        // roster gap. This keeps the preflight actionable without changing the fail-closed boundary: every
        // check still completes before the serialized upgrade transaction runs its first step.
        var leadership = await CheckLeadershipRosterAuthorityAsync(programId, ct);
        if (leadership is not null) return leadership;

        return new(true, "ready", "The active quality.analyst account and current authority cover the controlled closure evidence.", closureAt);
    }

    public async Task<IReadOnlyList<string>> UpgradeAsync(Guid programId, CancellationToken ct = default)
    {
        await UpgradeGate.WaitAsync(ct);
        try
        {
            var isolation = db.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable;
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
            if (db.Database.IsNpgsql())
                // The database lock covers multiple API instances. READ COMMITTED deliberately takes its
                // post-wait snapshots after the prior holder commits, so the next request sees every marker.
                await AcquirePostgresAdvisoryLockAsync(
                    $"SELECT pg_advisory_xact_lock(hashtext({"aerolink-showcase-upgrade"}), hashtext({programId.ToString("D")}))", ct);

            var applied = await ApplyUpgradeStepsAsync(programId, ct);
            await transaction.CommitAsync(ct);
            return applied;
        }
        finally
        {
            UpgradeGate.Release();
        }
    }

    private async Task AcquirePostgresAdvisoryLockAsync(FormattableString statement, CancellationToken ct)
    {
        // A complete fresh showcase legitimately takes longer than Npgsql's default 30-second command
        // timeout. A concurrent operator request must remain queued behind the transaction lock instead of
        // failing merely because the current owner is still building controlled rows. Keep the wider budget
        // local to lock acquisition; ordinary commands immediately return to the configured timeout.
        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(statement, ct);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousTimeout);
        }
    }

    private async Task<IReadOnlyList<string>> ApplyUpgradeStepsAsync(Guid programId, CancellationToken ct)
    {
        var authority = await CheckUpgradeAuthorityAsync(programId, ct);
        if (!authority.Ready)
            throw new InvalidOperationException($"FMS showcase upgrade cannot proceed: {authority.Code} {authority.Detail}");

        var applied = new List<string>();
        var steps = new (string Key, Func<Guid, CancellationToken, Task<string?>> Run)[]
        {
            ("leadership-roster", EnsureShowcaseLeadershipRosterAsync),
            ("release-campaign", async (id, token) => { await EnsureReleaseCampaignAsync(id, token); return "Release campaign present."; }),
            ("product-line", async (id, token) => { await EnsureProductLineAsync(id, token); return "Product-line configuration present."; }),
            ("verification-impact", ReconcileVerificationImpactAsync),
            ("downstream-impact", ReconcileDownstreamImpactAsync),
            ("test-change-reviews", EnsureTestChangeReviewsAsync),
            ("problem-report-build-scope", ReconcileProblemReportBuildScopeAsync),
            ("controlled-test-change-identity", ReconcileControlledTestChangeIdentityAsync),
            ("verification-coverage-gap", async (id, token) => { await EnsureVerificationCoverageGapAsync(id, token); return "In-work suspect coverage present."; }),
            ("approver-identity", ReconcileApproverIdentityAsync),
            ("released-campaign", EnsureReleasedCampaignAsync),
            ("code-traceability-demo", EnsureCodeTraceabilityAsync),
            ("scenario-richness", EnsureScenarioRichnessAsync),
        };

        foreach (var step in steps)
        {
            var recorded = await db.ShowcaseUpgradeSteps
                .SingleOrDefaultAsync(x => x.ProgramId == programId && x.StepKey == step.Key, ct);
            if (recorded is not null)
            {
                // Scenario richness has externally visible postconditions. An older build could have
                // recorded its marker before the final rows/links were committed; do not let that marker
                // turn an incomplete showcase into a permanent no-op. Removing only the upgrade marker
                // makes the same atomic run retry the owned additive work.
                if (step.Key == "scenario-richness" && !await ScenarioRichnessCompleteAsync(programId, ct))
                {
                    db.ShowcaseUpgradeSteps.Remove(recorded);
                    await db.SaveChangesAsync(ct);
                }
                else continue;
            }
            var detail = await step.Run(programId, ct);
            db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, step.Key,
                detail ?? "No change required.", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(ct);
            applied.Add($"{step.Key}: {detail ?? "No change required."}");
        }
        return applied;
    }

    private async Task<string?> EnsureShowcaseLeadershipRosterAsync(Guid programId, CancellationToken ct)
    {
        var names = ShowcaseLeadershipRoster.Select(x => x.UserName).ToArray();
        var accounts = await db.UserAccounts.Where(x => names.Contains(x.UserName))
            .ToDictionaryAsync(x => x.UserName, StringComparer.OrdinalIgnoreCase, ct);
        var assignments = await db.ProjectLeadershipAssignments.Where(x => x.ProgramId == programId).ToListAsync(ct);
        var added = 0;
        var preserved = 0;
        var deliberateVacancies = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var expected in ShowcaseLeadershipRoster)
        {
            var history = assignments.Where(x => x.Position == expected.Position).ToList();
            if (history.Any(x => x.EndedAt is null))
            {
                preserved++;
                continue;
            }
            if (history.Count > 0)
            {
                deliberateVacancies++;
                continue;
            }
            db.ProjectLeadershipAssignments.Add(new ProjectLeadershipAssignment(programId, expected.Position,
                accounts[expected.UserName].Id, "system.showcase-upgrade", now));
            added++;
        }
        await db.SaveChangesAsync(ct);
        return $"Project Leadership roster reconciled: {added} added, {preserved} preserved, {deliberateVacancies} deliberate vacancies.";
    }

    private async Task<DateTimeOffset?> HistoricalClosureApprovalAtAsync(Guid programId,
        IReadOnlyCollection<ProgramMembership> memberships, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (projectId is null) return null;
        var releaseId = await db.Releases.Where(x => x.ProjectId == projectId.Value && x.Version == "1.5")
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (releaseId is null) return null;
        var buildIds = (await db.SoftwareBuilds.AsNoTracking()
                .Where(x => x.ProjectId == projectId.Value && x.ReleaseId == releaseId.Value)
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => x.ProjectId == projectId.Value && x.ReleaseId == releaseId.Value)
            .ToListAsync(ct);
        var pairs = executions
            .Where(x => x.Outcome == TestOutcome.Fail && x.SoftwareBuildId is { } buildId && buildIds.Contains(buildId))
            .OrderBy(x => x.ExecutedAt).ThenBy(x => x.Id)
            .Select(failure => new
            {
                Failure = failure,
                Retest = executions.Where(candidate => candidate.RetestOfExecutionId == failure.Id
                        && candidate.Outcome == TestOutcome.Pass && candidate.SoftwareBuildId is { } buildId
                        && buildIds.Contains(buildId)
                        && candidate.ProcedureRevisionId == failure.ProcedureRevisionId)
                    .OrderBy(candidate => candidate.RecordedAt).ThenBy(candidate => candidate.Id)
                    .FirstOrDefault(),
            })
            .Where(pair => pair.Retest is not null)
            .ToList();
        if (pairs.Count < 2) return null;
        var planned = pairs[1].Retest!.RecordedAt.AddHours(1);
        // A pre-existing installation may have received its current authority years after the deterministic
        // historical execution. Do not backdate that authority; move the new operator-triggered approval
        // after the real grant while retaining the failed-execution -> passing-retest causal chain.
        var latestCurrentGrant = memberships.Where(x => x.EndedAt is null)
            .Select(x => (DateTimeOffset?)x.GrantedAt).Max();
        return latestCurrentGrant is { } grant && grant >= planned ? grant.AddMinutes(1) : planned;
    }

    private async Task<string?> ReconcileProblemReportBuildScopeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var activeReleases = await db.Releases.Where(x => x.ProjectId == projectId && !x.IsReleased).ToListAsync(ct);
        if (activeReleases.Count != 1) return "No unique active build was available; unscoped records were preserved.";

        var active = activeReleases[0];
        var reports = await db.ProblemReports.Where(x => x.ProjectId == projectId && x.TargetReleaseId == null).ToListAsync(ct);
        var terminal = new[] { ProblemReportState.Closed, ProblemReportState.Rejected };
        var reconciled = 0;
        foreach (var report in reports.Where(x => !terminal.Contains(x.State)))
        {
            var now = DateTimeOffset.UtcNow;
            report.Retarget(report.ResponsibleEngineerId, active.Id, now);
            if (!await db.ProblemReportLinks.AnyAsync(x => x.ProblemReportId == report.Id && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope, ct))
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", active.Id,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow, "system.workspace", now));
            // Seeded demo history, with no authenticated person behind it: it captures no display name and
            // renders as its handle, which is what a pre-#776 row looks like.
            db.ProblemReportRevisions.Add(new ProblemReportRevision(report.Id, report.Revision, "TargetBuildReconciled",
                "system.workspace", report.CanonicalHash(), report.CanonicalSnapshot(), now));
            reconciled++;
        }
        return $"Scoped {reconciled} active problem report(s) to Build {active.Version}; terminal history was preserved.";
    }

    private async Task<string?> ReconcileControlledTestChangeIdentityAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var ladderPolicy = await resolver.ResolveAsync(projectId, ct);
        // SQLite cannot order DateTimeOffset server-side; this is one Project's bounded TCR collection.
        var reviews = (await db.TestChangeReviews.Where(x => x.ProjectId == projectId).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).ToList();
        var sources = await db.SystemChangeRequests.Where(x => x.ProjectId == projectId)
            .ToDictionaryAsync(x => x.Id, ct);
        var items = await db.VerificationImpactItems.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var numbered = 0;
        // Only rows that have concluded test work is required. An unnumbered row used to mean "raised before
        // controlled numbering existed"; it now also means "raised and not yet assessed", and numbering one
        // of those would answer the assessment on the engineer's behalf.
        foreach (var review in reviews.Where(x => string.IsNullOrEmpty(x.BaseNumber)
            && x.Outcome == TestChangeReviewOutcome.ChangeRequired))
        {
            review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, review.ArtifactKey, ct, ladderPolicy), DateTimeOffset.UtcNow, ladderPolicy);
            numbered++;
        }

        var superseded = 0;
        foreach (var legacy in reviews.Where(x => x.State != TestChangeReviewState.Superseded
            && x.Discipline != TestChangeReviewDiscipline.System
            && x.ChangeRequestId is { } legacySourceId
            && sources.TryGetValue(legacySourceId, out var source) && source.Type == ChangeRequestType.System))
        {
            var subjects = items.Where(x => x.TestChangeReviewId == legacy.Id).Select(x => x.SubjectDisplayNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var successor = reviews.FirstOrDefault(candidate => candidate.Id != legacy.Id
                && candidate.ReleaseId == legacy.ReleaseId && candidate.Discipline == legacy.Discipline
                && candidate.State != TestChangeReviewState.Superseded
                && candidate.ChangeRequestId is { } candidateSourceId
                && sources.TryGetValue(candidateSourceId, out var candidateSource) && candidateSource.Type == ChangeRequestType.Software
                && items.Any(item => item.TestChangeReviewId == candidate.Id && subjects.Contains(item.SubjectDisplayNumber)));
            if (successor is null) continue;
            legacy.Supersede(successor.Id,
                $"Replaced by {successor.DisplayNumber}, raised from the correctly classified software change request for the same verification subject.", DateTimeOffset.UtcNow);
            foreach (var item in items.Where(x => x.TestChangeReviewId == legacy.Id)) item.Supersede(DateTimeOffset.UtcNow);
            superseded++;
        }
        return $"Assigned {numbered} legacy controlled TCR number(s) and superseded {superseded} incorrectly classified software package(s).";
    }

    private async Task<string?> EnsureCodeTraceabilityAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var releases = await db.Releases.Where(x => x.ProjectId == projectId && (x.Version == "1.5" || x.Version == "1.6")).ToListAsync(ct);
        var released = releases.SingleOrDefault(x => x.Version == "1.5"); var active = releases.SingleOrDefault(x => x.Version == "1.6");
        if (released is null || active is null) return "The showcase build pair is not available.";
        // SQLite cannot order DateTimeOffset server-side. There are only the controlled baselines for one
        // released build here, so materialize that bounded set and make the deterministic choice in memory.
        var baseline = (await db.CandidateBaselines.AsNoTracking()
            .Where(x => x.ReleaseId == released.Id && x.RequirementsMaterializedAt != null).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (baseline is null) return "The released LLR baseline is not materialized.";
        var llrs = await (from selection in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baseline.Id)
                          join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == RequirementLevel.LowLevel) on selection.ArtifactId equals artifact.Id
                          join revision in db.RequirementRevisions.AsNoTracking() on selection.RevisionId equals revision.Id
                          orderby artifact.BaseNumber
                          select new { ArtifactId = artifact.Id, RevisionId = revision.Id }).Take(5).ToListAsync(ct);
        if (llrs.Count < 5) return "Fewer than five LLR revisions are available for the demo scope.";
        // Five sample mappings, against a build that introduced 700 LLR revisions.
        //
        // This is deliberately a sample and no longer pretends to be the whole scope. Build 1.5 is the
        // originating build, so every LLR in its baseline was introduced by one of its own change requests
        // and every one of them owes implementation evidence — the honest number is 700, not five. The gate
        // used to read complete because the projection quietly measured the first five LLRs by number for
        // this Program alone.
        //
        // A released build carrying almost no code evidence is what adopting AeroLink mid-life actually looks
        // like: the code for 1.5 was written before anything recorded the link. Seeding 700 invented merge
        // requests would make the demonstration less truthful, not more.
        var now = new DateTimeOffset(2026, 6, 18, 15, 0, 0, TimeSpan.Zero); var added = 0;
        foreach (var release in new[] { released, active })
        {
            var count = release.Id == released.Id ? 5 : 4;
            for (var index = 0; index < count; index++)
            {
                var llr = llrs[index];
                if (await db.CodeTraceabilityRecords.AnyAsync(x => x.ReleaseId == release.Id && x.RequirementRevisionId == llr.RevisionId, ct)) continue;
                var noCode = index == count - 1;
                var reference = $"!{1842 + index + (release.Id == active.Id ? 20 : 0)}";
                var sha = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{release.Version}:{llr.RevisionId}:{reference}"))).ToLowerInvariant();
                db.CodeTraceabilityRecords.Add(new CodeTraceabilityRecord(projectId, release.Id, llr.ArtifactId, llr.RevisionId,
                    noCode ? CodeTraceDisposition.NoCodeChangeRequired : CodeTraceDisposition.GitLabMerge,
                    "aerolink-demo/fms-navigation", reference, $"Implement exact FMS LLR behavior for Build {release.Version}",
                    $"https://gitlab.com/aerolink-demo/fms-navigation/-/merge_requests/{reference[1..]}", sha,
                    now.AddDays(index), noCode ? "The approved LLR wording clarifies existing behavior; code already conforms and only verification evidence is required." : "",
                    true, "software.lead", now.AddDays(index))); added++;
            }
        }
        return $"Recorded {added} demonstration GitLab traceability mapping(s) as a labelled sample; the released build introduced far more LLR revisions than the sample covers.";
    }

    /// <summary>
    /// Gives the showcase's approval steps back the names of the people who hold them.
    ///
    /// The showcase submitted its reviews naming approvers "Engineering Lead" and "Engineering Manager". Those
    /// are jobs, not people, and an approval step is the one place where the difference matters most: the
    /// panel exists to tell a reader who is being waited on. It answered with a job title and then, having
    /// spent the name on that, had nothing left to say about their authority.
    ///
    /// Only the two literal strings the showcase itself wrote are repaired, matched together with the account
    /// that carries them. A name recorded by an actual reviewer is evidence and is never rewritten here — a
    /// controlled tool that quietly edits who signed something is worse than one that displays it awkwardly.
    /// </summary>
    private async Task<string?> ReconcileApproverIdentityAsync(Guid programId, CancellationToken ct)
    {
        var seededNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lead.reviewer"] = "Maya Patel",
            ["manager.reviewer"] = "Olivia Chen",
        };
        var placeholders = new[] { "Engineering Lead", "Engineering Manager" };
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var cycleIds = await db.ReviewCycles
            .Where(cycle => db.SystemChangeRequests.Any(scr => scr.Id == cycle.ChangeRequestId && scr.ProjectId == projectId))
            .Select(cycle => cycle.Id).ToListAsync(ct);
        var steps = await db.ApprovalSteps
            .Where(step => cycleIds.Contains(step.ReviewCycleId) && placeholders.Contains(step.ApproverName))
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var step in steps)
        {
            if (!seededNames.TryGetValue(step.ApproverId, out var person)) continue;
            // Written through the tracked property rather than the domain, which deliberately keeps a recorded
            // approver name immutable. This is the seeder correcting its own past output, not the product
            // editing an approval.
            db.Entry(step).Property(x => x.ApproverName).CurrentValue = person;
            repaired++;
        }
        if (repaired == 0) return "Approval steps already name people.";
        await db.SaveChangesAsync(ct);
        return $"Named the people behind {repaired} approval steps.";
    }

    private async Task<string?> EnsureTestChangeReviewsAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct);
        var ladderPolicy = await resolver.ResolveAsync(projectId, ct);
        var releases = await db.Releases.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var released = releases.Single(x => x.Version == "1.5");
        var inWork = releases.Single(x => x.Version == "1.6");
        var now = new DateTimeOffset(2024, 11, 28, 14, 0, 0, TimeSpan.Zero);

        var requests = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId
                && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        var existingReviews = await db.TestChangeReviews
            .Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var reviewsByRequestAndDiscipline = existingReviews
            .ToDictionary(x => (x.ChangeRequestId, x.Discipline));
        var raisedChangeIds = (await db.VerificationImpactItems
                .Where(x => x.ProjectId == projectId && x.RequirementChangeId != null)
                .Select(x => x.RequirementChangeId!.Value).ToListAsync(ct))
            .ToHashSet();

        // The deterministic showcase has 105 historical requests. Build the reconciliation from one
        // preloaded graph instead of issuing review/item queries per request.
        var automaticChangeDetection = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var request in requests)
        {
            foreach (var change in request.RequirementChanges.Where(x =>
                         x.Kind is RequirementChangeKind.Introduce or RequirementChangeKind.Modify
                         && !raisedChangeIds.Contains(x.Id)))
            {
                var discipline = change.Level switch
                {
                    RequirementLevel.System => TestChangeReviewDiscipline.System,
                    RequirementLevel.HighLevel => TestChangeReviewDiscipline.HighLevelSoftware,
                    _ => TestChangeReviewDiscipline.LowLevelSoftware
                };
                if (!reviewsByRequestAndDiscipline.TryGetValue((request.Id, discipline), out var review))
                {
                    // The showcase's packages exist precisely because they carry procedure decisions, so they
                    // are seeded as already assessed rather than as questions nobody in the demo will answer.
                    review = new TestChangeReview(projectId, request.TargetReleaseId, request.Id,
                        discipline, request.DisplayNumber, now, caseContractVersion: 0);
                    review.RecordTestChangeRequired("verification.engineer", now);
                    review.AssignControlledNumber(await IdentifierAllocator.NextTestChangeRequestAsync(db, review.ArtifactKey, ct, ladderPolicy), now, ladderPolicy);
                    db.TestChangeReviews.Add(review);
                    reviewsByRequestAndDiscipline.Add((request.Id, discipline), review);
                }
                var display = $"{change.BaseNumber}.{change.Revision:D2}";
                db.VerificationImpactItems.Add(change.Kind == RequirementChangeKind.Introduce
                    ? VerificationImpactItem.ForIntroducedRequirement(projectId, request.TargetReleaseId,
                        request.Id, review.Id, change.Id, display, change.VerificationMethod, now)
                    : VerificationImpactItem.ForModifiedRequirement(projectId, request.TargetReleaseId,
                        request.Id, review.Id, change.Id, display, change.VerificationMethod, now));
                raisedChangeIds.Add(change.Id);
            }
        }
        db.ChangeTracker.DetectChanges();
        db.ChangeTracker.AutoDetectChangesEnabled = automaticChangeDetection;
        await db.SaveChangesAsync(ct);

        var releasedBaselineId = await db.CandidateBaselines
            .Where(x => x.ProjectId == projectId && x.ReleaseId == released.Id && x.RequirementsMaterializedAt != null)
            .Select(x => x.Id).SingleAsync(ct);
        var releasedItems = await db.VerificationImpactItems
            .Where(x => x.ReleaseId == released.Id && x.State != VerificationImpactState.Resolved)
            .ToListAsync(ct);
        var changeIds = releasedItems.Where(x => x.RequirementChangeId is not null)
            .Select(x => x.RequirementChangeId!.Value).ToList();
        var changes = await db.RequirementChanges.Where(x => changeIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var exactByBaseNumber = await (from member in db.BaselineRequirements
                                      where member.BaselineId == releasedBaselineId
                                      join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                                      join revision in db.RequirementRevisions on member.RevisionId equals revision.Id
                                      select new { artifact.BaseNumber, artifact.Level, Revision = revision })
            .ToDictionaryAsync(x => x.BaseNumber, ct);
        var exactRevisionIds = exactByBaseNumber.Values.Select(x => x.Revision.Id).ToList();
        var procedureCoverage = (await (from coverage in db.TestCoverage
                                       where exactRevisionIds.Contains(coverage.RequirementRevisionId)
                                       join revision in db.TestProcedureRevisions on coverage.ProcedureRevisionId equals revision.Id
                                       join record in db.TestProcedures on revision.ProcedureId equals record.Id
                                       where revision.State == TestProcedureState.Approved
                                       select new
                                       {
                                           coverage.RequirementRevisionId,
                                           Procedure = record,
                                           Revision = revision
                                       }).ToListAsync(ct))
            .GroupBy(x => x.RequirementRevisionId)
            .ToDictionary(x => x.Key, x => x.First());
        var procedureSequences = (await db.TestProcedures.Where(x => x.ProjectId == projectId)
                .GroupBy(x => x.Level).Select(x => new { Level = x.Key, Count = x.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Level, x => x.Count);

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        foreach (var item in releasedItems)
        {
            if (item.RequirementChangeId is null || !changes.TryGetValue(item.RequirementChangeId.Value, out var change))
                continue;
            var exact = exactByBaseNumber[change.BaseNumber];
            item.LinkRequirementRevision(exact.Revision.Id, now);

            procedureCoverage.TryGetValue(exact.Revision.Id, out var procedure);
            if (procedure is null)
            {
                var level = exact.Level switch
                {
                    RequirementLevel.System => TestProcedureLevel.System,
                    RequirementLevel.HighLevel => TestProcedureLevel.HighLevel,
                    _ => TestProcedureLevel.LowLevel
                };
                var prefix = level switch
                {
                    TestProcedureLevel.System => "SYSTP",
                    TestProcedureLevel.HighLevel => "HLRTC",
                    _ => "LLRTC"
                };
                var sequence = procedureSequences.GetValueOrDefault(level) + 1;
                procedureSequences[level] = sequence;
                var record = new TestProcedure(projectId, $"{prefix}-{sequence:D6}",
                    $"Verify {change.BaseNumber}", "verification.engineer", now, level);
                var revision = new TestProcedureRevision(record.Id, 0,
                    $"Verify the approved behaviour of {change.BaseNumber}.", "Released FMS test environment",
                    "Exercise the requirement under nominal and boundary conditions.",
                    "Observed behaviour satisfies the approved requirement.", TestProcedureState.Approved,
                    "verification.engineer", now, effectiveBaselineId: exact.Revision.EffectiveBaselineId,
                    parentKind: VerificationProcedureParentKind.Allocated);
                db.AddRange(record, revision, new TestRequirementCoverage(revision.Id, exact.Revision.Id));
                procedure = new
                {
                    RequirementRevisionId = exact.Revision.Id,
                    Procedure = record,
                    Revision = revision
                };
                procedureCoverage[exact.Revision.Id] = procedure;
            }
            item.Resolve("verification.engineer", VerificationImpactOutcome.ProcedureCoverageConfirmed,
                $"Verification artifact alignment completed for released software build SW-01.50 under {change.BaseNumber}.",
                now, procedure.Procedure.Id, procedure.Revision.Id,
                change.Kind == RequirementChangeKind.Introduce
                    ? TestProcedureChangeAction.CreateNew
                    : TestProcedureChangeAction.ModifyExisting,
                preReleaseEvidenceRequired: false);
            db.VerificationImpactDecisionHistory.Add(new VerificationImpactDecisionHistory(
                item.Id, VerificationImpactHistoryAction.Resolved, item.Outcome,
                item.ResolvedProcedureId, item.ResolvedProcedureRevisionId,
                item.ResolutionRationale, "verification.engineer", now));
        }
        db.ChangeTracker.DetectChanges();
        db.ChangeTracker.AutoDetectChangesEnabled = automaticChangeDetection;
        await db.SaveChangesAsync(ct);

        // An assessment carrying procedure decisions plainly concluded that test work was required, so it is
        // recorded as having done so and given the controlled number that conclusion earns. Assessments with
        // nothing attached are left unanswered on purpose: the showcase should show both a queue with work
        // waiting to be judged and the test change requests that judging it produced.
        var reviewsWithWork = (await db.VerificationImpactItems
                .Where(x => x.ProjectId == projectId).Select(x => x.TestChangeReviewId).Distinct().ToListAsync(ct))
            .ToHashSet();
        foreach (var review in await db.TestChangeReviews
                     .Where(x => x.ProjectId == projectId && x.Outcome == TestChangeReviewOutcome.Pending)
                     .ToListAsync(ct))
        {
            if (!reviewsWithWork.Contains(review.Id)) continue;
            review.RecordTestChangeRequired("verification.engineer", now);
            if (string.IsNullOrEmpty(review.BaseNumber))
                review.AssignControlledNumber(
                    await IdentifierAllocator.NextTestChangeRequestAsync(db, review.ArtifactKey, ct, ladderPolicy), now, ladderPolicy);
        }
        await db.SaveChangesAsync(ct);

        var releasedReviews = await db.TestChangeReviews
            .Where(x => x.ReleaseId == released.Id && x.State == TestChangeReviewState.Draft).ToListAsync(ct);
        var incompleteReviewIds = (await db.VerificationImpactItems
                .Where(x => x.ReleaseId == released.Id && x.State != VerificationImpactState.Resolved)
                .Select(x => x.TestChangeReviewId).Distinct().ToListAsync(ct))
            .ToHashSet();
        foreach (var review in releasedReviews)
        {
            // These carry the released build's procedure decisions, so the assessment behind them concluded
            // test work was required. Older showcase databases predate the outcome and are brought forward.
            if (review.Outcome == TestChangeReviewOutcome.Pending)
                review.RecordTestChangeRequired("verification.engineer", now);
            // Build 1.5 is imported history from before the engineering-case contract. Classify that history
            // explicitly instead of inventing case prose or weakening the rule for newly authored packages.
            if (review.MissingCaseFields().Count > 0)
            {
                db.Entry(review).Property(x => x.CaseContractVersion).CurrentValue = 0;
                // Preserve the one existing historical submission byte-for-byte. A migrated Draft without
                // this explicit seed marker must upgrade to v2 before it can start a new review cycle.
                review.MarkAsLegacyHistoricalPackage("verification.engineer", now);
            }
            review.Submit("verification.engineer", "assurance.reviewer", !incompleteReviewIds.Contains(review.Id), now);
            review.Approve("assurance.reviewer",
                "Historical verification artifact changes and exact coverage were approved for released software build SW-01.50.", now);
        }
        await db.SaveChangesAsync(ct);

        var currentReviews = await db.TestChangeReviews.CountAsync(x => x.ReleaseId == inWork.Id, ct);
        return $"{releasedReviews.Count} historical Build 1.5 review(s) completed; {currentReviews} Build 1.6 review(s) remain active.";
    }

    /// <summary>
    /// What the showcase is supposed to contain, checked rather than assumed.
    ///
    /// The upgrade steps report what they did; this reports whether the result is right, which is a
    /// different question. A step that ran and a database that is correct are not the same claim, and the
    /// defect behind this work was precisely a database nobody had checked.
    /// </summary>
    public async Task<IReadOnlyList<ShowcaseInvariant>> CheckInvariantsAsync(Guid programId, CancellationToken ct = default)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return [new ShowcaseInvariant("project", false, "The showcase Program has no Project.")];

        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var baselines = await db.CandidateBaselines.AsNoTracking().Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var materialized = baselines.Where(x => x.RequirementsMaterializedAt is not null).ToList();
        var approved = await db.SystemChangeRequests.AsNoTracking()
            .CountAsync(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline), ct);
        var impacts = await db.VerificationImpactItems.CountAsync(ct);
        var procedures = await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct);
        var executions = await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct);
        var documents = await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct);
        var campaigns = await db.ReleaseCampaigns.CountAsync(x => x.ProjectId == projectId, ct);
        var components = await db.ProductLineComponents.CountAsync(x => x.ProjectId == projectId, ct);
        var allProjectRequests = await db.SystemChangeRequests.AsNoTracking()
            .Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var interfaceSteps = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.StepKey.StartsWith(InterfaceScenarioStepPrefix)).ToListAsync(ct);
        var interfaceIds = ParseScenarioIds(interfaceSteps, InterfaceScenarioStepPrefix);
        var interfaceRequests = allProjectRequests.Where(x => interfaceIds.Contains(x.Id)).ToList();
        var allProblemReports = await db.ProblemReports.AsNoTracking()
            .Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var problemSteps = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .Where(x => x.ProgramId == programId && x.StepKey.StartsWith(ProblemReportScenarioStepPrefix)).ToListAsync(ct);
        var problemIds = ParseScenarioIds(problemSteps, ProblemReportScenarioStepPrefix);
        var problemReports = allProblemReports.Where(x => problemIds.Contains(x.Id)).ToList();
        var activeRequests = allProjectRequests.Where(x => x.TargetReleaseId == releases.SingleOrDefault(r => r.Version == "1.6")?.Id).ToList();
        var requiredInterfaceStates = new[]
        {
            ChangeRequestState.Draft, ChangeRequestState.InReview, ChangeRequestState.Approved,
            ChangeRequestState.SelectedForBaseline, ChangeRequestState.Deferred, ChangeRequestState.Withdrawn,
        };
        var requiredProblemStates = new[]
        {
            ProblemReportState.Draft, ProblemReportState.Implementing, ProblemReportState.Verifying,
            ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed, ProblemReportState.Rejected,
        };
        var sqaAccount = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserName == "quality.analyst", ct);
        var hasActiveSqa = sqaAccount is not null && await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == sqaAccount.Id
            && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null, ct);
        var leadership = await db.ProjectLeadershipAssignments.AsNoTracking()
            .Where(x => x.ProgramId == programId).ToListAsync(ct);
        var leadershipHolderIds = leadership.Select(x => x.HolderUserId).Distinct().ToArray();
        var leadershipAccounts = await db.UserAccounts.AsNoTracking().Where(x => leadershipHolderIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var leadershipMemberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.ProgramId == programId && leadershipHolderIds.Contains(x.UserId)).ToListAsync(ct);
        var leadershipHealthy = ShowcaseLeadershipRoster.All(expected =>
        {
            var history = leadership.Where(x => x.Position == expected.Position).ToList();
            var active = history.Where(x => x.EndedAt is null).ToList();
            if (history.Count == 0 || active.Count > 1) return false;
            if (active.Count == 0) return true; // An ended assignment is an attributable deliberate vacancy.
            var holder = active[0];
            return leadershipAccounts.TryGetValue(holder.HolderUserId, out var account)
                && account.State == AccountState.Active
                && leadershipMemberships.Any(x => x.UserId == holder.HolderUserId
                    && x.Role == expected.RequiredRole && x.EndedAt == null);
        });
        var activeLeadershipCount = leadership.Count(x => x.EndedAt is null);
        // Ending SQA authority is itself controlled history. A new seed cannot invent a Closed record while
        // that authority is absent; an existing frozen closure remains valid and is accepted below.
        if (!hasActiveSqa)
            requiredProblemStates = requiredProblemStates.Where(state => state != ProblemReportState.Closed).ToArray();

        return
        [
            new("releases", releases.Count >= 2, $"{releases.Count} release(s); a released 1.5 and an in-work 1.6 are expected."),
            new("materialized-baseline", materialized.Count >= 1, $"{materialized.Count} materialized baseline(s)."),
            new("documents", documents >= 6, $"{documents} controlled document(s)."),
            new("procedures", procedures >= 500, $"{procedures} verification artifact(s)."),
            new("executions", executions >= 500, $"{executions} recorded execution(s)."),
            // The one this work exists for: approved change requests with an empty queue is the state the
            // product calls impossible, and a live installation was sitting in it.
            new("verification-impact", approved == 0 || impacts > 0,
                $"{approved} approved or selected change request(s) and {impacts} verification-impact item(s)."),
            new("release-campaign", campaigns >= 1, $"{campaigns} release campaign(s)."),
            new("product-line", components >= 1, $"{components} product-line component(s)."),
            new("leadership-roster", leadershipHealthy,
                $"{leadership.Select(x => x.Position).Distinct().Count()} of 8 positions have attributable history; {activeLeadershipCount} currently have eligible holders."),
            new("active-change-request-distribution", activeRequests.Count >= 16,
                $"{activeRequests.Count} active-build change request(s); the showcase contributes 8 baseline scenarios and 8 Interface scenarios."),
            new("interface-scenarios", interfaceSteps.Count == 8 && interfaceRequests.Count == 8
                    && requiredInterfaceStates.All(state => interfaceRequests.Any(x => x.State == state)),
                $"{interfaceRequests.Count} Interface change-control scenario(s); the active build should show draft, review, approval, selection, deferral and withdrawal."),
            new("problem-report-scenarios", problemSteps.Count == 8 && problemReports.Count == 8
                    && requiredProblemStates.All(state => problemReports.Any(x => x.State == state)),
                $"{problemReports.Count} Problem Report scenario(s); lifecycle variety should include active, closure and rejected records."),
            new("work-distribution", problemReports.Select(x => x.ResponsibleEngineerId)
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 5,
                $"{problemReports.Select(x => x.ResponsibleEngineerId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} responsible people hold seeded Problem Report work."),
            new("problem-report-build-scope", await ProblemReportBuildScopeInvariantAsync(problemReports, ct),
                "Every seeded Problem Report has one authoritative BuildScope release link matching its target build."),
            new("problem-report-controlled-evidence", await ProblemReportEvidenceInvariantAsync(programId, projectId, problemReports, ct),
                "Verified and closed seeded Problem Reports carry controlled resolution links and closure evidence."),
        ];
    }

    private async Task<bool> ProblemReportBuildScopeInvariantAsync(IReadOnlyCollection<ProblemReport> reports,
        CancellationToken ct)
    {
        if (reports.Count != 8) return false;
        foreach (var report in reports)
        {
            if (report.TargetReleaseId is null) return false;
            var links = await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == report.Id
                && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope).ToListAsync(ct);
            if (links.Count != 1 || links[0].ArtifactId != report.TargetReleaseId.Value) return false;
        }
        return true;
    }

    private async Task<bool> ProblemReportEvidenceInvariantAsync(Guid programId, Guid projectId,
        IReadOnlyCollection<ProblemReport> reports,
        CancellationToken ct)
    {
        if (reports.Count != 8) return false;
        var historicalReleaseId = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == "1.5")
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (historicalReleaseId is null) return false;
        var sqaAccount = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == "quality.analyst"
            , ct);
        var hasActiveSqa = sqaAccount is not null && await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == sqaAccount.Id
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null, ct);
        var hasSqaHistory = sqaAccount is not null && await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == sqaAccount.Id
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst, ct);
        if (!hasSqaHistory) return false;
        foreach (var index in new[] { 6, 7 })
        {
            var report = await ResolveProblemReportScenarioAsync(programId, projectId, index, historicalReleaseId.Value, ct);
            if (index == 7 && !hasActiveSqa && report is not null && report.ResolutionVerificationExecutionId is null
                && report.State == ProblemReportState.Verifying
                && await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.EventType == "ClosureApproved", ct))
                continue;
            if (report?.ResolutionVerificationExecutionId is null) return false;
            if (!await db.ProblemReportLinks.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.ArtifactType == "TestExecution" && x.ArtifactId == report.ResolutionVerificationExecutionId
                    && x.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification, ct)) return false;
            if (!await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.EventType == "ResolutionVerified", ct)) return false;
            var candidate = await db.ProblemReportClosureCandidates.AsNoTracking().Where(x => x.ProblemReportId == report.Id)
                .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
            if (candidate is null || candidate.VerificationExecutionId != report.ResolutionVerificationExecutionId) return false;
            if (index == 6 && candidate.State != ProblemReportClosureCandidateState.Pending) return false;
            if (index == 7)
            {
                var closureIsFrozen = report.State == ProblemReportState.Closed
                    && candidate.State == ProblemReportClosureCandidateState.Approved
                    && !string.IsNullOrWhiteSpace(candidate.ClosurePackageHash)
                    && await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                        && x.EventType == "ClosureApproved", ct);
                if (hasActiveSqa ? !closureIsFrozen : report.State != ProblemReportState.WaitingForSqaToClose && !closureIsFrozen)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Raises the verification-impact items an approved or selected change request should already have.
    ///
    /// Approval is what raises this work, and these change requests were approved directly in the seed
    /// rather than through the endpoint that normally does it — so a database seeded before the impact
    /// service existed has approved changes introducing and modifying requirements with nothing in the
    /// queue. The service is asked to raise them again; it already declines to duplicate an item that
    /// exists, so this adds only what is missing and leaves anything a user resolved untouched.
    /// </summary>
    private async Task<string?> ReconcileVerificationImpactAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;
        var requests = await db.SystemChangeRequests
            .Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        if (requests.Count == 0) return null;

        var before = await db.VerificationImpactItems.CountAsync(ct);
        var service = new VerificationImpactService(db, policyResolver: resolver);
        foreach (var request in requests) await service.RaiseForApprovedChangeRequestAsync(request, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        var raised = await db.VerificationImpactItems.CountAsync(ct) - before;
        return raised == 0 ? "Verification impact already complete." : $"Raised {raised} missing verification-impact item(s).";
    }

    private async Task<string?> ReconcileDownstreamImpactAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;
        var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .Where(x => x.ProjectId == projectId && (x.State == ChangeRequestState.Approved || x.State == ChangeRequestState.SelectedForBaseline))
            .ToListAsync(ct);
        var before = await db.DownstreamChangeAssessments.CountAsync(ct);
        var service = new DownstreamImpactService(db, policyResolver: resolver);
        foreach (var request in requests) await service.RaiseForApprovedChangeRequestAsync(request, DateTimeOffset.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        var raised = await db.DownstreamChangeAssessments.CountAsync(ct) - before;
        return raised == 0 ? "Downstream assessments already complete." : $"Raised {raised} missing downstream assessment(s).";
    }

    /// <summary>
    /// Procedure whose FMS 1.6 rework creates the showcase's suspect coverage.
    ///
    /// Deliberately not SYSTP-000001. Procedures are dealt requirements round-robin, so SYSTP-000001 covers
    /// SYSR-000001 and is therefore the first approved procedure any test that searches for one will find —
    /// putting it into revision took it out of the covering-procedure list and broke the suspect-coverage
    /// journey. A fixture that changes what other journeys discover is not an isolated fixture.
    /// </summary>
    private const string GapProcedureNumber = "SYSTP-000040";

    /// <summary>
    /// A showcase in which all 1,250 requirements are covered can never demonstrate the tool finding a
    /// verification gap, which is the question a verification engineer actually arrives with.
    ///
    /// The gap seeded here is one FMS 1.6 work item: an approved System procedure put back into revision.
    /// Coverage settles only when the procedure it names has no revision in flight, so the two requirements
    /// that procedure covers become Suspect — linked to something that no longer counts — without altering a
    /// single released FMS 1.5 record. The approved revision 0 is untouched, its coverage links are
    /// untouched, and the 1.5 baseline, build, executions and controlled documents all still agree.
    ///
    /// The Uncovered state is deliberately not seeded. Reaching it would take either removing coverage from
    /// a released requirement — a released baseline that failed its own coverage gate, which is a worse
    /// untruth than a missing demonstration state — or materializing the FMS 1.6 baseline, which would
    /// discard the WaitingForPrerequisite lifecycle position DEC-066 exists to show. Uncovered becomes
    /// reachable the moment somebody materializes 1.6, which is a governed action the product already
    /// offers; the requirements awaiting that step are already visible as verification-impact items.
    ///
    /// Idempotent, and safe to apply to a database seeded before this existed: it no-ops when the procedure
    /// is absent or already has a revision in flight.
    /// </summary>
    private async Task EnsureVerificationCoverageGapAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return;
        var procedure = await db.TestProcedures.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.BaseNumber == GapProcedureNumber, ct);
        if (procedure is null) return;
        if (await db.TestProcedureRevisions.AnyAsync(x => x.ProcedureId == procedure.Id && x.State != TestProcedureState.Approved, ct)) return;

        db.TestProcedureRevisions.Add(new TestProcedureRevision(procedure.Id, 1,
            "Verify oceanic round-robin waypoint sequencing against the revised FMS 1.6 behavior.",
            "Load the FMS 1.6 candidate software and the approved navigation database.",
            "Initialize oceanic mode, stimulate the revised sequencing inputs, and record each observable output.",
            "Every observed output meets the linked requirement acceptance criteria.",
            TestProcedureState.Draft, "test.author", new DateTimeOffset(2024, 11, 18, 9, 30, 0, TimeSpan.Zero)));
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureProductLineAsync(Guid programId,CancellationToken ct)
    {
        var projectId=await db.Projects.Where(x=>x.ProgramId==programId).Select(x=>x.Id).SingleAsync(ct);if(await db.ProductLineComponents.AnyAsync(x=>x.ProjectId==projectId,ct)){await EnsureProductLineCompletionAsync(projectId,ct);return;}var now=new DateTimeOffset(2024,11,20,14,0,0,TimeSpan.Zero);
        var guidance=new ProductLineComponent(projectId,"COMP-00001","Guidance computation core","Reusable lateral and vertical guidance behavior shared by the released and next-generation FMS configurations.","cm.fms",now);var display=new ProductLineComponent(projectId,"COMP-00002","Flight-deck display adapter","Controlled crew-interface adaptation for the active display platform.","cm.fms",now);var main=new ComponentStream(guidance.Id,"MAIN","Released guidance line","cm.fms",now);var next=new ComponentStream(guidance.Id,"NEXT","FMS 1.6 guidance line","cm.fms",now);var displayMain=new ComponentStream(display.Id,"MAIN","Display production line","cm.fms",now);var baseContent="{\"guidanceMode\":\"released\",\"roundRobin\":false,\"integrityMonitoring\":true}";var nextContent="{\"guidanceMode\":\"next\",\"roundRobin\":true,\"integrityMonitoring\":true}";var displayContent="{\"displayPlatform\":\"DU-4\",\"annunciationProfile\":\"certified\"}";var baseRevision=new ComponentStreamRevision(main.Id,1,baseContent,Hash(baseContent),"cm.fms",now);var nextRevision=new ComponentStreamRevision(next.Id,1,nextContent,Hash(nextContent),"cm.fms",now);var displayRevision=new ComponentStreamRevision(displayMain.Id,1,displayContent,Hash(displayContent),"cm.fms",now);guidance.Approve("cm.fms",now);display.Approve("cm.fms",now);var released=new ProductVariant(projectId,"FMS-1.5","Released FMS 1.5 configuration","{\"release\":\"1.5\",\"aircraft\":\"fleet\"}","cm.fms",now);var active=new ProductVariant(projectId,"FMS-1.6","Active FMS 1.6 configuration","{\"release\":\"1.6\",\"aircraft\":\"fleet\"}","cm.fms",now);var selections=new[]{new VariantComponentSelection(released.Id,baseRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(released.Id,displayRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(active.Id,nextRevision.Id,"{\"required\":true}","cm.fms",now),new VariantComponentSelection(active.Id,displayRevision.Id,"{\"required\":true}","cm.fms",now)};released.Approve(now);active.Approve(now);var decision=new ComponentPropagationDecision(active.Id,nextRevision.Id,PropagationDecisionKind.Accept,"FMS 1.6 accepts the round-robin guidance capability after controlled impact analysis.","cm.fms",now);var releasedManifest=$"{{\"variant\":\"FMS-1.5\",\"components\":[\"{baseRevision.ManifestHash}\",\"{displayRevision.ManifestHash}\"]}}";var activeManifest=$"{{\"variant\":\"FMS-1.6\",\"components\":[\"{nextRevision.ManifestHash}\",\"{displayRevision.ManifestHash}\"]}}";var baselines=new[]{new ProductVariantBaseline(released.Id,1,releasedManifest,Hash(releasedManifest),"cm.fms",now),new ProductVariantBaseline(active.Id,1,activeManifest,Hash(activeManifest),"cm.fms",now)};var change=new ConfigurationChangeSet(projectId,"CCS-00001","Propagate round-robin guidance","Controlled propagation from the NEXT stream into the active product configuration.","cm.fms",now);change.ConfigureMerge(guidance.Id,next.Id,baseRevision.Id,nextRevision.Id,nextRevision.Id,nextContent,null,now);change.Close(now);db.AddRange(guidance,display,main,next,displayMain,baseRevision,nextRevision,displayRevision,released,active);db.VariantComponentSelections.AddRange(selections);db.ComponentPropagationDecisions.Add(decision);db.ProductVariantBaselines.AddRange(baselines);db.ConfigurationChangeSets.Add(change);await db.SaveChangesAsync(ct);await EnsureProductLineCompletionAsync(projectId,ct);
    }

    private async Task EnsureProductLineCompletionAsync(Guid projectId,CancellationToken ct)
    {
        if(await db.ControlledLibraries.AnyAsync(x=>x.ProjectId==projectId,ct))return;
        var now=new DateTimeOffset(2024,11,22,14,0,0,TimeSpan.Zero);const string actor="cm.fms";
        const string jpeg="/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EH//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EH//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EH//2Q==";
        string Content(string statement,string threshold)=>JsonSerializer.Serialize(new{requirements=new[]{new{id="LIB-SYSR-00001.00",statement,verification="Test",richContent=new{blocks=new object[]{new{type="paragraph",text="The reusable integrity monitor is allocated identically across applicable FMS configurations."},new{type="table",rows=new[]{new[]{"Monitor","Threshold","Response"},new[]{"Navigation integrity",threshold,"Annunciate and inhibit"}}},new{type="symbol",value="P(alert) < 1E-5 per flight hour"},new{type="reference",label="Navigation integrity allocation",target="ARP4754A safety assessment §4.3"},new{type="image",dataUri="data:image/jpeg;base64,"+jpeg,alt="Navigation integrity monitor architecture",caption="Figure 1 - Controlled navigation integrity monitor architecture"}}}}},traces=new[]{new{source="LIB-SYSR-00001.00",target="FMS-SAFETY-ALLOC-01",type="Satisfies"}},tests=new[]{new{id="LIB-TP-00001",title="Verify integrity monitor alert threshold",covers=new[]{"LIB-SYSR-00001.00"},status="Passed"}}});
        var library=new ControlledLibrary(projectId,"LIB-00001","Navigation integrity assurance","Approved reusable requirements and verification evidence for navigation integrity monitoring.",actor,now);var content1=Content("The FMS shall annunciate loss of navigation integrity within 2 seconds of detecting an invalid solution.","2 seconds");var revision1=new ControlledLibraryRevision(library.Id,1,content1,Hash(content1),actor,now);library.Approve(actor,now);var content2=Content("The FMS shall annunciate loss of navigation integrity within 1 second of detecting an invalid solution.","1 second");var revision2=new ControlledLibraryRevision(library.Id,2,content2,Hash(content2),actor,now.AddDays(1));
        var variants=await db.ProductVariants.Where(x=>x.ProjectId==projectId).OrderBy(x=>x.VariantKey).ToListAsync(ct);var released=variants.Single(x=>x.VariantKey=="FMS-1.5");var active=variants.Single(x=>x.VariantKey=="FMS-1.6");var releasedReuse=new VariantLibraryReuse(released.Id,library.Id,revision1.Id,VariantReuseMode.SynchronizedCopy,"{\"releases\":[\"1.5\"]}",actor,now);var activeReuse=new VariantLibraryReuse(active.Id,library.Id,revision1.Id,VariantReuseMode.SynchronizedCopy,"{\"releases\":[\"1.6\"]}",actor,now);releasedReuse.NotifyUpstream(revision2.Id,now.AddDays(1));activeReuse.NotifyUpstream(revision2.Id,now.AddDays(1));releasedReuse.Decide(PropagationDecisionKind.Defer,revision2.Id,"FMS 1.5 retains its certified two-second response until the next maintenance baseline.",actor,now.AddDays(2));activeReuse.Decide(PropagationDecisionKind.Accept,revision2.Id,"FMS 1.6 accepts the improved one-second integrity response after impact analysis.",actor,now.AddDays(2));
        var releasedDecision=new LibraryPropagationDecision(releasedReuse.Id,released.Id,library.Id,revision1.Id,revision2.Id,PropagationDecisionKind.Defer,"FMS 1.5 retains its certified two-second response until the next maintenance baseline.",actor,now.AddDays(2));var activeDecision=new LibraryPropagationDecision(activeReuse.Id,active.Id,library.Id,revision1.Id,revision2.Id,PropagationDecisionKind.Accept,"FMS 1.6 accepts the improved one-second integrity response after impact analysis.",actor,now.AddDays(2));db.AddRange(library,revision1,revision2,releasedReuse,activeReuse,releasedDecision,activeDecision);await db.SaveChangesAsync(ct);
        foreach(var (variant,reuse) in new[]{(released,releasedReuse),(active,activeReuse)})
        {var components=await db.VariantComponentSelections.AsNoTracking().Where(x=>x.VariantId==variant.Id).Select(x=>new{revisionId=x.ComponentRevisionId,x.ApplicabilityJson}).ToListAsync(ct);var manifest=JsonSerializer.Serialize(new{format="AeroLink product-variant-manifest/v2",variant=variant.VariantKey,components,libraries=new[]{new{reuseId=reuse.Id,libraryId=library.Id,selectedRevisionId=reuse.SelectedRevisionId,latestUpstreamRevisionId=reuse.LatestUpstreamRevisionId,mode=reuse.Mode.ToString(),syncState=reuse.SynchronizationState.ToString(),reuse.ApplicabilityJson}}});var next=(await db.ProductVariantBaselines.Where(x=>x.VariantId==variant.Id).MaxAsync(x=>(int?)x.Revision,ct)??0)+1;db.ProductVariantBaselines.Add(new ProductVariantBaseline(variant.Id,next,manifest,Hash(manifest),actor,now.AddDays(3)));}
        var templateBody=JsonSerializer.Serialize(new{titlePrefix="Configured System Requirements",subtitle="Exact product-line requirements, traceability, verification evidence, and controlled rich content"});var template=new DocumentTemplate(projectId,"TPL-00001","AeroLink configured SYSRD",templateBody,actor,now);var templateRevision=template.Approve(actor,now);var templateSnapshot=JsonSerializer.Serialize(new{template.TemplateNumber,template.Title,templateKind="SYSRD",organization="AeroLink Flight Systems",body=JsonSerializer.Deserialize<object>(templateBody)});db.AddRange(template,new DocumentTemplateRevision(template.Id,templateRevision,"SYSRD","AeroLink Flight Systems",templateBody,Hash(templateSnapshot),actor,now));await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds a small, deterministic cross-section of the records people reach from the showcase landing page.
    ///
    /// The original FMS seed was excellent at exercising the released requirement/test volume, but it was
    /// almost entirely a single historical shape: no Interface change-control work and no Problem Reports.
    /// That made Team Work and the PR centre look empty even though the rest of the programme was populated.
    /// These rows are deliberately created through the same aggregate lifecycle as authored records, and are
    /// keyed by durable per-scenario ownership rows so an upgrade or restart cannot multiply them. No released
    /// baseline row is edited and no persistent data is cleared.
    /// </summary>
    private async Task<string?> EnsureScenarioRichnessAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty)
            throw new InvalidOperationException("The FMS showcase Project is required before scenario enrichment can be recorded.");
        var releases = await db.Releases.Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Version, ct);
        if (!releases.TryGetValue("1.6", out var active) || !releases.TryGetValue("1.5", out var released))
            throw new InvalidOperationException("Both FMS 1.5 and 1.6 releases are required before scenario enrichment can be recorded.");

        var interfaceCount = await EnsureInterfaceScenariosAsync(programId, projectId, active.Id, ct);
        var problemCount = await EnsureProblemReportScenariosAsync(programId, projectId, released.Id, active.Id, ct);
        if (!await ScenarioRichnessCompleteAsync(programId, ct))
            throw new InvalidOperationException("The FMS scenario enrichment did not reach its controlled postconditions; the upgrade step remains retryable.");
        return $"Ensured {interfaceCount} Interface change-control scenarios and {problemCount} Problem Report scenarios across Builds 1.5 and 1.6.";
    }

    private static HashSet<Guid> ParseScenarioIds(IEnumerable<ShowcaseUpgradeStep> steps, string prefix)
    {
        var ids = new HashSet<Guid>();
        foreach (var step in steps)
        {
            if (!step.StepKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(step.Detail, out var id)) return [];
            ids.Add(id);
        }
        return ids;
    }

    private async Task<SystemChangeRequest?> ResolveInterfaceScenarioAsync(Guid programId, Guid projectId, Guid releaseId,
        int index, CancellationToken ct)
    {
        var step = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.StepKey == InterfaceScenarioStepKey(index), ct);
        if (step is null) return null;
        if (!Guid.TryParse(step.Detail, out var artifactId))
            throw new InvalidOperationException($"The {step.StepKey} ownership record does not contain an artifact identity.");
        var request = await db.SystemChangeRequests.SingleOrDefaultAsync(x => x.Id == artifactId, ct)
            ?? throw new InvalidOperationException($"The {step.StepKey} ownership record names a missing change request.");
        if (request.ProjectId != projectId || request.Type != ChangeRequestType.Interface || request.TargetReleaseId != releaseId)
            throw new InvalidOperationException($"The {step.StepKey} ownership record names a change request outside its controlled scope.");
        return request;
    }

    private async Task<ProblemReport?> ResolveProblemReportScenarioAsync(Guid programId, Guid projectId, int index,
        Guid expectedReleaseId, CancellationToken ct)
    {
        var step = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.StepKey == ProblemReportScenarioStepKey(index), ct);
        if (step is null) return null;
        if (!Guid.TryParse(step.Detail, out var artifactId))
            throw new InvalidOperationException($"The {step.StepKey} ownership record does not contain an artifact identity.");
        var report = await db.ProblemReports.SingleOrDefaultAsync(x => x.Id == artifactId, ct)
            ?? throw new InvalidOperationException($"The {step.StepKey} ownership record names a missing Problem Report.");
        if (report.ProjectId != projectId || report.TargetReleaseId != expectedReleaseId)
            throw new InvalidOperationException($"The {step.StepKey} ownership record names a Problem Report outside its controlled scope.");
        return report;
    }

    private async Task<int> EnsureInterfaceScenariosAsync(Guid programId, Guid projectId, Guid releaseId, CancellationToken ct)
    {
        var existing = await db.SystemChangeRequests
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(ct);
        var usedNumbers = existing.Select(x => x.BaseNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedRequirementNumbers = (await db.Requirements.Where(x => x.ProjectId == projectId)
                .Select(x => x.BaseNumber).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingRequestIds = existing.Select(request => request.Id).ToList();
        foreach (var number in await db.RequirementChanges.Where(x => existingRequestIds.Contains(x.ChangeRequestId))
                     .Select(x => x.BaseNumber).ToListAsync(ct))
            usedRequirementNumbers.Add(number);
        var now = await EffectiveInterfaceTimelineAtAsync(programId,
            new DateTimeOffset(2024, 12, 2, 10, 0, 0, TimeSpan.Zero), ct);
        var missingScenarioCount = 0;
        for (var index = 1; index <= 8; index++)
            if (await ResolveInterfaceScenarioAsync(programId, projectId, releaseId, index, ct) is null)
                missingScenarioCount++;
        if (missingScenarioCount > 0)
            // This is a preflight, before any new request, requirement change, review or baseline selection
            // is written. A current account without current role authority is not a plausible historical
            // signature, and a stale/ended authority must never be repaired by the showcase seeder.
            foreach (var actor in InterfaceScenarioActors)
                await EnsureCurrentProgramAuthorityAsync(programId, actor.UserName, actor.Role, now, ct);

        var baseline = await db.CandidateBaselines
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.ReleaseId == releaseId && x.BaseNumber == "SW-01.60", ct);

        for (var i = 1; i <= 8; i++)
        {
            var marker = InterfaceScenarioMarker(i);
            if (await ResolveInterfaceScenarioAsync(programId, projectId, releaseId, i, ct) is not null) continue;
            var baseNumber = AllocateScenarioNumber("ICDCR", i, usedNumbers);
            var requirementBaseNumber = AllocateScenarioNumber("ICDR", i, usedRequirementNumbers);
            var author = InterfaceScenarioAuthors[(i - 1) % InterfaceScenarioAuthors.Length];
            var request = new SystemChangeRequest(baseNumber, 0, projectId, releaseId,
                i == 1 ? "Align navigation interface timing contract" : $"FMS 1.6 interface contract scenario {i}",
                "The controlled interface contract needs a documented FMS 1.6 decision.",
                $"The interface impact was reviewed against the current navigation and display boundaries. {marker}",
                "Record the exact interface behaviour and its compatibility decision.", author, now.AddDays(i), ChangeRequestType.Interface);
            request.AddRequirementChange(author, requirementBaseNumber, 0, RequirementLevel.Interface,
                RequirementChangeKind.Introduce,
                $"The FMS interface shall preserve deterministic navigation exchange behaviour {i:D2}.",
                "The interface requirement is retained as controlled showcase content.", "Not Applicable", now.AddDays(i));

            switch (i)
            {
                case 2:
                    request.SubmitForReview(author, [new("assurance.reviewer", "Development Assurance Reviewer")], now.AddDays(i).AddHours(1));
                    break;
                case 3:
                    request.SubmitForReview(author, [new("assurance.reviewer", "Development Assurance Reviewer")], now.AddDays(i).AddHours(1));
                    request.ApproveActiveStage("assurance.reviewer", now.AddDays(i).AddHours(2));
                    break;
                case 4:
                    request.SubmitForReview(author, [new("lead.reviewer", "Maya Patel")], now.AddDays(i).AddHours(1));
                    request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2));
                    break;
                case 5:
                    request.Defer(author, "Deferred pending the next interface supplier coordination window.", now.AddDays(i).AddHours(1));
                    break;
                case 6:
                    request.Withdraw(author, "Withdrawn after the interface contract was consolidated into another package.", now.AddDays(i).AddHours(1));
                    break;
                case 7:
                    request.SubmitForReview(author,
                        [new("lead.reviewer", "Maya Patel"), new("manager.reviewer", "Olivia Chen")], now.AddDays(i).AddHours(1));
                    break;
            }

            db.SystemChangeRequests.Add(request);
            db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, InterfaceScenarioStepKey(i), request.Id.ToString("D"), request.CreatedAt));
            usedNumbers.Add(baseNumber);
            usedRequirementNumbers.Add(requirementBaseNumber);
            // Selection is a separate fact from approval. Keep one Interface example in the active candidate
            // only when the expected draft baseline is available; an older installation without that baseline
            // must not be made to look as though a build accepted work it cannot name.
            if (i == 4 && baseline is not null && baseline.State == CandidateBaselineState.Draft)
                baseline.Select(request, "cm.fms", now.AddDays(i).AddHours(3));
        }
        await db.SaveChangesAsync(ct);
        return await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == programId
            && x.StepKey.StartsWith(InterfaceScenarioStepPrefix), ct);
    }

    private async Task<int> EnsureProblemReportScenariosAsync(Guid programId, Guid projectId, Guid releasedId, Guid activeId, CancellationToken ct)
    {
        var existing = await db.ProblemReports.Where(x => x.ProjectId == projectId).ToListAsync(ct);
        var usedNumbers = existing.Select(x => x.ReportNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = new[]
        {
            ProblemReportCategory.TaskDriver, ProblemReportCategory.ProductImprovement,
            ProblemReportCategory.CodeFunctional, ProblemReportCategory.CodeNonFunctional,
            ProblemReportCategory.RequirementsDocumentation, ProblemReportCategory.TestBlocking,
            ProblemReportCategory.TestNonBlocking, ProblemReportCategory.EnvironmentTooling,
        };
        // SQLite stores DateTimeOffset as a value it cannot order server-side. This is the bounded execution
        // set for one showcase Project, so keep the provider-neutral ordering in memory. Only a failed
        // Build 1.5 execution with an actual passing retest can seed closure evidence.
        var projectExecutions = await db.TestExecutions.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releasedId)
            .ToListAsync(ct);
        var releasedBuildIds = (await db.SoftwareBuilds.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.ReleaseId == releasedId)
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var failurePairs = projectExecutions
            .Where(x => x.Outcome == TestOutcome.Fail && x.SoftwareBuildId is { } buildId && releasedBuildIds.Contains(buildId))
            .OrderBy(x => x.ExecutedAt).ThenBy(x => x.Id)
            .Select(failure => new
            {
                Failure = failure,
                Retest = projectExecutions.Where(candidate => candidate.RetestOfExecutionId == failure.Id
                        && candidate.Outcome == TestOutcome.Pass && candidate.SoftwareBuildId is { } buildId
                        && releasedBuildIds.Contains(buildId)
                        && candidate.ProcedureRevisionId == failure.ProcedureRevisionId)
                    .OrderBy(candidate => candidate.RecordedAt).ThenBy(candidate => candidate.Id)
                    .FirstOrDefault(),
            })
            .Where(pair => pair.Retest is not null)
            .ToList();
        if (failurePairs.Count < 2)
            throw new InvalidOperationException("FMS closure scenarios require two failed Build 1.5 executions with passing retest successors.");

        var actorHandles = ProblemReportOwners.Concat(["project.lead", "quality.analyst"])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var actorNames = (await db.UserAccounts.AsNoTracking()
                .Where(x => actorHandles.Contains(x.UserName)).ToListAsync(ct))
            .ToDictionary(x => x.UserName, x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
        string? ActorName(string actor) => actorNames.TryGetValue(actor, out var name) ? name : null;
        var now = await EffectiveProblemReportTimelineAtAsync(programId,
            new DateTimeOffset(2024, 12, 12, 9, 0, 0, TimeSpan.Zero), ct);

        for (var i = 1; i <= 8; i++)
        {
            var marker = ProblemReportScenarioMarker(i);
            var expectedRelease = IsHistoricalProblemReportScenario(i) ? releasedId : activeId;
            if (await ResolveProblemReportScenarioAsync(programId, projectId, i, expectedRelease, ct) is not null) continue;
            var reportNumber = AllocateScenarioNumber("PR", i, usedNumbers);
            var owner = ProblemReportOwners[i - 1];
            var retestPair = i is 6 or 7 ? failurePairs[i - 6] : null;
            var createdAt = retestPair is null
                ? now.AddDays(i)
                : await EffectiveProblemReportTimelineAtAsync(programId,
                    retestPair.Failure.ExecutedAt.AddMinutes(5), ct);
            var report = new ProblemReport(projectId, reportNumber,
                i == 1 ? "Navigation database handoff follow-up" : $"FMS showcase problem report {i}",
                "The FMS demonstration record captures a controlled engineering concern for this scenario.",
                "Initial triage is retained with the report so the reader can follow the decision.", owner, createdAt,
                classification: "FMS engineering record",
                severity: i is 3 or 6 ? ProblemReportSeverity.High : ProblemReportSeverity.Major,
                priority: i is 1 or 6 ? ProblemReportPriority.High : ProblemReportPriority.Normal,
                origin: i is 6 or 7 ? "Test execution" : "Engineering review",
                affectedConfiguration: IsHistoricalProblemReportScenario(i) ? "FMS 1.5" : "FMS 1.6",
                targetReleaseId: expectedRelease,
                responsibleEngineerId: owner,
                additionalInformation: $"Synthetic, deterministic showcase content; no external incident is implied. {marker}",
                category: categories[i - 1]);

            // The endpoint writes one immutable event after each domain mutation. Keep the same event names,
            // exact post-mutation snapshots, actors and state edges in the synthetic lifecycle.
            AddScenarioRevision(report, i is 6 or 7 ? "ProblemReportCreatedFromFailedExecution" : "ProblemReportCreated",
                owner, createdAt, ActorName(owner));
            if (retestPair is not null)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution",
                    retestPair.Failure.Id, ProblemReportRelationshipPolicy.OriginatingFailure,
                    ProblemReportRelationshipProducer.FailureCreationWorkflow, owner, createdAt));

            var eventTime = createdAt;
            DateTimeOffset At(int hours, int minutes) => i is 6 or 7
                ? eventTime.AddMinutes(minutes)
                : eventTime.AddHours(hours);
            switch (i)
            {
                case 2:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    break;
                case 3:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    break;
                case 4:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    report.BeginImplementation(owner, At(3, 30));
                    AddScenarioRevision(report, "ImplementationStarted", owner, At(3, 30), ActorName(owner), ProblemReportState.Open, ProblemReportState.Implementing);
                    break;
                case 5:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    report.BeginImplementation(owner, At(3, 30));
                    AddScenarioRevision(report, "ImplementationStarted", owner, At(3, 30), ActorName(owner), ProblemReportState.Open, ProblemReportState.Implementing);
                    report.BeginInvestigation(owner, "The implementation path and affected interface were reviewed.",
                        "The demonstration root cause is bounded to the scenario record.", "No aircraft effect is claimed.",
                        "The active build remains clearly identified while work is in progress.", At(4, 40));
                    AddScenarioRevision(report, "InvestigationRecorded", owner, At(4, 40), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Implementing,
                        detail: "The implementation path and affected interface were reviewed.");
                    report.ProposeResolution(owner, "Apply the controlled corrective action in the next FMS 1.6 review.", At(5, 50));
                    AddScenarioRevision(report, "ResolutionProposed", owner, At(5, 50), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Verifying);
                    break;
                case 6:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    report.BeginImplementation(owner, At(3, 30));
                    AddScenarioRevision(report, "ImplementationStarted", owner, At(3, 30), ActorName(owner), ProblemReportState.Open, ProblemReportState.Implementing);
                    report.BeginInvestigation(owner, "The test finding was reproduced and isolated to the seeded path.",
                        "The deterministic test setup was the source of the observation.", "No released-build safety claim is changed.",
                        "Retain the failed observation and use the recorded retest evidence.", At(4, 40));
                    AddScenarioRevision(report, "InvestigationRecorded", owner, At(4, 40), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Implementing,
                        detail: "The test finding was reproduced and isolated to the seeded path.");
                    report.ProposeResolution(owner, "Correct the test setup and retain the retest result.", At(5, 50));
                    AddScenarioRevision(report, "ResolutionProposed", owner, At(5, 50), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Verifying);
                    break;
                case 7:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    report.BeginImplementation(owner, At(3, 30));
                    AddScenarioRevision(report, "ImplementationStarted", owner, At(3, 30), ActorName(owner), ProblemReportState.Open, ProblemReportState.Implementing);
                    report.BeginInvestigation(owner, "The correction was verified against the controlled scenario.",
                        "A non-functional test weakness was identified and corrected.", "No operational effect is claimed.",
                        "The active record remains available for SQA closure.", At(4, 40));
                    AddScenarioRevision(report, "InvestigationRecorded", owner, At(4, 40), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Implementing,
                        detail: "The correction was verified against the controlled scenario.");
                    report.ProposeResolution(owner, "Retain the corrected verification path in the active build.", At(5, 50));
                    AddScenarioRevision(report, "ResolutionProposed", owner, At(5, 50), ActorName(owner), ProblemReportState.Implementing, ProblemReportState.Verifying);
                    break;
                case 8:
                    report.ReadyForSccb(owner, At(1, 10));
                    AddScenarioRevision(report, "ReadyForSccb", owner, At(1, 10), ActorName(owner), ProblemReportState.Draft, ProblemReportState.ReadyForSccb);
                    report.OpenBySccb("project.lead", At(2, 20));
                    AddScenarioRevision(report, "OpenedBySccb", "project.lead", At(2, 20), ActorName("project.lead"), ProblemReportState.ReadyForSccb, ProblemReportState.Open);
                    report.ApplyDisposition(owner, ProblemReportDisposition.CannotReproduce,
                        "The reported condition could not be reproduced in the controlled showcase setup.", null, At(3, 30));
                    AddScenarioRevision(report, "DispositionRecorded", owner, At(3, 30), ActorName(owner), ProblemReportState.Open, ProblemReportState.Rejected,
                        detail: "The reported condition could not be reproduced in the controlled showcase setup.", rationale: "The reported condition could not be reproduced in the controlled showcase setup.");
                    break;
            }
            db.ProblemReports.Add(report);
            db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, ProblemReportScenarioStepKey(i), report.Id.ToString("D"), report.CreatedAt));
            usedNumbers.Add(reportNumber);
        }
        await db.SaveChangesAsync(ct);
        await EnsureProblemReportBuildScopeLinksAsync(programId, projectId, releasedId, activeId, ct);
        await EnsureProblemReportControlledEvidenceAsync(programId, projectId, ct);
        return await db.ShowcaseUpgradeSteps.CountAsync(x => x.ProgramId == programId
            && x.StepKey.StartsWith(ProblemReportScenarioStepPrefix), ct);
    }

    private ProblemReportRevision AddScenarioRevision(ProblemReport report, string eventType, string actor, DateTimeOffset occurredAt,
        string? actorDisplayName, ProblemReportState? fromState = null, ProblemReportState? toState = null,
        string? detail = null, string? rationale = null)
    {
        var snapshot = ProblemReportControlledEditingAdapter.EvidenceSnapshot(report);
        var revision = new ProblemReportRevision(report.Id, report.Revision, eventType, actor,
            report.CanonicalHash(), snapshot, occurredAt, detail: detail,
            fromState: fromState?.ToString(), toState: toState?.ToString(), rationale: rationale,
            actorDisplayName: actorDisplayName);
        db.ProblemReportRevisions.Add(revision);
        return revision;
    }

    private static string AllocateScenarioNumber(string prefix, int scenarioIndex, ISet<string> usedNumbers)
    {
        // Keep scenario ownership in the durable upgrade-step mapping, not this display number. A legitimate
        // record may already occupy the preferred slot, so move deterministically to the next free slot rather
        // than treating it as one of ours or allowing a uniqueness failure to consume the upgrade attempt.
        for (var offset = 0; offset < 1000; offset++)
        {
            var candidate = $"{prefix}-{PreferredScenarioNumber + scenarioIndex - 1 + offset:D5}";
            if (!usedNumbers.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException($"No free deterministic {prefix} number was available for FMS showcase scenario {scenarioIndex:D2}.");
    }

    private async Task EnsureProblemReportBuildScopeLinksAsync(Guid programId, Guid projectId, Guid releasedId, Guid activeId,
        CancellationToken ct)
    {
        for (var index = 1; index <= 8; index++)
        {
            var expectedRelease = IsHistoricalProblemReportScenario(index) ? releasedId : activeId;
            var report = await ResolveProblemReportScenarioAsync(programId, projectId, index, expectedRelease, ct)
                ?? throw new InvalidOperationException($"The {ProblemReportScenarioStepKey(index)} ownership record is missing.");
            if (report.TargetReleaseId != expectedRelease)
                throw new InvalidOperationException($"The {report.ReportNumber} FMS Problem Report scenario has an invalid target release.");

            var links = await db.ProblemReportLinks.Where(x => x.ProblemReportId == report.Id
                && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope).ToListAsync(ct);
            if (links.Any(x => x.ArtifactId != expectedRelease))
                throw new InvalidOperationException($"The {report.ReportNumber} FMS Problem Report scenario has an incorrect BuildScope link.");
            if (links.Count == 0)
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "Release", expectedRelease,
                    ProblemReportRelationshipPolicy.BuildScope, ProblemReportRelationshipProducer.TargetBuildWorkflow,
                    "system.workspace", report.CreatedAt));
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureProblemReportControlledEvidenceAsync(Guid programId, Guid projectId, CancellationToken ct)
    {
        var authority = await CheckUpgradeAuthorityAsync(programId, ct);
        if (!authority.Ready)
            throw new InvalidOperationException($"Controlled Problem Report evidence cannot be upgraded: {authority.Code} {authority.Detail}");
        var approvedClosureAt = authority.ClosureAt
            ?? throw new InvalidOperationException("Controlled Problem Report evidence has no attributable closure timestamp.");

        var releasedReleaseId = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == "1.5")
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("The FMS 1.5 release is required for historical closure-scenario evidence.");
        var releasedBuildIds = (await db.SoftwareBuilds.AsNoTracking()
                .Where(x => x.ProjectId == projectId && x.ReleaseId == releasedReleaseId)
                .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var executions = await db.TestExecutions.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ReleaseId == releasedReleaseId)
            .ToListAsync(ct);
        var failurePairs = executions
            .Where(x => x.Outcome == TestOutcome.Fail && x.SoftwareBuildId is { } buildId && releasedBuildIds.Contains(buildId))
            .OrderBy(x => x.ExecutedAt).ThenBy(x => x.Id)
            .Select(failure => new
            {
                Failure = failure,
                Retest = executions.Where(candidate => candidate.RetestOfExecutionId == failure.Id
                        && candidate.Outcome == TestOutcome.Pass && candidate.SoftwareBuildId is { } buildId
                        && releasedBuildIds.Contains(buildId)
                        && candidate.ProcedureRevisionId == failure.ProcedureRevisionId)
                    .OrderBy(candidate => candidate.RecordedAt).ThenBy(candidate => candidate.Id)
                    .FirstOrDefault(),
            })
            .Where(pair => pair.Retest is not null)
            .ToList();
        if (failurePairs.Count < 2)
            throw new InvalidOperationException("FMS closure scenarios require two failed Build 1.5 executions with passing retest successors.");

        // The host seeds the demo directory before FMS data. A frozen controlled closure must name the
        // actual seeded active SQA account and membership. The upgrade preflight above is deliberately
        // strict: no controlled closure candidate is attributed while the account is disabled/locked, the
        // current membership is ended, or the historical membership does not cover the approval time.
        var sqaAccount = await db.UserAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserName == QualityAnalystUserName && x.State == AccountState.Active, ct)
            ?? throw new InvalidOperationException("The seeded quality.analyst account is required before FMS closure scenarios can be frozen.");
        var sqaAccountId = sqaAccount.Id;
        var sqaMemberships = await db.ProgramMemberships.AsNoTracking()
            .Where(x => x.UserId == sqaAccountId && x.ProgramId == programId
                && x.Role == ProgramRole.SoftwareQualityAnalyst)
            .ToListAsync(ct);
        if (!sqaMemberships.Any(x => x.EndedAt is null))
            throw new InvalidOperationException("The current quality.analyst SoftwareQualityAnalyst membership is required before FMS closure scenarios can be frozen.");
        var hasActiveSqa = true;
        // CheckUpgradeAuthorityAsync has already preflighted every actor used by pending Problem Report
        // scenarios. Keep newly-created evidence after the latest real grant even when the mapped report
        // itself was authored in the original deterministic 2024 timeline.
        var latestProblemActorGrant = await LatestCurrentProblemReportActorGrantAsync(programId, ct);
        DateTimeOffset? lastEvidenceAt = null;
        DateTimeOffset EvidenceAt(TestExecution execution, DateTimeOffset lastReportEventAt)
        {
            var at = execution.RecordedAt.AddMinutes(1);
            if (latestProblemActorGrant is { } grant && at <= grant) at = grant.AddMinutes(1);
            if (at <= lastReportEventAt) at = lastReportEventAt.AddMinutes(1);
            if (lastEvidenceAt is { } previous && at <= previous) at = previous.AddMinutes(1);
            lastEvidenceAt = at;
            return at;
        }
        var actorHandles = new[] { "test.engineer", "quality.analyst" };
        var actorNames = (await db.UserAccounts.AsNoTracking()
                .Where(x => actorHandles.Contains(x.UserName)).ToListAsync(ct))
            .ToDictionary(x => x.UserName, x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
        string? ActorName(string actor) => actorNames.TryGetValue(actor, out var name) ? name : null;
        var evidenceService = new ProblemReportClosureCandidateService(db);
        var policy = new ProblemReportClosureVerificationPolicy(db);
        for (var index = 6; index <= 7; index++)
        {
            var marker = ProblemReportScenarioMarker(index);
            var report = await ResolveProblemReportScenarioAsync(programId, projectId, index, releasedReleaseId, ct)
                ?? throw new InvalidOperationException($"The {ProblemReportScenarioStepKey(index)} ownership record is missing.");
            var pair = failurePairs[index - 6];
            var failure = pair.Failure;
            var reportRevisionTimes = await db.ProblemReportRevisions.AsNoTracking()
                .Where(x => x.ProblemReportId == report.Id).Select(x => x.OccurredAt).ToListAsync(ct);
            var lastReportEventAt = reportRevisionTimes.Count == 0 ? report.CreatedAt : reportRevisionTimes.Max();

            // Reopening a frozen historical closure deliberately clears its verification execution and
            // advances the report revision. The old Build 1.5 retest is no longer a valid successor for that
            // new revision. With ended SQA authority, leave the report in that honest in-work state rather
            // than inventing a new passing execution or restoring the ended membership.
            if (!hasActiveSqa && report.ResolutionVerificationExecutionId is null && report.State == ProblemReportState.Verifying
                && await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.EventType == "ClosureApproved", ct))
                continue;

            var originLinks = await db.ProblemReportLinks.Where(x => x.ProblemReportId == report.Id
                && x.ArtifactType == "TestExecution"
                && x.Relationship == ProblemReportRelationshipPolicy.OriginatingFailure).ToListAsync(ct);
            if (originLinks.Any(x => x.ArtifactId != failure.Id))
                throw new InvalidOperationException($"The {marker} scenario has an incorrect originating failed execution link.");
            if (originLinks.Count == 0)
            {
                if (report.State is ProblemReportState.Closed or ProblemReportState.Rejected)
                    throw new InvalidOperationException($"The {marker} scenario is terminal and cannot receive missing origin evidence.");
                db.ProblemReportLinks.Add(ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution",
                    failure.Id, ProblemReportRelationshipPolicy.OriginatingFailure,
                    ProblemReportRelationshipProducer.FailureCreationWorkflow, report.ReportedBy, report.CreatedAt));
                await db.SaveChangesAsync(ct);
            }

            var execution = report.ResolutionVerificationExecutionId is { } selectedExecutionId
                ? await db.TestExecutions.SingleOrDefaultAsync(x => x.Id == selectedExecutionId, ct)
                    ?? throw new InvalidOperationException($"The {marker} scenario names missing verification execution {selectedExecutionId}.")
                : pair.Retest!;
            var verificationDecision = await policy.ValidateAsync(report, execution, ct);
            if (report.ResolutionVerificationExecutionId is null
                && !verificationDecision.Accepted
                && verificationDecision.Code == "pr_verification_not_successor")
            {
                // The historical Build 1.5 pass remains the causal retest, but it cannot verify corrective
                // action authored years later. Add one owned synthetic successor after the current Verifying
                // boundary. Its immutable identity is recorded before the report adopts it, so an interrupted
                // upgrade resumes without duplicating controlled execution evidence.
                var successorAt = lastReportEventAt.AddMinutes(1);
                if (latestProblemActorGrant is { } grant && successorAt <= grant)
                    successorAt = grant.AddMinutes(1);
                execution = await EnsureProblemReportVerificationSuccessorAsync(programId, index, report,
                    failure, pair.Retest!, successorAt, ct);
                verificationDecision = await policy.ValidateAsync(report, execution, ct);
            }
            if (!verificationDecision.Accepted)
                throw new InvalidOperationException($"The {marker} scenario failed closure verification policy: {verificationDecision.Code} {verificationDecision.Error}");

            // Reuse one controlled timestamp for all rows created for this report. The next governed report
            // receives a strictly later timestamp, preserving event ordering without putting the closure
            // candidate after its own SQA approval.
            var evidenceAt = EvidenceAt(execution, lastReportEventAt);

            if (report.ResolutionVerificationExecutionId is null)
            {
                if (report.State != ProblemReportState.Verifying)
                    throw new InvalidOperationException($"The {marker} scenario cannot record verification from {report.State}.");
                report.RecordResolutionVerification("test.engineer", execution.Id, evidenceAt);
                AddScenarioRevision(report, "ResolutionVerified", "test.engineer", evidenceAt, ActorName("test.engineer"),
                    ProblemReportState.Verifying, ProblemReportState.WaitingForSqaToClose,
                    detail: "Controlled verification evidence recorded for the deterministic showcase scenario.");
            }
            else if (report.ResolutionVerificationExecutionId != execution.Id)
                throw new InvalidOperationException($"The {marker} scenario has inconsistent verification evidence.");

            var existingDecision = await policy.ValidateAsync(report, execution, ct);
            if (!existingDecision.Accepted)
                throw new InvalidOperationException($"The {marker} scenario failed closure verification policy: {existingDecision.Code} {existingDecision.Error}");

            var resolutionLinks = await db.ProblemReportLinks.Where(x => x.ProblemReportId == report.Id
                && x.ArtifactType == "TestExecution"
                && x.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification).ToListAsync(ct);
            if (resolutionLinks.Any(x => x.ArtifactId != execution.Id))
                throw new InvalidOperationException($"The {marker} scenario has an incorrect resolution-verification link.");
            var resolutionLink = resolutionLinks.SingleOrDefault(x => x.ArtifactId == execution.Id);
            if (resolutionLink is null)
            {
                resolutionLink = ProblemReportRelationshipPolicy.CreateControlled(report.Id, "TestExecution", execution.Id,
                    ProblemReportRelationshipPolicy.ResolutionVerification,
                    ProblemReportRelationshipProducer.ResolutionVerificationWorkflow, "test.engineer", evidenceAt);
                db.ProblemReportLinks.Add(resolutionLink);
            }
            await db.SaveChangesAsync(ct);

            var candidates = await db.ProblemReportClosureCandidates
                .Where(x => x.ProblemReportId == report.Id).OrderByDescending(x => x.Sequence).ToListAsync(ct);
            // A reopened historical closure has an approved candidate for its old report revision. It is
            // immutable evidence, not an approval for this new verification; create a fresh candidate for
            // the current Waiting revision. A current pending candidate remains retryable and is reused.
            var candidate = candidates.FirstOrDefault(x => x.State == ProblemReportClosureCandidateState.Pending
                && x.ReportRevision == report.Revision && x.VerificationExecutionId == execution.Id);
            if (report.State == ProblemReportState.WaitingForSqaToClose && candidate is null)
                candidate = await evidenceService.CreateAsync(report, execution, resolutionLink, "test.engineer",
                    evidenceAt, ct);
            await db.SaveChangesAsync(ct);

            if (report.State == ProblemReportState.WaitingForSqaToClose)
            {
                var candidateDecision = await evidenceService.ValidateForApprovalAsync(report, ct);
                if (!candidateDecision.Accepted || candidateDecision.Candidate is null)
                    throw new InvalidOperationException($"The {marker} scenario failed closure-candidate validation: {candidateDecision.Code} {candidateDecision.Error}");
                candidate = candidateDecision.Candidate;
                if (index == 7 && hasActiveSqa)
                {
                    var closureAt = approvedClosureAt;
                    report.ApproveClosure("quality.analyst", sqaAccountId, closureAt);
                    var closureRevision = AddScenarioRevision(report, "ClosureApproved", "quality.analyst", closureAt, ActorName("quality.analyst"),
                        ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed,
                        detail: "Independent SQA closure approved for the deterministic showcase scenario.");
                    await evidenceService.FreezeForApprovalAsync(report, candidate, closureRevision, "quality.analyst", sqaAccountId,
                        ProgramRole.SoftwareQualityAnalyst.ToString(), closureAt, ct);
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<TestExecution> EnsureProblemReportVerificationSuccessorAsync(Guid programId, int index,
        ProblemReport report, TestExecution failure, TestExecution predecessor, DateTimeOffset recordedAt,
        CancellationToken ct)
    {
        var stepKey = ProblemReportVerificationExecutionStepKey(index);
        var recorded = await db.ShowcaseUpgradeSteps.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProgramId == programId && x.StepKey == stepKey, ct);
        if (recorded is not null)
        {
            if (!Guid.TryParse(recorded.Detail, out var executionId))
                throw new InvalidOperationException($"The {stepKey} ownership record does not contain an execution identity.");
            var existing = await db.TestExecutions.SingleOrDefaultAsync(x => x.Id == executionId, ct)
                ?? throw new InvalidOperationException($"The {stepKey} ownership record names a missing test execution.");
            if (existing.ProjectId != report.ProjectId
                || existing.ReleaseId != report.TargetReleaseId
                || existing.SoftwareBuildId != failure.SoftwareBuildId
                || existing.ProcedureRevisionId != failure.ProcedureRevisionId
                || existing.RetestOfExecutionId != predecessor.Id
                || existing.Outcome != TestOutcome.Pass
                || !string.Equals(existing.ExecutedBy, "test.engineer", StringComparison.OrdinalIgnoreCase)
                || existing.RecordedAt < recordedAt)
                throw new InvalidOperationException($"The {stepKey} ownership record names an execution outside its controlled showcase scope.");
            return existing;
        }

        if (failure.SoftwareBuildId is null || failure.ReleaseId is null || report.TargetReleaseId != failure.ReleaseId)
            throw new InvalidOperationException($"The {ProblemReportScenarioMarker(index)} scenario cannot create an exact Build 1.5 successor execution.");
        await EnsureCurrentProgramAuthorityAsync(programId, "test.engineer", ProgramRole.TestEngineer, recordedAt, ct);
        var execution = new TestExecution(report.ProjectId, failure.ProcedureRevisionId, failure.SoftwareBuildId,
            predecessor.Id, TestOutcome.Pass, "test.engineer", "FMS integration rig / controlled corrective retest",
            "A new controlled successor retest confirms the corrected deterministic showcase path.",
            $"evidence/fms-1.5/problem-report-{index:D2}-successor.json", recordedAt, recordedAt, failure.ReleaseId);
        db.TestExecutions.Add(execution);
        db.ShowcaseUpgradeSteps.Add(new ShowcaseUpgradeStep(programId, stepKey, execution.Id.ToString("D"), recordedAt));
        await db.SaveChangesAsync(ct);
        return execution;
    }

    private async Task<bool> ScenarioRichnessCompleteAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return false;
        var releases = await db.Releases.AsNoTracking().Where(x => x.ProjectId == projectId).ToDictionaryAsync(x => x.Version, ct);
        if (!releases.TryGetValue("1.5", out var released) || !releases.TryGetValue("1.6", out var active)) return false;
        var sqaAccount = await db.UserAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == "quality.analyst"
            && x.State == AccountState.Active, ct);
        var hasActiveSqa = sqaAccount is not null && await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == sqaAccount.Id
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst && x.EndedAt == null, ct);
        var hasSqaHistory = sqaAccount is not null && await db.ProgramMemberships.AsNoTracking().AnyAsync(x => x.UserId == sqaAccount.Id
                && x.ProgramId == programId && x.Role == ProgramRole.SoftwareQualityAnalyst, ct);
        if (!hasSqaHistory) return false;

        var interfaces = new List<SystemChangeRequest>();
        for (var index = 1; index <= 8; index++)
        {
            var request = await ResolveInterfaceScenarioAsync(programId, projectId, active.Id, index, ct);
            if (request is null) return false;
            interfaces.Add(request);
        }
        if (!interfaces.All(x => x.TargetReleaseId == active.Id && InterfaceScenarioAuthors.Contains(x.AuthorId, StringComparer.OrdinalIgnoreCase))
            || !Enum.GetValues<ChangeRequestState>().Where(x => x is ChangeRequestState.Draft or ChangeRequestState.InReview
                or ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline or ChangeRequestState.Deferred or ChangeRequestState.Withdrawn)
                .All(state => interfaces.Any(x => x.State == state))) return false;

        var reports = new List<ProblemReport>();
        for (var index = 1; index <= 8; index++)
        {
            var report = await ResolveProblemReportScenarioAsync(programId, projectId, index,
                IsHistoricalProblemReportScenario(index) ? released.Id : active.Id, ct);
            if (report is null) return false;
            reports.Add(report);
        }
        if (reports.Count != 8 || !reports.All(x => ProblemReportOwners.Contains(x.ResponsibleEngineerId, StringComparer.OrdinalIgnoreCase))) return false;
        if (reports.Count(x => x.TargetReleaseId == released.Id) != 6 || reports.Count(x => x.TargetReleaseId == active.Id) != 2) return false;
        var requiredStates = new[] { ProblemReportState.Draft, ProblemReportState.Implementing, ProblemReportState.Verifying,
            ProblemReportState.WaitingForSqaToClose, ProblemReportState.Closed, ProblemReportState.Rejected };
        if (!hasActiveSqa)
            requiredStates = requiredStates.Where(state => state != ProblemReportState.Closed).ToArray();
        if (!requiredStates.All(state => reports.Any(x => x.State == state))) return false;

        foreach (var report in reports)
        {
            var expectedRelease = report.TargetReleaseId;
            var buildLinks = await db.ProblemReportLinks.AsNoTracking().Where(x => x.ProblemReportId == report.Id
                && x.ArtifactType == "Release" && x.Relationship == ProblemReportRelationshipPolicy.BuildScope).ToListAsync(ct);
            if (expectedRelease is null || buildLinks.Count != 1 || buildLinks[0].ArtifactId != expectedRelease) return false;
        }
        foreach (var index in new[] { 6, 7 })
        {
            var report = reports[index - 1];
            if (index == 7 && !hasActiveSqa && report.ResolutionVerificationExecutionId is null
                && report.State == ProblemReportState.Verifying
                && await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.EventType == "ClosureApproved", ct))
                continue;
            if (report?.ResolutionVerificationExecutionId is null) return false;
            if (!await db.ProblemReportLinks.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id
                    && x.ArtifactType == "TestExecution" && x.ArtifactId == report.ResolutionVerificationExecutionId
                    && x.Relationship == ProblemReportRelationshipPolicy.ResolutionVerification, ct)) return false;
            if (!await db.ProblemReportRevisions.AsNoTracking().AnyAsync(x => x.ProblemReportId == report.Id && x.EventType == "ResolutionVerified", ct)) return false;
            var candidate = await db.ProblemReportClosureCandidates.AsNoTracking().Where(x => x.ProblemReportId == report.Id)
                .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
            if (candidate is null || candidate.VerificationExecutionId != report.ResolutionVerificationExecutionId) return false;
            if (index == 6 && candidate.State != ProblemReportClosureCandidateState.Pending) return false;
            if (index == 7)
            {
                var closureIsFrozen = report.State == ProblemReportState.Closed
                    && candidate.State == ProblemReportClosureCandidateState.Approved
                    && !string.IsNullOrWhiteSpace(candidate.ClosurePackageHash)
                    && await db.ProblemReportRevisions.AnyAsync(x => x.ProblemReportId == report.Id && x.EventType == "ClosureApproved", ct);
                // If the SQA membership was deliberately ended, a new closure cannot be fabricated. A
                // pre-existing frozen closure remains valid historical evidence; an unclosed scenario stays
                // in work and is retried after an operator grants authority again.
                if (hasActiveSqa ? !closureIsFrozen : report.State != ProblemReportState.WaitingForSqaToClose && !closureIsFrozen)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The campaign that shipped Build 1.5, complete and closed.
    ///
    /// Only the in-work build had one, so opening the decision room on a released build answered "Release
    /// readiness is not configured" — which reads as a fault in the product rather than as what it was, a
    /// page with nothing to describe. A released build is the one case where the decision room has the whole
    /// story to tell: everything that was being tracked, and every approval that let it ship.
    ///
    /// Built by driving the same lifecycle a real campaign goes through — verification, an ordered review,
    /// each approval in turn, then release — rather than by writing the finished state into the tables. A
    /// closed campaign assembled by hand would show the right words above evidence that never happened, and
    /// the invariants that guard the real path would never have been asked.
    ///
    /// Nothing on the page can be acted on afterwards, and that needs no work here: a released campaign
    /// refuses every mutation in the domain, which is where it belongs rather than in the buttons.
    ///
    /// Dated to the week after the 1.5 software build was produced, so the story reads in the order it
    /// happened and no approval is signed after the release it authorized.
    /// </summary>
    private async Task<string?> EnsureReleasedCampaignAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct);
        var release = await db.Releases.SingleOrDefaultAsync(x => x.ProjectId == project.Id && x.Version == "1.5", ct);
        if (release is null) return "This Program has no released build.";
        if (await db.ReleaseCampaigns.AnyAsync(x => x.ReleaseId == release.Id, ct)) return "The released build already has its campaign.";
        var baseline = await db.CandidateBaselines.SingleOrDefaultAsync(x => x.ReleaseId == release.Id, ct);
        var build = await db.SoftwareBuilds.SingleOrDefaultAsync(x => x.ReleaseId == release.Id, ct);
        // Without both, there is nothing real to point the campaign at, and a campaign referring to nothing
        // would be worse than the empty page it replaces.
        if (baseline is null || build is null) return "The released build has no baseline or software build to describe.";

        var now = new DateTimeOffset(2024, 6, 17, 14, 0, 0, TimeSpan.Zero);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "FMS 1.5 Release Campaign", "release.manager", now);
        campaign.StartVerification("release.manager", now.AddHours(1));
        campaign.SelectVerificationBuild(build.Id, "release.manager", now.AddHours(2));
        campaign.RecordExecutionProgress("VerificationCompleted",
            "Every verification artifact required for the 1.5 configuration was executed and its determination recorded.",
            "test.engineer", now.AddDays(1));
        db.ReleaseCampaigns.Add(campaign);

        var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges)
            .Where(x => x.TargetReleaseId == release.Id).OrderBy(x => x.BaseNumber).ToListAsync(ct);
        var addressed = 0;
        foreach (var request in requests)
        {
            var dispositions = request.RequirementChanges
                .Select(change => new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Requirement, change.DisplayNumber,
                    $"Confirm the proposed {change.Kind} requirement revision is complete and correctly allocated."))
                .ToList();
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Traceability, request.DisplayNumber,
                "Update and review all upstream and downstream trace links affected by this change."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Verification, request.DisplayNumber,
                "Update test coverage and execute the required verification on the released 1.5 build."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Document, request.DisplayNumber,
                "Regenerate every controlled output affected by this change."));
            // All addressed: this is a build that shipped, and an outstanding item on it would say the
            // opposite of what the record shows.
            foreach (var item in dispositions)
                item.Disposition(ImpactDispositionState.Addressed,
                    "Completed and verified before the 1.5 release review opened.", "release.manager", now.AddDays(1).AddHours(1));
            db.ImpactDispositions.AddRange(dispositions);
            addressed += dispositions.Count;
        }

        var manifest = Hash($"FMS 1.5 release manifest {baseline.ContentHash} {build.BuildNumber}");
        campaign.BeginReleaseReview("release.manager",
            [("program.manager", "Olivia Chen"), ("cm.fms", "Daniel Reyes")], manifest, now.AddDays(2));
        campaign.Approve("program.manager", now.AddDays(3));
        campaign.Approve("cm.fms", now.AddDays(4));
        campaign.Release(build.Id, manifest, "release.manager", now.AddDays(5));
        await db.SaveChangesAsync(ct);
        return $"Recorded the closed 1.5 release campaign with {addressed} addressed impacts and two signed approvals.";
    }

    private async Task EnsureReleaseCampaignAsync(Guid programId, CancellationToken ct)
    {
        var project = await db.Projects.SingleAsync(x => x.ProgramId == programId, ct); var release = await db.Releases.SingleAsync(x => x.ProjectId == project.Id && x.Version == "1.6", ct);
        if (await db.ReleaseCampaigns.AnyAsync(x => x.ReleaseId == release.Id, ct)) return;
        var baseline = await db.CandidateBaselines.SingleAsync(x => x.ReleaseId == release.Id, ct); var now = new DateTimeOffset(2024, 11, 15, 14, 0, 0, TimeSpan.Zero);
        var campaign = new ReleaseCampaign(project.Id, release.Id, baseline.Id, "FMS 1.6 Release Campaign", "release.manager", now); campaign.StartVerification("release.manager", now.AddMinutes(1));
        db.ReleaseCampaigns.Add(campaign); var requests = await db.SystemChangeRequests.Include(x => x.RequirementChanges).Where(x => x.TargetReleaseId == release.Id).OrderBy(x => x.BaseNumber).ToListAsync(ct);
        foreach (var request in requests)
        {
            var dispositions = request.RequirementChanges.Select(change => new ChangeImpactDisposition(campaign.Id, request.Id, ImpactKind.Requirement, change.DisplayNumber, $"Confirm the proposed {change.Kind} requirement revision is complete and correctly allocated.")).ToList();
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Traceability, request.DisplayNumber, "Update and review all upstream and downstream trace links affected by this change."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Verification, request.DisplayNumber, "Update test coverage and execute the required verification on the selected 1.6 build."));
            dispositions.Add(new(campaign.Id, request.Id, ImpactKind.Document, request.DisplayNumber, "Regenerate every controlled output affected by this change."));
            if (request.State == ChangeRequestState.SelectedForBaseline) foreach (var item in dispositions) item.Disposition(ImpactDispositionState.Addressed, "Completed during approved change integration; final release verification remains governed by campaign gates.", "release.manager", now.AddDays(1));
            db.ImpactDispositions.AddRange(dispositions);
        }
        await db.SaveChangesAsync(ct);
    }

    private static SystemChangeRequest BuildHistoricalRequest(string number, ChangeRequestType type, RequirementLevel level, int count, int offset, Guid projectId, Guid releaseId, DateTimeOffset now, string label)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, $"Establish FMS {label} requirement group {number[^2..]}", "The product baseline requires controlled FMS behavior.", "Operational and assurance needs were analyzed and allocated.", "Introduce the approved requirement set with verification criteria.", type == ChangeRequestType.System ? "systems.author" : "software.author", now, type, softwareLevel: type == ChangeRequestType.Software ? level : null);
        for (var j = 1; j <= count; j++) { var index = offset + j; var prefix = level == RequirementLevel.System ? "SYSR" : level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var revision = index % 11 == 0 ? 2 : index % 5 == 0 ? 1 : 0;
            request.AddRequirementChange(request.AuthorId, $"{prefix}-{index:D6}", revision, level, RequirementChangeKind.Introduce, CurrentStatement(level, index), $"Allocated {Topics[(index - 1) % Topics.Length]} capability for the FMS 1.5 baseline.", "Test", now); }
        request.MarkAsLegacyHistoricalPackage(request.AuthorId, now.AddMinutes(1));
        request.SubmitForReview(request.AuthorId, [new("assurance.reviewer", "Development Assurance Reviewer")], now.AddHours(2)); request.ApproveActiveStage("assurance.reviewer", now.AddDays(1)); return request;
    }

    private static List<(TestProcedure, TestProcedureRevision, List<Guid>)> BuildProcedures(Guid projectId,
        Guid baselineId, List<Guid> requirements, int count, TestProcedureLevel level, string prefix, DateTimeOffset now)
    {
        var buckets = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList(); for (var i = 0; i < requirements.Count; i++) buckets[i % count].Add(requirements[i]);
        return buckets.Select((ids, i) => { var number = $"{prefix}-{i + 1:D6}"; var procedure = new TestProcedure(projectId, number, $"Verify {level} FMS behavior group {i + 1:D3}", "test.author", now, level);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify all linked FMS requirement revisions.", "Load released FMS 1.5 software and the approved navigation database.", "Initialize the applicable mode, stimulate the defined inputs, and record each observable output.", "Every observed output meets the linked requirement acceptance criteria.", TestProcedureState.Approved, "test.author", now, effectiveBaselineId: baselineId, parentKind: VerificationProcedureParentKind.Allocated); return (procedure, revision, ids); }).ToList();
    }

    private static List<SystemChangeRequest> BuildActive16Requests(Guid projectId, Guid releaseId, Dictionary<string, CurrentRequirement> current, DateTimeOffset now)
    {
        var result = new List<SystemChangeRequest>();
        for (var i = 1; i <= 8; i++)
        {
            // The number names the level, so it is derived from the same rule the application uses rather
            // than written out by hand. i <= 4 is HLR work and the rest is LLR, matching the requirement
            // changes each request goes on to carry.
            var system = i <= 2; var type = system ? ChangeRequestType.System : ChangeRequestType.Software;
            var packageLevel = system ? (RequirementLevel?)null : i <= 4 ? RequirementLevel.HighLevel : RequirementLevel.LowLevel;
            var number = $"{ChangeRequestNumbering.Prefix(type, packageLevel)}-{(system ? 30 + i : 75 + i - 2):D5}";
            var request = new SystemChangeRequest(number, 0, projectId, releaseId, i == 1 ? "Introduce oceanic round-robin waypoint sequencing" : $"FMS 1.6 change package {i}", "Operational feedback or a product improvement requires controlled change.", "The impact to requirements, traces, and verification has been assessed.", "Update the applicable FMS behavior and verification assets.", type == ChangeRequestType.System ? "systems.author" : "software.author", now.AddDays(i), type, softwareLevel: packageLevel);
            if (i == 1) request.AddRequirementChange(request.AuthorId, "SYSR-000151", 0, RequirementLevel.System, RequirementChangeKind.Introduce, "The FMS shall support configurable round-robin sequencing of eligible oceanic waypoints.", "New FMS 1.6 capability.", "Test", now);
            else { var level = system ? RequirementLevel.System : i <= 4 ? RequirementLevel.HighLevel : RequirementLevel.LowLevel; var prefix = level == RequirementLevel.System ? "SYSR" : level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var max = level == RequirementLevel.System ? 150 : level == RequirementLevel.HighLevel ? 400 : 700; var idx = ((i * 37) % max) + 1; var row = current[$"{prefix}-{idx:D6}"]; request.AddRequirementChange(request.AuthorId, $"{prefix}-{idx:D6}", row.Revision.Revision + 1, level, RequirementChangeKind.Modify, CurrentStatement(level, idx) + " The behavior shall include the approved FMS 1.6 refinement.", "Product improvement or corrective action.", "Test", now); }
            request.MarkAsLegacyHistoricalPackage(request.AuthorId, now.AddMinutes(1));
            if (i <= 2) { request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel")], now.AddDays(i).AddHours(1)); request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2)); }
            else if (i == 3) { request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel"), new("manager.reviewer", "Olivia Chen")], now.AddDays(i).AddHours(1)); request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2)); request.ApproveActiveStage("manager.reviewer", now.AddDays(i).AddHours(3)); }
            else if (i == 4) request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Maya Patel"), new("manager.reviewer", "Olivia Chen")], now.AddDays(i).AddHours(1));
            else if (i == 8) request.Defer(request.AuthorId, "Deferred from FMS 1.6 pending operational priority confirmation.", now.AddDays(i).AddHours(2));
            result.Add(request);
        }
        return result;
    }

    private async Task<FmsShowcaseSummary> SummarizeAsync(Guid programId, CancellationToken ct)
    {
        var projectId = await db.Projects.Where(x => x.ProgramId == programId).Select(x => x.Id).SingleAsync(ct); var release16 = await db.Releases.Where(x => x.ProjectId == projectId && x.Version == "1.6").Select(x => x.Id).SingleAsync(ct);
        var baselineId = await db.CandidateBaselines.Where(x => x.ProjectId == projectId && x.Name.Contains("1.5 Released")).Select(x => x.Id).SingleAsync(ct);
        return new(programId, projectId, baselineId, release16,
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.System, ct),
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.HighLevel, ct),
            await db.Requirements.CountAsync(x => x.ProjectId == projectId && x.Level == RequirementLevel.LowLevel, ct),
            await db.SystemChangeRequests.CountAsync(x => x.ProjectId == projectId && x.Type == ChangeRequestType.System && x.TargetReleaseId != release16, ct),
            await db.SystemChangeRequests.CountAsync(x => x.ProjectId == projectId && x.Type == ChangeRequestType.Software && x.TargetReleaseId != release16, ct),
            await db.RequirementTraces.CountAsync(x => x.ProjectId == projectId, ct), await db.TestProcedures.CountAsync(x => x.ProjectId == projectId, ct),
            await db.TestExecutions.CountAsync(x => x.ProjectId == projectId, ct), await db.ControlledDocuments.CountAsync(x => x.ProjectId == projectId, ct));
    }

    private static string CurrentStatement(RequirementLevel level, int index) => level switch { RequirementLevel.System => $"The FMS shall provide controlled {Topics[(index - 1) % Topics.Length]} capability {index:D3} throughout the applicable operational modes.", RequirementLevel.HighLevel => $"The FMS software shall compute and manage {Topics[(index - 1) % Topics.Length]} behavior H{index:D3} using validated inputs and deterministic state transitions.", _ => $"The FMS low-level component shall implement {Topics[(index - 1) % Topics.Length]} algorithm L{index:D3} with bounded execution and explicit status reporting." };
    private static string HistoricalStatement(RequirementLevel level, string baseNumber, int revision) => $"Historical revision {revision:D2} of {baseNumber} defined the earlier approved {level} FMS behavior.";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private sealed record CurrentRequirement(RequirementArtifact Artifact, RequirementRevision Revision);
}
