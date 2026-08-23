using AeroLink.Domain.Common;
using AeroLink.Domain.Traceability;

namespace AeroLink.Domain.Tests;

public sealed class ExactLinkSuspectLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Raised_acknowledged_and_discharge_preserve_attribution_and_events()
    {
        var projectId = Guid.NewGuid(); var linkId = Guid.NewGuid(); var cause = Guid.NewGuid();
        var lifecycle = ExactLinkSuspectLifecycle.Raise(projectId, ExactLinkKind.RequirementTrace, linkId,
            ExactLinkLifecycleCauseKind.InternalRequirementRevision, cause, null, "engineer", "Upstream wording changed.", Now);

        lifecycle.Acknowledge("configuration.manager", "Impact is being assessed.", Now.AddMinutes(1));
        lifecycle.RecordResolution(ExactLinkResolutionOutcome.ExistingDownstreamRevisionRemainsValid,
            "configuration.manager", "The downstream revision remains valid.", Now.AddMinutes(2));

        Assert.Equal(ExactLinkLifecycleState.Closed, lifecycle.State);
        Assert.Equal("engineer", lifecycle.RaisedBy);
        Assert.Equal("configuration.manager", lifecycle.AcknowledgedBy);
        Assert.Equal(ExactLinkResolutionOutcome.ExistingDownstreamRevisionRemainsValid, lifecycle.Outcome);
        Assert.Equal([ExactLinkLifecycleEventType.Raised, ExactLinkLifecycleEventType.Acknowledged, ExactLinkLifecycleEventType.ResolutionRecorded],
            lifecycle.Events.Select(x => x.EventType));
        Assert.All(lifecycle.Events, x => Assert.Equal(cause, x.CauseRequirementRevisionId));
    }

    [Fact]
    public void Change_required_remains_blocking_and_requires_rationale()
    {
        var lifecycle = ExactLinkSuspectLifecycle.Raise(Guid.NewGuid(), ExactLinkKind.RequirementTrace, Guid.NewGuid(),
            ExactLinkLifecycleCauseKind.ExternalBaselineImport, null, Guid.NewGuid(), "engineer", "Customer package changed.", Now);
        lifecycle.RecordResolution(ExactLinkResolutionOutcome.DownstreamChangeRequiredNotYetApproved,
            "engineer", "A downstream change is required but not approved.", Now.AddMinutes(1));
        Assert.Equal(ExactLinkLifecycleState.ChangeRequired, lifecycle.State);
        Assert.Throws<DomainException>(() => lifecycle.Acknowledge("engineer", "Too late.", Now.AddMinutes(2)));
        Assert.Throws<DomainException>(() => lifecycle.RecordResolution(ExactLinkResolutionOutcome.NoDownstreamChangeRequired,
            "engineer", "", Now.AddMinutes(2)));
    }

    [Fact]
    public void Case_procedure_kind_uses_the_shared_lifecycle_seam_without_raising_assessment_events()
    {
        var lifecycle = ExactLinkSuspectLifecycle.Raise(Guid.NewGuid(), ExactLinkKind.CaseProcedure, Guid.NewGuid(),
            ExactLinkLifecycleCauseKind.InternalRequirementRevision, Guid.NewGuid(), null,
            "engineer", "The exact Case parent revision changed.", Now);

        Assert.Equal(ExactLinkKind.CaseProcedure, lifecycle.LinkKind);
        Assert.Single(lifecycle.Events);
        Assert.Equal(ExactLinkLifecycleEventType.Raised, lifecycle.Events.Single().EventType);
    }

    [Fact]
    public void Cause_and_link_kind_validation_fail_closed()
    {
        Assert.Throws<DomainException>(() => ExactLinkSuspectLifecycle.Raise(Guid.NewGuid(), (ExactLinkKind)99,
            Guid.NewGuid(), ExactLinkLifecycleCauseKind.InternalRequirementRevision, Guid.NewGuid(), null,
            "engineer", "Reason", Now));
        Assert.Throws<DomainException>(() => ExactLinkSuspectLifecycle.Raise(Guid.NewGuid(), ExactLinkKind.RequirementTrace,
            Guid.NewGuid(), (ExactLinkLifecycleCauseKind)99, null, Guid.NewGuid(), "engineer", "Reason", Now));
        Assert.Throws<DomainException>(() => ExactLinkSuspectLifecycle.Raise(Guid.Empty, ExactLinkKind.RequirementTrace,
            Guid.NewGuid(), ExactLinkLifecycleCauseKind.InternalRequirementRevision, Guid.NewGuid(), null,
            "engineer", "Reason", Now));
        Assert.Throws<DomainException>(() => ExactLinkSuspectLifecycle.Raise(Guid.NewGuid(), ExactLinkKind.RequirementTrace,
            Guid.NewGuid(), ExactLinkLifecycleCauseKind.InternalRequirementRevision, Guid.NewGuid(), Guid.NewGuid(),
            "engineer", "Reason", Now));
    }
}
