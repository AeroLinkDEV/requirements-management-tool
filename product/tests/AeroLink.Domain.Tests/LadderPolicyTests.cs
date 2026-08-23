using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Common;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;

namespace AeroLink.Domain.Tests;

public sealed class LadderPolicyTests
{
    private readonly ILadderPolicy policy = LegacyLadderPolicy.Instance;

    [Fact]
    public void Legacy_catalogue_contains_the_three_complete_level_bundles_in_ladder_order()
    {
        Assert.Equal([RequirementLevel.System, RequirementLevel.HighLevel, RequirementLevel.LowLevel], policy.OrderedLevels);
        Assert.Equal(["SYSR", "HLR", "LLR"], policy.Definitions.Select(x => x.RequirementPrefix));
        Assert.Equal(["SRCR", "HLRCR", "LLRCR"], policy.Definitions.Select(x => x.ChangeRequest!.Prefix));
        Assert.Equal(["SYSTP", "HLRTC", "LLRTC"], policy.Definitions.Select(x => x.Verification!.ProcedurePrefix));
        Assert.Equal(["System Test Procedures Document", "High-Level Software Test Cases Document", "Low-Level Software Test Cases Document"],
            policy.Definitions.Select(x => x.TestProcedureDocumentTitle));
        Assert.Equal(
            [TestChangeReviewDiscipline.System, TestChangeReviewDiscipline.HighLevelSoftware, TestChangeReviewDiscipline.LowLevelSoftware],
            policy.Definitions.Select(x => x.Verification!.Discipline));
        Assert.Equal(
            [ControlledDocumentType.Sysrd, ControlledDocumentType.SwrdHighLevel, ControlledDocumentType.SwrdLowLevel],
            policy.Definitions.Select(x => x.RequirementsDocumentType));
        Assert.All(policy.Definitions, definition =>
        {
            Assert.True(definition.Has(LevelCapabilities.HasChangeControl));
            Assert.True(definition.Has(LevelCapabilities.HasVerification));
            Assert.True(definition.Has(LevelCapabilities.HasRequirementsDocument));
            Assert.NotNull(definition.RequirementsCatalogue);
        });
        Assert.True(policy.HasCodeTraceability(RequirementLevel.LowLevel));
        Assert.False(policy.HasCodeTraceability(RequirementLevel.System));
    }

    [Fact]
    public void Legacy_catalogue_answers_parent_downstream_document_and_discipline_questions()
    {
        Assert.Equal([RequirementLevel.System], policy.ParentLevels(RequirementLevel.HighLevel));
        Assert.Equal([RequirementLevel.HighLevel], policy.ParentLevels(RequirementLevel.LowLevel));
        Assert.Empty(policy.ParentLevels(RequirementLevel.System));
        Assert.Equal([RequirementLevel.HighLevel], policy.DownstreamLevels(RequirementLevel.System));
        Assert.Equal([RequirementLevel.LowLevel], policy.DownstreamLevels(RequirementLevel.HighLevel));
        Assert.Empty(policy.DownstreamLevels(RequirementLevel.LowLevel));
        Assert.Equal(TestProcedureLevel.LowLevel, policy.ProcedureLevel(RequirementLevel.LowLevel));
        Assert.Equal(RequirementLevel.HighLevel, policy.RequirementLevelFor(TestProcedureLevel.HighLevel));
        Assert.Equal(RequirementLevel.LowLevel, policy.RequirementLevelFor(TestChangeReviewDiscipline.LowLevelSoftware));
        Assert.Equal(ControlledDocumentType.SwrdHighLevel, policy.RequirementsDocument(RequirementLevel.HighLevel));
        Assert.Equal(ControlledDocumentType.LowLevelTestCases, policy.TestProcedureDocument(RequirementLevel.LowLevel));
        Assert.Equal("High-Level Software Test Cases Document", policy.TestProcedureDocumentTitle(RequirementLevel.HighLevel));
        Assert.Equal("HLRCR", policy.ChangeRequestPrefix(ChangeRequestType.Software, RequirementLevel.HighLevel));
        Assert.Equal("LLRTCCR", policy.TestChangeReviewPrefix(TestChangeReviewDiscipline.LowLevelSoftware));
        Assert.Equal(ReviewSubject.System, policy.WorkflowSubject(ChangeRequestType.System));
        Assert.Equal(ReviewSubject.Software, policy.WorkflowSubject(ChangeRequestType.Software));
        Assert.Equal(ReviewSubject.LowLevelSoftwareCase, policy.WorkflowSubject(TestChangeReviewDiscipline.LowLevelSoftware));
        Assert.True(policy.IsChangeRequestScopeValid(ChangeRequestType.System, null));
        Assert.True(policy.IsChangeRequestScopeValid(ChangeRequestType.Software, RequirementLevel.HighLevel));
        Assert.False(policy.IsChangeRequestScopeValid(ChangeRequestType.Software, RequirementLevel.System));
        Assert.True(policy.AcceptsChangeRequest(ChangeRequestType.Software, RequirementLevel.LowLevel, RequirementLevel.LowLevel));
        Assert.False(policy.AcceptsChangeRequest(ChangeRequestType.Software, RequirementLevel.HighLevel, RequirementLevel.LowLevel));
        Assert.True(policy.IsKnownTestProcedurePrefix("SYSTP-000001"));
        Assert.True(policy.IsKnownTestProcedurePrefix("HLRTP-000001"));
        Assert.True(policy.IsKnownTestProcedurePrefix("LLRTP-000001"));
        Assert.False(policy.IsKnownTestProcedurePrefix("TP-000001"));
    }

