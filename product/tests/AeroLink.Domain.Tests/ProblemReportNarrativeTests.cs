using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// The authored companion to every narrative field.
///
/// A Problem Report's Analysis, Root cause, Effects, Containment, Workaround, Corrective action and
/// System/aircraft impact were plain strings while Problem and Additional information could hold
/// structure. These cover the pairing that fixes it: the plain column stays the readable projection every
/// other consumer depends on, and the rich one holds what the author actually wrote.
/// </summary>
public sealed class ProblemReportNarrativeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private const string Authored = "{\"blocks\":[{\"type\":\"paragraph\",\"text\":\"Queued behind the annunciator.\"}]}";

    private static ProblemReport NewReport() =>
        new(Guid.NewGuid(), "PR-00001", "Disconnect tone is late", "The tone follows the disconnect.",
            "", "verification.engineer", Now, category: ProblemReportCategory.CodeFunctional);

    [Fact]
    public void A_new_report_has_no_authored_narrative_and_says_so_with_empty_rather_than_invented_structure()
    {
        var report = NewReport();

        Assert.Equal("", report.AnalysisRich);
        Assert.Equal("", report.RootCauseRich);
        Assert.Equal("", report.EffectsRich);
        Assert.Equal("", report.ContainmentRich);
        Assert.Equal("", report.WorkaroundRich);
        Assert.Equal("", report.CorrectiveActionRich);
        Assert.Equal("", report.SystemAircraftImpactRich);
    }

    [Fact]
    public void Authoring_at_raise_time_writes_every_field_the_form_can_hold()
    {
        var report = NewReport();

        report.AuthorOnCreate(new ProblemReportNarrative(
            AnalysisRich: Authored, RootCauseRich: Authored, WorkaroundRich: Authored,
            CorrectiveActionRich: Authored, SystemAircraftImpactRich: Authored,
            Effects: "Crew loses the cue.", EffectsRich: Authored,
            Containment: "Brief the crews.", ContainmentRich: Authored),
            rootCause: "Annunciator queue", correctiveAction: "Reorder the queue",
            workaround: "Use the redundant channel", now: Now.AddMinutes(1));

        Assert.Equal(Authored, report.AnalysisRich);
        Assert.Equal(Authored, report.ContainmentRich);
        Assert.Equal("Queued behind the annunciator.", report.RootCause);
        Assert.Equal("Queued behind the annunciator.", report.Effects);
        Assert.Equal("Queued behind the annunciator.", report.Containment);
        Assert.Equal("Queued behind the annunciator.", report.Workaround);
    }

    /// <summary>
    /// Effects and Containment were reachable only through BeginInvestigation, so an editor that showed
    /// them could not save them. They are authorable here now, which is what makes showing them honest.
    /// </summary>
    [Fact]
    public void Effects_and_containment_are_authorable_through_the_editor()
    {
        var report = NewReport();

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(1), null, null,
            new ProblemReportNarrative(Effects: "Annunciation is lost.", Containment: "Crews briefed."));

        Assert.Equal("Annunciation is lost.", report.Effects);
        Assert.Equal("Crews briefed.", report.Containment);
    }

    /// <summary>
    /// A caller that does not show Effects or Containment leaves them alone. The editor round-trips a whole
    /// working copy, and a field it did not send is not an instruction to erase a controlled value.
    /// </summary>
    [Fact]
    public void An_edit_that_omits_the_investigation_fields_keeps_what_was_recorded()
    {
        var report = NewReport();
        report.AuthorOnCreate(new ProblemReportNarrative(Effects: "Annunciation is lost.", Containment: "Crews briefed."),
            null, null, null, Now.AddMinutes(1));

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(2), null, null,
            new ProblemReportNarrative(AnalysisRich: Authored));

        Assert.Equal("Annunciation is lost.", report.Effects);
        Assert.Equal("Crews briefed.", report.Containment);
        Assert.Equal(Authored, report.AnalysisRich);
    }

    /// <summary>
    /// The authored halves are committed evidence, not display state. Two reports whose plain text reads
    /// identically but whose structure differs are different records, and the hash has to say so.
    /// </summary>
    [Fact]
    public void The_authored_narrative_changes_the_content_commitment()
    {
        var report = NewReport();
        var before = report.CanonicalHash();

        report.UpdateDetails("verification.engineer", report.Title, report.Problem, "", "", "", "", "", "", "", "{}",
            report.Severity, report.Priority, Now.AddMinutes(1), null, null,
            new ProblemReportNarrative(RootCauseRich: Authored));

        Assert.NotEqual(before, report.CanonicalHash());
        Assert.Contains("\"rootCauseRich\"", report.CanonicalSnapshot());
        Assert.Equal(6, ProblemReportEvidenceContract.SchemaVersion);
    }

    [Fact]
    public void Typed_narrative_is_authoritative_over_a_conflicting_caller_projection()
    {
        var report = new ProblemReport(Guid.NewGuid(), "PR-00002", "Disconnect tone is late",
            "A forged plain problem", "", "verification.engineer", Now,
            problemRich: Authored, additionalInformation: "A forged plain note",
            additionalInformationRich: Authored, category: ProblemReportCategory.CodeFunctional);

        report.UpdateDetails("verification.engineer", report.Title, "A second forged problem", Authored,
            "A second forged note", Authored, "A forged analysis", "A forged cause",
            "A forged correction", "A forged impact", "{}", report.Severity, report.Priority,
            Now.AddMinutes(1), narrative: new ProblemReportNarrative(
                AnalysisRich: Authored, RootCauseRich: Authored, CorrectiveActionRich: Authored,
                SystemAircraftImpactRich: Authored));

        Assert.Equal("Queued behind the annunciator.", report.Problem);
        Assert.Equal("Queued behind the annunciator.", report.AdditionalInformation);
        Assert.Equal("Queued behind the annunciator.", report.Analysis);
        Assert.Equal("Queued behind the annunciator.", report.RootCause);
        Assert.Equal("Queued behind the annunciator.", report.CorrectiveAction);
        Assert.Equal("Queued behind the annunciator.", report.SystemAircraftImpact);
    }

    [Fact]
    public void Plain_only_and_empty_block_legacy_callers_keep_their_authored_text()
    {
        var report = new ProblemReport(Guid.NewGuid(), "PR-00003", "Disconnect tone is late",
            "Plain problem", "", "verification.engineer", Now,
            problemRich: "{\"blocks\":[]}", additionalInformation: "Plain note",
            additionalInformationRich: "{\"blocks\":[]}", category: ProblemReportCategory.CodeFunctional);

        Assert.Equal("Plain problem", report.Problem);
        Assert.Equal("Plain note", report.AdditionalInformation);
    }

    [Fact]
    public void A_problem_report_figure_requires_descriptive_alternative_text()
    {
        var image = $$"""{"blocks":[{"type":"image","attachmentId":"{{Guid.NewGuid()}}","alt":""}]}""";

        var error = Assert.Throws<AeroLink.Domain.Common.DomainException>(() =>
            new ProblemReport(Guid.NewGuid(), "PR-00004", "Disconnect tone is late", "Plain problem", "",
                "verification.engineer", Now, problemRich: image,
                category: ProblemReportCategory.CodeFunctional));

        Assert.Contains("descriptive alternative text", error.Message);
    }
}
