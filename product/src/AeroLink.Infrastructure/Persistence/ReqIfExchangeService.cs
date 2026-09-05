using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using AeroLink.Domain.Common;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Hierarchy;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ReqIfPreviewItem(string ExternalId, string Identifier, string Level, string Statement,
    string Rationale, string VerificationMethod, string RichText, string AttributesJson, string TagsJson,
    bool Valid, IReadOnlyList<string> Errors);
public sealed record ReqIfPreviewManifest(string ReqIfVersion, string SourceTool, IReadOnlyList<ReqIfPreviewItem> Items,
    int HierarchyCount, int RelationCount, int AttachmentCount, IReadOnlyList<string> Warnings);
public sealed record ReqIfExportResult(ReqIfExchangeJob Job, StoredEvidence Package);

/// <summary>Secure ReqIF 1.2 package builder/parser for AeroLink's governed interchange subset.</summary>
public sealed class ReqIfExchangeService(AeroLinkDbContext db, EvidenceFileStore store, ILadderPolicy? policy = null)
{
    private readonly ILadderPolicy ladderPolicy = policy ?? LegacyLadderPolicy.Instance;
    public const string ReqIfNamespace = "http://www.omg.org/spec/ReqIF/20110401/reqif.xsd";
    private const long MaxPackageBytes = 50L * 1024 * 1024;
    private const long MaxExpandedBytes = 150L * 1024 * 1024;
    private const int MaxEntries = 5000;

    public async Task<ReqIfExportResult> ExportAsync(Guid projectId, Guid? baselineId, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var selectedMembers = baselineId is null
            ? null
            : await db.BaselineRequirements.AsNoTracking().Where(x => x.BaselineId == baselineId).ToListAsync(ct);
        var selectedArtifactIds = selectedMembers?.Select(x => x.ArtifactId).ToHashSet();
        var selectedRevisionIds = selectedMembers?.Select(x => x.RevisionId).ToHashSet();
        var artifacts = await db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId && (selectedArtifactIds == null || selectedArtifactIds.Contains(x.Id))).OrderBy(x => x.BaseNumber).ToListAsync(ct);
        var artifactIds = artifacts.Select(x => x.Id).ToList();
        var allRevisions = await db.RequirementRevisions.AsNoTracking().Where(x => artifactIds.Contains(x.ArtifactId) && (selectedRevisionIds == null || selectedRevisionIds.Contains(x.Id))).ToListAsync(ct);
        var revisions = selectedRevisionIds is null
            ? allRevisions.GroupBy(x => x.ArtifactId).Select(x => x.OrderByDescending(r => r.Revision).First()).ToList()
            : allRevisions;
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var revisionArtifactIds = revisions.Select(x => x.ArtifactId).ToHashSet();
        var profiles = await db.RequirementRevisionProfiles.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToDictionaryAsync(x => x.RevisionId, ct);
        var specifications = await db.RequirementSpecifications.AsNoTracking().Where(x => x.ProjectId == projectId).OrderBy(x => x.DocumentNumber).ToListAsync(ct);
        var specificationIds = specifications.Select(x => x.Id).ToList();
        var nodes = await db.SpecificationNodes.AsNoTracking().Where(x => specificationIds.Contains(x.SpecificationId)).ToListAsync(ct);
        var traces = await db.RequirementTraces.AsNoTracking().Where(x => x.ProjectId == projectId && revisionIds.Contains(x.SourceRevisionId) && revisionIds.Contains(x.TargetRevisionId)).ToListAsync(ct);
        // Exact binding: a controlled export carries only Active evidence whose revision identity is one of
        // the exact revisions the export presents — never "whatever is Active for the artifact".
        var attachments = (await db.ControlledAttachments.AsNoTracking().Where(x => x.ProjectId == projectId && x.ArtifactType == "Requirement" && x.State == ControlledAttachmentState.Active && x.RevisionId != null && revisionIds.Contains(x.RevisionId.Value)).ToListAsync(ct));
        // Legacy evidence without a revision binding stays readable in place, but an export may neither
        // present it as evidence of an exact revision nor silently omit it, so a scoped legacy attachment
        // fails the controlled export with the stable binding diagnostic before anything is stored.
        var unboundScoped = (await (from attachment in db.ControlledAttachments.AsNoTracking()
                                    join artifact in db.Requirements.AsNoTracking() on attachment.ArtifactId equals artifact.Id
                                    where attachment.ProjectId == projectId && attachment.ArtifactType == "Requirement"
                                          && attachment.State == ControlledAttachmentState.Active && attachment.RevisionId == null
                                          && revisionArtifactIds.Contains(attachment.ArtifactId)
                                    select new { artifact.BaseNumber, attachment.OriginalFileName, attachment.UploadedAt }).ToListAsync(ct))
            .OrderBy(x => x.UploadedAt).Take(6).ToList();
        if (unboundScoped.Count > 0)
        {
            var named = string.Join(", ", unboundScoped.Select(x => $"{x.BaseNumber} \"{x.OriginalFileName}\""));
            throw new ControlledEvidenceBindingException(
                $"The exported requirements still carry active supporting attachments without an exact revision binding ({named}). "
                + "Bind each one to an eligible in-work revision through a governed upload. Evidence whose requirement revision is already carried by a released baseline cannot be rebound through product workflows; resolving it requires an explicit operator decision before this export can proceed.");
        }
        var exportedNodeCount=nodes.Count(x=>x.Type==SpecificationNodeType.Section||(x.RequirementArtifactId is Guid id&&revisionArtifactIds.Contains(id)));

