using System.Text.Json;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

if (args.Length != 2) throw new ArgumentException("An owned SQLite path and fixture record path are required.");
var databasePath = Path.GetFullPath(args[0]);
var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
if (!databasePath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
    || !Path.GetFileName(databasePath).StartsWith("aerolink-e2e-1037-lineage-", StringComparison.Ordinal)
    || File.Exists(databasePath))
    throw new InvalidOperationException("Lineage qualification requires a new, uniquely owned temporary database.");
await using var db = new AeroLinkDbContext(new DbContextOptionsBuilder<AeroLinkDbContext>()
    .UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()).Options);
await db.Database.EnsureCreatedAsync();
var program = new ProgramRecord("Disposable lineage fixture", "LINEAGE-" + Guid.NewGuid().ToString("N")[..12]);
var project = new ProjectRecord(program.Id, "Stored branch qualification", "Disposable navigation fixture");
var root = new SoftwareRelease(project.Id, "9.0", false);
root.MarkReleased(DateTimeOffset.UtcNow.AddDays(-2));
var releasedChild = new SoftwareRelease(project.Id, "10.5", false, root.Id);
releasedChild.MarkReleased(DateTimeOffset.UtcNow.AddDays(-1));
var workingChild = new SoftwareRelease(project.Id, "11.0", false, root.Id);
// This is a historical navigation fixture, including its persisted supported legacy ladder.
db.AddRange(program, project, root, releasedChild, workingChild,
    LegacyDefaultProjectLadderFactory.Create(project.Id, DateTimeOffset.UtcNow));
await db.SaveChangesAsync();
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
{
    purpose = "Synthetic stored branch fixture for visual navigation only; no engineering approvals or evidence are asserted.",
    programId = program.Id, projectId = project.Id, rootReleaseId = root.Id,
    childReleaseIds = new[] { releasedChild.Id, workingChild.Id },
    releases = new[] { root, releasedChild, workingChild }.Select(release => new
    { release.Id, release.Version, release.IsReleased, release.PredecessorReleaseId }),
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
