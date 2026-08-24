using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Verification;

namespace AeroLink.Api.Tests;

/// <summary>Test-only future profile. It never writes activation evidence or bypasses production gates.</summary>
internal static class ProcedureEnabledTestPolicy
{
    public static ILadderPolicy Create()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var configuration = ProjectLadderConfiguration.CreateDraft(projectId, now);
        var steps = new List<ProjectLadderStep>();
        foreach (var (level, position) in LegacyLadderPolicy.Instance.OrderedLevels.Select((x, i) => (x, i + 1)))
        {
            var kinds = level == RequirementLevel.System
                ? new[] { VerificationArtifactKind.Procedure }
                : new[] { VerificationArtifactKind.Case, VerificationArtifactKind.Procedure };
            var step = new ProjectLadderStep(configuration.Id, projectId, level, position,
                LegacyLadderPolicy.Instance.Definition(level).Capabilities, now, kinds);
            configuration.Steps.Add(step);
            steps.Add(step);
        }
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[0].Id, steps[1].Id, now));
        configuration.AllowedUpstream.Add(new ProjectLadderAllowedUpstream(
            configuration.Id, projectId, steps[1].Id, steps[2].Id, now));
        return new ResolvedProjectLadderPolicy(ProjectLadderResolver.Resolve(configuration));
    }
}
