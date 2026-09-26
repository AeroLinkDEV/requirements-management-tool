using AeroLink.Domain.Programs;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Finalization's feature rule for inherited starts (#1113). Inception materializes requirements, verification
/// procedures and an inception baseline, and the save boundary refuses records for a feature that is off, so an
/// inherited start keeps those three; a fresh start may switch them off.
/// </summary>
public sealed class ProjectSetupFeatureValidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    private static ProjectSetupDraft Draft(ProjectSetupStartKind kind, ProjectFeature? features)
    {
        var draft = new ProjectSetupDraft(Guid.NewGuid(), "owner@example.test", "Inherited");
        draft.UpdateAnswers(1, ProjectSetupStep.Features, null, null, kind,
            kind == ProjectSetupStartKind.AeroLinkBaseline ? Guid.NewGuid() : null,
            kind == ProjectSetupStartKind.ExternalBaseline ? Guid.NewGuid() : null,
            null, null, null, null, null, null, null, Now, features);
        return draft;
    }

    [Theory]
    [InlineData(ProjectSetupStartKind.AeroLinkBaseline, ProjectFeature.Verification)]
    [InlineData(ProjectSetupStartKind.AeroLinkBaseline, ProjectFeature.Release)]
    [InlineData(ProjectSetupStartKind.ExternalBaseline, ProjectFeature.Release)]
    public void An_inherited_start_keeps_requirements_verification_and_release(ProjectSetupStartKind kind, ProjectFeature switchedOff)
    {
        var error = Assert.Throws<ProjectSetupInvalidException>(() =>
            ProjectSetupService.ValidateFeatures(Draft(kind, ProjectFeatures.All & ~switchedOff)));
        Assert.Contains("Requirements, Verification and Release stay on", error.Message);
    }

    [Fact]
    public void Inherited_starts_may_switch_off_the_other_features_and_fresh_starts_may_switch_off_any()
    {
        const ProjectFeature core = ProjectFeature.Requirements | ProjectFeature.Verification | ProjectFeature.Release;
        ProjectSetupService.ValidateFeatures(Draft(ProjectSetupStartKind.AeroLinkBaseline, core));
        ProjectSetupService.ValidateFeatures(Draft(ProjectSetupStartKind.ExternalBaseline, core | ProjectFeature.Code));
        ProjectSetupService.ValidateFeatures(Draft(ProjectSetupStartKind.Fresh, ProjectFeature.ProblemReports));
        ProjectSetupService.ValidateFeatures(Draft(ProjectSetupStartKind.Fresh, ProjectFeature.None));
        // Unchosen means every feature, which every start accepts.
        ProjectSetupService.ValidateFeatures(Draft(ProjectSetupStartKind.AeroLinkBaseline, null));
    }
}
