using System.Reflection;

namespace AeroLink.Api.Tests;

public sealed class ServerAuthorityIdentityShapeTests
{
    private static readonly string[] AuthenticatedMutationContracts =
    [
        "CreateChangeRequestRequest",
        "CreateChangeRequestDraftRequest",
        "RequirementChangeRequest",
        "SubmitReviewRequest",
        "ActorRequest",
        "RequestChangesRequest",
        "CreateBaselineRequest",
        "BaselineSelectionRequest",
        "EmptyMutationRequest",
        "CreateBuildRequest",
        "RecordTestExecutionRequest",
        "DispositionImpactRequest",
        "BulkDispositionImpactRequest",
        "SelectBuildRequest",
        "StartReleaseReviewRequest"
    ];

    private static readonly string[] CallerSelectableIdentityProperties =
    [
        "ActorId", "AuthorId", "RecordedBy", "ExecutedBy", "OwnerId"
    ];

    [Fact]
    public void Authenticated_browser_contracts_expose_no_caller_selectable_identity()
    {
        var contracts = typeof(Program).Assembly.GetTypes()
            .Where(type => AuthenticatedMutationContracts.Contains(type.Name))
            .ToDictionary(type => type.Name);

        Assert.Equal(AuthenticatedMutationContracts.Length, contracts.Count);
        foreach (var contractName in AuthenticatedMutationContracts)
        {
            var properties = contracts[contractName].GetProperties(BindingFlags.Instance | BindingFlags.Public);
            Assert.DoesNotContain(properties, property =>
                CallerSelectableIdentityProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase));
        }
    }
}
