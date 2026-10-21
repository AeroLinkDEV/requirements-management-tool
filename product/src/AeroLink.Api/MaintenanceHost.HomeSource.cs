using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AeroLink.ConnectorProtocol;
using AeroLink.Infrastructure.Persistence.Maintenance;

namespace AeroLink.Api;

public static partial class AeroLinkMaintenanceHost
{
    // Explicit captured HOME metadata, never a synthetic Site declaration or an installation override.
    private sealed record HomeExportContext(
        int SchemaVersion, string SourceInstallationRoot, string SourceProductRoot, string SourceInstanceId,
        string SourceUserSid, string SourceConnectorPath, string SourceConnectorSha256,
        string SourceRingDirectory, string SourceEvidenceRoot, string VerifiedBackupArchivePath,
        string ConnectorDeploymentId, string ConnectorKeyId, string ConnectorPublicKeyFingerprint,
        string RestoreScriptPath, string RestoreScriptSha256, string WorkRoot);

    private static HomeExportContext ReadHomeExportContext(string path, HomeContinuityCompatibilityBinding binding)
    {
        RequireHomePath(path, directory: false);
        var context = JsonSerializer.Deserialize<HomeExportContext>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The HOME export context is invalid.");
        if (context.SchemaVersion != 1 || context.SourceInstanceId != binding.SourceInstallationId
            || context.SourceUserSid != binding.SourceUserSid
            || !SameHomePath(context.SourceRingDirectory, binding.SourceRingDirectory)
            || !SameHomePath(context.VerifiedBackupArchivePath, binding.VerifiedBackupArchivePath)
            || CurrentReaderSid(binding.ReaderUserSid) != binding.SourceUserSid)
            throw new InvalidOperationException("The HOME context must match the captured instance and original Windows user.");
        RequireHomePath(context.SourceInstallationRoot, directory: true);
        RequireHomePath(context.SourceProductRoot, directory: true);
        HomeContinuityKeyTransfer.RequireHomeSourceInstallation(context.SourceInstallationRoot, context.SourceInstanceId,
            context.SourceUserSid);
        RequireUnderRoot(context.SourceProductRoot, binding.SourceContentRoot, "The captured HOME content root");
        RequireUnderRoot(context.SourceProductRoot, binding.SourceExecutablePath, "The captured HOME executable");
        RequireUnderRoot(context.SourceProductRoot, binding.SourceConfigurationPath, "The captured HOME configuration");
        // Bind the genuine source checkout's persistent pointer, not the separate candidate-tool checkout.
        var pointerPath = Path.Combine(context.SourceProductRoot, ".local", "installation.json");
        RequireHomePath(pointerPath, directory: false);
        using (var pointer = JsonDocument.Parse(File.ReadAllText(pointerPath)))
        {
            var pointed = pointer.RootElement.GetProperty("installationRoot").GetString();
            if (string.IsNullOrWhiteSpace(pointed) || !SameHomePath(pointed, context.SourceInstallationRoot))
                throw new InvalidOperationException("The HOME source pointer does not match the actual installation.");
        }
        RequireHomePath(context.RestoreScriptPath, directory: false);
        if (!SameHomePath(context.RestoreScriptPath, binding.RestoreScriptPath)
            || Path.GetFileName(context.RestoreScriptPath) != "Restore-AeroLink.ps1"
            || !string.Equals(Sha256File(context.RestoreScriptPath), context.RestoreScriptSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The candidate HOME restore authority does not match the captured tool identity.");
        RequireHomePath(context.WorkRoot, directory: true);
        RequireHomePath(context.SourceEvidenceRoot, directory: true);
        if (HomePathsOverlap(context.WorkRoot, path))
            throw new InvalidOperationException("The HOME source context must remain outside disposable work.");
        foreach (var child in new[] { "restore-work", "restore-validation", "backup-verification" })
        {
            var childPath = Path.Combine(context.WorkRoot, child);
            if (Directory.Exists(childPath) || File.Exists(childPath)) RequireHomePath(childPath, directory: true);
        }
        foreach (var protectedPath in new[] { context.SourceInstallationRoot, context.SourceProductRoot,
            binding.SourceRingDirectory, context.SourceEvidenceRoot, Path.GetDirectoryName(context.SourceConnectorPath)!,
            Path.GetDirectoryName(binding.VerifiedBackupArchivePath)!, Path.GetDirectoryName(Path.GetDirectoryName(context.RestoreScriptPath))! })
        {
            if (HomePathsOverlap(context.WorkRoot, protectedPath))
                throw new InvalidOperationException("The HOME operation work root must be separate from source, keys, archive and tools.");
        }
        // Validate the key before any restore child/database mutation; no plaintext key is copied.
        _ = ReadHomeConnectorIdentity(context, context.ConnectorDeploymentId, context.ConnectorKeyId);
        return context;
    }

    private static HomeContinuityConnectorMaterial ReadHomeConnectorIdentity(
        HomeExportContext context, string expectedDeployment, string expectedKey)
    {
        RequireHomePath(context.SourceConnectorPath, directory: false);
        if (context.ConnectorDeploymentId != expectedDeployment || context.ConnectorKeyId != expectedKey
            || !string.Equals(Sha256File(context.SourceConnectorPath), context.SourceConnectorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The HOME connector does not match its captured deployment/key/file identity.");
        var pem = File.ReadAllText(context.SourceConnectorPath);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var fingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(key.ExportSubjectPublicKeyInfoPem());
        if (fingerprint[..24] != expectedKey
            || !string.Equals(fingerprint, context.ConnectorPublicKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The HOME connector does not match its captured public fingerprint.");
        return new(expectedDeployment, expectedKey, fingerprint, pem, context.SourceInstallationRoot,
            context.SourceConnectorPath, context.SourceUserSid);
    }

    private static void AddContinuityInstallationArguments(ProcessStartInfo start, string installationRoot,
        string? homeContextPath, string operationJournal)
    {
        if (homeContextPath is null)
        {
            start.ArgumentList.Add("-InstallationRoot");
            start.ArgumentList.Add(installationRoot);
            return;
        }
        var expectedHash = RequiredJournalValue(ReadContinuityOperationJournal(operationJournal), "HomeSourceContextSha256");
        if (!string.Equals(Sha256File(homeContextPath), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The admitted HOME source context changed; preserve the operation for recovery.");
        start.ArgumentList.Add("-HomeContinuityContextPath");
        start.ArgumentList.Add(Path.GetFullPath(homeContextPath));
        start.ArgumentList.Add("-HomeContinuityContextSha256");
        start.ArgumentList.Add(expectedHash);
    }

    private static void RequireHomePath(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || !(directory ? Directory.Exists(path) : File.Exists(path)))
            throw new InvalidOperationException("A HOME context path is missing or is not absolute.");
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("HOME source and operation paths must not traverse reparse points.");
    }

    private static bool SameHomePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool HomePathsOverlap(string left, string right) => SameHomePath(left, right)
        || Path.GetFullPath(left).StartsWith(Path.GetFullPath(right).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(right).StartsWith(Path.GetFullPath(left).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
