namespace AeroLink.Api.Tests;

public sealed class ServerAuthorityDiagnosticsTests
{
    [Fact]
    public void Standard_diagnostics_contains_no_human_login_or_committed_password()
    {
        var productRoot = FindProductRoot();
        var script = File.ReadAllText(Path.Combine(productRoot, "scripts", "Get-AeroLinkDiagnostics.ps1"));

        Assert.DoesNotContain("/api/auth/login", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AeroLink!2026", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[string]$Password", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/health/live", script, StringComparison.Ordinal);
        Assert.Contains("/health/ready", script, StringComparison.Ordinal);
        Assert.Contains("CreatesBrowserSession = $false", script, StringComparison.Ordinal);
    }

    private static string FindProductRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AeroLink.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate the product root.");
    }
}
