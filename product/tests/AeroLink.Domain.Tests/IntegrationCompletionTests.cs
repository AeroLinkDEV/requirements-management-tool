using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;

namespace AeroLink.Domain.Tests;

public sealed class IntegrationCompletionTests
{
    [Fact]
    public void Import_mapping_updates_are_version_checked_and_json_validated()
    {
        var now=DateTimeOffset.UtcNow;var mapping=new RequirementImportMapping(Guid.NewGuid(),"ReqIF default","{\"statement\":\"Description\"}","engineer",now);
        mapping.Update("ReqIF controlled","{\"statement\":\"ReqIF.Text\",\"identifier\":\"ReqIF.ForeignID\"}","engineer",1,now.AddMinutes(1));
        Assert.Equal(2,mapping.Version);Assert.Equal("ReqIF controlled",mapping.Name);using var document=JsonDocument.Parse(mapping.MappingJson);Assert.Equal("ReqIF.Text",document.RootElement.GetProperty("statement").GetString());
        Assert.Throws<DomainException>(()=>mapping.Update("stale","{}","engineer",1,now.AddMinutes(2)));
        Assert.Throws<DomainException>(()=>new RequirementImportMapping(Guid.NewGuid(),"invalid","[]","engineer",now));
    }
}
