using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Verification;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>
/// The controlled publication of a test change request.
///
/// The verification twin of <see cref="ChangeRequestOutputGenerator"/>, and deliberately its mirror: the same
/// professional publication, the same renderer, the same section shape. A package is the record that governs
/// procedure change, and an approver reading one outside the product needed the same document a change
/// request has always produced.
///
/// Where the change request publishes its requirement impact, this publishes its procedure impact — grouped
/// by what each proposal does, because "what does this package introduce, correct and withdraw" is the
/// question somebody reads it to answer.
/// </summary>
public sealed class TestChangeRequestOutputGenerator(AeroLinkDbContext db)
{
    public async Task<GeneratedOutput?> GenerateAsync(Guid testChangeRequestId, string format, CancellationToken ct)
    {
        var package = await db.TestChangeReviews.AsNoTracking()
            .Include(x => x.ProcedureChanges)
            .Include(x => x.ReviewCycles).ThenInclude(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == testChangeRequestId, ct);
        if (package is null) return null;

        var project = await db.Projects.AsNoTracking().SingleAsync(x => x.Id == package.ProjectId, ct);
        var program = await db.Programs.AsNoTracking().SingleAsync(x => x.Id == project.ProgramId, ct);
        var release = await db.Releases.AsNoTracking().SingleAsync(x => x.Id == package.ReleaseId, ct);

        var actorIds = new[] { package.AuthorId, package.AssignedEngineerId ?? "", package.ApprovedBy ?? "" }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var people = await db.UserAccounts.AsNoTracking().Where(x => actorIds.Contains(x.UserName))
            .ToDictionaryAsync(x => x.UserName, x => x.DisplayName, ct);
        string Person(string? userName) => string.IsNullOrWhiteSpace(userName)
            // A package raised by an assessment has no author, and naming one would be an invention in a
            // controlled document.
            ? "Raised by downstream assessment"
            : people.GetValueOrDefault(userName, userName);

        var latest = package.ReviewCycles.OrderByDescending(x => x.Sequence).FirstOrDefault();
        var manifest = latest?.SnapshotHash ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{package.DisplayNumber}|{package.Title}|{package.Problem}|{package.Analysis}|{package.Solution}|{package.UpdatedAt:O}"))).ToLowerInvariant();
        var approvals = latest?.Steps.OrderBy(x => x.Position)
            .Select(x => new PublicationApproval($"Review position {x.Position + 1}", x.ApproverName, x.ApproverId,
                x.State.ToString(), x.DecidedAt)).ToList() ?? [];

        var definition = new[]
        {
            new PublicationRecord("P", "Problem", "Problem statement", package.Problem, []),
            new PublicationRecord("A", "Analysis", "Impact and causal analysis", package.Analysis, []),
            new PublicationRecord("S", "Solution", "Proposed controlled solution", package.Solution, []),
        };

        PublicationRecord Record(TestProcedureChange change) => new(
            change.DisplayNumber, change.Level.ToString(), change.Kind + " procedure change",
            change.Kind == TestProcedureChangeKind.Retire
                ? "Retired from future effective baselines"
                : change.Objective,
            new[]
            {
                ("Change kind", change.Kind.ToString()),
                ("Objective", change.Objective),
                ("Preconditions", change.Preconditions),
                ("Steps", change.Steps),
                ("Expected result", change.ExpectedResult),
                ("Rationale", change.Rationale),
            });

        var introduced = package.ProcedureChanges.Where(x => x.Kind == TestProcedureChangeKind.Introduce)
            .OrderBy(x => x.BaseNumber).Select(Record).ToList();
        var modified = package.ProcedureChanges.Where(x => x.Kind == TestProcedureChangeKind.Modify)
            .OrderBy(x => x.BaseNumber).Select(Record).ToList();
        var retired = package.ProcedureChanges.Where(x => x.Kind == TestProcedureChangeKind.Retire)
            .OrderBy(x => x.BaseNumber).Select(Record).ToList();

        var sections = new List<PublicationSection>
        {
            new("Test Change Request Definition",
                "The approved Problem-Analysis-Solution case defines why the test work is needed and how it will be controlled.",
                definition),
        };
        if (introduced.Count > 0)
            sections.Add(new("New Test Procedures",
                $"{introduced.Count} new stable procedure identit{(introduced.Count == 1 ? "y is" : "ies are")} proposed.", introduced));
        if (modified.Count > 0)
            sections.Add(new("Modified Test Procedures",
                $"{modified.Count} existing procedure{(modified.Count == 1 ? " is" : "s are")} corrected by this package.", modified));
        if (retired.Count > 0)
            sections.Add(new("Retired Test Procedures",
                $"{retired.Count} procedure{(retired.Count == 1 ? " is" : "s are")} removed from future effective baselines while immutable history is retained.", retired));

        var raisedFrom = package.ChangeRequestId is not null
            ? $"Change request {package.SourceChangeRequestNumber}"
            : $"Problem Report {package.SourceProblemReportNumber}";

        var publication = new ProfessionalPublication(
            project.SoftwareProduct, program.Name + " (" + program.Code + ")", project.Name,
            package.Discipline switch
            {
                TestChangeReviewDiscipline.System => "System Test Change Request",
                TestChangeReviewDiscipline.HighLevelSoftware => "High-Level Software Test Change Request",
                _ => "Low-Level Software Test Change Request",
            },
            package.Title,
            "Controlled change case, procedure impact, review decisions, and what the package was raised from",
            package.BaseNumber, package.Revision.ToString("D2"), Humanize(package.State.ToString()),
            release.Version, "Not yet baseline-effective", Person(package.AuthorId), package.UpdatedAt, manifest,
            new[]
            {
                ("Author", Person(package.AuthorId)),
                ("Discipline", package.Discipline.ToString()),
                ("Raised from", raisedFrom),
                ("Assigned engineer", string.IsNullOrWhiteSpace(package.AssignedEngineerId) ? "Unassigned" : Person(package.AssignedEngineerId)),
                ("Target build", release.Version),
                ("Created", package.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")),
                ("Last updated", package.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")),
                ("Review cycle", latest is null ? "Not submitted" : latest.Sequence + " - " + latest.State),
                ("Review snapshot hash", latest?.SnapshotHash ?? "Not yet frozen for review"),
            },
            approvals,
            new[] { (package.Revision.ToString("D2"), Humanize(package.State.ToString()),
                package.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd"), Person(package.AuthorId)) },
            sections);

        return ProfessionalPublicationRenderer.Render(publication, format,
            package.DisplayNumber + "_" + SafeFileName(package.Title));
    }

    private static string Humanize(string value) =>
        string.Concat(value.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(value[i - 1]) ? " " + c : c.ToString()));

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(x => invalid.Contains(x) ? '-' : x).ToArray()).Trim();
        return safe.Length > 60 ? safe[..60].Trim() : safe;
    }
}
