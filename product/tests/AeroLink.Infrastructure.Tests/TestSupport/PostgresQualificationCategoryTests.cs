using System.Reflection;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Every test that needs the disposable PostgreSQL server carries the category the CI PostgreSQL lane selects
/// (#1122). A lane that picks qualifications by a hand-kept list of class names silently misses the next one, as
/// #1123 found for script suites: the new test then skips wherever there is no server and runs nowhere.
/// </summary>
public sealed class PostgresQualificationCategoryTests
{
    public const string Name = "Category";
    public const string Value = "PostgresQualification";

    [Fact]
    public void Every_disposable_postgres_test_is_in_the_postgres_qualification_category()
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var tests = typeof(PostgresQualificationCategoryTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(declared)
                .Where(method => method.GetCustomAttribute<DisposablePostgresFactAttribute>() is not null)
                .Select(method => (Type: type, Method: method)))
            .ToList();

        Assert.NotEmpty(tests);
        Assert.Empty(tests.Where(test => !InCategory(test.Method) && !InCategory(test.Type))
            .Select(test => $"{test.Type.FullName}.{test.Method.Name}"));
    }

    private static bool InCategory(MemberInfo member) =>
        member.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Select(argument => argument.Value as string).SequenceEqual([Name, Value]));
}