    [Fact]
    public void Customer_is_authored_selectable_external_origin_but_not_legacy_ladder()
    {
        Assert.DoesNotContain(RequirementLevel.Customer, policy.OrderedLevels);
        Assert.DoesNotContain(policy.Definitions, x => x.Level == RequirementLevel.Customer);

        var customer = policy.Definition(RequirementLevel.Customer);
        Assert.Equal("CUSR", customer.RequirementPrefix);
        Assert.Equal(LevelOriginKind.ExternalSourcePackage, customer.OriginKind);
        Assert.True(customer.UsesExternalOrigin);
        Assert.False(customer.Has(LevelCapabilities.HasChangeControl));
        Assert.False(policy.AcceptsChangeRequest(ChangeRequestType.Software, RequirementLevel.Customer));
    }

    [Fact]
    public void Interface_catalogue_entry_is_controlled_without_requirements_document_or_verification()
    {
        Assert.DoesNotContain(RequirementLevel.Interface, policy.OrderedLevels);

        var definition = policy.Definition(RequirementLevel.Interface);

        Assert.Equal("ICDR", definition.RequirementPrefix);
        Assert.Equal(LevelCapabilities.HasChangeControl, definition.Capabilities);
        Assert.Equal(ChangeRequestType.Interface, definition.ChangeRequest!.Type);
        Assert.Null(definition.ChangeRequest.SoftwareLevel);
        Assert.Equal("ICDCR", definition.ChangeRequest.Prefix);
        Assert.Null(definition.RequirementsDocumentType);
        Assert.Null(definition.RequirementsCatalogue);
        Assert.Null(definition.Verification);
        Assert.Equal(ReviewSubject.Interface, policy.WorkflowSubject(ChangeRequestType.Interface));
        Assert.True(policy.IsChangeRequestScopeValid(ChangeRequestType.Interface, null));
        Assert.True(policy.AcceptsChangeRequest(ChangeRequestType.Interface, RequirementLevel.Interface));
        Assert.False(policy.AcceptsChangeRequest(ChangeRequestType.System, RequirementLevel.Interface));
        Assert.True(policy.TryParseRequirementLevel("ICD", out var parsed) && parsed == RequirementLevel.Interface);
    }

