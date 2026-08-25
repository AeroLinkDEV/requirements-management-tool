using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

/// <summary>
/// #701: verification method is controlled engineering data, so a project declares which methods it permits
/// and review refuses anything else. These cover the vocabulary aggregate's own rules and the submission
/// boundary that consults it.
/// </summary>
public sealed class VerificationMethodVocabularyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Founding_vocabulary_is_the_products_verification_contract_in_authoring_order()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);

        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal([1, 2, 3, 4], vocabulary.Methods.Select(x => x.Position));
        Assert.Equal(["test", "analysis", "inspection", "demonstration"], vocabulary.Methods.Select(x => x.NormalizedValue));
        Assert.Equal(1, vocabulary.Version);
        Assert.All(vocabulary.Methods, method => Assert.Equal(Project, method.ProjectId));
        Assert.All(vocabulary.Methods, method => Assert.Equal(vocabulary.Id, method.VocabularyId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_member_is_refused(string blank)
    {
        var error = Assert.Throws<DomainException>(() =>
            ProjectVerificationVocabulary.Declaring(Project, ["Test", blank], Now));
        Assert.Equal("A verification method cannot be blank.", error.Message);
    }

    [Fact]
    public void A_member_longer_than_the_bound_is_refused()
    {
        var error = Assert.Throws<DomainException>(() =>
            ProjectVerificationVocabulary.Declaring(Project, [new string('x', 101)], Now));
        Assert.Contains("exceeds 100 characters", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vocabulary_cannot_be_created_empty()
    {
        var error = Assert.Throws<DomainException>(() => ProjectVerificationVocabulary.Declaring(Project, [], Now));
        Assert.Equal("A verification vocabulary requires at least one permitted method.", error.Message);
    }

    [Theory]
    [InlineData("Test", "test")]
    [InlineData("Test", "TEST")]
    [InlineData("Test", " Test ")]
    [InlineData("Service Experience", "service experience")]
    public void Members_differing_only_in_case_or_surrounding_whitespace_cannot_both_be_configured(
        string first, string second)
    {
        var error = Assert.Throws<DomainException>(() =>
            ProjectVerificationVocabulary.Declaring(Project, [first, second], Now));
        Assert.Contains("differ only in case or surrounding whitespace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_order_is_preserved_and_deterministic()
    {
        var vocabulary = ProjectVerificationVocabulary.Declaring(Project,
            ["Inspection", "Similarity", "Test"], Now);

        Assert.Equal(["Inspection", "Similarity", "Test"], vocabulary.OrderedValues);
        Assert.Equal(["Inspection", "Similarity", "Test"], vocabulary.ToPolicy().PermittedMethods);
        Assert.Equal("Inspection, Similarity, Test", vocabulary.ToPolicy().DescribePermitted());
    }

    [Fact]
    public void A_programme_can_add_its_own_method_and_surviving_members_keep_their_identity()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);
        var testMemberId = vocabulary.Methods.Single(x => x.DisplayValue == "Test").Id;

        vocabulary.ReplaceMembers(["Test", "Analysis", "Inspection", "Demonstration", "Similarity"], [], Now.AddDays(1));

        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration", "Similarity"], vocabulary.OrderedValues);
        Assert.Equal(2, vocabulary.Version);
        Assert.Equal(testMemberId, vocabulary.Methods.Single(x => x.DisplayValue == "Test").Id);
        Assert.Equal(5, vocabulary.Methods.Single(x => x.DisplayValue == "Similarity").Position);
    }

    [Fact]
    public void Reordering_moves_positions_without_recreating_members()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);
        var identities = vocabulary.Methods.ToDictionary(x => x.DisplayValue, x => x.Id);

        vocabulary.ReplaceMembers(["Analysis", "Test", "Inspection", "Demonstration"], [], Now.AddDays(1));

        Assert.Equal(["Analysis", "Test", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal(identities["Analysis"], vocabulary.Methods.Single(x => x.DisplayValue == "Analysis").Id);
        Assert.Equal(identities["Test"], vocabulary.Methods.Single(x => x.DisplayValue == "Test").Id);
    }

    [Fact]
    public void Removing_a_member_controlled_records_still_declare_is_refused()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);

        var error = Assert.Throws<DomainException>(() =>
            vocabulary.ReplaceMembers(["Analysis", "Inspection", "Demonstration"], ["Test"], Now.AddDays(1)));

        Assert.Contains("still declared by controlled requirement records", error.Message, StringComparison.Ordinal);
        Assert.Contains("Test", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The #701 review finding: re-spelling a configured member is a removal wearing a disguise.
    ///
    /// Review matches the configured spelling byte-for-byte, so a project whose requirements say "Test" and
    /// whose vocabulary is edited to say "test" would find every one of those requirements suddenly
    /// non-conforming and every future submission of one refused — with nothing having been removed and no
    /// refusal shown. Asking the question on normalized keys could not see that, because normalizing is
    /// exactly the difference review does not forgive.
    /// </summary>
    [Fact]
    public void Re_spelling_a_member_controlled_records_declare_is_refused_as_a_removal()
    {
        var vocabulary = ProjectVerificationVocabulary.Declaring(Project, ["Test"], Now);

        var error = Assert.Throws<DomainException>(() =>
            vocabulary.ReplaceMembers(["test"], ["Test"], Now.AddDays(1)));

        Assert.Contains("cannot be removed or re-spelled", error.Message, StringComparison.Ordinal);
        Assert.Contains("Test", error.Message, StringComparison.Ordinal);
        Assert.Equal(["Test"], vocabulary.OrderedValues);
        Assert.Equal(["Test"], vocabulary.StrandedBy(["test"], ["Test"]));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("TEST")]
    [InlineData("tEsT")]
    public void Every_casing_of_a_declared_member_is_refused(string respelling)
    {
        var vocabulary = ProjectVerificationVocabulary.Declaring(Project, ["Test", "Analysis"], Now);

        Assert.Throws<DomainException>(() =>
            vocabulary.ReplaceMembers([respelling, "Analysis"], ["Test"], Now.AddDays(1)));
        Assert.Equal(["Test", "Analysis"], vocabulary.OrderedValues);
        Assert.Equal(1, vocabulary.Version);
    }

    /// <summary>
    /// The other half of the same rule. Nothing declares the old spelling, so nothing can be stranded by
    /// changing it, and a programme correcting its own configuration must not be stopped.
    /// </summary>
    [Fact]
    public void A_casing_change_no_record_declares_remains_permitted()
    {
        var vocabulary = ProjectVerificationVocabulary.Declaring(Project, ["Test", "Analysis"], Now);
        var identity = vocabulary.Methods.Single(x => x.DisplayValue == "Test").Id;

        vocabulary.ReplaceMembers(["TEST", "Analysis"], ["Analysis"], Now.AddDays(1));

        Assert.Equal(["TEST", "Analysis"], vocabulary.OrderedValues);
        Assert.Equal(identity, vocabulary.Methods.Single(x => x.DisplayValue == "TEST").Id);
        Assert.Equal("test", vocabulary.Methods.Single(x => x.DisplayValue == "TEST").NormalizedValue);
        Assert.Equal(2, vocabulary.Version);
    }

    /// <summary>
    /// A stored value the vocabulary never permitted is the reconciliation report's business, not the
    /// configuration screen's. Letting it block unrelated edits would leave a project unable to configure
    /// anything until it had corrected history first.
    /// </summary>
    [Fact]
    public void A_declared_value_that_was_never_configured_does_not_block_an_edit()
    {
        var vocabulary = ProjectVerificationVocabulary.Declaring(Project, ["Test"], Now);

        vocabulary.ReplaceMembers(["Test", "Similarity"], ["Test", "Testing"], Now.AddDays(1));

        Assert.Equal(["Test", "Similarity"], vocabulary.OrderedValues);
    }

    [Fact]
    public void A_vocabulary_cannot_be_emptied_while_requirements_reference_its_values()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);

        var error = Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers([], ["Test"], Now.AddDays(1)));

        Assert.Equal("A verification vocabulary cannot be emptied. Configure the methods this programme permits.",
            error.Message);
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
        Assert.Equal(1, vocabulary.Version);
    }

    [Fact]
    public void A_vocabulary_cannot_be_emptied_even_when_nothing_references_it()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);

        Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers([], [], Now.AddDays(1)));
        Assert.Equal(["Test", "Analysis", "Inspection", "Demonstration"], vocabulary.OrderedValues);
    }

    [Fact]
    public void A_refused_replacement_leaves_the_configuration_byte_identical()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);
        var before = vocabulary.Methods
            .Select(x => (x.Id, x.Position, x.DisplayValue, x.NormalizedValue, x.Version, x.UpdatedAt)).ToArray();

        Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers(["Test", "test"], [], Now.AddDays(1)));
        Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers(["Test", ""], [], Now.AddDays(1)));
        Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers(["Analysis"], ["Test"], Now.AddDays(1)));
        Assert.Throws<DomainException>(() => vocabulary.ReplaceMembers(["test", "Analysis", "Inspection", "Demonstration"], ["Test"], Now.AddDays(1)));

        Assert.Equal(before, vocabulary.Methods
            .Select(x => (x.Id, x.Position, x.DisplayValue, x.NormalizedValue, x.Version, x.UpdatedAt)).ToArray());
        Assert.Equal(1, vocabulary.Version);
        Assert.Equal(Now, vocabulary.UpdatedAt);
    }

    [Fact]
    public void Stranding_is_reported_as_data_so_an_api_need_not_parse_the_refusal()
    {
        var vocabulary = ProjectVerificationVocabulary.Founding(Project, Now);

        Assert.Equal(["Test", "Inspection"],
            vocabulary.StrandedBy(["Analysis", "Demonstration"], ["Test", "Inspection"]));
        Assert.Empty(vocabulary.StrandedBy(["Analysis", "Demonstration"], ["Analysis"]));
        // Exact on both sides: a lower-cased declaration is a different value, and does not pin "Test".
        Assert.Empty(vocabulary.StrandedBy(["Analysis", "Demonstration"], ["test"]));
    }

    [Fact]
    public void Membership_is_exact_so_a_near_miss_is_not_quietly_accepted()
    {
        var policy = ProjectVerificationVocabulary.Founding(Project, Now).ToPolicy();

        Assert.True(policy.IsPermitted("Test"));
        Assert.False(policy.IsPermitted("test"));
        Assert.False(policy.IsPermitted("TEST"));
        Assert.False(policy.IsPermitted("Testing"));
        Assert.False(policy.IsPermitted(" Test"));
        Assert.False(policy.IsPermitted(""));
        Assert.False(policy.IsPermitted(null));
    }

    [Fact]
    public void Submission_refuses_a_method_outside_the_vocabulary_and_names_the_permitted_values()
    {
        var request = SystemRequestDeclaring("Testing");

        var error = Assert.Throws<DomainException>(() => request.SubmitForReview("author", [Approver], Now,
            ladderPolicy: LegacyLadderPolicy.Instance, verificationPolicy: OnlyTest));

        Assert.Contains("'Testing'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Permitted verification methods: Test.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("TEST")]
    [InlineData("Testing")]
    [InlineData("")]
    [InlineData("Similarity")]
    public void Submission_refuses_every_spelling_that_is_not_the_configured_one(string declared)
    {
        var request = SystemRequestDeclaring(declared);

        Assert.Throws<DomainException>(() => request.SubmitForReview("author", [Approver], Now,
            ladderPolicy: LegacyLadderPolicy.Instance, verificationPolicy: OnlyTest));
    }

    [Fact]
    public void Surrounding_whitespace_never_reaches_the_vocabulary_check()
    {
        // RequirementChange has always trimmed what it records, as every other authored field is trimmed.
        // The vocabulary check therefore sees "Test", and the record says "Test": no near-miss is being
        // accepted here, and nothing about the vocabulary re-spells anything.
        var request = SystemRequestDeclaring("  Test  ");
        Assert.Equal("Test", request.RequirementChanges.Single().VerificationMethod);

        request.SubmitForReview("author", [Approver], Now, ladderPolicy: LegacyLadderPolicy.Instance,
            verificationPolicy: OnlyTest);

        Assert.Equal(ChangeRequestState.InReview, request.State);
        Assert.False(OnlyTest.IsPermitted(" Test"));
    }

    [Fact]
    public void Submission_refusal_leaves_the_package_and_its_declared_method_unchanged()
    {
        var request = SystemRequestDeclaring("test");

        Assert.Throws<DomainException>(() => request.SubmitForReview("author", [Approver], Now,
            ladderPolicy: LegacyLadderPolicy.Instance, verificationPolicy: OnlyTest));

        Assert.Equal(ChangeRequestState.Draft, request.State);
        Assert.Empty(request.ReviewCycles);
        // The whole point of refusing rather than correcting: the record still says what its author wrote.
        Assert.Equal("test", request.RequirementChanges.Single().VerificationMethod);
        Assert.DoesNotContain(request.AuditEvents, x => x.EventType == "ReviewStarted");
    }

    [Fact]
    public void Submission_accepts_the_exact_configured_spelling()
    {
        var request = SystemRequestDeclaring("Test");

        var cycle = request.SubmitForReview("author", [Approver], Now,
            ladderPolicy: LegacyLadderPolicy.Instance, verificationPolicy: OnlyTest);

        Assert.Equal(1, cycle.Sequence);
        Assert.Equal(ChangeRequestState.InReview, request.State);
        Assert.Equal("Test", request.RequirementChanges.Single().VerificationMethod);
    }

    [Fact]
    public void A_programme_configured_method_submits_like_any_other()
    {
        var request = SystemRequestDeclaring("Similarity");

        request.SubmitForReview("author", [Approver], Now, ladderPolicy: LegacyLadderPolicy.Instance,
            verificationPolicy: new VerificationMethodPolicy(["Test", "Similarity"]));

        Assert.Equal(ChangeRequestState.InReview, request.State);
    }

    [Fact]
    public void A_level_without_verification_capability_keeps_its_not_applicable_semantics()
    {
        // Interface carries HasChangeControl and nothing else: an ICD has no verification artifact, so its
        // changes declare the product's sentinel rather than a method. Enforcement follows the effective
        // ladder capability, not a hard-coded level name.
        Assert.False(LegacyLadderPolicy.Instance.HasVerification(RequirementLevel.Interface));
        var request = new SystemChangeRequest("ICDCR-00001", 0, Project, Guid.NewGuid(), "Interface change",
            "Problem", "Analysis", "Solution", "author", Now, ChangeRequestType.Interface);
        request.AddRequirementChange("author", "ICDR-000001", 0, RequirementLevel.Interface,
            RequirementChangeKind.Introduce, "The interface shall remain compatible.", "Rationale",
            "Not applicable", Now);

        request.SubmitForReview("author", [Approver], Now, ladderPolicy: LegacyLadderPolicy.Instance,
            verificationPolicy: OnlyTest);

        Assert.Equal(ChangeRequestState.InReview, request.State);
        Assert.Equal("Not applicable", request.RequirementChanges.Single().VerificationMethod);
    }

    [Fact]
    public void A_retirement_declares_nothing_and_is_not_held_to_the_vocabulary()
    {
        var request = NewSystemRequest();
        request.AddRequirementChange("author", "SYSR-000001", 1, RequirementLevel.System,
            RequirementChangeKind.Retire, "", "No longer required", "", Now);

        request.SubmitForReview("author", [Approver], Now, ladderPolicy: LegacyLadderPolicy.Instance,
            verificationPolicy: OnlyTest);

        Assert.Equal(ChangeRequestState.InReview, request.State);
    }

    [Fact]
    public void No_policy_at_the_seam_disables_the_check_rather_than_substituting_a_default_set()
    {
        // Seeders and focused tests construct aggregates without a project vocabulary. That path must not
        // acquire an invisible second source of truth for what a project permits; the API always resolves
        // the persisted one.
        var request = SystemRequestDeclaring("Whatever the author typed");

        request.SubmitForReview("author", [Approver], Now, ladderPolicy: LegacyLadderPolicy.Instance);

        Assert.Equal(ChangeRequestState.InReview, request.State);
        Assert.Equal("Whatever the author typed", request.RequirementChanges.Single().VerificationMethod);
    }

    private static VerificationMethodPolicy OnlyTest => new(["Test"]);
    private static ApproverSelection Approver => new("assurance.reviewer", "Assurance Reviewer");

    private static SystemChangeRequest NewSystemRequest() =>
        new("SRCR-00001", 0, Project, Guid.NewGuid(), "Vocabulary boundary", "Problem", "Analysis", "Solution",
            "author", Now);

    private static SystemChangeRequest SystemRequestDeclaring(string verificationMethod)
    {
        var request = NewSystemRequest();
        request.AddRequirementChange("author", "SYSR-000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "The system shall sequence oceanic waypoints.", "Rationale",
            verificationMethod, Now, allowIncomplete: true);
        return request;
    }
}
