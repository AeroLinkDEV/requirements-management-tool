using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.ChangeControl;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed class ChangeRequestOutputGenerator(AeroLinkDbContext db)
{
    public async Task<GeneratedOutput?> GenerateAsync(Guid scrId, string format, CancellationToken ct)
    {
        var scr = await db.SystemChangeRequests.AsNoTracking().Include(x => x.RequirementChanges).Include(x => x.ReviewCycles).ThenInclude(x => x.Steps).Include(x => x.AuditEvents).SingleOrDefaultAsync(x => x.Id == scrId, ct); if (scr is null) return null;
        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == scr.ProjectId, ct); var program = await db.Programs.AsNoTracking().SingleAsync(x => x.Id == project.ProgramId, ct); var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == scr.TargetReleaseId, ct);
        var latest = scr.ReviewCycles.OrderByDescending(x => x.Sequence).FirstOrDefault(); var manifest = latest?.SnapshotHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scr.DisplayNumber}|{scr.Title}|{scr.Problem}|{scr.Analysis}|{scr.Solution}|{scr.UpdatedAt:O}"))).ToLowerInvariant();
        var approvals = latest?.Steps.OrderBy(x => x.Position).Select(x => new PublicationApproval($"Review position {x.Position + 1}", x.ApproverName, x.ApproverId, x.State.ToString(), x.DecidedAt)).ToList() ?? [];
        var definition = new[] { new PublicationRecord("P", "Problem", "Problem statement", scr.Problem, []), new PublicationRecord("A", "Analysis", "Impact and causal analysis", scr.Analysis, []), new PublicationRecord("S", "Solution", "Proposed controlled solution", scr.Solution, []) };
        var changedNumbers=scr.RequirementChanges.Where(x=>x.Kind!=RequirementChangeKind.Introduce).Select(x=>x.BaseNumber).ToList();
        var priorRows=await(from artifact in db.Requirements.AsNoTracking().Where(x=>changedNumbers.Contains(x.BaseNumber)&&x.ProjectId==scr.ProjectId) join revision in db.RequirementRevisions.AsNoTracking() on artifact.Id equals revision.ArtifactId select new{artifact.BaseNumber,revision.Revision,revision.Statement}).ToListAsync(ct);
        string Prior(RequirementChange change)=>priorRows.Where(x=>x.BaseNumber==change.BaseNumber&&x.Revision<change.Revision).OrderByDescending(x=>x.Revision).Select(x=>x.Statement).FirstOrDefault()??"Prior controlled wording was not available in the selected repository context.";
        PublicationRecord Record(RequirementChange change)=>new(change.DisplayNumber,change.Level.ToString(),change.Kind+" requirement change",change.Kind==RequirementChangeKind.Retire?Prior(change):change.Statement,new[]{("Change kind",change.Kind.ToString()),("Previous approved wording",change.Kind==RequirementChangeKind.Introduce?"Not applicable - new stable identity":Prior(change)),("Proposed wording",change.Kind==RequirementChangeKind.Retire?"Retired from future effective baselines":change.Statement),("Rationale",change.Rationale),("Verification method",change.VerificationMethod)});
        var introduced=scr.RequirementChanges.Where(x=>x.Kind==RequirementChangeKind.Introduce).OrderBy(x=>x.BaseNumber).Select(Record).ToList();var modified=scr.RequirementChanges.Where(x=>x.Kind==RequirementChangeKind.Modify).OrderBy(x=>x.BaseNumber).Select(Record).ToList();var retired=scr.RequirementChanges.Where(x=>x.Kind==RequirementChangeKind.Retire).OrderBy(x=>x.BaseNumber).Select(Record).ToList();
        var audit = scr.AuditEvents.OrderBy(x => x.OccurredAt).Select((x, i) => new PublicationRecord((i + 1).ToString("D3"), x.EventType, "", CanonicalAuditText(x.Detail), new[] { ("Actor", x.ActorId), ("Occurred", x.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")) })).ToList();
        var sections=new List<PublicationSection>{new("Change Request Definition","The approved Problem-Analysis-Solution case defines why the change is needed and how it will be controlled.",definition)};if(introduced.Count>0)sections.Add(new("New Requirements",$"{introduced.Count} new stable requirement identit{(introduced.Count==1?"y":"ies")} are proposed.",introduced));if(modified.Count>0)sections.Add(new("Modified Requirements - Old and New",$"{modified.Count} existing requirement revision{(modified.Count==1?" is":"s are")} shown with previous and proposed wording.",modified));if(retired.Count>0)sections.Add(new("Retired Requirements",$"{retired.Count} requirement{(retired.Count==1?" is":"s are")} removed from future effective baselines while immutable history is retained.",retired));sections.Add(new("Audit History","Append-only material events retained for this exact change-request revision.",audit));
        var publication = new ProfessionalPublication(project.SoftwareProduct, program.Name + " (" + program.Code + ")", project.Name, scr.Type == ChangeRequestType.System ? "System Change Request" : "Software Change Request", scr.Title,
            "Controlled change case, requirement impact, review decisions, and audit history", scr.BaseNumber, scr.Revision.ToString("D2"), Humanize(scr.State.ToString()), release.Version, "Not yet baseline-effective", scr.AuthorId, scr.UpdatedAt, manifest,
            new[] { ("Author", scr.AuthorId), ("Change-request type", scr.Type.ToString()), ("Target release", release.Version), ("Created", scr.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")), ("Last updated", scr.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")), ("Review cycle", latest is null ? "Not submitted" : latest.Sequence + " - " + latest.State), ("Review snapshot hash", latest?.SnapshotHash ?? "Not yet frozen for review") }, approvals,
            new[] { (scr.Revision.ToString("D2"), Humanize(scr.State.ToString()), scr.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd"), scr.AuthorId) }, sections);
        return ProfessionalPublicationRenderer.Render(publication, format, scr.DisplayNumber + "_" + SafeFileName(scr.Title));
    }

    private static string CanonicalAuditText(string detail) => System.Text.RegularExpressions.Regex.Replace(detail,
        @"\b(SYSR|HLR|LLR)-0*([0-9]{1,6})(\.[0-9]{2})\b",
        match => $"{match.Groups[1].Value.ToUpperInvariant()}-{int.Parse(match.Groups[2].Value):D6}{match.Groups[3].Value}",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static string Humanize(string value) => string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1]) ? " " + c : c.ToString()));
    private static string SafeFileName(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim(); return safe.Length > 60 ? safe[..60].Trim() : safe; }
}
