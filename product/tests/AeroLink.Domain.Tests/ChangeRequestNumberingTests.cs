using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;

namespace AeroLink.Domain.Tests;

/// <summary>
/// What a change request is called, and why it cannot be called anything else.
///
/// The prefix names the level of requirement the change request is allowed to carry: SRCR for System, and
/// HLRCR or LLRCR for the two software levels, which are worked, reviewed and approved by different people.
/// A reader who sees the identifier already knows which of the three they are holding, so the identifier and
/// the record's declared scope must never be able to disagree.
/// </summary>
public sealed class ChangeRequestNumberingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ReleaseId = Guid.NewGuid();

    private static SystemChangeRequest Create(string number, ChangeRequestType type, RequirementLevel? level) =>
        new(number, 0, ProjectId, ReleaseId, "Title", "Problem", "Analysis", "Solution", "author", Now, type,
            softwareLevel: level);

    [Theory]
    [InlineData(ChangeRequestType.System, null, "SRCR")]
    [InlineData(ChangeRequestType.Software, RequirementLevel.HighLevel, "HLRCR")]
    [InlineData(ChangeRequestType.Software, RequirementLevel.LowLevel, "LLRCR")]
    public void The_prefix_is_decided_by_what_the_change_request_may_change(
        ChangeRequestType type, RequirementLevel? level, string expected)
    {
        Assert.Equal(expected, ChangeRequestNumbering.Prefix(type, level));
        var request = Create($"{expected}-00042", type, level);
        Assert.Equal($"{expected}-00042.00", request.DisplayNumber);
    }

    [Fact]
    public void A_software_change_request_cannot_exist_without_a_level_to_be_numbered_by()
    {
        // Not merely constrained: HLR and LLR change requests are numbered apart, so a software change
        // request with no level is a controlled record that cannot be named.
        Assert.Throws<DomainException>(() => ChangeRequestNumbering.Prefix(ChangeRequestType.Software, null));
        Assert.Throws<DomainException>(() => Create("HLRCR-00043", ChangeRequestType.Software, null));
        Assert.Throws<DomainException>(() => Create("HLRCR-00043", ChangeRequestType.Software, RequirementLevel.System));
    }

    [Fact]
    public void The_legacy_system_prefix_helper_ignores_an_optional_software_level()
    {
        Assert.Equal("SRCR", ChangeRequestNumbering.Prefix(ChangeRequestType.System, RequirementLevel.HighLevel));
    }

    [Fact]
    public void The_identifier_and_the_declared_scope_cannot_disagree()
    {
        // An LLRCR that says it is HLR work, or a System change request numbered as software, would be a
        // record whose name lies about what is inside it.
        Assert.Throws<DomainException>(() => Create("LLRCR-00044", ChangeRequestType.Software, RequirementLevel.HighLevel));
        Assert.Throws<DomainException>(() => Create("HLRCR-00044", ChangeRequestType.Software, RequirementLevel.LowLevel));
        Assert.Throws<DomainException>(() => Create("HLRCR-00044", ChangeRequestType.System, null));
        Assert.Throws<DomainException>(() => Create("SRCR-00044", ChangeRequestType.Software, RequirementLevel.HighLevel));
    }

    [Fact]
    public void The_retired_prefixes_are_no_longer_valid_identifiers()
    {
        // Nothing in the database carries them any more, and nothing should be able to create one.
        Assert.Throws<DomainException>(() => ArtifactNumber.ValidateBase("SCR-00032"));
        Assert.Throws<DomainException>(() => ArtifactNumber.ValidateBase("SWCR-00077"));
    }

    [Fact]
    public void The_new_prefixes_keep_the_padded_form_and_still_allow_growth_past_five_digits()
    {
        Assert.Equal("SRCR-00032", ArtifactNumber.ValidateBase("srcr-00032"));
        Assert.Equal("ICDCR-00042", ArtifactNumber.ValidateBase("icdcr-00042"));
        Assert.Equal("HLRCR-00077.02", ArtifactNumber.Display("HLRCR-00077", 2));
        // Five digits is the padded form, not a ceiling — the allocator counts without a bound.
        Assert.Equal("LLRCR-100000", ArtifactNumber.ValidateBase("LLRCR-100000"));
        // Zero-padded wider forms stay rejected, so retired eight-digit identifiers cannot creep back.
        Assert.Throws<DomainException>(() => ArtifactNumber.ValidateBase("SRCR-00000001"));
    }
}
