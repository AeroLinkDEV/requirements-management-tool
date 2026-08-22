using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Verification;

namespace AeroLink.Infrastructure.Tests;

internal static class VerificationConsumerTestData
{
    private static readonly VerificationArtifactKey[] CurrentKeys =
    [
        new(VerificationDiscipline.System, VerificationArtifactKind.Procedure),
        new(VerificationDiscipline.HighLevelSoftware, VerificationArtifactKind.Case),
        new(VerificationDiscipline.LowLevelSoftware, VerificationArtifactKind.Case),
    ];

    public static IReadOnlyList<IVerificationArtifactConsumerRegistration> Typed(
        IEnumerable<ILadderConsumerRegistration> consumers) => consumers.Select(Typed).ToArray();

    public static VerificationArtifactConsumerRegistration Typed(ILadderConsumerRegistration registration) =>
        registration.Id switch
        {
            "verification.procedure-level" => new(registration.Id, registration.Description, CurrentKeys,
                VerificationArtifactCapability.Identity | VerificationArtifactCapability.Header
                | VerificationArtifactCapability.Revision | VerificationArtifactCapability.Lifecycle),
            "verification.test-change-workflow" => new(registration.Id, registration.Description, CurrentKeys,
                VerificationArtifactCapability.ChangeReview),
            "verification.coverage" => new(registration.Id, registration.Description, CurrentKeys,
                VerificationArtifactCapability.Coverage),
            "baseline.controlled-documents" => new(registration.Id, registration.Description, CurrentKeys,
                VerificationArtifactCapability.ControlledDocument),
            "release.readiness" => new(registration.Id, registration.Description, CurrentKeys,
                VerificationArtifactCapability.Execution),
            _ => new(registration.Id, registration.Description, [], VerificationArtifactCapability.None),
        };
}
