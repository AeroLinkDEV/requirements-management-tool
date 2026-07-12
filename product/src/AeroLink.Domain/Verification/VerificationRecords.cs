using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

public enum TestProcedureState { Draft, Approved, Retired }
public enum TestOutcome { Pass, Fail, Blocked }
public enum TestProcedureLevel { System, HighLevel, LowLevel }

public sealed class TestProcedure
{
    private TestProcedure() { }
    public TestProcedure(Guid projectId, string baseNumber, string title, string ownerId, DateTimeOffset now,
        TestProcedureLevel level = TestProcedureLevel.HighLevel)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("A test procedure title is required.");
        Id = Guid.NewGuid(); ProjectId = projectId; BaseNumber = ArtifactNumber.ValidateBase(baseNumber);
        Title = title.Trim(); OwnerId = ownerId.Trim(); CreatedAt = now; Level = level;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BaseNumber { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string OwnerId { get; private set; } = string.Empty;
    public TestProcedureLevel Level { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class TestProcedureRevision
{
    private TestProcedureRevision() { }
    public TestProcedureRevision(Guid procedureId, int revision, string objective, string preconditions,
        string steps, string expectedResult, TestProcedureState state, string authorId, DateTimeOffset now)
    {
        if (revision < 0) throw new DomainException("Test procedure revision cannot be negative.");
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(steps) || string.IsNullOrWhiteSpace(expectedResult))
            throw new DomainException("Objective, steps, and expected result are required.");
        Id = Guid.NewGuid(); ProcedureId = procedureId; Revision = revision; Objective = objective.Trim();
        Preconditions = preconditions.Trim(); Steps = steps.Trim(); ExpectedResult = expectedResult.Trim();
        State = state; AuthorId = authorId.Trim(); CreatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid ProcedureId { get; private set; }
    public int Revision { get; private set; }
    public string Objective { get; private set; } = string.Empty;
    public string Preconditions { get; private set; } = string.Empty;
    public string Steps { get; private set; } = string.Empty;
    public string ExpectedResult { get; private set; } = string.Empty;
    public TestProcedureState State { get; private set; }
    public string AuthorId { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class TestRequirementCoverage
{
    private TestRequirementCoverage() { }
    public TestRequirementCoverage(Guid procedureRevisionId, Guid requirementRevisionId)
    { Id = Guid.NewGuid(); ProcedureRevisionId = procedureRevisionId; RequirementRevisionId = requirementRevisionId; }
    public Guid Id { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
    public Guid RequirementRevisionId { get; private set; }
}

public sealed class TestExecution
{
    private TestExecution() { }
    public TestExecution(Guid projectId, Guid procedureRevisionId, Guid? softwareBuildId, Guid? retestOfExecutionId,
        TestOutcome outcome, string executedBy, string configuration, string determination, string evidenceReference,
        DateTimeOffset executedAt, DateTimeOffset recordedAt)
    {
        if (string.IsNullOrWhiteSpace(executedBy)) throw new DomainException("The person making the result determination is required.");
        if (string.IsNullOrWhiteSpace(determination)) throw new DomainException("A human result determination is required.");
        if (outcome != TestOutcome.Blocked && string.IsNullOrWhiteSpace(evidenceReference))
            throw new DomainException("Pass and Fail results require an evidence reference.");
        Id = Guid.NewGuid(); ProjectId = projectId; ProcedureRevisionId = procedureRevisionId;
        SoftwareBuildId = softwareBuildId; RetestOfExecutionId = retestOfExecutionId; Outcome = outcome;
        ExecutedBy = executedBy.Trim(); Configuration = configuration.Trim(); Determination = determination.Trim();
        EvidenceReference = evidenceReference.Trim(); ExecutedAt = executedAt; RecordedAt = recordedAt;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid ProcedureRevisionId { get; private set; }
    public Guid? SoftwareBuildId { get; private set; }
    public Guid? RetestOfExecutionId { get; private set; }
    public TestOutcome Outcome { get; private set; }
    public string ExecutedBy { get; private set; } = string.Empty;
    public string Configuration { get; private set; } = string.Empty;
    public string Determination { get; private set; } = string.Empty;
    public string EvidenceReference { get; private set; } = string.Empty;
    public DateTimeOffset ExecutedAt { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}
