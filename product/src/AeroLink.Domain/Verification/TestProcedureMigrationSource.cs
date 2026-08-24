using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>
/// Typed, durable source-of-generation record for the governed #726 Procedure execution cutover: exactly one
/// row per exact source Case revision, naming the generated Procedure artifact/revision it produced.
///
/// This is the structured Case→Procedure provenance that survives independently of executable links, so a
/// historical revision preserved as a non-executable mirror still has an exact, queryable source relation —
/// never only prose in an audit event. The unique <see cref="SourceCaseRevisionId"/> makes regeneration
/// idempotent across crashes and reruns.
/// </summary>
public sealed class TestProcedureMigrationSource
{
    private TestProcedureMigrationSource() { }

    public TestProcedureMigrationSource(Guid projectId, Guid sourceCaseRevisionId,
        Guid generatedProcedureArtifactId, Guid generatedProcedureRevisionId)
    {
        if (projectId == Guid.Empty || sourceCaseRevisionId == Guid.Empty
            || generatedProcedureArtifactId == Guid.Empty || generatedProcedureRevisionId == Guid.Empty)
            throw new DomainException(
                "A Procedure migration source requires a project, exact source Case revision, and generated Procedure artifact/revision identities.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        SourceCaseRevisionId = sourceCaseRevisionId;
        GeneratedProcedureArtifactId = generatedProcedureArtifactId;
        GeneratedProcedureRevisionId = generatedProcedureRevisionId;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid SourceCaseRevisionId { get; private set; }
    public Guid GeneratedProcedureArtifactId { get; private set; }
    public Guid GeneratedProcedureRevisionId { get; private set; }
}