        var xml = BuildDocument(projectId, now, artifacts, revisions, profiles, specifications, nodes, traces, attachments);
        await using var package = new MemoryStream();
        using (var zip = new ZipArchive(package, ZipArchiveMode.Create, true))
        {
            var reqif = zip.CreateEntry("aerolink-exchange.reqif", CompressionLevel.Optimal);
            await using (var output = reqif.Open())
            await using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Async = true, Encoding = new UTF8Encoding(false), Indent = true }))
                await xml.SaveAsync(writer, ct);
            foreach (var attachment in attachments)
            {
                if (!store.Exists(attachment.StorageKey)) continue;
                var entry = zip.CreateEntry(AttachmentPath(attachment), CompressionLevel.Optimal);
                await using var target = entry.Open(); await using var source = store.OpenRead(attachment.StorageKey);
                await source.CopyToAsync(target, ct);
            }
            var manifestEntry = zip.CreateEntry("aerolink-manifest.json", CompressionLevel.Optimal);
            await using var manifestTarget = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestTarget, new { format = "ReqIF 1.2", projectId, baselineId, generatedAt = now, requirements = revisions.Count, hierarchyNodes = exportedNodeCount, relations = traces.Count, attachments = attachments.Count, sha256 = attachments.ToDictionary(x => AttachmentPath(x), x => x.Sha256) }, cancellationToken: ct);
        }
        package.Position = 0;
        var fileName = $"aerolink-{projectId:N}-{now:yyyyMMddHHmmss}.reqifz";
        var stored = await store.StoreAsync(package, fileName, "application/zip", ct);
        var auditManifest = JsonSerializer.Serialize(new { format = "ReqIF 1.2", namespaceUri = ReqIfNamespace, coverage = new[] { "stable-identities", "immutable-revisions", "hierarchy", "relations", "rich-text-source", "schema-attributes", "tags", "attachment-binaries" }, requirementCount = revisions.Count, hierarchyCount = exportedNodeCount, relationCount = traces.Count, attachmentCount = attachments.Count });
        var job = new ReqIfExchangeJob(projectId, ReqIfExchangeDirection.Export, stored.OriginalFileName, stored.Sha256, stored.StorageKey, auditManifest, revisions.Count, exportedNodeCount, traces.Count, attachments.Count, attachments.Count(x => !store.Exists(x.StorageKey)), 0, actor, now);
        db.ReqIfExchangeJobs.Add(job); await db.SaveChangesAsync(ct);
        return new(job, stored);
    }

    public Task<ReqIfExchangeJob> PreviewImportAsync(Guid projectId, Stream source, string fileName, string contentType,
        string actor, DateTimeOffset now, CancellationToken ct) =>
        PreviewImportAsync(projectId, source, fileName, contentType, actor, now, ct, ladderPolicy);

    /// <summary>Previews with an explicitly resolved project policy; the legacy overload remains the direct-call default.</summary>
    public async Task<ReqIfExchangeJob> PreviewImportAsync(Guid projectId, Stream source, string fileName, string contentType,
        string actor, DateTimeOffset now, CancellationToken ct, ILadderPolicy effectivePolicy)
    {
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        await using var input = new MemoryStream(); await source.CopyToAsync(input, ct);
        if (input.Length == 0 || input.Length > MaxPackageBytes) throw new InvalidOperationException("ReqIF packages must be non-empty and no larger than 50 MB.");
        input.Position = 0;
        var stored = await store.StoreAsync(input, Path.GetFileName(fileName), contentType, ct);
        input.Position = 0;
        try
        {
            var parsed = ParsePackage(input, fileName, effectivePolicy);
            var existing = (await db.Requirements.AsNoTracking().Where(x => x.ProjectId == projectId).Select(x => x.BaseNumber).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicateIds = parsed.Items.GroupBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase).Where(x => x.Key.Length > 0 && x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = parsed.Items.Select(item =>
            {
                var errors = item.Errors.ToList();
                if (existing.Contains(item.Identifier)) errors.Add("Identifier already exists; reconcile it through a controlled modification workflow.");
                if (duplicateIds.Contains(item.Identifier)) errors.Add("Identifier is duplicated in this package.");
                return item with { Valid = errors.Count == 0, Errors = errors };
            }).ToList();
            var warnings=parsed.Warnings.ToList();if(items.Count==0)warnings.Add("No requirements were mapped from this package.");
            var manifest = parsed with { Items = items,Warnings=warnings };
            var errorCount = items.Sum(x => x.Errors.Count)+(items.Count==0?1:0);
            var job = new ReqIfExchangeJob(projectId, ReqIfExchangeDirection.Import, stored.OriginalFileName, stored.Sha256, stored.StorageKey,
                JsonSerializer.Serialize(manifest), items.Count, parsed.HierarchyCount, parsed.RelationCount, parsed.AttachmentCount,
                parsed.Warnings.Count, errorCount, actor, now);
            db.ReqIfExchangeJobs.Add(job); await db.SaveChangesAsync(ct); return job;
        }
        catch { store.Delete(stored.StorageKey); throw; }
    }

    public Stream OpenPackage(ReqIfExchangeJob job) => store.OpenRead(job.StorageKey);
    public static ReqIfPreviewManifest ReadManifest(ReqIfExchangeJob job) => JsonSerializer.Deserialize<ReqIfPreviewManifest>(job.ManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("The ReqIF preview manifest is invalid.");

    public static ReqIfPreviewManifest ParsePackage(Stream package, string fileName) =>
        ParsePackage(package, fileName, LegacyLadderPolicy.Instance);

    public static ReqIfPreviewManifest ParsePackage(Stream package, string fileName, ILadderPolicy ladderPolicy)
    {
        if (fileName.EndsWith(".reqifz", StringComparison.OrdinalIgnoreCase) || IsZip(package))
        {
            package.Position = 0; using var zip = new ZipArchive(package, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > MaxEntries) throw new InvalidOperationException("The ReqIF package contains too many entries.");
            long expanded = 0; foreach (var entry in zip.Entries) { expanded += entry.Length; ValidateEntry(entry.FullName); if (expanded > MaxExpandedBytes) throw new InvalidOperationException("The expanded ReqIF package is too large."); }
            var reqif = zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".reqif", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("The ReqIFZ package does not contain a .reqif document.");
            using var xml = reqif.Open();
            return ParseXml(xml, zip.Entries.Count(x => !x.FullName.EndsWith(".reqif", StringComparison.OrdinalIgnoreCase) && !x.FullName.EndsWith("aerolink-manifest.json", StringComparison.OrdinalIgnoreCase) && !x.FullName.EndsWith('/')), ladderPolicy);
        }
        package.Position = 0; return ParseXml(package, 0, ladderPolicy);
    }

    private static ReqIfPreviewManifest ParseXml(Stream stream, int attachmentCount, ILadderPolicy ladderPolicy)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxExpandedBytes, MaxCharactersFromEntities = 0 };
        using var reader = XmlReader.Create(stream, settings); var doc = XDocument.Load(reader, LoadOptions.None);
        if (doc.Root?.Name.LocalName != "REQ-IF") throw new InvalidOperationException("The document root is not REQ-IF.");
        var warnings = new List<string>();
        if (doc.Root.Name.NamespaceName != ReqIfNamespace) warnings.Add($"Namespace '{doc.Root.Name.NamespaceName}' differs from the ReqIF 1.2 namespace.");
        var version = Value(doc, "REQ-IF-VERSION"); if (version is not ("1.0" or "1.2")) warnings.Add($"Package declares ReqIF version '{version}'. AeroLink targets the ReqIF 1.2 standard and its normative 1.0 XML schema version.");
        var sourceTool = Value(doc, "SOURCE-TOOL-ID");
        var definitions = doc.Descendants().Where(x => x.Name.LocalName.StartsWith("ATTRIBUTE-DEFINITION-", StringComparison.Ordinal)).Select(x => new { Id = Attr(x, "IDENTIFIER"), Name = Attr(x,"LONG-NAME") is { Length: > 0 } name ? name : ChildValue(x, "LONG-NAME") }).Where(x => x.Id.Length > 0).GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First().Name);
        var items = new List<ReqIfPreviewItem>();
        foreach (var obj in doc.Descendants().Where(x => x.Name.LocalName == "SPEC-OBJECT"))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in obj.Descendants().Where(x => x.Parent?.Name.LocalName == "VALUES"))
            {
                var reference = value.Descendants().FirstOrDefault(x => x.Name.LocalName.EndsWith("-REF", StringComparison.Ordinal))?.Value.Trim() ?? "";
                var name = definitions.GetValueOrDefault(reference, reference);
                var raw = Attr(value, "THE-VALUE"); if (raw.Length == 0) raw = value.Elements().FirstOrDefault(x => x.Name.LocalName == "THE-VALUE")?.Value.Trim() ?? "";
                if (name.Length > 0) values[name] = raw;
            }
            var objectType = obj.Descendants().FirstOrDefault(x => x.Name.LocalName == "SPEC-OBJECT-TYPE-REF")?.Value.Trim() ?? "";
            if (objectType.Equals("SOT-SECTION", StringComparison.OrdinalIgnoreCase) || Pick(values, "AeroLink.Kind").Equals("Section", StringComparison.OrdinalIgnoreCase)) continue;
            var external = Attr(obj, "IDENTIFIER");
            var identifier = Pick(values, "AeroLink.Identifier", "Identifier", "ReqIF.ForeignID", "ReqIF.ForeignId", "ID");
            var statement = Pick(values, "AeroLink.Statement", "Statement", "Text", "Description", "ReqIF.Text");
            var errors = new List<string>(); if (identifier.Length == 0) errors.Add("No requirement identifier could be mapped."); if (statement.Length == 0) errors.Add("No requirement statement could be mapped.");
            var importedLevel = Pick(values, "AeroLink.Level", "Level");
            var level = ladderPolicy.ParseImportedRequirementLevel(importedLevel).ToString();
            if (importedLevel is not ("System" or "HighLevel" or "LowLevel"))
            {
                if (importedLevel.Length > 0) warnings.Add($"Requirement {identifier}: level '{importedLevel}' mapped to System.");
            }
            items.Add(new(external, identifier, level, statement, Pick(values, "AeroLink.Rationale", "Rationale"), Pick(values, "AeroLink.VerificationMethod", "Verification Method"), Pick(values, "AeroLink.RichTextSource", "Rich Text"), JsonOr(values, "AeroLink.Attributes", "{}"), JsonOr(values, "AeroLink.Tags", "[]"), errors.Count == 0, errors));
        }
        return new(version, sourceTool, items, doc.Descendants().Count(x => x.Name.LocalName == "SPEC-HIERARCHY"), doc.Descendants().Count(x => x.Name.LocalName == "SPEC-RELATION"), attachmentCount, warnings.Distinct().ToList());
    }

    private static XDocument BuildDocument(Guid projectId, DateTimeOffset now, IReadOnlyList<RequirementArtifact> artifacts,
        IReadOnlyList<RequirementRevision> revisions, IReadOnlyDictionary<Guid, RequirementRevisionProfile> profiles,
        IReadOnlyList<RequirementSpecification> specifications, IReadOnlyList<SpecificationNode> nodes,
        IReadOnlyList<AeroLink.Domain.Traceability.RequirementTraceLink> traces, IReadOnlyList<ControlledAttachment> attachments)
    {
        XNamespace r = ReqIfNamespace, xhtml = "http://www.w3.org/1999/xhtml", xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var datatypeIds = new[] { "DT-STRING", "DT-XHTML" };
        var fields = new[] { "Kind", "Identifier", "Level", "Revision", "Statement", "Rationale", "VerificationMethod", "RichTextSource", "Attributes", "Tags", "ArtifactId", "RevisionId", "Attachments" };
        string Def(string f) => $"AD-{f.ToUpperInvariant()}";
        XElement Definition(string f) => new XElement(r + "ATTRIBUTE-DEFINITION-STRING", new XAttribute("IDENTIFIER", Def(f)), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", $"AeroLink.{f}"), new XElement(r + "TYPE", new XElement(r + "DATATYPE-DEFINITION-STRING-REF", "DT-STRING")));
        XElement ValueNode(string field, string value) => new XElement(r + "ATTRIBUTE-VALUE-STRING", new XAttribute("THE-VALUE", value ?? ""), new XElement(r + "DEFINITION", new XElement(r + "ATTRIBUTE-DEFINITION-STRING-REF", Def(field))));
        var revisionByArtifact = revisions.ToDictionary(x => x.ArtifactId);
        var artifactByRevision = revisions.ToDictionary(x => x.Id, x => x.ArtifactId);
        var objects = new List<XElement>();
        foreach (var artifact in artifacts)
        {
            if (!revisionByArtifact.TryGetValue(artifact.Id, out var revision)) continue; profiles.TryGetValue(revision.Id, out var profile);
            var attachmentManifest = attachments.Where(x => x.ArtifactId == artifact.Id).Select(x => new { x.Id, x.LogicalId, x.Version, x.Label, x.OriginalFileName, x.ContentType, x.Size, x.Sha256, path = AttachmentPath(x) });
            objects.Add(new XElement(r + "SPEC-OBJECT", new XAttribute("IDENTIFIER", ObjectId(artifact.Id)), new XAttribute("LAST-CHANGE", revision.CreatedAt.ToString("O")), new XAttribute("LONG-NAME", artifact.BaseNumber),
                new XElement(r + "VALUES",
                    ValueNode("Kind", "Requirement"), ValueNode("Identifier", artifact.BaseNumber), ValueNode("Level", artifact.Level.ToString()), ValueNode("Revision", revision.Revision.ToString()),
                    ValueNode("Statement", revision.Statement), ValueNode("Rationale", revision.Rationale), ValueNode("VerificationMethod", revision.VerificationMethod),
                    ValueNode("RichTextSource", profile?.RichText ?? revision.Statement), ValueNode("Attributes", profile?.AttributesJson ?? "{}"), ValueNode("Tags", profile?.TagsJson ?? "[]"),
                    ValueNode("ArtifactId", artifact.Id.ToString()), ValueNode("RevisionId", revision.Id.ToString()), ValueNode("Attachments", JsonSerializer.Serialize(attachmentManifest))),
                new XElement(r + "TYPE", new XElement(r + "SPEC-OBJECT-TYPE-REF", "SOT-REQUIREMENT"))));
        }
        foreach (var section in nodes.Where(x => x.Type == SpecificationNodeType.Section))
            objects.Add(new XElement(r + "SPEC-OBJECT", new XAttribute("IDENTIFIER", SectionId(section.Id)), new XAttribute("LAST-CHANGE", section.CreatedAt.ToString("O")), new XAttribute("LONG-NAME", section.Heading), new XElement(r + "TYPE", new XElement(r + "SPEC-OBJECT-TYPE-REF", "SOT-SECTION"))));

        XElement Hierarchy(SpecificationNode node)
        {
            var reference = node.Type == SpecificationNodeType.Section ? SectionId(node.Id) : node.RequirementArtifactId is Guid id ? ObjectId(id) : "";
            var children = nodes.Where(x => x.SpecificationId == node.SpecificationId && x.ParentId == node.Id && Exportable(x)).OrderBy(x => x.Position).ToList();
            return new XElement(r + "SPEC-HIERARCHY", new XAttribute("IDENTIFIER", $"H-{node.Id:N}"), new XAttribute("LAST-CHANGE", node.CreatedAt.ToString("O")), new XElement(r + "OBJECT", new XElement(r + "SPEC-OBJECT-REF", reference)), children.Count == 0 ? null : new XElement(r + "CHILDREN", children.Select(Hierarchy)));
        }
        var specificationElements = specifications.Select(spec => new XElement(r + "SPECIFICATION",
            new XAttribute("IDENTIFIER", $"SPEC-{spec.Id:N}"), new XAttribute("LAST-CHANGE", spec.CreatedAt.ToString("O")),
            new XAttribute("LONG-NAME", $"{spec.DocumentNumber} {spec.Title}"),
            new XElement(r + "CHILDREN", nodes.Where(x => x.SpecificationId == spec.Id && x.ParentId == null && Exportable(x)).OrderBy(x => x.Position).Select(Hierarchy)),
            new XElement(r + "TYPE", new XElement(r + "SPECIFICATION-TYPE-REF", "SPT-DOCUMENT")))).ToList();
        if (specificationElements.Count == 0)
        {
            var defaultHierarchy = artifacts.Where(x => revisionByArtifact.ContainsKey(x.Id)).Select((x, i) => new XElement(r + "SPEC-HIERARCHY",
                new XAttribute("IDENTIFIER", $"H-DEFAULT-{i}"), new XAttribute("LAST-CHANGE", now.ToString("O")),
                new XElement(r + "OBJECT", new XElement(r + "SPEC-OBJECT-REF", ObjectId(x.Id)))));
            specificationElements.Add(new XElement(r + "SPECIFICATION", new XAttribute("IDENTIFIER", $"SPEC-{projectId:N}"),
                new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "AeroLink Requirements"),
                new XElement(r + "CHILDREN", defaultHierarchy), new XElement(r + "TYPE", new XElement(r + "SPECIFICATION-TYPE-REF", "SPT-DOCUMENT"))));
        }
        var relations = traces.Where(x => artifactByRevision.ContainsKey(x.SourceRevisionId) && artifactByRevision.ContainsKey(x.TargetRevisionId))
            .Select(x => new XElement(r + "SPEC-RELATION", new XAttribute("IDENTIFIER", $"REL-{x.Id:N}"), new XAttribute("LAST-CHANGE", x.CreatedAt.ToString("O")),
                new XAttribute("LONG-NAME", x.Type.ToString()), new XElement(r + "SOURCE", new XElement(r + "SPEC-OBJECT-REF", ObjectId(artifactByRevision[x.SourceRevisionId]))),
                new XElement(r + "TARGET", new XElement(r + "SPEC-OBJECT-REF", ObjectId(artifactByRevision[x.TargetRevisionId]))),
                new XElement(r + "TYPE", new XElement(r + "SPEC-RELATION-TYPE-REF", "SRT-TRACE"))));
        bool Exportable(SpecificationNode node)=>node.Type==SpecificationNodeType.Section||(node.RequirementArtifactId is Guid artifactId&&revisionByArtifact.ContainsKey(artifactId));
        return new(new XElement(r + "REQ-IF", new XAttribute(XNamespace.Xmlns + "xhtml", xhtml), new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XAttribute(xsi + "schemaLocation", $"{ReqIfNamespace} reqif.xsd"),
            new XElement(r + "THE-HEADER", new XElement(r + "REQ-IF-HEADER", new XAttribute("IDENTIFIER", $"HEADER-{projectId:N}"), new XElement(r + "COMMENT", "ReqIF 1.2 governed export from AeroLink"), new XElement(r + "CREATION-TIME", now.ToString("O")), new XElement(r + "REPOSITORY-ID", projectId), new XElement(r + "REQ-IF-TOOL-ID", "AeroLink 2.0"), new XElement(r + "REQ-IF-VERSION", "1.0"), new XElement(r + "SOURCE-TOOL-ID", "AeroLink"), new XElement(r + "TITLE", "AeroLink controlled requirements exchange"))),
            new XElement(r + "CORE-CONTENT", new XElement(r + "REQ-IF-CONTENT",
                new XElement(r + "DATATYPES", new XElement(r + "DATATYPE-DEFINITION-STRING", new XAttribute("IDENTIFIER", datatypeIds[0]), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("MAX-LENGTH", 2000000), new XAttribute("LONG-NAME", "AeroLink lossless string")), new XElement(r + "DATATYPE-DEFINITION-XHTML", new XAttribute("IDENTIFIER", datatypeIds[1]), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "XHTML"))),
                new XElement(r + "SPEC-TYPES", new XElement(r + "SPEC-OBJECT-TYPE", new XAttribute("IDENTIFIER", "SOT-REQUIREMENT"), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "AeroLink Requirement"), new XElement(r + "SPEC-ATTRIBUTES", fields.Select(Definition))), new XElement(r + "SPEC-OBJECT-TYPE", new XAttribute("IDENTIFIER", "SOT-SECTION"), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "AeroLink Section")), new XElement(r + "SPECIFICATION-TYPE", new XAttribute("IDENTIFIER", "SPT-DOCUMENT"), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "AeroLink Specification")), new XElement(r + "SPEC-RELATION-TYPE", new XAttribute("IDENTIFIER", "SRT-TRACE"), new XAttribute("LAST-CHANGE", now.ToString("O")), new XAttribute("LONG-NAME", "AeroLink Trace"))),
                new XElement(r + "SPEC-OBJECTS", objects), new XElement(r + "SPEC-RELATIONS", relations), new XElement(r + "SPECIFICATIONS", specificationElements), new XElement(r + "SPEC-RELATION-GROUPS"))), new XElement(r + "TOOL-EXTENSIONS")));
    }

    private static string ObjectId(Guid id) => $"REQ-{id:N}";
    private static string SectionId(Guid id) => $"SECTION-{id:N}";
    private static string AttachmentPath(ControlledAttachment x) => $"attachments/{x.Id:N}-{SafeName(x.OriginalFileName)}";
    private static string SafeName(string name) { var value = Path.GetFileName(name); foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_'); return value; }
    private static bool IsZip(Stream stream) { if (!stream.CanSeek || stream.Length < 4) return false; var position = stream.Position; Span<byte> bytes = stackalloc byte[4]; stream.ReadExactly(bytes); stream.Position = position; return bytes[0] == 0x50 && bytes[1] == 0x4b; }
    private static void ValidateEntry(string name) { var normalized = name.Replace('\\', '/'); if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(name)) throw new InvalidOperationException("The ReqIF package contains an unsafe entry path."); }
    private static string Attr(XElement element, string name) => element.Attribute(name)?.Value.Trim() ?? "";
    private static string ChildValue(XElement element, string name) => element.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
    private static string Value(XDocument doc, string name) => doc.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
    private static string Pick(IReadOnlyDictionary<string, string> values, params string[] names) { foreach (var name in names) if (values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim(); return ""; }
    private static string JsonOr(IReadOnlyDictionary<string, string> values, string name, string fallback) { var value = Pick(values, name); if (value.Length == 0) return fallback; try { using var _ = JsonDocument.Parse(value); return value; } catch { return fallback; } }
}
