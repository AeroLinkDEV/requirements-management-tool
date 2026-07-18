using System.Text.Json;

namespace AeroLink.Api.Tests;

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void Production_defaults_require_postgresql_and_do_not_embed_a_database_connection()
    {
        var appSettingsPath = Path.Combine(FindApiContentRoot(), "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
        var root = document.RootElement;

        Assert.Equal("PostgreSql", root.GetProperty("Database").GetProperty("Provider").GetString());
        Assert.Equal(string.Empty, root.GetProperty("ConnectionStrings").GetProperty("AeroLink").GetString());
        Assert.False(root.GetProperty("DemoData").GetProperty("Enabled").GetBoolean());
        Assert.False(root.GetProperty("Identity").GetProperty("SeedDemoAccounts").GetBoolean());
        Assert.True(root.GetProperty("Identity").GetProperty("CookieSecure").GetBoolean());
        Assert.Empty(root.GetProperty("Cors").GetProperty("AllowedOrigins").EnumerateArray());
        Assert.False(root.GetProperty("Integrations").GetProperty("AllowInsecureWebhookTargets").GetBoolean());
        Assert.False(root.GetProperty("Integrations").GetProperty("AllowPrivateWebhookTargets").GetBoolean());
        Assert.NotEqual("*", root.GetProperty("AllowedHosts").GetString());
    }

    private static string FindApiContentRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx")))
            current = current.Parent;

        if (current is null)
            throw new InvalidOperationException("Could not locate the product solution root for API tests.");

        return Path.Combine(current.FullName, "src", "AeroLink.Api");
    }
}
