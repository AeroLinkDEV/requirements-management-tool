using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Documents;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Notifications;

public enum NotificationSourceFamily { CRStep, TCRStep, DocStep, CRAudit, DocEvent, RequirementAssignment, DocumentAssignment, DiagnosticOperation }

/// <summary>The immutable original request. Current eligibility is a separate query.</summary>
public sealed class NotificationContext
{
    private NotificationContext() { }
    private NotificationContext(UserNotification notice, NotificationSourceFamily family, Guid sourceId,
        string identifier, int revision, string stage, string snapshotHash)
    {
        NotificationId = notice.Id; ProjectId = notice.ProjectId; Recipient = notice.Recipient;
        EventType = notice.Type; SourceFamily = family; SourceId = sourceId;
        RecordId = notice.ArtifactId; Identifier = identifier; Revision = revision;
        Stage = stage; SnapshotHash = snapshotHash; SourceSchemaVersion = 1;
    }
    public Guid NotificationId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Recipient { get; private set; } = "";
    public string EventType { get; private set; } = "";
    public NotificationSourceFamily SourceFamily { get; private set; }
    public Guid SourceId { get; private set; }
    public int SourceSchemaVersion { get; private set; }
    public Guid? RecordId { get; private set; }
    public Guid? ChangeRequestId { get; private set; }
    public Guid? TestChangeReviewId { get; private set; }
    public Guid? ReviewCycleId { get; private set; }
    public Guid? ApprovalStepId { get; private set; }
    public Guid? AuditEventId { get; private set; }
    public Guid? DocumentId { get; private set; }
    public Guid? DocumentRevisionId { get; private set; }
    public Guid? DocumentStepId { get; private set; }
    public Guid? DocumentEventId { get; private set; }
    public Guid? DocumentAssignmentId { get; private set; }
    public Guid? ArtifactAssignmentId { get; private set; }
    public Guid? OperationId { get; private set; }
    public int Cycle { get; private set; }
    public int Revision { get; private set; }
    public long SourceVersion { get; private set; }
    public string Identifier { get; private set; } = "";
    public string Stage { get; private set; } = "";
    public string SnapshotHash { get; private set; } = "";

