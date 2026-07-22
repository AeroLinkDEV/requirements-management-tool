using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ConfigurationRequirement(string Id,string Statement,string Verification,string Source,string RichContentJson);
public sealed record ConfigurationTrace(string Source,string Target,string Type);
public sealed record ConfigurationTest(string Id,string Title,IReadOnlyList<string> Covers,string Status);
public sealed record ConfigurationProjection(Guid BaselineId,string VariantKey,string VariantName,int Revision,string ManifestHash,IReadOnlyList<object> Components,IReadOnlyList<object> Libraries,IReadOnlyList<ConfigurationRequirement> Requirements,IReadOnlyList<ConfigurationTrace> Traces,IReadOnlyList<ConfigurationTest> Tests,object Metrics);

public sealed class VariantConfigurationProjectionService(AeroLinkDbContext db)
{
    public async Task<ConfigurationProjection?> ProjectAsync(Guid baselineId,CancellationToken ct)
    {
        var baseline=await db.ProductVariantBaselines.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==baselineId,ct);if(baseline is null)return null;var variant=await db.ProductVariants.AsNoTracking().SingleAsync(x=>x.Id==baseline.VariantId,ct);
        using var manifest=JsonDocument.Parse(baseline.ManifestJson);var root=manifest.RootElement;var componentIds=Ids(root,"components","revisionId");var libraryIds=Ids(root,"libraries","selectedRevisionId");
        if(componentIds.Count==0)componentIds=await db.VariantComponentSelections.AsNoTracking().Where(x=>x.VariantId==variant.Id).Select(x=>x.ComponentRevisionId).ToListAsync(ct);
        if(libraryIds.Count==0)libraryIds=await db.VariantLibraryReuses.AsNoTracking().Where(x=>x.VariantId==variant.Id).Select(x=>x.SelectedRevisionId).ToListAsync(ct);
        var componentRevisions=await(from revision in db.ComponentStreamRevisions.AsNoTracking().Where(x=>componentIds.Contains(x.Id)) join stream in db.ComponentStreams.AsNoTracking() on revision.StreamId equals stream.Id join component in db.ProductLineComponents.AsNoTracking() on stream.ComponentId equals component.Id select new{revision.Id,component.ComponentNumber,component.Name,stream.StreamKey,revision.Revision,revision.ManifestHash,revision.ContentJson}).ToListAsync(ct);
        var libraryRevisions=await(from revision in db.ControlledLibraryRevisions.AsNoTracking().Where(x=>libraryIds.Contains(x.Id)) join library in db.ControlledLibraries.AsNoTracking() on revision.LibraryId equals library.Id select new{revision.Id,library.LibraryNumber,library.Name,revision.Revision,revision.ManifestHash,revision.ContentJson}).ToListAsync(ct);
        var requirements=new List<ConfigurationRequirement>();var traces=new List<ConfigurationTrace>();var tests=new List<ConfigurationTest>();foreach(var source in componentRevisions.Select(x=>(Source:x.ComponentNumber,x.ContentJson)).Concat(libraryRevisions.Select(x=>(Source:x.LibraryNumber,x.ContentJson))))ReadContent(source.Source,source.ContentJson,requirements,traces,tests);
        requirements=requirements.GroupBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Select(x=>x.Last()).OrderBy(x=>x.Id,StringComparer.Ordinal).ToList();traces=traces.Distinct().OrderBy(x=>x.Source).ThenBy(x=>x.Target).ToList();tests=tests.GroupBy(x=>x.Id,StringComparer.OrdinalIgnoreCase).Select(x=>x.Last()).OrderBy(x=>x.Id).ToList();var covered=requirements.Count(r=>tests.Any(t=>t.Covers.Contains(r.Id,StringComparer.OrdinalIgnoreCase)));var verified=requirements.Count(r=>tests.Any(t=>t.Status.Equals("Passed",StringComparison.OrdinalIgnoreCase)&&t.Covers.Contains(r.Id,StringComparer.OrdinalIgnoreCase)));
        var components=componentRevisions.OrderBy(x=>x.ComponentNumber).Select(x=>(object)new{x.Id,x.ComponentNumber,x.Name,x.StreamKey,x.Revision,x.ManifestHash}).ToList();var libraries=libraryRevisions.OrderBy(x=>x.LibraryNumber).Select(x=>(object)new{x.Id,x.LibraryNumber,x.Name,x.Revision,x.ManifestHash}).ToList();return new(baseline.Id,variant.VariantKey,variant.Name,baseline.Revision,baseline.ManifestHash,components,libraries,requirements,traces,tests,new{requirements=requirements.Count,traces=traces.Count,tests=tests.Count,covered,verified,coveragePercent=requirements.Count==0?100:Math.Round(covered*100d/requirements.Count,1),verificationPercent=requirements.Count==0?100:Math.Round(verified*100d/requirements.Count,1)});
    }
    private static List<Guid> Ids(JsonElement root,string array,string property){var result=new List<Guid>();if(!root.TryGetProperty(array,out var items)||items.ValueKind!=JsonValueKind.Array)return result;foreach(var item in items.EnumerateArray())if(item.TryGetProperty(property,out var value)&&Guid.TryParse(value.GetString(),out var id))result.Add(id);return result;}
    private static void ReadContent(string source,string json,List<ConfigurationRequirement> requirements,List<ConfigurationTrace> traces,List<ConfigurationTest> tests)
    {using var doc=JsonDocument.Parse(json);var root=doc.RootElement;if(root.TryGetProperty("requirements",out var reqs)&&reqs.ValueKind==JsonValueKind.Array)foreach(var item in reqs.EnumerateArray()){var id=Text(item,"id");if(id.Length>0)requirements.Add(new(id,Text(item,"statement"),Text(item,"verification"),source,item.TryGetProperty("richContent",out var rich)?Canonical(rich):"{\"blocks\":[]}"));}if(root.TryGetProperty("traces",out var links)&&links.ValueKind==JsonValueKind.Array)foreach(var item in links.EnumerateArray())traces.Add(new(Text(item,"source"),Text(item,"target"),Text(item,"type")));if(root.TryGetProperty("tests",out var procedures)&&procedures.ValueKind==JsonValueKind.Array)foreach(var item in procedures.EnumerateArray()){var covers=item.TryGetProperty("covers",out var values)&&values.ValueKind==JsonValueKind.Array?values.EnumerateArray().Select(x=>x.GetString()??"").Where(x=>x.Length>0).ToList():[];tests.Add(new(Text(item,"id"),Text(item,"title"),covers,Text(item,"status")));}}
    private static string Text(JsonElement item,string name)=>item.TryGetProperty(name,out var value)?value.GetString()?.Trim()??"":"";
    private static string Canonical(JsonElement value)=>JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=false});
    public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
