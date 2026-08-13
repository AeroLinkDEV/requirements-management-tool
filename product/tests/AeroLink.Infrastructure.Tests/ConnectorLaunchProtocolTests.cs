using System.Security.Cryptography;
using AeroLink.ConnectorProtocol;

namespace AeroLink.Infrastructure.Tests;

public sealed class ConnectorLaunchProtocolTests
{
    [Fact]
    public void Signed_envelope_binds_every_controlled_launch_field_and_rejects_wrong_key_expiry_and_unknown_fields()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var original = Envelope(now);
        var signed = ConnectorLaunchProtocol.Sign(original, signingKey);
        var publicKey = ConnectorLaunchProtocol.ExportPublicKey(signingKey);

        Assert.Equal(original, ConnectorLaunchProtocol.Verify(signed, publicKey, now));
        Assert.Equal("connector_envelope_signature_invalid", Assert.Throws<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.Verify(signed, ConnectorLaunchProtocol.ExportPublicKey(wrongKey), now)).Code);
        Assert.Equal("connector_envelope_expired", Assert.Throws<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.Verify(signed, publicKey, now.AddMinutes(6))).Code);
        using var unsupportedKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Equal("connector_key_unsupported", Assert.Throws<ConnectorProtocolException>(() =>
            ConnectorLaunchProtocol.PublicKeyFingerprint(ConnectorLaunchProtocol.ExportPublicKey(unsupportedKey))).Code);

        foreach (var changed in Mutations(original))
        {
            var changedPayload = ConnectorLaunchProtocol.Sign(changed, signingKey).Split('.')[0];
            var forged = changedPayload + "." + signed.Split('.')[1];
            Assert.Equal("connector_envelope_signature_invalid", Assert.Throws<ConnectorProtocolException>(
                () => ConnectorLaunchProtocol.Verify(forged, publicKey, now)).Code);
        }

        var originalPieces = signed.Split('.'); var originalJson = Decode(originalPieces[0]);
        foreach (var version in new[] { ConnectorLaunchProtocol.Version, ConnectorLaunchProtocol.ProfileVersion })
        {
            var changedPayload = Encode(System.Text.Encoding.UTF8.GetBytes(originalJson.Replace(version, "unsupported-version", StringComparison.Ordinal)));
            Assert.Equal("connector_envelope_signature_invalid", Assert.Throws<ConnectorProtocolException>(
                () => ConnectorLaunchProtocol.Verify($"{changedPayload}.{originalPieces[1]}", publicKey, now)).Code);
        }

        var pieces = signed.Split('.'); var payload = Decode(pieces[0]);
        var withUnknown = payload.TrimEnd('}') + ",\"server\":\"https://attacker.example\"}";
        var unknownPayload = Encode(System.Text.Encoding.UTF8.GetBytes(withUnknown));
        var unknownSignature = Encode(signingKey.SignData(System.Text.Encoding.ASCII.GetBytes(unknownPayload), HashAlgorithmName.SHA256));
        Assert.Equal("connector_envelope_invalid", Assert.Throws<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.Verify($"{unknownPayload}.{unknownSignature}", publicKey, now)).Code);
    }

    [Theory]
    [InlineData("https://Example.COM", "https://example.com")]
    [InlineData("https://example.com:443", "https://example.com")]
    [InlineData("https://example.com:8443", "https://example.com:8443")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080")]
    [InlineData("http://[::1]:5080", "http://[::1]:5080")]
    public void Origin_normalization_is_exact_and_bounded(string input, string expected) =>
        Assert.Equal(expected, ConnectorLaunchProtocol.NormalizeOrigin(input, allowInsecureLoopback: true));

    [Theory]
    [InlineData("https://user@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?server=other")]
    [InlineData("http://example.com")]
    [InlineData("file:///c:/temp")]
    public void Unsafe_origins_are_rejected(string input) =>
        Assert.Equal("connector_origin_invalid", Assert.Throws<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.NormalizeOrigin(input, allowInsecureLoopback: true)).Code);

    [Fact]
    public void Trust_enrollment_rotation_revocation_and_replay_are_explicit_and_audited()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-connector-trust-{Guid.NewGuid():N}");
        try
        {
            var store = new ConnectorTrustStore(root); var now = DateTimeOffset.UtcNow;
            using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var first = Manifest("deployment-a", "https://a.example", firstKey, now);
            var second = Manifest("deployment-a", "https://b.example", secondKey, now.AddMinutes(1));
            store.Enroll(first, "operator", now);
            Assert.Equal(first.KeyId, store.Require("deployment-a", first.KeyId).KeyId);
            store.Enroll(second, "operator", now.AddMinutes(1));
            Assert.Equal("connector_deployment_untrusted", Assert.Throws<ConnectorProtocolException>(() => store.Require("deployment-a", first.KeyId)).Code);
            Assert.Equal(second.KeyId, store.Require("deployment-a", second.KeyId).KeyId);
            store.ConsumeNonce("deployment-a", "nonce-123456789", now.AddMinutes(5), now);
            Assert.Equal("connector_envelope_replayed", Assert.Throws<ConnectorProtocolException>(() => store.ConsumeNonce("deployment-a", "nonce-123456789", now.AddMinutes(5), now)).Code);
            store.Revoke("deployment-a", second.KeyId, "operator", now.AddMinutes(2));
            Assert.Equal("connector_deployment_untrusted", Assert.Throws<ConnectorProtocolException>(() => store.Require("deployment-a", second.KeyId)).Code);
            Assert.Equal("connector_key_retired", Assert.Throws<ConnectorProtocolException>(() => store.Enroll(second, "operator", now.AddMinutes(3))).Code);
            Assert.Contains("enroll", File.ReadAllText(Path.Combine(root, "trust-audit.log")));
            Assert.Contains("revoke", File.ReadAllText(Path.Combine(root, "trust-audit.log")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Workspaces_are_isolated_by_deployment_project_document_revision_and_grant_and_never_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-workspaces-{Guid.NewGuid():N}"); var now = DateTimeOffset.UtcNow;
        try
        {
            var legacy = Path.Combine(root, "SDP-000001.00.docx"); Directory.CreateDirectory(root); File.WriteAllText(legacy, "legacy unsent work");
            var first = Envelope(now); var grant = Guid.NewGuid();
            var firstPath = ConnectorWorkspaceLayout.CreateNew(root, first, grant);
            var unsent = Path.Combine(firstPath, ConnectorWorkspaceLayout.SafeDocumentFileName(first.RevisionNumber)); File.WriteAllText(unsent, "unsent work");
            Assert.Equal("connector_workspace_exists", Assert.Throws<ConnectorProtocolException>(() => ConnectorWorkspaceLayout.CreateNew(root, first, grant)).Code);
            Assert.Equal("unsent work", File.ReadAllText(unsent));
            var otherProject = first with { ProjectId = Guid.NewGuid() }; var otherDeployment = first with { DeploymentId = "deployment-b" };
            Assert.NotEqual(firstPath, ConnectorWorkspaceLayout.CreateNew(root, otherProject, Guid.NewGuid()));
            Assert.NotEqual(firstPath, ConnectorWorkspaceLayout.CreateNew(root, otherDeployment, Guid.NewGuid()));
            Assert.Equal("legacy unsent work", File.ReadAllText(legacy));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Protected_workspace_metadata_round_trips_without_tokens_and_rejects_tampering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-protected-workspaces-{Guid.NewGuid():N}");
        try
        {
            var envelope = Envelope(DateTimeOffset.UtcNow); var workspaceId = Guid.NewGuid();
            var path = ConnectorWorkspaceLayout.CreateNew(root, envelope, workspaceId);
            byte[] Protect(byte[] value) => value.Select(item => (byte)(item ^ 0x5a)).ToArray();
            var store = new ConnectorWorkspaceStore(root, Protect, Protect);
            var metadata = new ConnectorWorkspaceMetadata(2, workspaceId, envelope.DeploymentId, envelope.Origin,
                Guid.NewGuid(), envelope.ProjectId, envelope.DocumentId, envelope.DocumentNumber, envelope.RevisionId,
                envelope.RevisionNumber, envelope.EditSessionId, Guid.NewGuid(), envelope.Mode, envelope.SourceAttachmentId,
                envelope.SourceSha256, "SDP-000001.00.docx", ConnectorWorkspaceState.Connected, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15));
            store.Save(path, metadata);
            Assert.Equal(metadata, store.Load(path)); Assert.Single(store.Scan());
            var protectedBytes = File.ReadAllBytes(Path.Combine(path, ConnectorWorkspaceStore.MetadataFileName));
            Assert.DoesNotContain("accessToken", System.Text.Encoding.UTF8.GetString(protectedBytes), StringComparison.OrdinalIgnoreCase);
            protectedBytes[0] ^= 0xff; File.WriteAllBytes(Path.Combine(path, ConnectorWorkspaceStore.MetadataFileName), protectedBytes);
            Assert.Equal("connector_workspace_invalid", Assert.Throws<ConnectorProtocolException>(() => store.Load(path)).Code);
            Assert.Empty(store.Scan());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(ConnectorWorkspaceState.Downloading)]
    [InlineData(ConnectorWorkspaceState.Connected)]
    [InlineData(ConnectorWorkspaceState.Retrying)]
    [InlineData(ConnectorWorkspaceState.LeaseAtRisk)]
    [InlineData(ConnectorWorkspaceState.Finalizing)]
    [InlineData(ConnectorWorkspaceState.CleanupPending)]
    [InlineData(ConnectorWorkspaceState.SourceConflict)]
    [InlineData(ConnectorWorkspaceState.Expired)]
    [InlineData(ConnectorWorkspaceState.ForceUnlocked)]
    [InlineData(ConnectorWorkspaceState.Abandoned)]
    public void Interrupted_workspace_states_survive_restart_without_a_reusable_credential(ConnectorWorkspaceState state)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-restart-{Guid.NewGuid():N}");
        try
        {
            var envelope = Envelope(DateTimeOffset.UtcNow); var workspaceId = Guid.NewGuid();
            var path = ConnectorWorkspaceLayout.CreateNew(root, envelope, workspaceId);
            byte[] Protect(byte[] value) => value.Select(item => (byte)(item ^ 0x37)).ToArray();
            var storeBeforeCrash = new ConnectorWorkspaceStore(root, Protect, Protect);
            var metadata = new ConnectorWorkspaceMetadata(2, workspaceId, envelope.DeploymentId, envelope.Origin,
                Guid.NewGuid(), envelope.ProjectId, envelope.DocumentId, envelope.DocumentNumber, envelope.RevisionId,
                envelope.RevisionNumber, envelope.EditSessionId, Guid.NewGuid(), envelope.Mode, envelope.SourceAttachmentId,
                envelope.SourceSha256, "SDP-000001.00.docx", state, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                LocalSha256: new string('b', 64), LeaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(15),
                RetainUntil: ConnectorWorkspaceLifecycle.RetainUntil(state, DateTimeOffset.UtcNow));
            storeBeforeCrash.Save(path, metadata);

            var storeAfterRestart = new ConnectorWorkspaceStore(root, Protect, Protect);
            Assert.Equal(metadata, Assert.Single(storeAfterRestart.Scan()).Metadata);
            Assert.DoesNotContain("token", string.Join('|', typeof(ConnectorWorkspaceMetadata).GetProperties().Select(property => property.Name)), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Heartbeat_retries_transient_failures_marks_lease_risk_and_maps_terminal_server_states()
    {
        var now = DateTimeOffset.UtcNow; var lease = now.AddMinutes(15);
        var first = ConnectorHeartbeatPolicy.Failure(0, now, lease); Assert.Equal(ConnectorWorkspaceState.Retrying, first.State); Assert.Equal(TimeSpan.FromSeconds(10), first.NextDelay); Assert.False(first.Terminal);
        var third = ConnectorHeartbeatPolicy.Failure(2, now, lease); Assert.Equal(ConnectorWorkspaceState.LeaseAtRisk, third.State); Assert.Equal(TimeSpan.FromMinutes(1), third.NextDelay);
        Assert.Equal(ConnectorWorkspaceState.Expired, ConnectorHeartbeatPolicy.Failure(1, now, lease, "stale_connector_session").State);
        var offlinePastLease = ConnectorHeartbeatPolicy.Failure(3, lease.AddSeconds(1), lease);
        Assert.Equal(ConnectorWorkspaceState.Expired, offlinePastLease.State); Assert.True(offlinePastLease.Terminal);
        Assert.Equal(ConnectorWorkspaceState.ForceUnlocked, ConnectorHeartbeatPolicy.Failure(1, now, lease, "connector_force_unlocked").State);
        Assert.Equal(ConnectorWorkspaceState.SourceConflict, ConnectorHeartbeatPolicy.Failure(1, now, lease, "document_snapshot_conflict").State);
        Assert.Equal(ConnectorWorkspaceState.Connected, ConnectorHeartbeatPolicy.Success().State);
    }

    [Fact]
    public void Word_state_and_candidate_lifecycle_prevent_unsaved_cleanup_and_candidate_overwrite()
    {
        Assert.False(ConnectorWorkspaceLifecycle.CanUpload(ConnectorWordDocumentState.OpenUnsaved));
        Assert.True(ConnectorWorkspaceLifecycle.CanUpload(ConnectorWordDocumentState.OpenSaved));
        Assert.False(ConnectorWorkspaceLifecycle.CanCleanup(ConnectorWordDocumentState.OpenSaved));
        Assert.True(ConnectorWorkspaceLifecycle.CanCleanup(ConnectorWordDocumentState.Closed));
        var root = Path.Combine(Path.GetTempPath(), $"aerolink-candidates-{Guid.NewGuid():N}");
        try { Assert.NotEqual(ConnectorWorkspaceLifecycle.CreateCandidateSet(root), ConnectorWorkspaceLifecycle.CreateCandidateSet(root)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Redirects_and_cross_origin_responses_are_rejected_for_every_connector_operation()
    {
        var origin = new Uri("https://a.example");
        using var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
        { RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://a.example/api/document-connector/redeem") };
        Assert.Equal("connector_redirect_refused", Assert.Throws<ConnectorProtocolException>(() => ConnectorHttpPolicy.ValidateResponse(redirect, origin)).Code);

        using var crossOrigin = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://attacker.example/api/document-connector/id/download") };
        Assert.Equal("connector_origin_mismatch", Assert.Throws<ConnectorProtocolException>(() => ConnectorHttpPolicy.ValidateResponse(crossOrigin, origin)).Code);

        using var sameOrigin = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://a.example/api/document-connector/id/download") };
        ConnectorHttpPolicy.ValidateResponse(sameOrigin, origin);
    }

    [Fact]
    public void Redemption_cannot_redefine_any_signed_target_field()
    {
        var envelope = Envelope(DateTimeOffset.UtcNow);
        var exact = Redemption(envelope); ConnectorLaunchProtocol.ValidateRedemption(envelope, exact);
        var changed = new ConnectorRedemptionIdentity[]
        {
            exact with { Mode = "release" }, exact with { DeploymentId = "deployment-b" },
            exact with { Origin = "https://attacker.example" }, exact with { ProjectId = Guid.NewGuid() },
            exact with { DocumentId = Guid.NewGuid() }, exact with { DocumentNumber = "SDP-999999" },
            exact with { RevisionId = Guid.NewGuid() }, exact with { RevisionNumber = "SDP-000001.99" },
            exact with { SourceAttachmentId = Guid.NewGuid() }, exact with { SourceSize = exact.SourceSize + 1 },
            exact with { SourceSha256 = new string('b', 64) }, exact with { EditSessionId = Guid.NewGuid() },
            exact with { RecoveryWorkspaceId = Guid.NewGuid() }
        };
        Assert.All(changed, value => Assert.Equal("connector_redemption_mismatch",
            Assert.Throws<ConnectorProtocolException>(() => ConnectorLaunchProtocol.ValidateRedemption(envelope, value)).Code));
    }

    [Fact]
    public async Task Download_copy_is_bounded_and_requires_the_exact_signed_length()
    {
        await using var exact = new MemoryStream(); await ConnectorLaunchProtocol.CopyExactlyAsync(new MemoryStream(new byte[32]), exact, 32);
        Assert.Equal(32, exact.Length);
        await using var tooLong = new MemoryStream();
        Assert.Equal("connector_download_oversized", (await Assert.ThrowsAsync<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.CopyExactlyAsync(new MemoryStream(new byte[33]), tooLong, 32))).Code);
        await using var tooShort = new MemoryStream();
        Assert.Equal("connector_download_size_mismatch", (await Assert.ThrowsAsync<ConnectorProtocolException>(
            () => ConnectorLaunchProtocol.CopyExactlyAsync(new MemoryStream(new byte[31]), tooShort, 32))).Code);
    }

    private static ConnectorLaunchEnvelope Envelope(DateTimeOffset now) => new(ConnectorLaunchProtocol.Version,
        ConnectorLaunchProtocol.ProfileVersion, "deployment-a", "https://a.example", "key-1", "nonce-123456789",
        now.AddMinutes(5), Guid.NewGuid(), Guid.NewGuid(), "SDP-000001", Guid.NewGuid(), "SDP-000001.00", "edit",
        Guid.NewGuid(), 4096, new string('a', 64), Guid.NewGuid());

    private static IEnumerable<ConnectorLaunchEnvelope> Mutations(ConnectorLaunchEnvelope value)
    {
        yield return value with { Origin = "https://attacker.example" }; yield return value with { ProjectId = Guid.NewGuid() };
        yield return value with { DocumentId = Guid.NewGuid() }; yield return value with { DocumentNumber = "SDP-999999" };
        yield return value with { RevisionId = Guid.NewGuid() }; yield return value with { RevisionNumber = "SDP-000001.99" };
        yield return value with { Mode = "release" }; yield return value with { SourceAttachmentId = Guid.NewGuid() };
        yield return value with { SourceSize = value.SourceSize + 1 }; yield return value with { SourceSha256 = new string('b', 64) };
        yield return value with { ExpiresAt = value.ExpiresAt.AddSeconds(1) }; yield return value with { Nonce = "nonce-other-123456" };
        yield return value with { DeploymentId = "deployment-b" }; yield return value with { KeyId = "key-2" };
        yield return value with { EditSessionId = Guid.NewGuid() }; yield return value with { RecoveryWorkspaceId = Guid.NewGuid() };
    }

    private static ConnectorRedemptionIdentity Redemption(ConnectorLaunchEnvelope value) => new(value.Mode,
        value.DeploymentId, value.Origin, value.ProjectId, value.DocumentId, value.DocumentNumber, value.RevisionId,
        value.RevisionNumber, value.SourceAttachmentId, value.SourceSize, value.SourceSha256, value.EditSessionId,
        value.RecoveryWorkspaceId);

    private static ConnectorEnrollmentManifest Manifest(string deployment, string origin, ECDsa key, DateTimeOffset now)
    {
        var publicKey = ConnectorLaunchProtocol.ExportPublicKey(key); var fingerprint = ConnectorLaunchProtocol.PublicKeyFingerprint(publicKey);
        return new(ConnectorLaunchProtocol.Version, ConnectorLaunchProtocol.ProfileVersion, deployment, origin, fingerprint[..24], publicKey, fingerprint, false, now);
    }
    private static string Decode(string value) { var padded = value.Replace('-', '+').Replace('_', '/'); padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() }; return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)); }
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
