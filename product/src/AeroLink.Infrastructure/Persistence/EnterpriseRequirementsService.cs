using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Requirements;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record InterchangeRequirementRow(int RowNumber,string Identifier,string Level,string Statement,string Rationale,string VerificationMethod,bool Valid,IReadOnlyList<string> Errors);
public sealed record DiffSpan(string Kind,string Text);

public sealed class EnterpriseRequirementsService(AeroLinkDbContext db)
{
    private static readonly (RequirementLevel Level,string SchemaKey,string SchemaName,string SpecNumber,string SpecTitle)[] Defaults=
    [
        (RequirementLevel.System,"SYSTEM-REQ","System Requirement","SYSRD-000001","System Requirements Specification"),
        (RequirementLevel.HighLevel,"HLR","High-Level Software Requirement","HLRD-000001","High-Level Software Requirements Specification"),
        (RequirementLevel.LowLevel,"LLR","Low-Level Software Requirement","LLRD-000001","Low-Level Software Requirements Specification")
    ];

    public async Task SynchronizeProjectAsync(Guid projectId,string actor,CancellationToken ct=default)
    {
        if(!await db.Projects.AnyAsync(x=>x.Id==projectId,ct))return;var now=DateTimeOffset.UtcNow;
        // Two indexed counts, rather than loading the project to discover there is nothing to do. This is
        // the whole difference between a requirements page that answers in a tenth of a second and one that
        // takes nine, because this method runs on every read of the explorer.
        // One indexed count, against the only thing that can create work here.
        //
        // The backfill gives each requirement a schema profile and a place in its specification. New
        // revisions already get their profile from baseline materialization, so they cannot leave anything
        // for this to do — which matters, because counting revisions for a project requires a join through
        // fifty thousand rows on every read, and measurement showed that check alone costing more than the
        // page it was guarding.
        var artifactCount=await db.Requirements.CountAsync(x=>x.ProjectId==projectId,ct);
        var watermark=await db.ProjectWorkspaceSynchronizations.SingleOrDefaultAsync(x=>x.ProjectId==projectId,ct);
        if(watermark is not null&&watermark.IsCurrent(artifactCount))return;
        var schemas=await db.ArtifactSchemas.Include(x=>x.Fields).Where(x=>x.ProjectId==projectId).ToListAsync(ct);
        foreach(var item in Defaults)
        {
            var schema=schemas.SingleOrDefault(x=>x.Key==item.SchemaKey);
            if(schema is null)
            {
                schema=new(projectId,item.SchemaKey,item.SchemaName,item.Level.ToString(),$"Controlled {item.SchemaName.ToLowerInvariant()} schema.",actor,now);
                schema.AddField("verification_method","Verification Method",SchemaFieldType.Enumeration,true,10,"[\"Test\",\"Analysis\",\"Inspection\",\"Demonstration\"]",actor,now);
                schema.AddField("rationale","Rationale",SchemaFieldType.LongText,false,20,"[]",actor,now);
                schema.AddField("criticality","Criticality",SchemaFieldType.Enumeration,false,30,"[\"Normal\",\"Safety Significant\",\"Mission Critical\"]",actor,now);
                schema.AddField("owner","Owner",SchemaFieldType.User,false,40,"[]",actor,now);
                if(item.Level!=RequirementLevel.System) schema.AddField("derived","Derived Requirement",SchemaFieldType.Boolean,false,50,"[]",actor,now);
                db.ArtifactSchemas.Add(schema);schemas.Add(schema);
            }
            else if(item.Level!=RequirementLevel.System&&schema.Fields.All(x=>x.Key!="derived"))
                schema.AddField("derived","Derived Requirement",SchemaFieldType.Boolean,false,50,"[]",actor,now);
        }
        await db.SaveChangesAsync(ct);
        var specs=await db.RequirementSpecifications.Where(x=>x.ProjectId==projectId).ToListAsync(ct);
        foreach(var item in Defaults)
            if(specs.All(x=>x.Level!=item.Level.ToString())){var spec=new RequirementSpecification(projectId,item.SpecNumber,item.SpecTitle,item.Level.ToString(),$"Authoritative structured {item.SpecTitle.ToLowerInvariant()}.",actor,now);db.RequirementSpecifications.Add(spec);specs.Add(spec);}
        await db.SaveChangesAsync(ct);
        var sections=await db.SpecificationNodes.Where(x=>specs.Select(s=>s.Id).Contains(x.SpecificationId)&&x.Type==SpecificationNodeType.Section).ToListAsync(ct);
        foreach(var spec in specs)
            for(var i=1;i<=5;i++)if(sections.All(x=>x.SpecificationId!=spec.Id||x.Position!=i*1000)){var section=new SpecificationNode(spec.Id,null,i*1000,SpecificationNodeType.Section,$"{i}. {SectionName(i)}",null,actor,now);db.SpecificationNodes.Add(section);sections.Add(section);}
        await db.SaveChangesAsync(ct);

        var artifacts=await db.Requirements.AsNoTracking().Where(x=>x.ProjectId==projectId).OrderBy(x=>x.BaseNumber).ToListAsync(ct);
        if(artifacts.Count==0)
        {
            if(watermark is null)db.ProjectWorkspaceSynchronizations.Add(new ProjectWorkspaceSynchronization(projectId,0,now));
            else watermark.Record(0,now);
            await db.SaveChangesAsync(ct);
            return;
        }
        var artifactIds=artifacts.Select(x=>x.Id).ToList();
        var revisions=await db.RequirementRevisions.AsNoTracking().Where(x=>artifactIds.Contains(x.ArtifactId)).OrderByDescending(x=>x.Revision).ToListAsync(ct);
        var current=revisions.GroupBy(x=>x.ArtifactId).ToDictionary(x=>x.Key,x=>x.First());
        var profiled=(await db.RequirementRevisionProfiles.AsNoTracking().Where(x=>revisions.Select(r=>r.Id).Contains(x.RevisionId)).Select(x=>x.RevisionId).ToListAsync(ct)).ToHashSet();
        var placed=(await db.SpecificationNodes.AsNoTracking().Where(x=>x.RequirementArtifactId!=null&&artifactIds.Contains(x.RequirementArtifactId.Value)).Select(x=>x.RequirementArtifactId!.Value).ToListAsync(ct)).ToHashSet();
        foreach(var artifact in artifacts)
        {
            if(!current.TryGetValue(artifact.Id,out var revision))continue;var definition=Defaults.Single(x=>x.Level==artifact.Level);var schema=schemas.Single(x=>x.Key==definition.SchemaKey);
            if(!profiled.Contains(revision.Id))
            {
                var attributes=JsonSerializer.Serialize(new{rationale=revision.Rationale,verification_method=revision.VerificationMethod,criticality=artifact.BaseNumber.EndsWith("5")?"Safety Significant":"Normal",owner=artifact.Level==RequirementLevel.System?"systems.author":"software.author",derived=artifact.Level!=RequirementLevel.System});
                db.RequirementRevisionProfiles.Add(new(revision.Id,schema.Id,$"<p>{SecurityElement.Escape(revision.Statement)}</p>",attributes,"[]",actor,now));
            }
            if(!placed.Contains(artifact.Id))
            {
                var spec=specs.Single(x=>x.Level==artifact.Level.ToString());var bucket=(StableNumber(artifact.BaseNumber)%5)+1;var parent=sections.Single(x=>x.SpecificationId==spec.Id&&x.Position==bucket*1000);
                db.SpecificationNodes.Add(new(spec.Id,parent.Id,StableNumber(artifact.BaseNumber),SpecificationNodeType.Requirement,"",artifact.Id,actor,now));
            }
        }
        // Recorded only after the backfill actually completed, so a failure part-way through leaves the
        // watermark behind and the next read finishes the work rather than skipping it.
        if(watermark is null)db.ProjectWorkspaceSynchronizations.Add(new ProjectWorkspaceSynchronization(projectId,artifactCount,now));
        else watermark.Record(artifactCount,now);
        await db.SaveChangesAsync(ct);
    }

