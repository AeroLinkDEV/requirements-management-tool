from pathlib import Path
import re


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one replacement anchor, found {count}")
    file.write_text(text.replace(old, new), encoding="utf-8")


replace_once(
    "product/client/tests/history-and-build-provenance.spec.ts",
    """  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  for (const retired of ['release-planning', 'baselines']) {
    await page.goto(`${root}/${retired}`)
    await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
  }
})""",
    """  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  await page.goto(`${root}/release-planning`)
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()

  await page.goto(`${root}/baselines`)
  await expect(page.getByRole('heading', { name: 'Candidate Baselines' })).toBeVisible()
})""",
)

path = Path("product/tests/AeroLink.Infrastructure.Tests/TestProcedureMaterializationTests.cs")
text = path.read_text(encoding="utf-8")
pattern = re.compile(
    r"    \[Fact\]\n    public async Task A_predecessor_with_no_procedure_manifest_starts_the_successor_empty_rather_than_failing\(\)\n    \{.*?\n    \}\n\n    \[Fact\]\n    public async Task A_decision_that_asked_for_a_procedure_settles_when_the_test_change_request_delivers_it\(\)",
    re.S,
)
replacement = '''    [Fact]
    public async Task A_predecessor_with_no_procedure_manifest_requires_an_explicit_legacy_bootstrap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-tp-legacy-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        try
        {
            await using var db = new AeroLinkDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var (project, release) = await SeedProjectAsync(db, "FMSL");

            var legacy = new CandidateBaseline("SW-00.10", 0, project.Id, release.Id, null, "Legacy", "cm", now);
            legacy.Select(ApprovedChangeRequest(project.Id, release.Id, "SRCR-00099", now), "cm", now);
            legacy.Freeze("cm", now);
            legacy.MarkRequirementsMaterialized("cm", new string('a', 64), 0, now);
            db.Add(legacy);
            await db.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<DomainException>(() =>
                MaterializeAsync(db, project.Id, release.Id, "SW-00.20", legacy.Id, now,
                    Change("SYSTP-000001", 0, TestProcedureChangeKind.Introduce, "Oceanic sequencing")));

            Assert.Contains("legacy bootstrap snapshot", error.Message, StringComparison.OrdinalIgnoreCase);
            var successor = await db.CandidateBaselines.SingleAsync(x => x.BaseNumber == "SW-00.20");
            Assert.Null(successor.TestProceduresMaterializedAt);
            Assert.Empty(await db.BaselineTestProcedures.Where(x => x.BaselineId == successor.Id).ToListAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_decision_that_asked_for_a_procedure_settles_when_the_test_change_request_delivers_it()'''
text, count = pattern.subn(replacement, text)
if count != 1:
    raise RuntimeError(f"TestProcedureMaterializationTests replacement count: {count}")
path.write_text(text, encoding="utf-8")

path = Path("product/src/AeroLink.Api/BaselineEndpoints.cs")
text = path.read_text(encoding="utf-8")
pattern = re.compile(
    r'''        app\.MapGet\("/api/baselines/predecessors", async \(Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct\) =>\n        \{.*?\n        \}\);\n\n        app\.MapPost\("/api/baselines",''',
    re.S,
)
replacement = '''        app.MapGet("/api/baselines/predecessors", async (Guid projectId, Guid releaseId, AeroLinkDbContext db, CancellationToken ct) =>
        {
            var rows = await (from baseline in db.CandidateBaselines.AsNoTracking()
                              join release in db.Releases.AsNoTracking() on baseline.ReleaseId equals release.Id
                              where baseline.ProjectId == projectId && baseline.ReleaseId != releaseId
                                    && baseline.RequirementsMaterializedAt != null
                              select new
                              {
                                  baseline.Id,
                                  baseline.BaseNumber,
                                  baseline.Revision,
                                  baseline.Name,
                                  baseline.ReleaseId,
                                  release = release.Version,
                                  release.IsReleased,
                                  baseline.RequirementsHash,
                                  baseline.FrozenAt,
                                  requirementCount = db.BaselineRequirements.Count(x => x.BaselineId == baseline.Id)
                              }).ToListAsync(ct);
            var items = rows
                .OrderByDescending(x => x.IsReleased)
                .ThenByDescending(x => x.release, StringComparer.Ordinal)
                .ThenByDescending(x => x.FrozenAt)
                .Select(x => new
                {
                    x.Id,
                    displayNumber = ArtifactNumber.Display(x.BaseNumber, x.Revision),
                    x.Name,
                    x.ReleaseId,
                    x.release,
                    x.IsReleased,
                    x.RequirementsHash,
                    x.requirementCount
                });
            return Results.Ok(items);
        });

        app.MapPost("/api/baselines",'''
text, count = pattern.subn(replacement, text)
if count != 1:
    raise RuntimeError(f"BaselineEndpoints predecessor replacement count: {count}")
path.write_text(text, encoding="utf-8")
