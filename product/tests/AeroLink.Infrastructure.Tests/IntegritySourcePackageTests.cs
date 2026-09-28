using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using AeroLink.Domain.Common;
using AeroLink.Domain.Content;
using AeroLink.Infrastructure.Persistence;
using AeroLink.Tests;

namespace AeroLink.Infrastructure.Tests;

// Authoring gate: independent Java output protects the package wire contract; tampering, silent losses,
// or unsafe date interpretation fail here. Existing CSV/XLSX tests cannot exercise this new boundary.
public sealed class IntegritySourcePackageTests
{
    [Fact]
    public async Task The_real_extractor_preserves_scoped_bytes_and_typed_source_facts_and_is_not_live_evidence()
    {
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync();
        Assert.Throws<DomainException>(() => IntegritySourcePackage.Read(bytes, hash, false));
        var package = IntegritySourcePackage.Read(bytes, hash, true);
        Assert.Equal(["1001", "1002"], package.Items.Select(x => x.Item.Id));
        var item = package.Items[0].Item;
        Assert.Equal("東京", item.Fields.Single(x => x.Name == "Custom Japanese").Value.GetString());
        Assert.Equal("PR-ſ", item.Fields.Single(x => x.Name == "External Key").Value.GetString());
        Assert.Equal("forward", item.Relationships[0].GetProperty("direction").GetString());
        Assert.Equal("1.2", item.Relationships[0].GetProperty("targetRevision").GetString());
        Assert.Equal("historical.jane", item.History[0].GetProperty("sourceSignature").GetProperty("reportedSigner").GetString());
        Assert.Equal(File.ReadAllBytes(Path.Combine(IntegrityFixturePackage.Repository(), "product/tools/integrity-extractor/fixtures/source/evidence.txt")),
            package.Members[item.Attachments.Single(x => x.Name == "original-evidence.txt").Path]);
        var description = item.Fields.Single(x => x.Name == "Description");
        var converted = IntegrityRichContent.Convert(description.Value.GetString()!, description.Representation, item, _ => Guid.NewGuid());
        Assert.Contains(RichContent.Read(converted.Content).SelectMany(x => x.Runs ?? []), run => run.Text == "freezes" && run.Bold);
        Assert.Contains("https://example.invalid/do-not-fetch", RichContent.ToPlainText(converted.Content));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("unlisted")]
    [InlineData("duplicate")]
    [InlineData("traversal")]
    public async Task Unverified_archive_content_is_refused_before_import(string attack)
    {
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync();
        using var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var entry in input.Entries)
            {
                if (attack == "missing" && entry.FullName == "items/1001.json") continue;
                using var source = entry.Open(); using var target = output.CreateEntry(entry.FullName).Open();
                if (attack == "changed" && entry.FullName == "items/1001.json") target.Write(Encoding.UTF8.GetBytes("{}"));
                else source.CopyTo(target);
            }
            if (attack is "unlisted" or "duplicate" or "traversal")
            {
                var name = attack == "duplicate" ? "items/1001.json" : attack == "traversal" ? "../escape" : "extra.txt";
                using var added = output.CreateEntry(name).Open(); added.WriteByte(1);
            }
        }
        Assert.Throws<DomainException>(() => IntegritySourcePackage.Read(buffer.ToArray(), hash, true));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-field")]
    [InlineData("duplicate-property")]
    [InlineData("loss")]
    [InlineData("unproven")]
    public async Task Correct_hashes_do_not_make_unsupported_or_incomplete_source_data_acceptable(string defect)
    {
        var (bytes, _) = await IntegrityFixturePackage.ReadAsync();
        using var input = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var members = new Dictionary<string, byte[]>();
        foreach (var entry in input.Entries)
        { using var stream = entry.Open(); using var buffer = new MemoryStream(); stream.CopyTo(buffer); members.Add(entry.FullName, buffer.ToArray()); }
        var manifest = JsonNode.Parse(members["manifest.json"])!;
        var item = JsonNode.Parse(members["items/1001.json"])!;
        if (defect == "version") manifest["version"] = 2;
        if (defect == "null-field") item["fields"]!.AsArray().Add((JsonNode?)null);
        if (defect == "loss") item["findings"]!.AsArray().Add("Historical attachment bytes missing");
        if (defect == "unproven") manifest["evidenceMode"] = "unproven";
        var itemJson = item.ToJsonString();
        if (defect == "duplicate-property") itemJson = itemJson.Insert(1, "\"id\":\"1001\",");
        members["items/1001.json"] = Encoding.UTF8.GetBytes(itemJson);
        var entryMetadata = manifest["members"]!.AsArray().Single(x => x!["path"]!.GetValue<string>() == "items/1001.json")!;
        entryMetadata["size"] = members["items/1001.json"].Length;
        entryMetadata["sha256"] = Convert.ToHexStringLower(SHA256.HashData(members["items/1001.json"]));
        members["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var expectedRoot = Convert.ToHexStringLower(SHA256.HashData(members["manifest.json"]));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var member in members) { using var stream = archive.CreateEntry(member.Key).Open(); stream.Write(member.Value); }
        Assert.Throws<DomainException>(() => IntegritySourcePackage.Read(output.ToArray(), expectedRoot, true));
    }

    [Theory]
    [InlineData("2023-01-01", "date-only", null)]
    [InlineData("2024-03-01T12:34:56", "local-unknown-zone", null)]
    [InlineData("2024-03-01T12:34:56.123456+02:00", "explicit-offset", "2024-03-01T10:34:56.123456+00:00")]
    [InlineData("2024-03-01T12:34:56.1234567Z", "unrepresentable", null)]
    [InlineData("yesterday", "unreadable", null)]
    public void Source_date_meaning_is_preserved_without_assuming_a_zone_or_truncating(string raw, string meaning, string? instant)
    {
        var result = IntegritySourcePackage.ReadDate(raw);
        Assert.Equal(raw, result.Raw); Assert.Equal(meaning, result.Meaning);
        Assert.Equal(instant is null ? null : DateTimeOffset.Parse(instant), result.Instant);
        Assert.Equal(meaning is "unreadable" or "unrepresentable", result.Finding is not null);
    }

    [Theory]
    [InlineData("{\"notRich\":\"source data\"}")]
    [InlineData("{\"blocks\":[{\"type\":\"image\",\"attachmentId\":\"5812b84e-21a9-44ae-8a47-d79091abdd94\"}]}")]
    public async Task A_text_field_cannot_impersonate_native_rich_content_or_reference_unattributed_images(string raw)
    {
        var (bytes, hash) = await IntegrityFixturePackage.ReadAsync();
        var item = IntegritySourcePackage.Read(bytes, hash, true).Items[0].Item;
        var rendered = IntegrityRichContent.Convert(raw, "text", item, _ => throw new InvalidOperationException("Text must not resolve an image."));
        Assert.Equal(raw, RichContent.ToPlainText(rendered.Content));
        Assert.Empty(RichContent.ReferencedAttachments(rendered.Content));
    }
}
