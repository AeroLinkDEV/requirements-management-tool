using AeroLink.Domain.Common;
using AeroLink.Domain.Programs;

namespace AeroLink.Domain.Tests;

public sealed class ProjectSetupDraftTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 22, 0, 0, TimeSpan.Zero);
    private static readonly Guid Creator = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Draft_allocates_backing_identities_once_and_increments_version_on_save()
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test", "Flight Recorder");
        var projectId = draft.ProjectId;
        var programId = draft.InternalProgramId;
        var releaseId = draft.InitialReleaseId;

        draft.UpdateAnswers(1, ProjectSetupStep.StartingPoint, "Renamed Project", "Recorder", ProjectSetupStartKind.Fresh,
            null, null, "1.3", "[]", "{}", "{}", true,
            "{\"mode\":\"ConfigureLater\",\"status\":\"Pending\"}", "{}", Now);

        Assert.Equal(projectId, draft.ProjectId);
        Assert.Equal(programId, draft.InternalProgramId);
        Assert.Equal(releaseId, draft.InitialReleaseId);
        Assert.Equal(2, draft.Version);
        Assert.Equal("SW-01.30", draft.InitialReleaseCanonicalIdentity);
        Assert.True(draft.ReviewRulesAccepted);
        Assert.NotNull(draft.ReviewRulesAcceptanceHash);
    }

    [Fact]
    public void Draft_starts_with_every_feature_and_keeps_a_valid_choice_until_changed()
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test");
        Assert.Null(draft.EnabledFeatures);

        const ProjectFeature prOnly = ProjectFeature.TeamWork | ProjectFeature.ProblemReports | ProjectFeature.Release;
        draft.UpdateAnswers(1, ProjectSetupStep.Features, null, null, null, null, null, null,
            null, null, null, null, null, null, Now, prOnly);
        Assert.Equal(prOnly, draft.EnabledFeatures);
        Assert.Equal(2, draft.Version);

        // A later save without features keeps the choice; nothing at all is a valid choice too.
        draft.UpdateAnswers(2, ProjectSetupStep.Review, "Project", null, null, null, null, null,
            null, null, null, null, null, null, Now);
        Assert.Equal(prOnly, draft.EnabledFeatures);
        draft.UpdateAnswers(3, ProjectSetupStep.Features, null, null, null, null, null, null,
            null, null, null, null, null, null, Now, ProjectFeature.None);
        Assert.Equal(ProjectFeature.None, draft.EnabledFeatures);
    }

    [Theory]
    [InlineData(ProjectFeature.Code, "Code needs Requirements")]
    [InlineData(ProjectFeature.Verification | ProjectFeature.ProblemReports, "Verification needs Requirements")]
    [InlineData((ProjectFeature)128, "unknown feature")]
    public void Draft_refuses_a_feature_set_DEC_136_does_not_allow(ProjectFeature enabled, string refusal)
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test");
        var error = Assert.Throws<DomainException>(() => draft.UpdateAnswers(1, ProjectSetupStep.Features, "Renamed",
            null, null, null, null, null, null, null, null, null, null, null, Now, enabled));
        Assert.Contains(refusal, error.Message);
        // A refused save changes nothing.
        Assert.Null(draft.EnabledFeatures);
        Assert.Equal("", draft.ProjectName);
        Assert.Equal(1, draft.Version);
    }

    [Fact]
    public void Changing_ladder_or_rules_invalidates_prior_acceptance()
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test");
        draft.UpdateAnswers(1, ProjectSetupStep.Review, "Project", "Product", ProjectSetupStartKind.Fresh,
            null, null, "0.01", "[]", "{}", "{}", true, "{\"mode\":\"ConfigureLater\"}", "{}", Now);
        var hash = draft.ReviewRulesAcceptanceHash;

        draft.UpdateAnswers(draft.Version, ProjectSetupStep.Ladder, null, null, null, null, null, null,
            null, "{\"steps\":[]}", null, null, null, null, Now.AddMinutes(1));

        Assert.False(draft.ReviewRulesAccepted);
        Assert.Null(draft.ReviewRulesAcceptanceHash);
        Assert.NotEqual(hash, draft.ReviewRulesAcceptanceHash);
    }

    [Fact]
    public void Finalization_retains_completed_ids_for_lost_response_recovery()
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test");
        draft.UpdateAnswers(1, ProjectSetupStep.Review, "Project", "Product", ProjectSetupStartKind.Fresh,
            null, null, "0.01", "[]", "{}", "{}", true, "{\"mode\":\"ConfigureLater\"}", "{}", Now);
        Assert.True(draft.BeginFinalization(draft.Version, "request-1", Now.AddMinutes(1)));
        var programId = Guid.NewGuid(); var projectId = Guid.NewGuid(); var releaseId = Guid.NewGuid();
        draft.Complete("{\"version\":\"0.01\",\"officialBuildName\":\"SW-00.01\"}",
            programId, projectId, releaseId, Now.AddMinutes(2));

        Assert.Equal(ProjectSetupState.Completed, draft.State);
        Assert.Equal(projectId, draft.CompletedProjectId);
        Assert.False(draft.BeginFinalization(draft.Version, "request-2", Now.AddMinutes(3)));
    }

    [Theory]
    [InlineData("0.01", "SW-00.01")]
    [InlineData("1.02", "SW-01.02")]
    [InlineData("1.3", "SW-01.30")]
    public void Official_identity_uses_current_major_minor_format(string raw, string expected)
        => Assert.Equal(expected, SoftwareBuildIdentifier.FromVersion(raw));

    [Theory]
    [InlineData("1..3")]
    [InlineData("1.3.0")]
    [InlineData("+1.3")]
    [InlineData("001.3")]
    [InlineData("1.003")]
    public void Invalid_version_shapes_fail_closed(string raw)
        => Assert.Throws<DomainException>(() => SoftwareBuildIdentifier.Parse(raw));

    [Fact]
    public void Source_identity_is_a_disjoint_typed_choice()
    {
        var draft = new ProjectSetupDraft(Creator, "owner@example.test");
        Assert.Throws<DomainException>(() => draft.UpdateAnswers(1, ProjectSetupStep.StartingPoint,
            null, null, ProjectSetupStartKind.AeroLinkBaseline, Guid.Empty, Guid.NewGuid(), null,
            null, null, null, null, null, null, Now));
    }
}
