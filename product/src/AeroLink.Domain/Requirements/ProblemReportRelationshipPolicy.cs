namespace AeroLink.Domain.Requirements;

public enum ProblemReportRelationshipProducer
{
    TargetBuildWorkflow,
    FailureCreationWorkflow,
    ChangeRequestWorkflow,
    TestChangeRequestWorkflow,
    ResolutionVerificationWorkflow,
    DispositionWorkflow,
    RelatedProblemReportWorkflow,
    GenericContextWorkflow,
}

/// <summary>
/// Canonical authority for Problem Report relationship semantics. Controlled relationships may be
/// produced only by their named workflow; the generic endpoint is limited to explicitly neutral context.
/// </summary>
public static class ProblemReportRelationshipPolicy
{
    public const string BuildScope = "BuildScope";
    public const string OriginatingFailure = "OriginatingFailure";
    public const string ProposedCorrectiveAction = "ProposedCorrectiveAction";
    public const string ApprovedCorrectiveAction = "ApprovedCorrectiveAction";
    public const string VerificationForProblem = "VerificationForProblem";
    public const string ResolutionVerification = "ResolutionVerification";
    public const string DuplicateOf = "DuplicateOf";

    /// <summary>
    /// Two Problem Reports that belong together — a shared cause, a shared correction, a fix that cannot
    /// land without the other.
    ///
    /// Deliberately symmetric and deliberately unlabelled beyond "related". A directed relationship
    /// asserts which report is the parent, and that is a judgement nobody has been asked to make yet;
    /// naming a kind now would mean inventing an answer and storing it. Directed kinds can arrive later
    /// as additional definitions here, and the rows written today stay true when they do — a related pair
    /// is still a related pair once somebody can also say one drives the other.
    ///
    /// Controlled, and produced only by its own workflow: the generic links endpoint must not be able to
    /// forge one, which is exactly what DuplicateOf beside it is protected from.
    /// </summary>
    public const string RelatedProblemReport = "RelatedProblemReport";
    public const string AffectedRequirement = "AffectedRequirement";

    public static IReadOnlyList<ProblemReportRelationshipDefinition> Definitions { get; } =
    [
        new(BuildScope, "Release", ProblemReportRelationshipProducer.TargetBuildWorkflow, true),
        new(OriginatingFailure, "TestExecution", ProblemReportRelationshipProducer.FailureCreationWorkflow, true),
        new(ProposedCorrectiveAction, "ChangeRequest", ProblemReportRelationshipProducer.ChangeRequestWorkflow, true),
        new(ApprovedCorrectiveAction, "ChangeRequest", ProblemReportRelationshipProducer.ChangeRequestWorkflow, true),
        new(VerificationForProblem, "TestChangeRequest", ProblemReportRelationshipProducer.TestChangeRequestWorkflow, true),
        new(ResolutionVerification, "TestExecution", ProblemReportRelationshipProducer.ResolutionVerificationWorkflow, true),
        new(DuplicateOf, "ProblemReport", ProblemReportRelationshipProducer.DispositionWorkflow, true),
        new(RelatedProblemReport, "ProblemReport", ProblemReportRelationshipProducer.RelatedProblemReportWorkflow, true),
        new(AffectedRequirement, "Requirement", ProblemReportRelationshipProducer.GenericContextWorkflow, false),
    ];

    public static ProblemReportRelationshipDefinition? Find(string? relationship) =>
        Definitions.SingleOrDefault(definition => string.Equals(
            definition.Relationship, relationship?.Trim(), StringComparison.Ordinal));

    public static bool IsGenericContextPair(string artifactType, string? relationship)
    {
        var definition = Find(relationship);
        return definition is { IsControlled: false }
            && string.Equals(definition.ArtifactType, artifactType, StringComparison.Ordinal);
    }

    public static bool Matches(string relationship, string artifactType) =>
        Find(relationship) is { } definition
        && string.Equals(definition.ArtifactType, artifactType, StringComparison.Ordinal);

    public static ProblemReportLink CreateControlled(Guid problemReportId, string artifactType, Guid artifactId,
        string relationship, ProblemReportRelationshipProducer producer, string actor, DateTimeOffset now)
    {
        var definition = Find(relationship);
        if (definition is not { IsControlled: true }
            || definition.Producer != producer
            || !string.Equals(definition.ArtifactType, artifactType, StringComparison.Ordinal))
            throw new AeroLink.Domain.Common.DomainException("The controlled Problem Report relationship does not belong to this workflow and artifact type.");
        return new ProblemReportLink(problemReportId, artifactType, artifactId, definition.Relationship, actor, now);
    }

    public static ProblemReportLink CreateGenericContext(Guid problemReportId, string artifactType, Guid artifactId,
        string relationship, string actor, DateTimeOffset now)
    {
        if (!IsGenericContextPair(artifactType, relationship))
            throw new AeroLink.Domain.Common.DomainException("The Problem Report relationship is not permitted as generic context.");
        return new ProblemReportLink(problemReportId, artifactType, artifactId, relationship.Trim(), actor, now);
    }
}

public sealed record ProblemReportRelationshipDefinition(
    string Relationship, string ArtifactType, ProblemReportRelationshipProducer Producer, bool IsControlled);
