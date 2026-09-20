using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectControlledWriteScopeTests
{
    [Fact]
    public async Task Scope_locks_existing_project_and_nested_work_must_join_the_same_scope()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Controlled scope", "SCOPE");
        var project = new ProjectRecord(program.Id, "Controlled scope", "Controlled scope product");
        db.AddRange(program, project);
        await db.SaveChangesAsync();

        await using var scope = await ProjectControlledWriteScope.AcquireAsync(db, project.Id);
        Assert.Same(db.Database.CurrentTransaction!.GetDbTransaction(), scope.Transaction.GetDbTransaction());
        Assert.Same(scope, ProjectControlledWriteScope.Join(db, project.Id, scope));
        Assert.Throws<InvalidOperationException>(() => ProjectControlledWriteScope.Join(db, Guid.NewGuid(), scope));
        await scope.CommitAsync();
    }

    [Fact]
    public async Task Acquiring_without_an_explicit_scope_cannot_adopt_an_ambient_transaction()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite(connection).Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Ambient scope", "AMBIENT");
        var project = new ProjectRecord(program.Id, "Ambient scope", "Ambient scope product");
        db.AddRange(program, project);
        await db.SaveChangesAsync();

        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectControlledWriteScope.AcquireAsync(db, project.Id));
    }
}
