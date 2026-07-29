namespace AeroLink.Domain.Programs;

/// <summary>
/// One reconciliation step applied to an existing showcase Program.
///
/// The seeder returned early whenever a Program with the showcase code already existed, so every invariant
/// added after a database was first seeded simply never reached it. A live installation ended up with two
/// approved FMS 1.6 change requests and an empty verification-impact queue — the one state the product
/// says is impossible — because the code that raises those items shipped after that database was created.
///
/// Recording each step by key rather than tracking a single version number means an interrupted upgrade
/// resumes at the step it stopped on, and a step added later applies on its own without renumbering
/// anything. The row is the evidence that a step ran; steps are ordered and idempotent, so re-running is
/// always safe and never duplicates.
/// </summary>
public sealed class ShowcaseUpgradeStep
{
    private ShowcaseUpgradeStep() { }

    public ShowcaseUpgradeStep(Guid programId, string stepKey, string detail, DateTimeOffset appliedAt)
    {
        Id = Guid.NewGuid();
        ProgramId = programId;
        StepKey = stepKey.Trim();
        Detail = detail.Trim();
        AppliedAt = appliedAt;
    }

    public Guid Id { get; private set; }
    public Guid ProgramId { get; private set; }
    public string StepKey { get; private set; } = "";
    /// <summary>What the step actually changed, so an operator can read what an upgrade did.</summary>
    public string Detail { get; private set; } = "";
    public DateTimeOffset AppliedAt { get; private set; }
}
