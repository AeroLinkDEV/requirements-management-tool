using AeroLink.Domain.Identity;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// #816 Slice 3 P2 regression: an administrator's current identity edit (display name / email) must
/// survive application restart and demo-seed reconciliation. Before the fix, EnsureSeededAsync
/// unconditionally called RefreshDirectoryProfile on curated accounts, silently reverting admin edits.
///
/// The fix gates the reconciliation on the absence of an IdentityUpdated audit event: once an admin has
/// edited the current profile, the seeder no longer overwrites it. Historical attribution is unchanged.
///
/// Each phase uses a fresh DbContext to match the real application lifecycle (a restart creates a new
/// context, it does not reuse a tracked-entity graph from a previous session).
/// </summary>
public sealed class SeededIdentityEditPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aerolink-seededit-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public SeededIdentityEditPersistenceTests()
    {
        _connectionString = $"Data Source={_path};Pooling=False";
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    private AeroLinkDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(_connectionString).Options;
        return new AeroLinkDbContext(options);
    }

    [Fact]
    public async Task An_admin_identity_edit_survives_seed_reconciliation()
    {
        // Phase 1: initial seeding creates the curated account.
        using (var db = CreateContext())
        {
            var seeder = new IdentitySeeder(db);
            await seeder.EnsureSeededAsync();
            var account = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "test.engineer");
            Assert.Equal("Ethan Brooks", account.DisplayName);
            Assert.Equal("ethan.brooks@aerolink.local", account.Email);
        }

        // Phase 2: an administrator edits the current identity (same path as the identity PATCH endpoint).
        using (var db = CreateContext())
        {
            var account = await db.UserAccounts.SingleAsync(x => x.UserName == "test.engineer");
            account.RefreshDirectoryProfile("Ethan Brooks-Reyes", "ethan.reyes@edited.test");
            db.SecurityAuditEvents.Add(new SecurityAuditEvent("IdentityUpdated", "admin", account.UserName,
                "Success", "Current profile changed. Historical records were not changed.", "local", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // Phase 3: application restart — re-seeding reconciliation runs against the edited account.
        using (var db = CreateContext())
        {
            var seeder = new IdentitySeeder(db);
            await seeder.EnsureSeededAsync();
            await seeder.EnsureSeededAsync(); // second run: idempotency
        }

        // Phase 4: the edited identity survives.
        using (var verify = CreateContext())
        {
            var after = await verify.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "test.engineer");
            Assert.Equal("Ethan Brooks-Reyes", after.DisplayName);
            Assert.Equal("ethan.reyes@edited.test", after.Email);
            Assert.Equal("test.engineer", after.UserName);
            Assert.True(await verify.SecurityAuditEvents.AnyAsync(
                x => x.EventType == "IdentityUpdated" && x.Target == "test.engineer"));
        }
    }

    [Fact]
    public async Task Seeded_accounts_without_an_identity_edit_are_still_reconciled_on_reseed()
    {
        // Phase 1: initial seeding.
        using (var db = CreateContext())
        {
            var seeder = new IdentitySeeder(db);
            await seeder.EnsureSeededAsync();
            var account = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "test.engineer");
            Assert.Equal("Ethan Brooks", account.DisplayName);
        }

        // Phase 2: a direct DB change with NO IdentityUpdated audit event is treated as seed-repairable.
        using (var db = CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE user_accounts SET DisplayName = 'Direct DB Change' WHERE UserName = 'test.engineer'");
        }

        // Phase 3: re-seeding overwrites the direct change back to the seed default.
        using (var db = CreateContext())
        {
            var seeder = new IdentitySeeder(db);
            await seeder.EnsureSeededAsync();
            var after = await db.UserAccounts.AsNoTracking().SingleAsync(x => x.UserName == "test.engineer");
            Assert.Equal("Ethan Brooks", after.DisplayName);
        }
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* temp cleanup best effort */ }
    }
}
