using AeroLink.Domain.Common;
using AeroLink.Domain.Imports;

namespace AeroLink.Domain.Tests;

/// <summary>
/// Bringing in a program that already exists somewhere else.
///
/// The whole point of this record is that the baseline it produces can be told apart from one this product
/// built, forever. These cover the gates that keep that true: an import cannot be accepted without having
/// been reconciled, its provenance cannot be incomplete, and what it asserts is narrow and stated.
/// </summary>
public sealed class BaselineImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string Digest = "9f2c4b1e7a0d3c5589ab41e2f7c60d9b8e35a1470c2df6b849e0d17ac3d07a38";

    private static BaselineImport Create(ImportedArtifactKinds carries = ImportedArtifactKinds.Requirements) =>
        new(ProjectId, "IBM Rational DOORS", "9.6.1.13", "FMS Sys Req v4.2",
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero),
            "FMS_SYSTEM_REQUIREMENTS_2026-07-14.reqifz", Digest, 43_842_112, carries,
            "m.chen", Now.AddDays(-3), "a.okafor", Now);

    private static BaselineImport Reconciled()
    {
        var import = Create();
        import.RecordAnalysis(Now.AddMinutes(1));
        import.RecordMapping("""{"modules":3}""", Now.AddMinutes(2));
        import.NoteSourceRecordsAccountedFor(5412, Now.AddMinutes(2).AddSeconds(30));
        import.RecordReconciliation("""{"in":5412,"requirements":5180}""", Now.AddMinutes(3));
        return import;
    }

    [Fact]
    public void An_import_records_where_it_came_from_before_it_does_anything()
    {
        var import = Create();

        Assert.Equal(BaselineImportState.Draft, import.State);
        Assert.Equal("IBM Rational DOORS", import.SourceSystem);
        Assert.Equal("FMS Sys Req v4.2", import.SourceBaselineName);
        Assert.Equal(Digest, import.ExtractSha256);
        Assert.Equal("m.chen", import.ExtractedBy);
        Assert.Null(import.AcceptedBy);
        Assert.Null(import.ReleaseId);
    }

    [Fact]
    public void Provenance_that_cannot_be_checked_later_is_refused_now()
    {
        // A hash is what makes "this is a true copy" checkable years afterwards. Anything that is not a
        // SHA-256 digest is a story about a file nobody kept.
        Assert.Throws<DomainException>(() => new BaselineImport(ProjectId, "DOORS", "9.6", "v4.2", Now,
            "extract.reqifz", "not-a-digest", 10, ImportedArtifactKinds.Requirements, "m.chen", Now, "a.okafor", Now));
        Assert.Throws<DomainException>(() => new BaselineImport(ProjectId, "", "9.6", "v4.2", Now,
            "extract.reqifz", Digest, 10, ImportedArtifactKinds.Requirements, "m.chen", Now, "a.okafor", Now));
        Assert.Throws<DomainException>(() => new BaselineImport(ProjectId, "DOORS", "9.6", "v4.2", Now,
            "extract.reqifz", Digest, 0, ImportedArtifactKinds.Requirements, "m.chen", Now, "a.okafor", Now));
    }

    [Fact]
    public void An_import_declares_what_it_carries_so_a_second_source_stays_possible()
    {
        // A Program is expected to come from one source, but requirements arriving from one system and test
        // procedures from another is foreseeable. Declaring the kinds now costs nothing and keeps that open.
        Assert.Throws<DomainException>(() => Create(ImportedArtifactKinds.None));

        var both = Create(ImportedArtifactKinds.Requirements | ImportedArtifactKinds.TestProcedures);
        Assert.True(both.Carries.HasFlag(ImportedArtifactKinds.Requirements));
        Assert.True(both.Carries.HasFlag(ImportedArtifactKinds.TestProcedures));
    }

    [Fact]
    public void The_gates_run_in_order_and_none_can_be_skipped()
    {
        var import = Create();

        Assert.Throws<DomainException>(() => import.RecordMapping("{}", Now));
        Assert.Throws<DomainException>(() => import.RecordReconciliation("{}", Now));
        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.NewGuid(), Now));

        import.RecordAnalysis(Now.AddMinutes(1));
        Assert.Throws<DomainException>(() => import.RecordReconciliation("{}", Now));
        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.NewGuid(), Now));

        import.RecordMapping("""{"modules":3}""", Now.AddMinutes(2));
        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.NewGuid(), Now));

        import.NoteSourceRecordsAccountedFor(5412, Now.AddMinutes(2).AddSeconds(30));
        import.RecordReconciliation("""{"in":5412}""", Now.AddMinutes(3));
        import.Accept("a.okafor", Guid.NewGuid(), Now.AddMinutes(4));
        Assert.Equal(BaselineImportState.Accepted, import.State);
    }

    [Fact]
    public void Changing_the_mapping_discards_the_reconciliation_it_produced()
    {
        var import = Reconciled();
        Assert.NotEqual("", import.ReconciliationJson);

        import.RecordMapping("""{"modules":4}""", Now.AddMinutes(4));

        // The counts described the old mapping. Keeping them would let somebody accept an import against a
        // reconciliation that no longer describes what it would do.
        Assert.Equal("", import.ReconciliationJson);
        Assert.Equal(BaselineImportState.Mapped, import.State);
        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.NewGuid(), Now.AddMinutes(5)));
    }

    [Fact]
    public void Accepting_names_the_person_and_the_build_it_creates()
    {
        var import = Reconciled();
        var releaseId = Guid.NewGuid();

        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.Empty, Now.AddMinutes(4)));
        Assert.Throws<DomainException>(() => import.Accept("  ", releaseId, Now.AddMinutes(4)));

        import.Accept("a.okafor", releaseId, Now.AddMinutes(4));

        Assert.Equal("a.okafor", import.AcceptedBy);
        Assert.Equal(Now.AddMinutes(4), import.AcceptedAt);
        Assert.Equal(releaseId, import.ReleaseId);
    }

    [Fact]
    public void An_accepted_import_is_immutable_because_its_baseline_exists()
    {
        var import = Reconciled();
        import.Accept("a.okafor", Guid.NewGuid(), Now.AddMinutes(4));

        Assert.Throws<DomainException>(() => import.Abandon(Now.AddMinutes(5)));
        Assert.Throws<DomainException>(() => import.RecordMapping("{}", Now.AddMinutes(5)));
        Assert.Throws<DomainException>(() => import.RecordReconciliation("{}", Now.AddMinutes(5)));
    }

    [Fact]
    public void An_import_that_accounts_for_no_source_objects_cannot_be_reconciled()
    {
        var import = Create();
        import.RecordAnalysis(Now.AddMinutes(1));
        import.RecordMapping("""{"modules":3}""", Now.AddMinutes(2));

        // Reconcile means every source object is accounted for. Against nothing that is vacuously true, and
        // accepting it would produce an empty build asserting that a program was brought in from elsewhere —
        // the one outcome no later gate would catch.
        Assert.Throws<DomainException>(() => import.RecordReconciliation("""{"in":0}""", Now.AddMinutes(3)));
        Assert.Equal(BaselineImportState.Mapped, import.State);

        import.NoteSourceRecordsAccountedFor(5412, Now.AddMinutes(3));
        import.RecordReconciliation("""{"in":5412}""", Now.AddMinutes(4));
        Assert.Equal(BaselineImportState.Reconciled, import.State);
    }

    [Fact]
    public void What_an_import_accounted_for_is_held_by_the_import_not_counted_from_its_identities()
    {
        // A re-extract is a delta: an object already recorded by an earlier import is marked seen again and
        // keeps the import that first recorded it. Counting identity rows would report a second import of the
        // same program as holding nothing, and refuse to reconcile the one case the delta rule exists for.
        var second = Create();
        second.RecordAnalysis(Now.AddMinutes(1));
        second.RecordMapping("""{"modules":3}""", Now.AddMinutes(2));
        second.NoteSourceRecordsAccountedFor(5412, Now.AddMinutes(3));

        Assert.Equal(5412, second.SourceRecordCount);
        second.RecordReconciliation("""{"in":5412,"new":0}""", Now.AddMinutes(4));
        Assert.Equal(BaselineImportState.Reconciled, second.State);
    }

    [Fact]
    public void Recording_more_of_the_extract_discards_the_reconciliation_it_produced()
    {
        var import = Reconciled();
        Assert.Equal(BaselineImportState.Reconciled, import.State);

        import.NoteSourceRecordsAccountedFor(5500, Now.AddMinutes(4));

        // Same reason re-mapping discards it: those counts described a different set of objects, and the
        // Reconcile gate exists precisely to stop an acceptance resting on counts that no longer hold.
        Assert.Equal("", import.ReconciliationJson);
        Assert.Equal(BaselineImportState.Mapped, import.State);
        Assert.Throws<DomainException>(() => import.Accept("a.okafor", Guid.NewGuid(), Now.AddMinutes(5)));
    }

    [Fact]
    public void Source_records_cannot_be_recorded_against_an_import_that_has_not_been_analysed()
    {
        var draft = Create();
        Assert.Throws<DomainException>(() => draft.NoteSourceRecordsAccountedFor(10, Now.AddMinutes(1)));

        var accepted = Reconciled();
        accepted.Accept("a.okafor", Guid.NewGuid(), Now.AddMinutes(4));
        Assert.Throws<DomainException>(() => accepted.NoteSourceRecordsAccountedFor(10, Now.AddMinutes(5)));
    }

    [Fact]
    public void An_unaccepted_import_can_be_walked_away_from()
    {
        var import = Reconciled();
        import.Abandon(Now.AddMinutes(4));
        Assert.Equal(BaselineImportState.Abandoned, import.State);
    }
}
