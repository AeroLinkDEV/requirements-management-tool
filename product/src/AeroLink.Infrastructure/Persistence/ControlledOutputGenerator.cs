using System.IO.Compression;
using System.Security;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record GeneratedOutput(byte[] Content, string ContentType, string FileName);
internal sealed record OutputRow(string Number, string Level, string Text, string Source);

public sealed class ControlledOutputGenerator(AeroLinkDbContext db, RichContentPublisher richContent,
    ILadderPolicy? policy = null, IProjectLadderPolicyResolver? policyResolver = null)
{
    private readonly ILadderPolicy fallbackPolicy = policy ?? LegacyLadderPolicy.Instance;
    public async Task<GeneratedOutput?> GenerateTraceabilityAsync(Guid baselineId,string format,CancellationToken ct)
    {
        var baseline=await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==baselineId,ct);if(baseline is null||baseline.RequirementsMaterializedAt is null)return null;
        var project=await db.Projects.AsNoTracking().SingleAsync(x=>x.Id==baseline.ProjectId,ct);
        var ladderPolicy = policyResolver is null ? fallbackPolicy : await policyResolver.ResolveAsync(project.Id, ct);
        var allowedLevels = ladderPolicy.OrderedLevels.ToArray();
        var program=await db.Programs.AsNoTracking().SingleAsync(x=>x.Id==project.ProgramId,ct);var release=await db.Releases.AsNoTracking().SingleAsync(x=>x.Id==baseline.ReleaseId,ct);
        var requirements=await(from member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==baselineId) join artifact in db.Requirements.AsNoTracking().Where(x=>allowedLevels.Contains(x.Level)) on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id orderby artifact.Level,artifact.BaseNumber select new{revision.Id,display=artifact.BaseNumber+"."+revision.Revision.ToString("D2"),level=artifact.Level,revision.Statement}).ToListAsync(ct);
        var ids=requirements.Select(x=>x.Id).ToList();var links=await db.RequirementTraces.AsNoTracking().Where(x=>ids.Contains(x.SourceRevisionId)||ids.Contains(x.TargetRevisionId)).ToListAsync(ct);var byId=requirements.ToDictionary(x=>x.Id);
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy)
            links = links.Where(link => byId.TryGetValue(link.SourceRevisionId, out var source)
                && byId.TryGetValue(link.TargetRevisionId, out var target)
                && IsConfiguredTrace(ladderPolicy, source.level, target.level, link.Type)).ToList();
        var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
        var effectiveProcedureRevisionIds = procedureEffectivity?.RevisionIds ?? [];
        var allowedProcedureLevels = ladderPolicy.Definitions.Where(x => x.Verification is not null).Select(x => x.Verification!.ProcedureLevel).ToArray();
        var coverage=await(from link in db.TestCoverage.AsNoTracking().Where(x=>ids.Contains(x.RequirementRevisionId)&&effectiveProcedureRevisionIds.Contains(x.ProcedureRevisionId)) join revision in db.TestProcedureRevisions.AsNoTracking() on link.ProcedureRevisionId equals revision.Id join procedure in db.TestProcedures.AsNoTracking().Where(x=>allowedProcedureLevels.Contains(x.Level)) on revision.ProcedureId equals procedure.Id select new{link.RequirementRevisionId,ProcedureRevisionId=revision.Id,display=procedure.BaseNumber+"."+revision.Revision.ToString("D2")}).ToListAsync(ct);
        var procedureTitles=await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,coverage.Select(x=>x.ProcedureRevisionId).Distinct().ToList(),ct);
        var records=requirements.Select(req=>{var parents=links.Where(x=>x.SourceRevisionId==req.Id&&byId.ContainsKey(x.TargetRevisionId)).Select(x=>byId[x.TargetRevisionId].display).ToList();var children=links.Where(x=>x.TargetRevisionId==req.Id&&byId.ContainsKey(x.SourceRevisionId)).Select(x=>byId[x.SourceRevisionId].display).ToList();var tests=coverage.Where(x=>x.RequirementRevisionId==req.Id).Select(x=>$"{x.display} - {procedureTitles[x.ProcedureRevisionId].Title}").ToList();var artifactNoun=req.level==RequirementLevel.System?"procedure":"case";return new PublicationRecord(req.display,req.level.ToString(),"Full lifecycle linkage",req.Statement,new[]{("Parent requirement revisions",parents.Count==0?"Top-level / none":string.Join("; ",parents)),("Child requirement revisions",children.Count==0?"Leaf-level / none":string.Join("; ",children)),($"Verification {artifactNoun} revisions",tests.Count==0?"Coverage gap - none recorded":string.Join("; ",tests))});}).ToList();
        var generatedAt=DateTimeOffset.UtcNow;var approvals=await ApprovalBasis(baselineId,release.Id,generatedAt,ct);var hash=baseline.RequirementsHash??baseline.ContentHash??new string('0',64);var status=release.IsReleased?"Approved and Released":"Controlled Draft";
        var createdBy=(await db.BaselineEvents.AsNoTracking().Where(x=>x.BaselineId==baseline.Id&&x.EventType=="CandidateBaselineCreated").ToListAsync(ct)).OrderBy(x=>x.OccurredAt).Select(x=>x.ActorId).FirstOrDefault()??"system";
        var publication=new ProfessionalPublication(project.SoftwareProduct,program.Name+" ("+program.Code+")",project.Name,"Lifecycle Traceability Report",$"{project.SoftwareProduct} Full Traceability Evidence",$"Readable upward, downward, change-authority, and verification linkage for baseline {baseline.DisplayNumber}","TRACE-"+release.Version.Replace(".",""),"00",status,release.Version,baseline.DisplayNumber,createdBy,generatedAt,hash,new[]{("Requirements",records.Count.ToString("N0")),("Trace links",links.Count.ToString("N0")),("Verification links",coverage.Count.ToString("N0")),("Requirement manifest hash",hash)},approvals,new[]{("00",status,generatedAt.UtcDateTime.ToString("yyyy-MM-dd"),createdBy)},new[]{new PublicationSection("Complete Requirement Linkage","Each row identifies one exact baseline requirement revision and all of its upward, downward, and verification relationships.",records)});
        return ProfessionalPublicationRenderer.Render(publication,format,$"TRACEABILITY_{release.Version}_{baseline.DisplayNumber}");
    }

    public async Task<GeneratedOutput?> GenerateAsync(Guid documentId, string format, CancellationToken ct)
    {
        var document = await db.ControlledDocuments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == documentId, ct); if (document is null) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == document.ProjectId, ct); var program = await db.Programs.AsNoTracking().SingleAsync(x => x.Id == project.ProgramId, ct); var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == document.ReleaseId, ct); var baseline = await db.CandidateBaselines.AsNoTracking().SingleAsync(x => x.Id == document.BaselineId, ct);
        var ladderPolicy = policyResolver is null
            ? fallbackPolicy
            : await policyResolver.ResolveAsync(document.ProjectId, ct);
        // A retained document is historical evidence even when its level was removed from the current
        // ladder. Interpret its stored enum with the characterized catalogue for regeneration; current
        // policy still governs creation/listing, while a download must not turn into an "unknown" record.
        var interpretationPolicy = ladderPolicy;
        var approvalProcedureLevel = ProcedureLevelFor(document.Type, interpretationPolicy);
        var requirementLevel = RequirementLevelFor(document.Type, interpretationPolicy);
        if (requirementLevel is null && approvalProcedureLevel is null)
        {
            interpretationPolicy = LegacyLadderPolicy.Instance;
            approvalProcedureLevel = ProcedureLevelFor(document.Type, interpretationPolicy);
            requirementLevel = RequirementLevelFor(document.Type, interpretationPolicy);
        }
        if (requirementLevel is null && approvalProcedureLevel is null)
            throw new DomainException($"Unknown controlled document type: {document.Type}.");
        var procedureSnapshot = approvalProcedureLevel is null
            ? null
            : await ControlledProcedureDocumentSnapshotProjection.ForDocumentAsync(db, document.BaselineId,
                approvalProcedureLevel.Value, document.GeneratedAt, ct);
        var isCaseDocument = document.Type is ControlledDocumentType.HighLevelTestCases or ControlledDocumentType.LowLevelTestCases;
        var records = requirementLevel is not null
            ? await RequirementPublicationRows(document.BaselineId, requirementLevel.Value, ct)
            : await ProcedurePublicationRows(procedureSnapshot!, approvalProcedureLevel!.Value, isCaseDocument, ct);
        var isProcedureDocument = approvalProcedureLevel is not null;
        var verificationNoun = approvalProcedureLevel == TestProcedureLevel.System || !isCaseDocument ? "procedure" : "case";
        // A document reports the procedure-manifest state that existed when that document was generated, not
        // the baseline's current state. The baseline manifest is the configuration hash; the document's
        // content basis is rendered separately into the publication front matter.
        var testProcedureManifestHashAtGeneration = baseline.TestProceduresMaterializedAt is not null
            && baseline.TestProceduresMaterializedAt.Value <= document.GeneratedAt
                ? baseline.TestProceduresHash ?? "Exact verification artifact manifest hash unavailable"
                : "Not materialized when this document was generated";
        var approvals = await ApprovalBasis(document.BaselineId, document.ReleaseId, document.GeneratedAt, ct,
            approvalProcedureLevel, procedureSnapshot); var createdBy = (await db.BaselineEvents.AsNoTracking().Where(x => x.BaselineId == baseline.Id && x.EventType == "CandidateBaselineCreated").ToListAsync(ct)).OrderBy(x => x.OccurredAt).Select(x => x.ActorId).FirstOrDefault() ?? "system";
        var releasedWhenGenerated = release.IsReleased && (release.ReleasedAt is null || release.ReleasedAt <= document.GeneratedAt);
        var status = releasedWhenGenerated ? "Approved and Released" : "Controlled Draft"; var type = DocumentTypeName(document.Type);

        // The layout that produced this document, if one was recorded against it. Resolved by the exact
        // template revision stored on the record rather than by whatever is current, so a document
        // regenerated after the template was revised still comes out as the document that was approved.
        var templateRevision = document.TemplateRevisionId is null
            ? null
            : await db.DocumentTemplateRevisions.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == document.TemplateRevisionId, ct);
        var templateName = templateRevision is null
            ? null
            : await db.DocumentTemplates.AsNoTracking().Where(x => x.Id == templateRevision.TemplateId)
                .Select(x => x.TemplateNumber + " " + x.Title).SingleOrDefaultAsync(ct);
        var layout = PublicationLayout.TryRead(templateRevision?.BodyJson);

        var isRequirementDocument = requirementLevel is not null;
        var level = requirementLevel;

        List<PublicationSection> sections;
        string title = document.Title;
        string subtitle = $"Authoritative {type.ToLowerInvariant()} for {project.SoftwareProduct}";
        if (layout is null)
        {
            sections = [new("Controlled Records", $"This section contains {records.Count:N0} exact, revision-controlled records rendered from baseline {baseline.DisplayNumber}.", records)];
            if (isRequirementDocument)
                sections.Add(new("Annex A - Upward Requirement Traceability", "This annex identifies the exact parent requirement revision(s) for every published requirement. Top-level System requirements are explicitly identified.", await TraceAnnexRows(document.BaselineId, level!.Value, document.GeneratedAt, interpretationPolicy, ct)));
        }
        else
        {
            var values = new Dictionary<string, string>
            {
                ["product"] = project.SoftwareProduct, ["project"] = project.Name,
                ["program"] = program.Name, ["release"] = release.Version,
                ["baseline"] = baseline.DisplayNumber, ["documentType"] = type,
                ["documentTitle"] = document.Title, ["documentNumber"] = document.DocumentNumber,
                ["recordCount"] = records.Count.ToString("N0"),
            };
            title = PublicationLayout.Fill(layout.TitlePattern, values);
            if (title.Length == 0) title = document.Title;
            subtitle = PublicationLayout.Fill(layout.SubtitlePattern, values);

            sections = [];
            foreach (var section in layout.Sections)
            {
                var heading = PublicationLayout.Fill(section.Heading, values);
                var introduction = PublicationLayout.Fill(section.Introduction, values);
                sections.Add(section.Content switch
                {
                    PublicationSectionContent.ControlledRecords => new(heading, introduction, records),
                    PublicationSectionContent.UpwardTraceAnnex when isRequirementDocument =>
                        new(heading, introduction, await TraceAnnexRows(document.BaselineId, level!.Value, document.GeneratedAt, interpretationPolicy, ct)),
                    PublicationSectionContent.VerificationAnnex when isRequirementDocument =>
                        new(heading, introduction, await VerificationAnnexRows(document.BaselineId, level!.Value, ct)),
                    // A trace or verification annex has no meaning in a procedure document. The heading is
                    // still rendered, because the programme's layout said it belongs there; what would be
                    // wrong is a heading in a controlled document with nothing under it and no explanation.
                    PublicationSectionContent.UpwardTraceAnnex or PublicationSectionContent.VerificationAnnex =>
                        new(heading,
                            introduction.Length > 0 ? introduction : "This annex applies to requirement documents and is not applicable to this document type.",
                            []),
                    _ => new(heading, introduction, []),
                });
            }
        }

        var publication = new ProfessionalPublication(project.SoftwareProduct, program.Name + " (" + program.Code + ")", project.Name, type, title,
            subtitle, document.DocumentNumber, document.Revision.ToString("D2"), status, release.Version, baseline.DisplayNumber, createdBy, document.GeneratedAt, document.ContentHash,
            [("Controlled records", records.Count.ToString("N0")), ("Baseline content hash", baseline.ContentHash ?? "Not frozen"), ("Requirement manifest hash", baseline.RequirementsHash ?? "Not materialized"),
             ($"Test {verificationNoun} manifest hash", testProcedureManifestHashAtGeneration),
             ($"Test {verificationNoun} configuration basis", procedureSnapshot is null
                 ? "Not applicable"
                 : procedureSnapshot.IsExactManifest
                     ? "Exact immutable verification artifact manifest"
                     : "Legacy generation-time compatibility snapshot — exact historical manifest was not recorded"),
             // Named in the front matter so a reader can tell which layout produced what they are holding.
             ("Document template", templateRevision is null ? "Built-in layout" : $"{templateName} revision {templateRevision.Revision} (approved {templateRevision.ApprovedAt.UtcDateTime:yyyy-MM-dd} by {templateRevision.ApprovedBy}, manifest {templateRevision.ManifestHash[..Math.Min(12, templateRevision.ManifestHash.Length)]})"),
             ("Approval basis", isProcedureDocument
                 ? $"Named approvers and snapshot references from the exact approved test change requests that authorized the included verification {verificationNoun}s; upstream requirement-change authority is labelled separately; completed release approvals remain separate release authority"
                 : "Named approvers from exact approved change requests and completed release approvals recorded by generation time")], approvals,
            new[] { (document.Revision.ToString("D2"), status, document.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd"), createdBy) }, sections);
        return ProfessionalPublicationRenderer.Render(publication, format, $"{document.DocumentNumber}.{document.Revision:D2}_{release.Version}");
    }

    private static RequirementLevel? RequirementLevelFor(ControlledDocumentType type, ILadderPolicy ladderPolicy) =>
        ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).RequirementsDocumentType == type)
            .Select(level => (RequirementLevel?)level)
            .SingleOrDefault();

    private static TestProcedureLevel? ProcedureLevelFor(ControlledDocumentType type, ILadderPolicy ladderPolicy) =>
        ladderPolicy.OrderedLevels
            .Where(level => ladderPolicy.Definition(level).Verification?.DocumentType == type)
            .Select(level => ladderPolicy.Definition(level).Verification!.ProcedureLevel)
            .Cast<TestProcedureLevel?>()
            .SingleOrDefault()
            ?? type switch
            {
                ControlledDocumentType.SystemTestProcedures => TestProcedureLevel.System,
                ControlledDocumentType.HighLevelTestProcedures or ControlledDocumentType.HighLevelTestCases => TestProcedureLevel.HighLevel,
                ControlledDocumentType.LowLevelTestProcedures or ControlledDocumentType.LowLevelTestCases => TestProcedureLevel.LowLevel,
                _ => null,
            };

    private async Task<List<PublicationRecord>> RequirementPublicationRows(Guid baselineId, RequirementLevel level, CancellationToken ct)
    {
        var rows = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId) join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == level) on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id orderby artifact.BaseNumber select new { RevisionId = revision.Id, artifact.BaseNumber, revision.Revision, revision.Statement, revision.Rationale, revision.VerificationMethod, Scr = scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision }).ToListAsync(ct);

        // The tables, figures, and symbols an author wrote belong in the document that carries the
        // requirement. Publishing only the plain statement would put a requirement in front of an approver
        // in a form its author never wrote, and a document that disagrees with the record it came from is
        // worse than no document.
        var revisionIds = rows.Select(x => x.RevisionId).ToList();
        var authored = await db.RequirementRevisionProfiles.AsNoTracking()
            .Where(x => revisionIds.Contains(x.RevisionId))
            .ToDictionaryAsync(x => x.RevisionId, x => x.RichText, ct);
        var images = await richContent.ResolveImagesAsync(authored.Values, ct);

        return rows.Select(x => new PublicationRecord(x.BaseNumber + "." + x.Revision.ToString("D2"), level.ToString(), "", x.Statement,
            new[] { ("Rationale", x.Rationale), ("Verification method", x.VerificationMethod), ("Source change request", x.Scr) },
            Supplementary(x.RevisionId, x.Statement))).ToList();

        // Supporting content defaults to the statement itself when an author wrote none, and printing the
        // statement twice reads as a defect in the document rather than as completeness.
        string Supplementary(Guid revisionId, string statement)
        {
            if (!authored.TryGetValue(revisionId, out var content)) return "";
            var adds = AeroLink.Domain.Content.RichContent.HasStructure(content)
                || AeroLink.Domain.Content.RichContent.ToPlainText(content) != statement;
            return adds ? RichContentPublisher.ForPublication(content, images) : "";
        }
    }
    private async Task<List<PublicationRecord>> TraceAnnexRows(Guid baselineId, RequirementLevel level,
        DateTimeOffset generatedAt, ILadderPolicy ladderPolicy, CancellationToken ct)
    {
        var sources=await(from member in db.BaselineRequirements.AsNoTracking().Where(x=>x.BaselineId==baselineId) join artifact in db.Requirements.AsNoTracking().Where(x=>x.Level==level) on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id orderby artifact.BaseNumber select new{revision.Id,display=artifact.BaseNumber+"."+revision.Revision.ToString("D2")}).ToListAsync(ct);
        var sourceIds=sources.Select(x=>x.Id).ToList();var links=(await db.RequirementTraces.AsNoTracking().Where(x=>sourceIds.Contains(x.SourceRevisionId)).ToListAsync(ct)).Where(x=>x.CreatedAt<=generatedAt).ToList();var targetIds=links.Select(x=>x.TargetRevisionId).Distinct().ToList();
        var targets=await(from revision in db.RequirementRevisions.AsNoTracking().Where(x=>targetIds.Contains(x.Id)) join artifact in db.Requirements.AsNoTracking() on revision.ArtifactId equals artifact.Id select new{revision.Id,display=artifact.BaseNumber+"."+revision.Revision.ToString("D2"),level=artifact.Level}).ToDictionaryAsync(x=>x.Id,ct);
        if (ladderPolicy is not ILegacyLadderCompatibilityPolicy)
            links = links.Where(link => targets.TryGetValue(link.TargetRevisionId, out var target)
                && IsConfiguredTrace(ladderPolicy, level, target.level, link.Type)).ToList();
        return sources.Select(source=>{var parents=links.Where(x=>x.SourceRevisionId==source.Id).Select(x=>targets.TryGetValue(x.TargetRevisionId,out var target)?$"{target.display} ({target.level}, {x.Type})":x.TargetRevisionId.ToString()).ToList();return new PublicationRecord(source.display,level.ToString(),"Parent trace",parents.Count==0?(level==RequirementLevel.System?"Top-level System requirement - no upward requirement parent applies.":"No parent trace recorded."):string.Join("; ",parents),new[]{("Parent count",parents.Count.ToString())});}).ToList();
    }

    private static bool IsConfiguredTrace(ILadderPolicy policy, RequirementLevel source, RequirementLevel target,
        RequirementTraceType type)
    {
        try { RequirementTracePolicy.Validate(policy, source, target, type); return true; }
        catch (DomainException) { return false; }
    }
    /// <summary>
    /// Verification coverage for every published requirement, for programmes whose standard puts that annex
    /// in the requirement document rather than in a separate report.
    /// </summary>
    private async Task<List<PublicationRecord>> VerificationAnnexRows(Guid baselineId, RequirementLevel level, CancellationToken ct)
    {
        var sources = await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
                             join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == level) on member.ArtifactId equals artifact.Id
                             join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
                             orderby artifact.BaseNumber
                             select new { revision.Id, display = artifact.BaseNumber + "." + revision.Revision.ToString("D2"), revision.VerificationMethod }).ToListAsync(ct);
        var ids = sources.Select(x => x.Id).ToList();
        var procedureEffectivity = await TestProcedureEffectivity.ForBaselineAsync(db, baselineId, ct);
        var effectiveProcedureRevisionIds = procedureEffectivity?.RevisionIds ?? [];
        var coverage = await (from link in db.TestCoverage.AsNoTracking().Where(x => ids.Contains(x.RequirementRevisionId)
                                  && effectiveProcedureRevisionIds.Contains(x.ProcedureRevisionId))
                              join revision in db.TestProcedureRevisions.AsNoTracking() on link.ProcedureRevisionId equals revision.Id
                              join procedure in db.TestProcedures.AsNoTracking() on revision.ProcedureId equals procedure.Id
                              select new { link.RequirementRevisionId, link.IsSuspect, ProcedureRevisionId = revision.Id, display = procedure.BaseNumber + "." + revision.Revision.ToString("D2") }).ToListAsync(ct);
        var procedureTitles = await TestProcedureRevisionTitleProjection.ForRevisionsAsync(db,
            coverage.Select(x => x.ProcedureRevisionId).Distinct().ToList(), ct);
        return sources.Select(source =>
        {
            var covering = coverage.Where(x => x.RequirementRevisionId == source.Id).ToList();
            var artifactNoun = level == RequirementLevel.System ? "procedure" : "case";
            // A gap is stated as a gap. A blank cell where coverage should be reads as an oversight in the
            // document rather than as a fact about the product.
            var body = covering.Count == 0
                ? $"Coverage gap - no approved verification {artifactNoun} covers this requirement revision."
                : string.Join("; ", covering.Select(x => $"{x.display} - {procedureTitles[x.ProcedureRevisionId].Title}{(x.IsSuspect ? " (suspect)" : "")}"));
            return new PublicationRecord(source.display, level.ToString(), "Verification coverage", body,
                new[] { ("Verification method", source.VerificationMethod), ($"Covering {artifactNoun} revisions", covering.Count.ToString()), ("Suspect links", covering.Count(x => x.IsSuspect).ToString()) });
        }).ToList();
    }

    private async Task<List<PublicationRecord>> ProcedurePublicationRows(
        ControlledProcedureDocumentSnapshot snapshot, TestProcedureLevel level, bool isCaseDocument, CancellationToken ct)
    {
        var rows = snapshot.Rows;
        // #420: the exact TCR that authorized each procedure revision is the controlled provenance for that
        // revision (DEC-103 removed separate procedure-level approval). Legacy revisions with no source TCR
        // are stated truthfully as legacy/unattributed rather than assigned a fabricated package.
        var tcrIds = rows.Where(x => x.SourceTestChangeRequestId is not null)
            .Select(x => x.SourceTestChangeRequestId!.Value).Distinct().ToList();
        var tcrDisplay = await db.TestChangeReviews.AsNoTracking()
            .Where(x => tcrIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayNumber, ct);
        var title = level == TestProcedureLevel.System ? "System Test Procedure"
            : isCaseDocument
                ? level == TestProcedureLevel.HighLevel ? "High-Level Software Test Case" : "Low-Level Software Test Case"
                : level == TestProcedureLevel.HighLevel ? "High-Level Software Test Procedure" : "Low-Level Software Test Procedure";
        var stepsLabel = isCaseDocument && level != TestProcedureLevel.System ? "Case steps" : "Procedure steps";
        return rows.OrderBy(x => x.BaseNumber)
            .Select(x => new PublicationRecord(x.BaseNumber + "." + x.Revision.ToString("D2"), title, x.Title, x.Objective, new[] { ("State", x.State.ToString()), ("Author / owner", x.AuthorId), ("Preconditions", x.Preconditions), (stepsLabel, x.Steps), ("Expected result", x.ExpectedResult), ("Source test change request", x.SourceTestChangeRequestId is null ? "Legacy / unattributed" : tcrDisplay.GetValueOrDefault(x.SourceTestChangeRequestId.Value, "Unknown TCR")) })).ToList();
    }
    private async Task<List<PublicationApproval>> ApprovalBasis(Guid baselineId, Guid releaseId,
        DateTimeOffset generatedAt, CancellationToken ct, TestProcedureLevel? procedureLevel = null,
        ControlledProcedureDocumentSnapshot? procedureSnapshot = null)
    {
        var scrIds = await db.BaselineSelections.AsNoTracking().Where(x => x.BaselineId == baselineId).Select(x => x.ChangeRequestId).ToListAsync(ct);
        var cycles = (await db.ReviewCycles.AsNoTracking().Include(x => x.Steps).Where(x => x.ChangeRequestId != null && scrIds.Contains(x.ChangeRequestId.Value) && x.State == ReviewCycleState.Approved).ToListAsync(ct)).Where(x => x.CompletedAt <= generatedAt).ToList();
        var scrRole = procedureLevel is not null ? "Upstream Change Authority" : "Change Authority";
        var approvals = cycles.SelectMany(x => x.Steps.Where(s => s.State == ApprovalStepState.Approved && s.DecidedAt <= generatedAt).Select(s => new PublicationApproval(scrRole, s.ApproverName, s.ApproverId, "Approved", s.DecidedAt))).ToList();
        if (procedureLevel is not null && procedureSnapshot is not null)
        {
            // The carried revision is the authority link. A successor baseline may inherit a revision whose
            // original TCR is not selected again, while a selected retire-only or other-discipline TCR does
            // not authorize a record in this document. Derive exactly from this document snapshot.
            var tcrIds = procedureSnapshot.Rows.Where(x => x.SourceTestChangeRequestId is not null)
                .Select(x => x.SourceTestChangeRequestId!.Value).Distinct().ToList();
            var tcrDisplay = await db.TestChangeReviews.AsNoTracking()
                .Where(x => tcrIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.DisplayNumber, ct);
            var tcrCycles = (await db.ReviewCycles.AsNoTracking().Include(x => x.Steps)
                    .Where(x => x.TestChangeReviewId != null && tcrIds.Contains(x.TestChangeReviewId.Value)
                                && x.State == ReviewCycleState.Approved).ToListAsync(ct))
                .Where(x => x.CompletedAt <= generatedAt).ToList();
            foreach (var cycle in tcrCycles)
            {
                var tcrNumber = tcrDisplay.GetValueOrDefault(cycle.TestChangeReviewId!.Value, "Unknown TCR");
                var snapshotReference = string.IsNullOrWhiteSpace(cycle.SnapshotHash)
                    ? "snapshot unavailable"
                    : $"snapshot {cycle.SnapshotHash[..Math.Min(12, cycle.SnapshotHash.Length)]}";
                foreach (var step in cycle.Steps
                             .Where(s => s.State == ApprovalStepState.Approved && s.DecidedAt <= generatedAt))
                {
                    var stage = !string.IsNullOrWhiteSpace(step.StageName)
                        ? step.StageName
                        : !string.IsNullOrWhiteSpace(step.Authority)
                            ? step.Authority
                            : $"Stage {step.Position + 1}";
                    var signedAt = step.DecidedAt!.Value.UtcDateTime.ToString("HH:mm 'UTC'");
                    approvals.Add(new PublicationApproval(
                        $"Test Change Authority · {tcrNumber} · cycle {cycle.Sequence} · {stage}",
                        step.ApproverName, step.ApproverId,
                        $"Approved · signed {signedAt} · {snapshotReference}", step.DecidedAt));
                }
            }
        }
        var campaigns = await db.ReleaseCampaigns.AsNoTracking().Include(x => x.Approvals).Where(x => x.ReleaseId == releaseId).ToListAsync(ct);
        approvals.AddRange(campaigns.SelectMany(x => x.Approvals.Where(a => a.State == AeroLink.Domain.Releases.ReleaseApprovalState.Approved && a.ApprovedAt <= generatedAt).Select(a => new PublicationApproval("Release Authority", a.ApproverName, a.ApproverId, "Approved", a.ApprovedAt))));
        return approvals.GroupBy(x => new { x.Role, x.UserId }).Select(x => x.OrderByDescending(a => a.DecidedAt).First()).OrderBy(x => x.Role).ThenBy(x => x.Name).ToList();
    }
    private static string DocumentTypeName(ControlledDocumentType type) => type switch { ControlledDocumentType.Sysrd => "System Requirements Document", ControlledDocumentType.SwrdHighLevel => "High-Level Software Requirements Document", ControlledDocumentType.SwrdLowLevel => "Low-Level Software Requirements Document", ControlledDocumentType.SystemTestProcedures => "System Test Procedure Document", ControlledDocumentType.HighLevelTestProcedures => "High-Level Test Procedure Document", ControlledDocumentType.HighLevelTestCases => "High-Level Test Case Document", ControlledDocumentType.LowLevelTestProcedures => "Low-Level Test Procedure Document", ControlledDocumentType.LowLevelTestCases => "Low-Level Test Case Document", _ => throw new DomainException($"Unknown controlled document type: {type}.") };

    private async Task<List<OutputRow>> RequirementRows(Guid baselineId, RequirementLevel level, CancellationToken ct) => await (from member in db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId)
        join artifact in db.Requirements.AsNoTracking().Where(x => x.Level == level) on member.ArtifactId equals artifact.Id join revision in db.RequirementRevisions.AsNoTracking() on member.RevisionId equals revision.Id
        join scr in db.SystemChangeRequests.AsNoTracking() on revision.SourceChangeRequestId equals scr.Id orderby artifact.BaseNumber select new OutputRow(artifact.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, level.ToString(), revision.Statement, scr.BaseNumber + "." + (scr.Revision < 10 ? "0" : "") + scr.Revision)).ToListAsync(ct);

    private async Task<List<OutputRow>> ProcedureRows(Guid projectId, TestProcedureLevel level, CancellationToken ct) => await (from procedure in db.TestProcedures.AsNoTracking().Where(x => x.ProjectId == projectId && x.Level == level)
        join revision in db.TestProcedureRevisions.AsNoTracking() on procedure.Id equals revision.ProcedureId orderby procedure.BaseNumber select new OutputRow(procedure.BaseNumber + "." + (revision.Revision < 10 ? "0" : "") + revision.Revision, level.ToString(), revision.Objective + " Expected result: " + revision.ExpectedResult, revision.AuthorId)).ToListAsync(ct);

    private static byte[] BuildDocx(string title, string product, IEnumerable<(string Label, string Value)> metadata, IReadOnlyList<OutputRow> rows, string hash)
    {
        using var output = new MemoryStream(); using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Entry(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/><Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/><Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/></Types>");
            Entry(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Entry(zip, "word/_rels/document.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/></Relationships>");
            Entry(zip, "word/styles.xml", Styles()); Entry(zip, "word/header1.xml", Header(product)); Entry(zip, "word/footer1.xml", Footer(hash));
            var body = new StringBuilder(); body.Append(P(title, "Title")).Append(P("CONTROLLED LIFECYCLE OUTPUT", "Subtitle")); foreach (var item in metadata) body.Append(P(item.Label.ToUpperInvariant() + ": " + item.Value, "Meta")); body.Append(P("Controlled Records", "Heading1"));
            foreach (var row in rows) body.Append(P(row.Number + "  |  " + row.Level, "Heading2", true)).Append(P(row.Text, "Normal", true)).Append(P("Source: " + row.Source, "Source"));
            body.Append("<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId2\"/><w:footerReference w:type=\"default\" r:id=\"rId3\"/><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\"/></w:sectPr>");
            Entry(zip, "word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>" + body + "</w:body></w:document>");
        }
        return output.ToArray();
    }
    private static string Styles() => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        Style("Normal", "Normal", 22, "25364D", false, 0, 120, 300) + Style("Title", "Title", 46, "102A43", true, 0, 80, 280) + Style("Subtitle", "Subtitle", 20, "168578", true, 0, 220, 280) + Style("Meta", "Meta", 20, "526274", false, 0, 40, 280) + Style("Heading1", "Heading 1", 32, "2E74B5", true, 360, 200, 280) + Style("Heading2", "Heading 2", 26, "2E74B5", true, 280, 140, 280) + Style("Source", "Source", 18, "718096", false, 0, 120, 280) + "</w:styles>";
    private static string Style(string id, string name, int size, string color, bool bold, int before, int after, int line) => $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:before=\"{before}\" w:after=\"{after}\" w:line=\"{line}\" w:lineRule=\"auto\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/><w:color w:val=\"{color}\"/><w:sz w:val=\"{size}\"/>{(bold ? "<w:b/>" : "")}</w:rPr></w:style>";
    private static string P(string text, string style, bool keepNext = false) => $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/>{(keepNext ? "<w:keepNext/>" : "")}</w:pPr><w:r><w:t xml:space=\"preserve\">{SecurityElement.Escape(text)}</w:t></w:r></w:p>";
    private static string Header(string product) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"8\" w:color=\"168578\"/></w:pBdr></w:pPr><w:r><w:rPr><w:b/><w:color w:val=\"102A43\"/><w:sz w:val=\"18\"/></w:rPr><w:t>{SecurityElement.Escape(product)}  |  AEROLINK CONTROLLED OUTPUT</w:t></w:r></w:p></w:hdr>";
    private static string Footer(string hash) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:color w:val=\"718096\"/><w:sz w:val=\"14\"/></w:rPr><w:t>CONTROLLED - Manifest {hash[..12]} - Page </w:t></w:r><w:fldSimple w:instr=\"PAGE\"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p></w:ftr>";
    private static void Entry(ZipArchive zip, string name, string content) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content); }

    private static byte[] BuildPdf(string title, string product, IEnumerable<(string Label, string Value)> metadata, IReadOnlyList<OutputRow> rows)
    {
        var firstPage = new List<(string Text, int Size, bool Bold)> { (product + "  |  AEROLINK CONTROLLED OUTPUT", 9, true), ("", 4, false), (title, 20, true), ("CONTROLLED LIFECYCLE OUTPUT", 10, true), ("", 7, false) };
        firstPage.AddRange(metadata.Select(x => ($"{x.Label}: {x.Value}", 9, false))); firstPage.Add(("", 7, false)); firstPage.Add(("Controlled Records", 14, true));
        var pages = new List<List<(string Text, int Size, bool Bold)>> { firstPage };
        var usedHeight = firstPage.Sum(LineHeight);
        const int availableHeight = 688;
        foreach (var row in rows)
        {
            var block = new List<(string Text, int Size, bool Bold)> { ($"{row.Number} | {row.Level}", 10, true) };
            block.AddRange(Wrap(row.Text, 105).Select(x => (x, 8, false)));
            block.Add(($"Source: {row.Source}", 7, false)); block.Add(("", 6, false));
            var blockHeight = block.Sum(LineHeight);
            if (usedHeight + blockHeight > availableHeight)
            {
                var continuation = new List<(string Text, int Size, bool Bold)> { (product + "  |  AEROLINK CONTROLLED OUTPUT", 9, true), (title, 11, true), ("", 5, false) };
                pages.Add(continuation); usedHeight = continuation.Sum(LineHeight);
            }
            pages[^1].AddRange(block); usedHeight += blockHeight;
        }
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" }; var pageObjectNumbers = Enumerable.Range(0, pages.Count).Select(i => 5 + i * 2).ToList(); objects.Add($"<< /Type /Pages /Kids [{string.Join(" ", pageObjectNumbers.Select(x => x + " 0 R"))}] /Count {pages.Count} >>"); objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"); objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");
        for (var p = 0; p < pages.Count; p++) { var contentNumber = 6 + p * 2; objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentNumber} 0 R >>"); var stream = new StringBuilder("0.086 0.522 0.471 RG 1.5 w 54 760 m 558 760 l S\nBT\n"); var y = 744; foreach (var line in pages[p]) { stream.Append($"/{(line.Bold ? "F2" : "F1")} {line.Size} Tf 1 0 0 1 54 {y} Tm ({PdfEscape(line.Text)}) Tj\n"); y -= LineHeight(line); } stream.Append($"/F1 7 Tf 1 0 0 1 54 28 Tm (CONTROLLED - Page {p + 1} of {pages.Count}) Tj\nET"); var s = stream.ToString(); objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(s)} >>\nstream\n{s}\nendstream"); }
        using var output = new MemoryStream(); using var writer = new StreamWriter(output, Encoding.ASCII, 1024, true) { NewLine = "\n" }; writer.Write("%PDF-1.4\n"); writer.Flush(); var offsets = new List<long> { 0 }; for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); writer.Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); writer.Flush(); } var xref = output.Position; writer.Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"); foreach (var offset in offsets.Skip(1)) writer.Write($"{offset:D10} 00000 n \n"); writer.Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"); writer.Flush(); return output.ToArray();
    }
    private static int LineHeight((string Text, int Size, bool Bold) line) => line.Size + 4;
    private static IEnumerable<string> Wrap(string text, int width) { for (var start = 0; start < text.Length;) { var length = Math.Min(width, text.Length - start); if (start + length < text.Length) { var split = text.LastIndexOf(' ', start + length - 1, length); if (split > start) length = split - start; } yield return text.Substring(start, length).Trim(); start += length; while (start < text.Length && text[start] == ' ') start++; } }
    private static string PdfEscape(string value) => new(value.Select(c => c is '(' or ')' or '\\' ? ' ' : c > 126 ? '?' : c).ToArray());
}