    public static IReadOnlyList<InterchangeRequirementRow> ParseImport(Stream input,string fileName)
    {
        using var memory=new MemoryStream();input.CopyTo(memory);memory.Position=0;
        var rows=fileName.EndsWith(".xlsx",StringComparison.OrdinalIgnoreCase)?ParseXlsx(memory):ParseCsv(Encoding.UTF8.GetString(memory.ToArray()));
        if(rows.Count>50_001)throw new InvalidOperationException("Imports are limited to 50,000 requirements plus one header row.");
        if(rows.Count==0)return [];
        var header=rows[0].Select((x,i)=>(Key:NormalizeHeader(x),Index:i)).ToDictionary(x=>x.Key,x=>x.Index,StringComparer.OrdinalIgnoreCase);
        string Cell(string[] row,string key)=>header.TryGetValue(key,out var index)&&index<row.Length?row[index].Trim():"";
        var output=new List<InterchangeRequirementRow>();
        for(var i=1;i<rows.Count;i++)
        {
            var row=rows[i];if(row.All(string.IsNullOrWhiteSpace))continue;var id=Cell(row,"identifier");var level=Cell(row,"level");var statement=Cell(row,"statement");var rationale=Cell(row,"rationale");var method=Cell(row,"verificationmethod");var errors=new List<string>();
            try{AeroLink.Domain.Common.ArtifactNumber.ValidateBase(id);}catch{errors.Add("Identifier must use PREFIX-######## format.");}
            if(!TryLevel(level,out _))errors.Add("Level must be System, HighLevel/HLR, or LowLevel/LLR.");if(string.IsNullOrWhiteSpace(statement))errors.Add("Statement is required.");if(string.IsNullOrWhiteSpace(method))errors.Add("VerificationMethod is required.");
            output.Add(new(i+1,id,level,statement,rationale,method,errors.Count==0,errors));
        }
        return output;
    }
    public static bool TryLevel(string text,out RequirementLevel level)
    { var value=text.Replace(" ","").Replace("-","").ToLowerInvariant();level=value switch{"system" or "sysr"=>RequirementLevel.System,"highlevel" or "hlr"=>RequirementLevel.HighLevel,"lowlevel" or "llr"=>RequirementLevel.LowLevel,_=>(RequirementLevel)(-1)};return (int)level>=0; }
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static IReadOnlyList<DiffSpan> Diff(string oldText,string newText)
    {
        var a=Tokenize(oldText).Take(400).ToArray();var b=Tokenize(newText).Take(400).ToArray();var dp=new int[a.Length+1,b.Length+1];
        for(var i=a.Length-1;i>=0;i--)for(var j=b.Length-1;j>=0;j--)dp[i,j]=a[i]==b[j]?dp[i+1,j+1]+1:Math.Max(dp[i+1,j],dp[i,j+1]);
        var raw=new List<DiffSpan>();var x=0;var y=0;while(x<a.Length||y<b.Length){if(x<a.Length&&y<b.Length&&a[x]==b[y]){raw.Add(new("same",a[x++]));y++;}else if(y<b.Length&&(x==a.Length||dp[x,y+1]>=dp[x+1,y]))raw.Add(new("added",b[y++]));else raw.Add(new("removed",a[x++]));}
        return raw.GroupAdjacent().Select(g=>new DiffSpan(g.Kind,string.Join(" ",g.Text))).ToList();
    }
    private static IEnumerable<string> Tokenize(string value)=>value.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    private static string NormalizeHeader(string value)=>new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static int StableNumber(string value)=>int.TryParse(new string(value.Where(char.IsDigit).ToArray()),out var n)?n:Math.Abs(value.GetHashCode());
    private static string SectionName(int i)=>i switch{1=>"Functional Behavior",2=>"Navigation and Guidance",3=>"Data and Interfaces",4=>"Integrity and Monitoring",_=>"Operational Constraints"};