    [Fact]
    public void Interface_change_requests_use_their_own_prefix_and_reject_mismatched_levels()
    {
        var project = Guid.NewGuid();
        var release = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var request = new SystemChangeRequest("ICDCR-00001", 0, project, release, "Interface contract",
            "Problem", "Analysis", "Solution", "author", now, ChangeRequestType.Interface);
        request.AddRequirementChange("author", "ICDR-00001", 0, RequirementLevel.Interface,
            RequirementChangeKind.Introduce, "The interface shall remain compatible.", "Rationale", "Not applicable", now);
        request.SubmitForReview("author", [new("reviewer", "Reviewer")], now);
        request.ApproveActiveStage("reviewer", now);

        Assert.Equal(ChangeRequestState.Approved, request.State);
        Assert.Throws<DomainException>(() => new SystemChangeRequest("ICDCR-00002", 0, project, release,
            "Wrong scope", "Problem", "Analysis", "Solution", "author", now, ChangeRequestType.Interface,
            softwareLevel: RequirementLevel.HighLevel));
        Assert.Throws<DomainException>(() => request.AddRequirementChange("author", "SYSR-000002", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce, "Wrong level", "Rationale", "N/A", now));
    }

    [Fact]
    public void Test_procedure_prefix_validation_requires_the_catalogue_family()
    {
        var now = DateTimeOffset.UtcNow;
        _ = new TestProcedure(Guid.NewGuid(), "LLRTP-000001", "Low-level procedure", "owner", now,
            TestProcedureLevel.LowLevel, artifactKind: VerificationArtifactKind.Procedure,
            parentKind: VerificationProcedureParentKind.Derived);

        var mismatch = Assert.Throws<DomainException>(() =>
            new TestProcedure(Guid.NewGuid(), "LLRTP-000002", "Wrong-level procedure", "owner", now, TestProcedureLevel.System));
        Assert.Contains("not a valid", mismatch.Message);

        Assert.Throws<DomainException>(() => new TestProcedure(Guid.NewGuid(), "CUSTOM-000001",
            "Custom-numbered procedure", "owner", now, TestProcedureLevel.System));
    }

    [Fact]
    public void Level_definition_rejects_missing_or_disabled_capability_bindings()
    {
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.System, "SYSR", LevelCapabilities.HasVerification));
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.System, "SYSR", LevelCapabilities.HasRequirementsDocument));
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.System, "SYSR", LevelCapabilities.None,
            changeRequest: new(ChangeRequestType.System, null, "SRCR")));
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.HighLevel, "HLR", LevelCapabilities.HasVerification,
            verification: new(TestProcedureLevel.System, TestChangeReviewDiscipline.System, "SYSTP", ControlledDocumentType.SystemTestProcedures)));
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.LowLevel, "LLR", LevelCapabilities.HasChangeControl,
            changeRequest: new(ChangeRequestType.Software, RequirementLevel.HighLevel, "HLRCR")));
        Assert.Throws<DomainException>(() => new LevelDefinition(
            RequirementLevel.System, "SYSR", LevelCapabilities.HasRequirementsDocument,
            requirementsDocumentType: ControlledDocumentType.SystemTestProcedures));
    }

    [Fact]
    public void Unknown_policy_values_fail_closed_and_reqif_legacy_fallback_is_explicit()
    {
        Assert.Throws<DomainException>(() => policy.Definition((RequirementLevel)99));
        Assert.Throws<DomainException>(() => policy.RequirementLevelFor((TestProcedureLevel)99));
        Assert.Throws<DomainException>(() => policy.ChangeRequestPrefix((ChangeRequestType)99, null));
        Assert.Equal(RequirementLevel.System, policy.ParseImportedRequirementLevel(null));
        Assert.Equal(RequirementLevel.System, policy.ParseImportedRequirementLevel("retired-level"));
        Assert.Equal(RequirementLevel.HighLevel, policy.ParseImportedRequirementLevel("HighLevel"));
        Assert.Equal(RequirementLevel.LowLevel, policy.ParseImportedRequirementLevel("LowLevel"));
        Assert.True(policy.TryParseRequirementLevel("System", out var system) && system == RequirementLevel.System);
        Assert.True(policy.TryParseRequirementLevel("High-Level", out var high) && high == RequirementLevel.HighLevel);
        Assert.True(policy.TryParseRequirementLevel("LLR", out var low) && low == RequirementLevel.LowLevel);
        Assert.False(policy.TryParseRequirementLevel("Unknown", out _));
    }
}
