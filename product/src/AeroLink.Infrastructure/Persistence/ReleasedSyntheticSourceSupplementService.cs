using System.Security.Cryptography;
using System.Text;
using AeroLink.Domain.Baselines;
using AeroLink.Domain.Common;
using AeroLink.Domain.Integrations;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Releases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AeroLink.Infrastructure.Persistence;

/// <summary>Exact source-only manifest accepted by the released synthetic supplement command.</summary>
public sealed record ReleasedSyntheticSourceSupplementManifest(
    int SchemaVersion,
    Guid ProjectId,
    Guid ReleaseId,
    Guid ReleaseCampaignId,
    Guid BaselineId,
    Guid RepositoryConfigurationId,
    long ExpectedConfigurationVersion,
    long ExpectedSourceSelectionVersion,
    string InstanceBaseUrl,
    long RemoteProjectId,
    string RepositoryPath,
    string CommitSha,
    string RequestedReference,
    GitLabReferenceKind ReferenceKind,
    Guid OperationId,
    string PolicyId,
    string AuthorizationReference,
    string Reason)
{
    public string Digest()
    {
        ValidateShape();
        var canonical = string.Join("\n", [
            SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ProjectId.ToString("D"),
            ReleaseId.ToString("D"),
            ReleaseCampaignId.ToString("D"),
            BaselineId.ToString("D"),
            RepositoryConfigurationId.ToString("D"),
            ExpectedConfigurationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ExpectedSourceSelectionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReleasedSyntheticSourceSupplementService.CanonicalOrigin(InstanceBaseUrl),
            RemoteProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            RepositoryPath.Trim(),
            CommitSha.Trim().ToLowerInvariant(),
            RequestedReference.Trim(),
            ReferenceKind.ToString(),
            OperationId.ToString("D"),
            PolicyId.Trim(),
            AuthorizationReference.Trim(),
            Reason.Trim(),
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public void ValidateShape()
    {
        if (SchemaVersion != 1) throw new DomainException("The released source supplement manifest version is not supported.");
        if (ProjectId == Guid.Empty || ReleaseId == Guid.Empty || ReleaseCampaignId == Guid.Empty || BaselineId == Guid.Empty
            || RepositoryConfigurationId == Guid.Empty || OperationId == Guid.Empty)
            throw new DomainException("A released source supplement manifest requires all exact local identities.");
        if (ExpectedConfigurationVersion < 1 || ExpectedSourceSelectionVersion != 0)
            throw new DomainException("A first released source supplement requires a positive configuration version and source version zero.");
        _ = ReleasedSyntheticSourceSupplementService.CanonicalOrigin(InstanceBaseUrl);
        if (RemoteProjectId <= 0) throw new DomainException("A released source supplement manifest requires a positive remote project identity.");
        _ = CodeEvidenceValidation.RepositoryPath(RepositoryPath, "A released source supplement manifest requires a repository path.");
        _ = CodeEvidenceValidation.Sha(CommitSha, "A released source supplement manifest requires a full commit SHA.");
        if (string.IsNullOrWhiteSpace(RequestedReference) || RequestedReference.Trim().Length > 256
            || RequestedReference.Any(char.IsControl))
            throw new DomainException("A released source supplement manifest requires a bounded source reference.");
        if (!Enum.IsDefined(ReferenceKind)) throw new DomainException("The source reference kind is not supported.");
        if (string.IsNullOrWhiteSpace(PolicyId) || PolicyId.Trim().Length > 120)
            throw new DomainException("A released source supplement manifest requires a policy identity.");
        if (string.IsNullOrWhiteSpace(AuthorizationReference) || AuthorizationReference.Trim().Length > 300)
            throw new DomainException("A released source supplement manifest requires an owner authorization reference.");
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length > 4000)
            throw new DomainException("A released source supplement manifest requires an attributable reason.");
    }
}

public sealed record ReleasedSyntheticSourceSupplementPreflight(
    string ManifestDigest,
    bool IsReplay,
    ReleasedSyntheticSourceSupplement? Existing,
    GitLabCommitReference? Observation,
    Guid RepositoryConfigurationId,
    long ConfigurationVersion);

public sealed record ReleasedSyntheticSourceSupplementResult(
    Guid SupplementId,
    Guid SourceSnapshotId,
    Guid ProjectId,
    Guid ReleaseId,
    Guid OperationId,
    string ManifestDigest,
    string CommitSha,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    int AuthorityScopeVersion,
    string AuthorityScopeDigest,
    bool IsReplay);

/// <summary>
/// Implements the one, source-only, synthetic release supplement authorized by DEC-131.
/// Remote verification is performed before the project-controlled write scope; the scoped method only
/// revalidates local authority and appends the standalone snapshot and provenance row.
/// </summary>
public sealed class ReleasedSyntheticSourceSupplementService(
    AeroLinkDbContext db,
    GitLabMetadataReader reader,
    IOptions<ProjectGitLabOptions> settings)
{
    public const string PolicyId = "DEC-131";
    public const string AuthorizationReference = "issue-1023-owner-approved-source-only";
    public const string SyntheticProgramCode = "FMSLIVE";
    public const string SyntheticProjectName = "FMS Product Development";
    public const string SyntheticSoftwareProduct = "Flight Management System";
    public const string ReleasedVersion = "1.5";
    private const string ReleasedCampaignOwnershipStep = "released-campaign";
    private const int AuthorityScopeVersion = 1;

    /// <summary>Performs the read-only local and GitLab preflight. It never acquires a write scope.</summary>
    public async Task<ReleasedSyntheticSourceSupplementPreflight> PreviewAsync(
        ReleasedSyntheticSourceSupplementManifest manifest, CancellationToken ct = default)
    {
        var digest = ValidateManifest(manifest);
        var local = await ValidateLocalAsync(manifest, digest, ct);
        if (local.Existing is not null)
            return new(digest, true, local.Existing, null, local.Configuration.Id, local.Configuration.Version);

        var observation = await reader.ResolveCommitAsync(local.Configuration, manifest.RequestedReference,
            manifest.ReferenceKind, ct);
        if (!observation.Succeeded || observation.Value is null)
            throw new DomainException($"GitLab did not confirm the exact supplement commit ({observation.Code}). {observation.Detail}");
        ValidateObservation(manifest, observation.Value);
        return new(digest, false, null, observation.Value, local.Configuration.Id, local.Configuration.Version);
    }

    /// <summary>
    /// Appends the standalone source snapshot and supplement after the caller has completed the remote
    /// preflight. It intentionally does not create a source selection event or current source pointer.
    /// </summary>
    public async Task<ReleasedSyntheticSourceSupplementResult> ApplyAsync(
        ProjectControlledWriteScope scope,
        ReleasedSyntheticSourceSupplementManifest manifest,
        ReleasedSyntheticSourceSupplementPreflight preflight,
        string actor,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ProjectControlledWriteScope.Require(db, manifest.ProjectId, scope);
        var digest = ValidateManifest(manifest);
        if (!string.Equals(digest, preflight.ManifestDigest, StringComparison.Ordinal))
            throw new DomainException("The supplement manifest changed after preflight. Preview it again before applying.");

        await LockRepositoryConfigurationAsync(manifest.ProjectId, ct);
        var local = await ValidateLocalAsync(manifest, digest, ct);
        if (local.Existing is not null)
        {
            if (!SameReceipt(local.Existing, manifest, digest))
                throw new DomainException("A different released source supplement already exists for this release.");
            return Result(local.Existing, true);
        }

        if (preflight.IsReplay || preflight.Observation is null)
            throw new DomainException("The supplement preflight does not contain a fresh exact commit observation.");
        if (preflight.RepositoryConfigurationId != local.Configuration.Id
            || preflight.ConfigurationVersion != local.Configuration.Version)
            throw new DomainException("The verified repository configuration changed after supplement preflight. Preview again.");
        ValidateObservation(manifest, preflight.Observation);

        var timestamp = now.ToUniversalTime();
        var snapshot = new GitLabSourceSnapshot(manifest.ProjectId, local.Configuration.Id,
            CanonicalOrigin(settings.Value.BaseUrl), preflight.Observation.ProjectId,
            local.Configuration.RemotePathWithNamespace!, preflight.Observation.Sha,
            preflight.Observation.RequestedReference, actor, timestamp, local.Configuration.Version);
        var supplement = new ReleasedSyntheticSourceSupplement(manifest.ProjectId, manifest.ReleaseId,
            manifest.ReleaseCampaignId, manifest.BaselineId, local.Configuration.Id, local.Configuration.Version,
            snapshot.Id, snapshot.InstanceBaseUrl, snapshot.RemoteProjectId, snapshot.PathWithNamespace,
            snapshot.CommitSha, manifest.RequestedReference, manifest.ReferenceKind.ToString(), manifest.OperationId,
            manifest.SchemaVersion, digest, AuthorityScopeVersion, local.AuthorityScopeDigest,
            manifest.PolicyId.Trim(), manifest.AuthorizationReference.Trim(),
            manifest.Reason, actor, timestamp);
        db.GitLabSourceSnapshots.Add(snapshot);
        db.ReleasedSyntheticSourceSupplements.Add(supplement);
        return Result(supplement, false);
    }

    public static string CanonicalOrigin(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new DomainException("A released source supplement requires an approved HTTPS GitLab origin.");
        var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
        if (path.Contains("//", StringComparison.Ordinal) || path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(x => x is "." or ".." || x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))))
            throw new DomainException("The GitLab origin path is unsafe.");
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort || uri.Port == 443 ? string.Empty : $":{uri.Port}";
        return $"https://{host}{port}{path}";
    }

    private string ValidateManifest(ReleasedSyntheticSourceSupplementManifest manifest)
    {
        manifest.ValidateShape();
        if (!string.Equals(manifest.PolicyId.Trim(), PolicyId, StringComparison.Ordinal)
            || !string.Equals(manifest.AuthorizationReference.Trim(), AuthorizationReference, StringComparison.Ordinal))
            throw new DomainException("This operation is limited to the accepted DEC-131 source-only authorization.");
        if (!string.Equals(manifest.InstanceBaseUrl.Trim(), CanonicalOrigin(settings.Value.BaseUrl), StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The supplement manifest origin does not match the installation's approved GitLab origin.");
        return manifest.Digest();
    }

    private async Task LockRepositoryConfigurationAsync(Guid projectId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = db.Database.IsNpgsql()
            ? "SELECT \"Id\" FROM \"project_repository_configurations\" WHERE \"ProjectId\" = @project_id FOR NO KEY UPDATE"
            : "SELECT \"Id\" FROM \"project_repository_configurations\" WHERE \"ProjectId\" = @project_id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@project_id";
        parameter.Value = projectId;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is null or DBNull)
            throw new DomainException("The supplement project has no repository configuration to lock.");
    }

    private async Task<LocalFacts> ValidateLocalAsync(ReleasedSyntheticSourceSupplementManifest manifest,
        string digest, CancellationToken ct)
    {
        var project = await (from item in db.Projects.AsNoTracking()
                             join program in db.Programs.AsNoTracking() on item.ProgramId equals program.Id
                             where item.Id == manifest.ProjectId
                             select new { Project = item, Program = program }).SingleOrDefaultAsync(ct);
        if (project is null) throw new DomainException("The supplement project does not exist.");
        if (!string.Equals(project.Program.Code, SyntheticProgramCode, StringComparison.Ordinal)
            || !string.Equals(project.Project.Name, SyntheticProjectName, StringComparison.Ordinal)
            || !string.Equals(project.Project.SoftwareProduct, SyntheticSoftwareProduct, StringComparison.Ordinal))
            throw new DomainException("The supplement project is not the positively identified FMSLIVE synthetic project.");
        if (!await db.ShowcaseUpgradeSteps.AsNoTracking().AnyAsync(x => x.ProgramId == project.Project.ProgramId
                && x.StepKey == ReleasedCampaignOwnershipStep, ct))
            throw new DomainException("The supplement project has no durable released-showcase ownership marker.");
        var authorityScope = settings.Value.ReleasedSyntheticSourceSupplementScope;
        if (!TryReadScope(authorityScope, out var configuredScope))
            throw new DomainException("The released source supplement authority scope is not configured.");

        var release = await db.Releases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == manifest.ReleaseId
            && x.ProjectId == manifest.ProjectId, ct);
        if (release is null || !string.Equals(release.Version, ReleasedVersion, StringComparison.Ordinal)
            || !release.IsReleased)
            throw new DomainException("The supplement requires the exact released synthetic FMS 1.5 build.");
        var campaign = await db.ReleaseCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == manifest.ReleaseCampaignId
            && x.ProjectId == manifest.ProjectId && x.ReleaseId == manifest.ReleaseId, ct);
        if (campaign is null || campaign.BaselineId != manifest.BaselineId || campaign.State != ReleaseCampaignState.Released)
            throw new DomainException("The supplement requires the exact released FMS campaign and baseline.");
        var baseline = await db.CandidateBaselines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == manifest.BaselineId
            && x.ProjectId == manifest.ProjectId && x.ReleaseId == manifest.ReleaseId, ct);
        if (baseline is null || baseline.State != CandidateBaselineState.Released)
            throw new DomainException("The supplement requires the exact released FMS baseline.");

        var configuration = await db.ProjectRepositoryConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == manifest.ProjectId, ct);
        if (configuration is null || configuration.Id != manifest.RepositoryConfigurationId
            || configuration.Version != manifest.ExpectedConfigurationVersion
            || configuration.Status != ProjectRepositorySetupStatus.Verified
            || configuration.RemoteProjectId != manifest.RemoteProjectId
            || !string.Equals(configuration.RemotePathWithNamespace, manifest.RepositoryPath, StringComparison.Ordinal)
            || !GitLabMetadataReader.MatchesVerifiedRepositoryIdentity(configuration, settings.Value.BaseUrl))
            throw new DomainException("The verified repository configuration does not match the supplement manifest.");
        if (configuredScope.ProjectId != manifest.ProjectId || configuredScope.ProgramId != project.Project.ProgramId
            || configuredScope.ReleaseId != manifest.ReleaseId || configuredScope.BaselineId != manifest.BaselineId
            || configuredScope.CampaignId != manifest.ReleaseCampaignId)
            throw new DomainException("The supplement manifest is outside the explicitly configured released-synthetic authority scope.");
        var authorityScopeDigest = AuthorityScopeDigest(configuredScope);

        var existing = await db.ReleasedSyntheticSourceSupplements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == manifest.ProjectId && x.ReleaseId == manifest.ReleaseId, ct);
        if (existing is not null)
        {
            if (!SameReceipt(existing, manifest, digest))
                throw new DomainException("A different released source supplement already exists for this release.");
            await ValidateExistingSnapshotAsync(existing, manifest, ct);
            await EnsureNoOrdinarySourceOrRelationshipHistoryAsync(manifest, ct);
            if (existing.AuthorityScopeVersion != AuthorityScopeVersion
                || !string.Equals(existing.AuthorityScopeDigest, authorityScopeDigest, StringComparison.Ordinal))
                throw new DomainException("The configured released-synthetic authority scope changed after the supplement was recorded.");
            return new(configuration, existing, authorityScopeDigest);
        }

        await EnsureNoOrdinarySourceOrRelationshipHistoryAsync(manifest, ct);
        return new(configuration, null, authorityScopeDigest);
    }

    private async Task ValidateExistingSnapshotAsync(ReleasedSyntheticSourceSupplement existing,
        ReleasedSyntheticSourceSupplementManifest manifest, CancellationToken ct)
    {
        var snapshot = await db.GitLabSourceSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProjectId == manifest.ProjectId && x.Id == existing.SourceSnapshotId, ct);
        if (snapshot is null
            || snapshot.RepositoryConfigurationId != manifest.RepositoryConfigurationId
            || snapshot.ConfigurationVersion != manifest.ExpectedConfigurationVersion
            || !string.Equals(snapshot.InstanceBaseUrl, CanonicalOrigin(manifest.InstanceBaseUrl), StringComparison.Ordinal)
            || snapshot.RemoteProjectId != manifest.RemoteProjectId
            || !string.Equals(snapshot.PathWithNamespace, manifest.RepositoryPath.Trim(), StringComparison.Ordinal)
            || !string.Equals(snapshot.CommitSha, manifest.CommitSha.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new DomainException("The existing supplement does not have the exact immutable source snapshot recorded by its manifest.");
    }

    private async Task EnsureNoOrdinarySourceOrRelationshipHistoryAsync(
        ReleasedSyntheticSourceSupplementManifest manifest, CancellationToken ct)
    {
        if (await db.GitLabSourceSelectionEvents.AsNoTracking().AnyAsync(x => x.ProjectId == manifest.ProjectId
                && x.ReleaseId == manifest.ReleaseId, ct)
            || await db.GitLabCurrentSourceSelections.AsNoTracking().AnyAsync(x => x.ProjectId == manifest.ProjectId
                && x.ReleaseId == manifest.ReleaseId, ct))
            throw new DomainException("The release already has ordinary source selection history; the first-init supplement is refused.");
        if (await db.GitLabMergeRequestRelationships.AsNoTracking().AnyAsync(x => x.ProjectId == manifest.ProjectId
                && x.ReleaseId == manifest.ReleaseId, ct)
            || await db.GitLabFileRelationships.AsNoTracking().AnyAsync(x => x.ProjectId == manifest.ProjectId
                && x.ReleaseId == manifest.ReleaseId, ct))
            throw new DomainException("The release already has Code relationships; the source-only supplement is refused.");
    }

    private static void ValidateObservation(ReleasedSyntheticSourceSupplementManifest manifest,
        GitLabCommitReference observation)
    {
        if (observation.ProjectId != manifest.RemoteProjectId
            || !string.Equals(observation.Sha, manifest.CommitSha, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("GitLab observed a different repository or commit than the reviewed supplement manifest.");
    }

    private static bool SameReceipt(ReleasedSyntheticSourceSupplement existing,
        ReleasedSyntheticSourceSupplementManifest manifest, string digest) =>
        existing.OperationId == manifest.OperationId
        && existing.ManifestVersion == manifest.SchemaVersion
        && string.Equals(existing.ManifestDigest, digest, StringComparison.Ordinal)
        && existing.ReleaseCampaignId == manifest.ReleaseCampaignId
        && existing.BaselineId == manifest.BaselineId
        && existing.RepositoryConfigurationId == manifest.RepositoryConfigurationId
        && existing.ConfigurationVersion == manifest.ExpectedConfigurationVersion
        && string.Equals(existing.InstanceBaseUrl, CanonicalOrigin(manifest.InstanceBaseUrl), StringComparison.Ordinal)
        && existing.RemoteProjectId == manifest.RemoteProjectId
        && string.Equals(existing.RepositoryPath, manifest.RepositoryPath.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.CommitSha, manifest.CommitSha.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(existing.RequestedReference, manifest.RequestedReference.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.ReferenceKind, manifest.ReferenceKind.ToString(), StringComparison.Ordinal)
        && string.Equals(existing.PolicyId, manifest.PolicyId.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.AuthorizationReference, manifest.AuthorizationReference.Trim(), StringComparison.Ordinal)
        && string.Equals(existing.Reason, manifest.Reason.Trim(), StringComparison.Ordinal);

    private static bool TryReadScope(ReleasedSyntheticSourceSupplementScopeOptions options,
        out (Guid ProgramId, Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid CampaignId) scope)
    {
        scope = default;
        if (!Guid.TryParse(options.ProgramId, out var programId) || programId == Guid.Empty
            || !Guid.TryParse(options.ProjectId, out var projectId) || projectId == Guid.Empty
            || !Guid.TryParse(options.ReleaseId, out var releaseId) || releaseId == Guid.Empty
            || !Guid.TryParse(options.BaselineId, out var baselineId) || baselineId == Guid.Empty
            || !Guid.TryParse(options.CampaignId, out var campaignId) || campaignId == Guid.Empty)
            return false;
        scope = (programId, projectId, releaseId, baselineId, campaignId);
        return true;
    }

    private static string AuthorityScopeDigest(
        (Guid ProgramId, Guid ProjectId, Guid ReleaseId, Guid BaselineId, Guid CampaignId) scope)
    {
        var canonical = string.Join("\n", AuthorityScopeVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            scope.ProgramId.ToString("D"), scope.ProjectId.ToString("D"), scope.ReleaseId.ToString("D"),
            scope.BaselineId.ToString("D"), scope.CampaignId.ToString("D"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static ReleasedSyntheticSourceSupplementResult Result(ReleasedSyntheticSourceSupplement row, bool replay) =>
        new(row.Id, row.SourceSnapshotId, row.ProjectId, row.ReleaseId, row.OperationId, row.ManifestDigest,
            row.CommitSha, row.RecordedAt, row.RecordedBy, row.AuthorityScopeVersion, row.AuthorityScopeDigest, replay);

    private sealed record LocalFacts(ProjectRepositoryConfiguration Configuration,
        ReleasedSyntheticSourceSupplement? Existing, string AuthorityScopeDigest);
}
