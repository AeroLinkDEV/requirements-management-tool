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
        var changes = scr.RequirementChanges.OrderBy(x => x.BaseNumber).ThenBy(x => x.Revision).Select(x => new PublicationRecord(x.DisplayNumber, x.Level.ToString(), x.Kind + " requirement change", string.IsNullOrWhiteSpace(x.Statement) ? "The requirement is proposed for retirement." : x.Statement, new[] { ("Change kind", x.Kind.ToString()), ("Rationale", x.Rationale), ("Verification method", x.VerificationMethod) })).ToList();
        var audit = scr.AuditEvents.OrderBy(x => x.OccurredAt).Select((x, i) => new PublicationRecord((i + 1).ToString("D3"), x.EventType, "", x.Detail, new[] { ("Actor", x.ActorId), ("Occurred", x.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")) })).ToList();
        var publication = new ProfessionalPublication(project.SoftwareProduct, program.Name + " (" + program.Code + ")", project.Name, scr.Type == ChangeRequestType.System ? "System Change Request" : "Software Change Request", scr.Title,
            "Controlled change case, requirement impact, review decisions, and audit history", scr.BaseNumber, scr.Revision.ToString("D2"), Humanize(scr.State.ToString()), release.Version, "Not yet baseline-effective", scr.AuthorId, scr.UpdatedAt, manifest,
            new[] { ("Author", scr.AuthorId), ("Change-request type", scr.Type.ToString()), ("Target release", release.Version), ("Created", scr.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")), ("Last updated", scr.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")), ("Review cycle", latest is null ? "Not submitted" : latest.Sequence + " - " + latest.State), ("Review snapshot hash", latest?.SnapshotHash ?? "Not yet frozen for review") }, approvals,
            new[] { (scr.Revision.ToString("D2"), Humanize(scr.State.ToString()), scr.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd"), scr.AuthorId) },
            new[] { new PublicationSection("Change Request Definition", "The approved Problem-Analysis-Solution case defines why the change is needed and how it will be controlled.", definition), new PublicationSection("Proposed Requirement Changes", $"This revision contains {changes.Count} proposed requirement change{(changes.Count == 1 ? "" : "s")}.", changes), new PublicationSection("Audit History", "Append-only material events retained for this exact change-request revision.", audit) });
        return ProfessionalPublicationRenderer.Render(publication, format, scr.DisplayNumber + "_" + SafeFileName(scr.Title));
    }
    private static string Humanize(string value) => string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1]) ? " " + c : c.ToString()));
    private static string SafeFileName(string value) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim(); return safe.Length > 60 ? safe[..60].Trim() : safe; }
}
