using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record FmsShowcaseSummary(Guid ProgramId, Guid ProjectId, Guid ReleasedBaselineId, Guid ActiveReleaseId,
    int SystemRequirements, int HighLevelRequirements, int LowLevelRequirements, int HistoricalScrs,
    int HistoricalSwcrs, int TraceLinks, int TestProcedures, int TestExecutions, int Documents);

public sealed class FmsShowcaseSeeder(AeroLinkDbContext db)
{
    public const string ProgramCode = "FMSLIVE";
    private static readonly string[] Topics = ["flight plan", "lateral navigation", "vertical navigation", "performance prediction", "navigation database", "guidance", "radio navigation", "position estimation", "fuel management", "crew interface", "departure procedures", "arrival procedures", "approach management", "airspace constraints", "route sequencing"];

    public async Task<FmsShowcaseSummary> EnsureSeededAsync(CancellationToken ct = default)
    {
        var existing = await db.Programs.AsNoTracking().SingleOrDefaultAsync(x => x.Code == ProgramCode, ct);
        if (existing is not null) { await EnsureReleaseCampaignAsync(existing.Id, ct); return await SummarizeAsync(existing.Id, ct); }

        var start = new DateTimeOffset(2024, 1, 8, 14, 0, 0, TimeSpan.Zero);
        var program = new ProgramRecord("Flight Management System Live Program", ProgramCode);
        var project = new ProjectRecord(program.Id, "FMS Product Development", "Flight Management System");
        var release15 = new SoftwareRelease(project.Id, "1.5", true); var release16 = new SoftwareRelease(project.Id, "1.6", false, release15.Id);
        db.AddRange(program, project, release15, release16); await db.SaveChangesAsync(ct);

        var historical = new List<SystemChangeRequest>();
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"SCR-{i:D8}", ChangeRequestType.System, RequirementLevel.System, 5, (i - 1) * 5, project.Id, release15.Id, start.AddDays(i), "system"));
        for (var i = 1; i <= 30; i++) historical.Add(BuildHistoricalRequest($"SWCR-{i:D8}", ChangeRequestType.Software, RequirementLevel.HighLevel, i <= 10 ? 14 : 13, (i - 1) * 13 + Math.Min(i - 1, 10), project.Id, release15.Id, start.AddDays(40 + i), "HLR"));
        for (var i = 1; i <= 45; i++) historical.Add(BuildHistoricalRequest($"SWCR-{i + 30:D8}", ChangeRequestType.Software, RequirementLevel.LowLevel, i <= 25 ? 16 : 15, (i - 1) * 15 + Math.Min(i - 1, 25), project.Id, release15.Id, start.AddDays(80 + i), "LLR"));
        db.SystemChangeRequests.AddRange(historical); await db.SaveChangesAsync(ct);

        var baseline15 = new CandidateBaseline("SWBL-00000015", 0, project.Id, release15.Id, null, "FMS 1.5 Released Product Baseline", "cm.fms", start.AddDays(150));
        foreach (var request in historical) baseline15.Select(request, "cm.fms", start.AddDays(150));
        baseline15.Freeze("cm.fms", start.AddDays(151)); db.CandidateBaselines.Add(baseline15); await db.SaveChangesAsync(ct);
        await new RequirementBaselineMaterializer(db).MaterializeAsync(baseline15.Id, "cm.fms", start.AddDays(152), ct);

        var currentRows = await (from member in db.BaselineRequirements.Where(x => x.BaselineId == baseline15.Id)
                                 join artifact in db.Requirements on member.ArtifactId equals artifact.Id
                                 join revision in db.RequirementRevisions on member.RevisionId equals revision.Id
                                 select new { artifact, revision }).ToListAsync(ct);
        foreach (var row in currentRows.Where(x => x.revision.Revision > 0))
            for (var rev = 0; rev < row.revision.Revision; rev++) db.RequirementRevisions.Add(new RequirementRevision(row.artifact.Id, rev,
                HistoricalStatement(row.artifact.Level, row.artifact.BaseNumber, rev), "Earlier approved wording retained for history.", row.revision.VerificationMethod,
                RequirementRevisionState.Superseded, row.revision.SourceScrId, baseline15.Id, start.AddDays(10 + rev)));
        await db.SaveChangesAsync(ct);

        var current = currentRows.ToDictionary(x => x.artifact.BaseNumber, x => new CurrentRequirement(x.artifact, x.revision));
        var systems = current.Values.Where(x => x.Artifact.Level == RequirementLevel.System).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var hlrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.HighLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        var llrs = current.Values.Where(x => x.Artifact.Level == RequirementLevel.LowLevel).OrderBy(x => x.Artifact.BaseNumber).ToList();
        db.RequirementTraces.AddRange(hlrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, systems[i % systems.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Allocated software behavior satisfies the parent system requirement.", start.AddDays(153))));
        db.RequirementTraces.AddRange(llrs.Select((x, i) => new RequirementTraceLink(project.Id, x.Revision.Id, hlrs[i % hlrs.Count].Revision.Id, RequirementTraceType.DerivedFrom, "Detailed behavior implements the parent high-level requirement.", start.AddDays(153))));
        await db.SaveChangesAsync(ct);

        var build15 = new SoftwareBuild(project.Id, release15.Id, baseline15.Id, "FMS-1.5.0-RELEASE", "Released operational FMS 1.5 software configuration.", "cm.fms", start.AddDays(160));
        db.SoftwareBuilds.Add(build15); await db.SaveChangesAsync(ct);
        var procedures = new List<(TestProcedure Procedure, TestProcedureRevision Revision, List<Guid> Requirements)>();
        procedures.AddRange(BuildProcedures(project.Id, systems.Select(x => x.Revision.Id).ToList(), 75, TestProcedureLevel.System, "SYSTP", start.AddDays(154)));
        procedures.AddRange(BuildProcedures(project.Id, hlrs.Select(x => x.Revision.Id).ToList(), 160, TestProcedureLevel.HighLevel, "HLRTP", start.AddDays(155)));
        procedures.AddRange(BuildProcedures(project.Id, llrs.Select(x => x.Revision.Id).ToList(), 280, TestProcedureLevel.LowLevel, "LLRTP", start.AddDays(156)));
        db.TestProcedures.AddRange(procedures.Select(x => x.Procedure)); db.TestProcedureRevisions.AddRange(procedures.Select(x => x.Revision));
        db.TestCoverage.AddRange(procedures.SelectMany(x => x.Requirements.Select(req => new TestRequirementCoverage(x.Revision.Id, req)))); await db.SaveChangesAsync(ct);
        var executionNumber = 0;
        foreach (var item in procedures)
        {
            executionNumber++; var executed = start.AddDays(157).AddMinutes(executionNumber);
            if (executionNumber % 103 == 0)
            {
                var fail = new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Fail, "test.engineer", "FMS integration rig / build 1.5", "Initial observation did not satisfy the expected result.", $"evidence/fms-1.5/fail-{executionNumber:D4}.json", executed, executed);
                db.TestExecutions.Add(fail); db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, fail.Id, TestOutcome.Pass, "test.engineer", "FMS integration rig / corrected configuration", "Retest successfully verified every linked requirement.", $"evidence/fms-1.5/retest-{executionNumber:D4}.json", executed.AddHours(2), executed.AddHours(2)));
            }
            else db.TestExecutions.Add(new TestExecution(project.Id, item.Revision.Id, build15.Id, null, TestOutcome.Pass, "test.engineer", "FMS integration rig / build 1.5", "Observed results satisfy the approved expected result and linked requirements.", $"evidence/fms-1.5/pass-{executionNumber:D4}.json", executed, executed));
        }
        await db.SaveChangesAsync(ct);

        var docSpecs = new[] {
            (ControlledDocumentType.Sysrd,"SYSRD-00000015","FMS System Requirements Document",150),
            (ControlledDocumentType.SwrdHighLevel,"HLRD-00000015","FMS High-Level Software Requirements Document",400),
            (ControlledDocumentType.SwrdLowLevel,"LLRD-00000015","FMS Low-Level Software Requirements Document",700),
            (ControlledDocumentType.SystemTestProcedures,"SYSTD-00000015","FMS System Test Procedures",75),
            (ControlledDocumentType.HighLevelTestProcedures,"HLRTD-00000015","FMS HLR Test Procedures",160),
            (ControlledDocumentType.LowLevelTestProcedures,"LLRTD-00000015","FMS LLR Test Procedures",280) };
        foreach (var spec in docSpecs) db.ControlledDocuments.Add(new ControlledDocument(project.Id, release15.Id, baseline15.Id, spec.Item1, spec.Item2, spec.Item3, 0, Hash($"{baseline15.RequirementsHash}|{spec.Item1}|{spec.Item4}"), spec.Item4, start.AddDays(159)));

        var activeRequests = BuildActive16Requests(project.Id, release16.Id, current, start.AddDays(300));
        db.SystemChangeRequests.AddRange(activeRequests); await db.SaveChangesAsync(ct);
        var baseline16 = new CandidateBaseline("SWBL-00000016", 0, project.Id, release16.Id, baseline15.Id, "FMS 1.6 Working Candidate", "cm.fms", start.AddDays(310));
        foreach (var request in activeRequests.Where(x => x.State == ScrState.Approved).Take(2)) baseline16.Select(request, "cm.fms", start.AddDays(311));
        db.CandidateBaselines.Add(baseline16); await db.SaveChangesAsync(ct);
        await EnsureReleaseCampaignAsync(program.Id, ct);
        return await SummarizeAsync(program.Id, ct);
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
            if (request.State == ScrState.SelectedForBaseline) foreach (var item in dispositions) item.Disposition(ImpactDispositionState.Addressed, "Completed during approved change integration; final release verification remains governed by campaign gates.", "release.manager", now.AddDays(1));
            db.ImpactDispositions.AddRange(dispositions);
        }
        await db.SaveChangesAsync(ct);
    }

    private static SystemChangeRequest BuildHistoricalRequest(string number, ChangeRequestType type, RequirementLevel level, int count, int offset, Guid projectId, Guid releaseId, DateTimeOffset now, string label)
    {
        var request = new SystemChangeRequest(number, 0, projectId, releaseId, $"Establish FMS {label} requirement group {number[^2..]}", "The product baseline requires controlled FMS behavior.", "Operational and assurance needs were analyzed and allocated.", "Introduce the approved requirement set with verification criteria.", type == ChangeRequestType.System ? "systems.author" : "software.author", now, type);
        for (var j = 1; j <= count; j++) { var index = offset + j; var prefix = level == RequirementLevel.System ? "SYSR" : level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var revision = index % 11 == 0 ? 2 : index % 5 == 0 ? 1 : 0;
            request.AddRequirementChange(request.AuthorId, $"{prefix}-{index:D8}", revision, level, RequirementChangeKind.Introduce, CurrentStatement(level, index), $"Allocated {Topics[(index - 1) % Topics.Length]} capability for the FMS 1.5 baseline.", "Test", now); }
        request.SubmitForReview(request.AuthorId, [new("assurance.reviewer", "Development Assurance Reviewer")], now.AddHours(2)); request.ApproveActiveStage("assurance.reviewer", now.AddDays(1)); return request;
    }

    private static List<(TestProcedure, TestProcedureRevision, List<Guid>)> BuildProcedures(Guid projectId, List<Guid> requirements, int count, TestProcedureLevel level, string prefix, DateTimeOffset now)
    {
        var buckets = Enumerable.Range(0, count).Select(_ => new List<Guid>()).ToList(); for (var i = 0; i < requirements.Count; i++) buckets[i % count].Add(requirements[i]);
        return buckets.Select((ids, i) => { var number = $"{prefix}-{i + 1:D8}"; var procedure = new TestProcedure(projectId, number, $"Verify {level} FMS behavior group {i + 1:D3}", "test.author", now, level);
            var revision = new TestProcedureRevision(procedure.Id, 0, "Verify all linked FMS requirement revisions.", "Load released FMS 1.5 software and the approved navigation database.", "Initialize the applicable mode, stimulate the defined inputs, and record each observable output.", "Every observed output meets the linked requirement acceptance criteria.", TestProcedureState.Approved, "test.author", now); return (procedure, revision, ids); }).ToList();
    }

    private static List<SystemChangeRequest> BuildActive16Requests(Guid projectId, Guid releaseId, Dictionary<string, CurrentRequirement> current, DateTimeOffset now)
    {
        var result = new List<SystemChangeRequest>();
        for (var i = 1; i <= 8; i++)
        {
            var system = i <= 2; var type = system ? ChangeRequestType.System : ChangeRequestType.Software; var number = system ? $"SCR-{30 + i:D8}" : $"SWCR-{75 + i - 2:D8}";
            var request = new SystemChangeRequest(number, 0, projectId, releaseId, i == 1 ? "Introduce oceanic round-robin waypoint sequencing" : $"FMS 1.6 change package {i}", "Operational feedback or a product improvement requires controlled change.", "The impact to requirements, traces, and verification has been assessed.", "Update the applicable FMS behavior and verification assets.", type == ChangeRequestType.System ? "systems.author" : "software.author", now.AddDays(i), type);
            if (i == 1) request.AddRequirementChange(request.AuthorId, "SYSR-00000151", 0, RequirementLevel.System, RequirementChangeKind.Introduce, "The FMS shall support configurable round-robin sequencing of eligible oceanic waypoints.", "New FMS 1.6 capability.", "Test", now);
            else { var level = i <= 4 ? RequirementLevel.HighLevel : RequirementLevel.LowLevel; var prefix = level == RequirementLevel.HighLevel ? "HLR" : "LLR"; var max = level == RequirementLevel.HighLevel ? 400 : 700; var idx = ((i * 37) % max) + 1; var row = current[$"{prefix}-{idx:D8}"]; request.AddRequirementChange(request.AuthorId, $"{prefix}-{idx:D8}", row.Revision.Revision + 1, level, RequirementChangeKind.Modify, CurrentStatement(level, idx) + " The behavior shall include the approved FMS 1.6 refinement.", "Product improvement or corrective action.", "Test", now); }
            if (i <= 2) { request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Engineering Lead")], now.AddDays(i).AddHours(1)); request.ApproveActiveStage("lead.reviewer", now.AddDays(i).AddHours(2)); }
            else if (i <= 4) request.SubmitForReview(request.AuthorId, [new("lead.reviewer", "Engineering Lead"), new("manager.reviewer", "Engineering Manager")], now.AddDays(i).AddHours(1));
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
