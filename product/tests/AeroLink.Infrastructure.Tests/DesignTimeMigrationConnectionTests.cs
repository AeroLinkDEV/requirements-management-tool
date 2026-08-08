using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Design-time EF must fail closed: no implicit connection to the persistent AeroLink database may exist.
/// The resolver is deterministic and environment-free; the factory-level tests that read the process
/// environment are isolated in a non-parallel collection so they cannot race other tests.
/// </summary>
public sealed class DesignTimeMigrationConnectionResolverTests
{
    [Fact]
    public void Missing_connection_fails_closed_without_connection_contents()
    {
        var error = Assert.Throws<InvalidOperationException>(() => DesignTimeMigrationConnection.Resolve(null));

        Assert.Contains(DesignTimeMigrationConnection.EnvironmentVariable, error.Message);
        Assert.Contains("disposable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("54329", error.Message);
        Assert.DoesNotContain("Database=", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Empty_or_whitespace_connection_fails_closed(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => DesignTimeMigrationConnection.Resolve(configured));
    }

    [Fact]
    public void Explicit_connection_is_used_and_trimmed()
    {
        const string connection = "Host=127.0.0.1;Port=54331;Database=aerolink_migrations_test;Username=postgres";

        Assert.Equal(connection, DesignTimeMigrationConnection.Resolve($"  {connection}  "));
    }
}

[CollectionDefinition("DesignTimeEnvironment", DisableParallelization = true)]
public sealed class DesignTimeEnvironmentCollection;

[Collection("DesignTimeEnvironment")]
public sealed class DesignTimeDbContextFactoryTests
{
    [Fact]
    public void Factory_fails_closed_when_the_environment_variable_is_missing()
    {
        WithEnvironment(null, () =>
        {
            Assert.Throws<InvalidOperationException>(
                () => new DesignTimeDbContextFactory().CreateDbContext([]));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Factory_fails_closed_for_empty_or_whitespace_connection(string value)
    {
        WithEnvironment(value, () =>
        {
            Assert.Throws<InvalidOperationException>(
                () => new DesignTimeDbContextFactory().CreateDbContext([]));
        });
    }

    [Fact]
    public void Factory_uses_the_explicit_disposable_connection()
    {
        const string connection = "Host=127.0.0.1;Port=54331;Database=aerolink_migrations_test;Username=postgres";

        WithEnvironment(connection, () =>
        {
            using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
            Assert.True(context.Database.IsNpgsql());
            Assert.Equal(connection, context.Database.GetDbConnection().ConnectionString);
        });
    }

    private static void WithEnvironment(string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(DesignTimeMigrationConnection.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesignTimeMigrationConnection.EnvironmentVariable, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesignTimeMigrationConnection.EnvironmentVariable, previous);
        }
    }
}
