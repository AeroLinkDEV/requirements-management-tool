using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Hierarchy;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ProjectCreationSourceReconcilerTests
{
    [Fact]
    public void ReconcileMapsForeignValuesAndBindsAllReviewedSourceFacts()
    {
        var source = Source("foreign/1");
        var mapping = Mapping(source);
        var result = Reconcile(source, mapping);
        Assert.True(result.Ready, string.Join("; ", result.Errors));
        var item = Assert.Single(result.Requirements);
        Assert.Equal("foreign/1", item.SourceIdentifier);
        Assert.Equal("Test", item.VerificationMethod);
        Assert.Equal(1, result.IncludedObjects);
        Assert.Equal(64, result.ManifestHash!.Length);
        var reordered = mapping with { Objects = mapping.Objects.Select(x => x with { Attributes = x.Attributes.Reverse().ToArray() }).ToArray() };
        Assert.Equal(result.ManifestHash, Reconcile(source, reordered).ManifestHash);
        var changed = mapping with { Objects = mapping.Objects.Select(x => x with { Attributes = x.Attributes.Select(a => a.SourceAttribute == "Created By" ? a with { Reason = "Changed reviewed source disposition" } : a).ToArray() }).ToArray() };
        Assert.NotEqual(result.ManifestHash, Reconcile(source, changed).ManifestHash);
    }

    [Fact]
    public void ReconcileRefusesStaleHashUnmappedAttributesAndDuplicateSourceIdentities()
    {
        var source = Source("foreign/1");
        var mapping = Mapping(source);
        Assert.False(Reconcile(source, mapping with { SourceSha256 = new string('0', 64) }).Ready);
        var unmapped = mapping with { Objects = mapping.Objects.Select(x => x with { Attributes = x.Attributes.Where(x => x.SourceAttribute != "Created By").ToArray() }).ToArray() };
        Assert.Contains(Reconcile(source, unmapped).Errors, x => x.Contains("unmapped"));
        var duplicate = source with { Objects = [source.Objects[0], source.Objects[0] with { Key = "another" }] };
        Assert.Contains(Reconcile(duplicate, Mapping(duplicate)).Errors, x => x.Contains("occurs more than once"));
        Assert.Null(Reconcile(source, unmapped).ManifestHash);
    }

    [Fact]
    public void ReconcileRequiresExplicitTraceDirectionAndIncludedCompatibleEndpoints()
    {
        var source = Source("foreign/1");
        source = source with
        {
            Objects = [source.Objects[0], source.Objects[0] with { Key = "child", SourceIdentifier = "foreign/2" }],
            Relations = [new("link", source.Objects[0].Key, "child", "source-derivation", new Dictionary<string, string>())]
        };
        var initial = Mapping(source);
        var mapping = initial with
        {
            Objects = [initial.Objects[0], initial.Objects[1] with { Level = RequirementLevel.HighLevel }],
            Relations = [new("link", true, null, RequirementTraceType.AllocatedFrom, null)]
        };
        Assert.False(Reconcile(source, mapping).Ready);
        mapping = mapping with { Relations = [mapping.Relations[0] with { SourceIsParent = true }] };
        Assert.True(Reconcile(source, mapping).Ready);
        Assert.False(Reconcile(source, mapping with { Relations = [mapping.Relations[0] with { SourceIsParent = false }] }).Ready);
        var excluded = mapping with { Objects = [mapping.Objects[0], mapping.Objects[1] with { Include = false, ExclusionReason = "Not in this inception" }] };
        Assert.Contains(Reconcile(source, excluded).Errors, x => x.Contains("both exact endpoint"));
        var resolved = excluded with { Relations = [new("link", false, "Child explicitly excluded", null, null)] };
        Assert.True(Reconcile(source, resolved).Ready);
        Assert.Equal(1, Reconcile(source, resolved).ExcludedObjects);
        Assert.Equal(1, Reconcile(source, resolved).ExcludedRelations);
    }

    [Fact]
    public void UnsupportedFindingsNeedExplicitDispositionAndZeroIncludedContentCannotPass()
    {
        var source = Source("foreign/1") with { Findings = ["Attachment requires exclusion"] };
        var mapping = Mapping(source);
        Assert.False(Reconcile(source, mapping).Ready);
        mapping = mapping with { FindingResolutions = new Dictionary<string, string> { [source.Findings[0]] = "Attachment is outside supported requirements profile" } };
        Assert.True(Reconcile(source, mapping).Ready);
        Assert.False(Reconcile(source, mapping with { Objects = [mapping.Objects[0] with { Include = false, ExclusionReason = "Excluded" }] }).Ready);
    }

    [Fact]
    public void ForeignIdentifierCanBeMappedFromAnUnfamiliarColumnWithoutInventingIt()
    {
        var source = Source("foreign/1");
        source = source with { Objects = [source.Objects[0] with { SourceIdentifier = "" }] };
        var mapping = Mapping(source);
        Assert.False(Reconcile(source, mapping).Ready);
        mapping = mapping with { Objects = [mapping.Objects[0] with { Attributes = mapping.Objects[0].Attributes.Select(x => x.SourceAttribute == "Identifier" ? x with { Destination = InceptionAttributeDestination.SourceIdentifier } : x).ToArray() }] };
        Assert.Equal("foreign/1", Assert.Single(Reconcile(source, mapping).Requirements).SourceIdentifier);
        Assert.True(Reconcile(source, mapping).Ready);
        Assert.False(Reconcile(source with { Objects = [source.Objects[0] with { Kind = "TestCase" }] }, mapping).Ready);
    }

    private static InceptionReconciliation Reconcile(ProjectCreationSourceAnalysis source, InceptionMapping mapping) =>
        ProjectCreationSourceReconciler.Reconcile(source, mapping, LegacyLadderPolicy.Instance,
            new VerificationMethodPolicy(FoundingVerificationMethods.Ordered), new string('a', 64));

    private static ProjectCreationSourceAnalysis Source(string identifier)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"Identifier,Statement,VerificationMethod,Created By\n{identifier},Source wording,T,source.author\n"));
        return ProjectCreationSourceParser.Analyse(input, "source.csv");
    }
    private static InceptionMapping Mapping(ProjectCreationSourceAnalysis source) => new(source.Sha256,
        source.Objects.Select(x => new InceptionObjectMapping(x.Key, true, null, RequirementLevel.System,
        [
            new("Identifier", InceptionAttributeDestination.SourceOnly, "Exact foreign identifier retained"),
            new("Statement", InceptionAttributeDestination.Statement),
            new("VerificationMethod", InceptionAttributeDestination.VerificationMethod, ValueMappings: new Dictionary<string, string> { ["T"] = "Test" }),
            new("Created By", InceptionAttributeDestination.SourceOnly, "Source-system authorship, never AeroLink authorship")
        ])).ToArray(), [], new Dictionary<string, string>());
}
