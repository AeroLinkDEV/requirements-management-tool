using System.Diagnostics;
using System.Text.Json;

namespace AeroLink.Tests;

/// <summary>Runs the shipped Java extractor; importer fixtures are never produced by the importer under test.</summary>
internal static class IntegrityFixturePackage
{
    private static readonly Lazy<Task<(byte[] Bytes, string Hash)>> Once = new(BuildAsync);
    public static Task<(byte[] Bytes, string Hash)> ReadAsync() => Once.Value;
    public static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "product", "tools", "integrity-extractor", "Build-Extractor.ps1")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static async Task<(byte[], string)> BuildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerolink-integrity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Fixture cleanup must stay in the temporary directory.");
        var repository = Repository();
        var script = Path.Combine(root, "fixture.ps1");
        await File.WriteAllTextAsync(script, """
            param($Repository, $OutputRoot)
            $ErrorActionPreference = 'Stop'
            $built = & (Join-Path $Repository 'product/tools/integrity-extractor/Build-Extractor.ps1') -OutputDirectory (Join-Path $OutputRoot 'classes')
            Copy-Item -LiteralPath (Join-Path $Repository 'product/tools/integrity-extractor/fixtures') -Destination (Join-Path $OutputRoot 'fixtures') -Recurse
            $configuration = Join-Path $OutputRoot 'fixtures/config.json'
            & $built.Java -cp $built.ClassPath $built.MainClass fixture $configuration (Join-Path $OutputRoot 'checkpoint') (Join-Path $OutputRoot 'source.zip')
            if ($LASTEXITCODE -ne 0) { throw 'Fixture extraction failed' }
            # Resumption must verify checkpoints and produce the same package contents under the same freeze.
            & $built.Java -cp $built.ClassPath $built.MainClass fixture $configuration (Join-Path $OutputRoot 'checkpoint') (Join-Path $OutputRoot 'resumed.zip')
            if ($LASTEXITCODE -ne 0) { throw 'Fixture resumption failed' }
            if ((Get-Content (Join-Path $OutputRoot 'source.zip.manifest.sha256') -Raw) -ne (Get-Content (Join-Path $OutputRoot 'resumed.zip.manifest.sha256') -Raw)) { throw 'Resumption changed the manifest' }
            # A retained checkpoint must not hide a changed source on resume.
            $sourceItem = Join-Path $OutputRoot 'fixtures/source/1001.json'
            $changed = (Get-Content -LiteralPath $sourceItem -Raw).Replace('Navigation freeze', 'Changed during source freeze')
            [IO.File]::WriteAllText($sourceItem, $changed)
            & $built.Java -cp $built.ClassPath $built.MainClass fixture $configuration (Join-Path $OutputRoot 'checkpoint') (Join-Path $OutputRoot 'changed.zip')
            if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath (Join-Path $OutputRoot 'changed.zip'))) { throw 'A changed source was silently sealed from checkpoints' }
            exit 0
            """);
        try
        {
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-File", script, repository, root }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the Java build.");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            if (process.ExitCode != 0) throw new InvalidOperationException(await output + "\n" + await error);
            return (await File.ReadAllBytesAsync(Path.Combine(root, "source.zip")),
                (await File.ReadAllTextAsync(Path.Combine(root, "source.zip.manifest.sha256"))).Trim());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