    public static NotificationContext ChangeRequestStep(UserNotification notice, SystemChangeRequest record,
        ReviewCycle cycle, ApprovalStep step)
    {
        Check(notice, record.ProjectId, record.Id, step.ApproverId);
        CheckStep(cycle, step);
        if (cycle.ChangeRequestId != record.Id) throw new DomainException("Notification cycle does not belong to its change request.");
        return new(notice, NotificationSourceFamily.CRStep, step.Id, record.DisplayNumber, record.Revision,
            step.StageKind.ToString(), cycle.SnapshotHash)
        { ChangeRequestId = record.Id, ReviewCycleId = cycle.Id, ApprovalStepId = step.Id, Cycle = cycle.Sequence };
    }
    public static NotificationContext TestChangeStep(UserNotification notice, TestChangeReview record,
        ReviewCycle cycle, ApprovalStep step)
    {
        Check(notice, record.ProjectId, record.Id, step.ApproverId); CheckStep(cycle, step);
        if (cycle.TestChangeReviewId != record.Id) throw new DomainException("Notification cycle does not belong to its test change request.");
        return new(notice, NotificationSourceFamily.TCRStep, step.Id, record.DisplayNumber, record.Revision,
            step.StageKind.ToString(), cycle.SnapshotHash)
        { TestChangeReviewId = record.Id, ReviewCycleId = cycle.Id, ApprovalStepId = step.Id, Cycle = cycle.Sequence };
    }
    public static NotificationContext DocumentStep(UserNotification notice, ManagedDocument document,
        ManagedDocumentRevision revision, ManagedDocumentReviewStep step)
    {
        Check(notice, document.ProjectId, document.Id, step.ApproverId);
        if (revision.DocumentId != document.Id || step.RevisionId != revision.Id || step.Cycle != revision.CurrentReviewCycle)
            throw new DomainException("Notification step does not belong to its document revision and round.");
        return new(notice, NotificationSourceFamily.DocStep, step.Id, $"{document.DocumentNumber}.{revision.Revision:D2}",
            revision.Revision, step.Kind.ToString(), revision.SnapshotHash)
        { DocumentId = document.Id, DocumentRevisionId = revision.Id, DocumentStepId = step.Id, Cycle = step.Cycle };
    }
    public static NotificationContext RequirementAssignment(UserNotification notice, RequirementArtifact requirement, ArtifactAssignment assignment)
    {
        Check(notice, requirement.ProjectId, requirement.Id, assignment.AssignedTo);
        if (assignment.ProjectId != requirement.ProjectId || assignment.ArtifactId != requirement.Id || assignment.ArtifactType != "Requirement")
            throw new DomainException("Notification assignment does not belong to its requirement.");
        return new(notice, NotificationSourceFamily.RequirementAssignment, assignment.Id, requirement.BaseNumber,
            0, "Assignment", "") { ArtifactAssignmentId = assignment.Id, SourceVersion = assignment.Version };
    }
    public static NotificationContext DocumentAssignment(UserNotification notice, ManagedDocument document,
        ManagedDocumentRevision? revision, ManagedDocumentAssignment assignment)
    {
        Check(notice, document.ProjectId, document.Id, assignment.NewAssigneeId);
        if (assignment.DocumentId != document.Id || assignment.RevisionId != revision?.Id || revision is not null && revision.DocumentId != document.Id)
            throw new DomainException("Notification ownership assignment does not belong to its document revision.");
        return new(notice, NotificationSourceFamily.DocumentAssignment, assignment.Id, document.DocumentNumber,
            revision?.Revision ?? 0, assignment.AssignmentType, revision?.SnapshotHash ?? "")
        { DocumentId = document.Id, DocumentRevisionId = revision?.Id, DocumentAssignmentId = assignment.Id };
    }
    public static NotificationContext ChangesRequested(UserNotification notice, SystemChangeRequest record,
        ReviewCycle cycle, ApprovalStep step, AuditEvent audit)
    {
        Check(notice, record.ProjectId, record.Id, record.AuthorId); CheckStep(cycle, step);
        if (cycle.ChangeRequestId != record.Id || !record.AuditEvents.Any(x => x.Id == audit.Id) || audit.EventType != "ChangesRequested")
            throw new DomainException("Notification return event does not belong to its original review.");
        return new(notice, NotificationSourceFamily.CRAudit, audit.Id, record.DisplayNumber, record.Revision,
            "Changes requested", cycle.SnapshotHash)
        { ChangeRequestId = record.Id, ReviewCycleId = cycle.Id, ApprovalStepId = step.Id, AuditEventId = audit.Id, Cycle = cycle.Sequence };
    }
    public static NotificationContext Diagnostic(UserNotification notice, NotificationOperation operation)
    {
        if (notice.Type != "NotificationTransportTest" || notice.Recipient != operation.Actor || operation.Family != "TransportTest")
            throw new DomainException("A diagnostic context must belong to its original administrator command.");
        return new(notice, NotificationSourceFamily.DiagnosticOperation, operation.Id, "Transport diagnostic", 0,
            "Synthetic diagnostic", "") { OperationId = operation.Id };
    }
    public static NotificationContext DocumentReturned(UserNotification notice, ManagedDocument document,
        ManagedDocumentRevision revision, ManagedDocumentReviewStep step, ManagedDocumentEvent returned)
    {
        Check(notice, document.ProjectId, document.Id, revision.ResponsibleOwnerId);
        if (revision.DocumentId != document.Id || step.RevisionId != revision.Id || returned.DocumentId != document.Id || returned.EventType != "DocumentReturned")
            throw new DomainException("Notification return does not belong to its original document round.");
        return new(notice, NotificationSourceFamily.DocEvent, returned.Id, $"{document.DocumentNumber}.{revision.Revision:D2}",
            revision.Revision, "Document returned", revision.SnapshotHash)
        { DocumentId = document.Id, DocumentRevisionId = revision.Id, DocumentStepId = step.Id, DocumentEventId = returned.Id, Cycle = step.Cycle };
    }
    private static void Check(UserNotification notice, Guid project, Guid record, string recipient)
    {
        if (notice.ProjectId != project || notice.ArtifactId != record || !string.Equals(notice.Recipient, recipient, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Notification context does not match its project, record and original recipient.");
    }
    private static void CheckStep(ReviewCycle cycle, ApprovalStep step)
    {
        if (step.ReviewCycleId != cycle.Id || !cycle.Steps.Any(x => x.Id == step.Id))
            throw new DomainException("Notification step does not belong to its original cycle.");
    }
}