    private static List<string[]> ParseCsv(string text)
    {
        var rows=new List<string[]>();var row=new List<string>();var field=new StringBuilder();var quoted=false;
        for(var i=0;i<text.Length;i++){var c=text[i];if(c=='"'){if(quoted&&i+1<text.Length&&text[i+1]=='"'){field.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){row.Add(field.ToString());field.Clear();}else if((c=='\r'||c=='\n')&&!quoted){if(c=='\r'&&i+1<text.Length&&text[i+1]=='\n')i++;row.Add(field.ToString());field.Clear();rows.Add(row.ToArray());row=[];}else field.Append(c);}if(field.Length>0||row.Count>0){row.Add(field.ToString());rows.Add(row.ToArray());}return rows;
    }
    private static List<string[]> ParseXlsx(Stream stream)
    {
        using var zip=new ZipArchive(stream,ZipArchiveMode.Read,true);if(zip.Entries.Sum(x=>x.Length)>WorkbookLimit)throw new InvalidOperationException("The workbook expands beyond the 100 MB safety limit.");XNamespace ns="http://schemas.openxmlformats.org/spreadsheetml/2006/main";var shared=new List<string>();var sharedEntry=zip.GetEntry("xl/sharedStrings.xml");
        if(sharedEntry is not null){using var s=sharedEntry.Open();shared=ReadWorkbookXml(s).Descendants(ns+"si").Select(x=>string.Concat(x.Descendants(ns+"t").Select(t=>t.Value))).ToList();}
        var sheet=zip.GetEntry("xl/worksheets/sheet1.xml")??throw new InvalidOperationException("The workbook must contain a first worksheet.");using var ss=sheet.Open();var doc=ReadWorkbookXml(ss);var output=new List<string[]>();
        foreach(var r in doc.Descendants(ns+"row")){var cells=new SortedDictionary<int,string>();foreach(var c in r.Elements(ns+"c")){var reference=(string?)c.Attribute("r")??"A1";var col=ColumnIndex(reference);var raw=c.Element(ns+"v")?.Value??c.Element(ns+"is")?.Value??"";var value=(string?)c.Attribute("t")=="s"&&int.TryParse(raw,out var idx)&&idx<shared.Count?shared[idx]:raw;cells[col]=value;}if(cells.Count>0){var row=new string[cells.Keys.Max()+1];foreach(var cell in cells)row[cell.Key]=cell.Value;output.Add(row);}}return output;
    }
    private const long WorkbookLimit=100L*1024*1024;

    /// <summary>
    /// Reads one part of an uploaded workbook.
    ///
    /// The size check above reads the uncompressed lengths a zip file declares about itself, and a crafted
    /// archive can declare a small number and then expand without bound — so on its own that check is a
    /// control over a number the attacker chose. The ceiling here applies to characters actually read, which
    /// is what stops the expansion. The resolver is closed off as well: nothing in a spreadsheet somebody
    /// uploaded should be able to make this server fetch a document.
    /// </summary>
    private static XDocument ReadWorkbookXml(Stream part)
    {
        var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=WorkbookLimit,MaxCharactersFromEntities=0};
        using var reader=XmlReader.Create(part,settings);
        return XDocument.Load(reader,LoadOptions.None);
    }

    private static int ColumnIndex(string reference){var n=0;foreach(var c in reference.TakeWhile(char.IsLetter))n=n*26+(char.ToUpperInvariant(c)-'A'+1);return n-1;}
}

file static class DiffGrouping
{
    public sealed record Group(string Kind,List<string> Text);
    public static IEnumerable<Group> GroupAdjacent(this IEnumerable<DiffSpan> spans){Group? current=null;foreach(var span in spans){if(current is null||current.Kind!=span.Kind){if(current is not null)yield return current;current=new(span.Kind,[span.Text]);}else current.Text.Add(span.Text);}if(current is not null)yield return current;}
}

public sealed class EnterpriseWorkspaceSeeder(AeroLinkDbContext db)
{
    public async Task EnsureAllAsync(CancellationToken ct=default){var projects=await db.Projects.AsNoTracking().Select(x=>x.Id).ToListAsync(ct);foreach(var id in projects)await new EnterpriseRequirementsService(db).SynchronizeProjectAsync(id,"system.workspace",ct);}
}
