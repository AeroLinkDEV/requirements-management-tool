using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class LegacyProcedureManifestBootstrapDomainTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Rule =
        "Latest non-Draft controlled revision per project procedure; latest Retired suppresses.";

    [Fact]
    public void A_legacy_bootstrap_is_an_explicit_attributable_manifest_not_ordinary_release_work()
    {
        var baseline = FrozenWithRequirements();
        baseline.MarkReleased("release.cm", Now.AddHours(1));

        baseline.BootstrapLegacyTestProcedures("migration.cm", Hash, 42, 3, Rule, Now.AddHours(2));

        Assert.Equal(Hash, baseline.TestProceduresHash);
        Assert.Equal(Now.AddHours(2), baseline.TestProceduresMaterializedAt);
        var recorded = Assert.Single(baseline.Events,
            item => item.EventType == "LegacyProcedureManifestBootstrapped");
        Assert.Equal("migration.cm", recorded.ActorId);
        Assert.Contains("42 active verification artifact revisions", recorded.Detail);
        Assert.Contains("3 retired verification artifact identities suppressed", recorded.Detail);
        Assert.Contains(Hash, recorded.Detail);
        Assert.Contains(Rule, recorded.Detail);

        // The narrow migration method is one-shot. Ordinary released-build materialization remains refused and
        // cannot become a second route around released configuration immutability.
        Assert.Throws<DomainException>(() =>
            baseline.BootstrapLegacyTestProcedures("migration.cm", Hash, 42, 3, Rule, Now.AddHours(3)));
        Assert.Throws<DomainException>(() =>
            baseline.MarkTestProceduresMaterialized("migration.cm", Hash, 42, Now.AddHours(3)));
    }

    [Fact]
    public void Draft_or_unmaterialized_requirements_cannot_be_described_as_a_legacy_procedure_snapshot()
    {
        var draft = new CandidateBaseline("SW-98.00", 0, ProjectId, ReleaseId, null,
            "Draft", "cm", Now);
        Assert.Throws<DomainException>(() =>
            draft.BootstrapLegacyTestProcedures("migration.cm", Hash, 0, 0, Rule, Now));

        var frozen = FrozenWithRequirements(materializeRequirements: false);
        Assert.Throws<DomainException>(() =>
            frozen.BootstrapLegacyTestProcedures("migration.cm", Hash, 0, 0, Rule, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void The_bootstrap_refuses_an_unverifiable_manifest_hash(string hash)
    {
        var baseline = FrozenWithRequirements();
        Assert.Throws<DomainException>(() =>
            baseline.BootstrapLegacyTestProcedures("migration.cm", hash, 0, 0, Rule, Now));
    }

    private static CandidateBaseline FrozenWithRequirements(bool materializeRequirements = true)
    {
        var baseline = new CandidateBaseline("SW-98.00", 0, ProjectId, ReleaseId, null,
            "Legacy predecessor", "cm", Now);
        baseline.Select(ApprovedChangeRequest(), "cm", Now);
        baseline.Freeze("cm", Now.AddMinutes(1));
        if (materializeRequirements)
            baseline.MarkRequirementsMaterialized("cm", Hash, 1, Now.AddMinutes(2));
        return baseline;
    }

    private static SystemChangeRequest ApprovedChangeRequest()
    {
        var request = new SystemChangeRequest("SRCR-09800", 0, ProjectId, ReleaseId,
            "Legacy procedure bootstrap", "Problem", "Analysis", "Solution", "author", Now);
        request.AddRequirementChange("author", "SYSR-09800000", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The product shall retain its legacy verification inventory.",
            "Migration integrity.", "Test", Now);
        request.SubmitForReview("author", [new ApproverSelection("reviewer", "Reviewer")], Now);
        request.ApproveActiveStage("reviewer", Now.AddMinutes(1));
        return request;
    }
}
