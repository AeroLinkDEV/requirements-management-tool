using AeroLink.Domain.Common;

namespace AeroLink.Domain.Requirements;

public enum DocumentTemplateState { Draft, InWork, Approved }
public enum ProblemReportState { Draft, Open, Investigating, ResolutionProposed, Closed }
public enum ConfigurationChangeSetState { Draft, InWork, Conflict, Closed }

public sealed class DocumentTemplate
{
    private DocumentTemplate() { }
    public DocumentTemplate(Guid projectId, string templateNumber, string title, string body, string ownerId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; TemplateNumber = Required(templateNumber, "A document-template number is required.");
        Title = Required(title, "A document-template title is required."); Body = body?.Trim() ?? "";
        OwnerId = Required(ownerId, "A document-template owner is required."); State = DocumentTemplateState.Draft;
        CreatedAt = UpdatedAt = now; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string TemplateNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Body { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public DocumentTemplateState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void UpdateDraft(string title, string body, string ownerId, DateTimeOffset now)
    { Title = Required(title, "A document-template title is required."); Body = body?.Trim() ?? ""; OwnerId = Required(ownerId, "A document-template owner is required."); UpdatedAt = now; Version++; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

public sealed class ProblemReport
{
    private ProblemReport() { }
    public ProblemReport(Guid projectId, string reportNumber, string title, string problem, string analysis, string reportedBy, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; ReportNumber = Required(reportNumber, "A problem-report number is required.");
        Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required.");
        Analysis = analysis?.Trim() ?? ""; ReportedBy = Required(reportedBy, "A problem-report owner is required.");
        State = ProblemReportState.Draft; CreatedAt = UpdatedAt = now; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ReportNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Problem { get; private set; } = "";
    public string Analysis { get; private set; } = "";
    public string ReportedBy { get; private set; } = "";
    public ProblemReportState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void UpdateDraft(string title, string problem, string analysis, DateTimeOffset now)
    { Title = Required(title, "A problem-report title is required."); Problem = Required(problem, "A problem statement is required."); Analysis = analysis?.Trim() ?? ""; UpdatedAt = now; Version++; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}

public sealed class ConfigurationChangeSet
{
    private ConfigurationChangeSet() { }
    public ConfigurationChangeSet(Guid projectId, string changeSetNumber, string title, string description, string ownerId, DateTimeOffset now)
    {
        Id = Guid.NewGuid(); ProjectId = projectId; ChangeSetNumber = Required(changeSetNumber, "A configuration change-set number is required.");
        Title = Required(title, "A configuration change-set title is required."); Description = description?.Trim() ?? "";
        OwnerId = Required(ownerId, "A configuration change-set owner is required."); State = ConfigurationChangeSetState.Draft;
        CreatedAt = UpdatedAt = now; Version = 1;
    }
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string ChangeSetNumber { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string OwnerId { get; private set; } = "";
    public ConfigurationChangeSetState State { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public long Version { get; private set; }
    public void UpdateDraft(string title, string description, string ownerId, DateTimeOffset now)
    { Title = Required(title, "A configuration change-set title is required."); Description = description?.Trim() ?? ""; OwnerId = Required(ownerId, "A configuration change-set owner is required."); UpdatedAt = now; Version++; }
    private static string Required(string? value, string error) => string.IsNullOrWhiteSpace(value) ? throw new DomainException(error) : value.Trim();
}
