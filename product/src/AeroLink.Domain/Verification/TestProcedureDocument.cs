using AeroLink.Domain.Common;

namespace AeroLink.Domain.Verification;

/// <summary>What a node in a test procedure document holds: a heading, or one controlled procedure.</summary>
public enum TestProcedureDocumentNodeType { Section, Procedure }

/// <summary>
/// The controlled document a project's test procedures are written into — SYSTD, HLRTD, LLRTD.
///
/// Procedures had no container. A requirement is authored into a requirements document (SYSRD, HLRD, LLRD)
/// and its place in that document is part of what it is; a procedure existed only as a loose artifact
/// belonging to a project and a level, so "which document is this procedure in, and where in it" had no
/// answer, and the Test Procedure Explorer had nothing to group by.
///
/// Deliberately the same shape as <c>RequirementSpecification</c>: the verification side of this product is
/// meant to read like the requirements side with different artifacts, not like a different product.
/// </summary>
public sealed class TestProcedureDocument
{
    private TestProcedureDocument() { }

    public TestProcedureDocument(Guid projectId, string documentNumber, string title, TestProcedureLevel level,
        string description, string actor, DateTimeOffset now)
    {
        if (projectId == Guid.Empty) throw new DomainException("A verification artifact document belongs to a Project.");
        if (!Enum.IsDefined(level)) throw new DomainException("A verification artifact document requires a known level.");
        Id = Guid.NewGuid();
        ProjectId = projectId;
        DocumentNumber = Required(documentNumber, "document number");
        Title = Required(title, "title");
        Level = level;
        Description = description?.Trim() ?? "";
        CreatedBy = Required(actor, "author");
        CreatedAt = now;
        UpdatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    /// <summary>SYSTD-000001, HLRTD-000001, LLRTD-000001 — the verification counterparts of SYSRD, HLRD, LLRD.</summary>
    public string DocumentNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public TestProcedureLevel Level { get; private set; }
    public string Description { get; private set; } = "";
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; } = 1;

    public void UpdateDraft(string title, string description, string actor, DateTimeOffset now)
    {
        Title = Required(title, "title");
        Description = description?.Trim() ?? "";
        CreatedBy = string.IsNullOrWhiteSpace(actor) ? CreatedBy : actor.Trim();
        Touch(now);
    }

    /// <summary>Records that the document's structure moved, without claiming its content changed.</summary>
    public void RecordStructureUpdate(DateTimeOffset now) => Touch(now);

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new DomainException($"A verification artifact document requires a {name}.") : value.Trim();
}

/// <summary>
/// One place in a test procedure document: a section heading, or a procedure sitting inside one.
///
/// The mirror of <c>SpecificationNode</c>, and for the same reason — a document is a structure somebody
/// arranged, not an accident of the order things were created in. Membership lives here rather than as a
/// column on the procedure, exactly as a requirement's membership lives on its node: one place decides both
/// which document holds an artifact and where in that document it sits, so the two can never disagree.
/// </summary>
public sealed class TestProcedureDocumentNode
{
    private TestProcedureDocumentNode() { }

    public TestProcedureDocumentNode(Guid documentId, Guid? parentId, int position,
        TestProcedureDocumentNodeType type, string heading, Guid? procedureId, string actor, DateTimeOffset now)
    {
        if (documentId == Guid.Empty) throw new DomainException("A node belongs to a verification artifact document.");
        if (type == TestProcedureDocumentNodeType.Procedure && procedureId is null)
            throw new DomainException("Artifact nodes need a verification artifact.");
        if (type == TestProcedureDocumentNodeType.Section && string.IsNullOrWhiteSpace(heading))
            throw new DomainException("Section nodes need a heading.");
        Id = Guid.NewGuid();
        DocumentId = documentId;
        ParentId = parentId;
        Position = position;
        Type = type;
        Heading = heading?.Trim() ?? "";
        ProcedureId = procedureId;
        CreatedBy = actor?.Trim() ?? "";
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid? ParentId { get; private set; }
    public int Position { get; private set; }
    public TestProcedureDocumentNodeType Type { get; private set; }
    public string Heading { get; private set; } = "";
    /// <summary>The procedure this node places. Null on a section.</summary>
    public Guid? ProcedureId { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public void UpdateDraft(Guid? parentId, int position, string heading, DateTimeOffset now)
    {
        if (Type == TestProcedureDocumentNodeType.Section && string.IsNullOrWhiteSpace(heading))
            throw new DomainException("Section nodes need a heading.");
        if (parentId == Id) throw new DomainException("A node cannot be its own parent.");
        ParentId = parentId;
        Position = position;
        Heading = heading?.Trim() ?? Heading;
        _ = now;
    }
}
