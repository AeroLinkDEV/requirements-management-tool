using AeroLink.Domain.Common;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class VerificationRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Draft_procedure_requires_independent_approval_before_execution_use()
    {
        var revision = new TestProcedureRevision(Guid.NewGuid(), 0, "Verify behavior", "Configured rig",
            "Execute the behavior", "The approved result is observed", TestProcedureState.Draft, "test.author", Now);

        Assert.Throws<DomainException>(() => revision.Approve("test.author"));
        Assert.Equal(TestProcedureState.Draft, revision.State);

        revision.Approve("verification.approver");
        Assert.Equal(TestProcedureState.Approved, revision.State);
        Assert.Throws<DomainException>(() => revision.Approve("another.approver"));
    }
}
