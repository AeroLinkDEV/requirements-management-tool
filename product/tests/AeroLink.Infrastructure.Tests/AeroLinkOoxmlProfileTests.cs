using System.IO.Compression;
using System.Text;
using AeroLink.DocumentSecurity;

namespace AeroLink.Infrastructure.Tests;

public sealed class AeroLinkOoxmlProfileTests
{
    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Default Extension="png" ContentType="image/png"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """;

    private const string RootRelationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private const string Document = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:body><w:p><w:r><w:t>Safe content</w:t></w:r></w:p><w:sectPr/></w:body>
        </w:document>
        """;

    [Fact]
    public void Validate_AcceptsMinimalMacroFreeDocument()
    {
        var result = AeroLinkOoxmlProfile.Validate(Package());

        Assert.Equal(AeroLinkOoxmlProfile.Version, result.Profile);
        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, result.Result);
        Assert.Equal(3, result.Entries);
    }

    [Fact]
    public void Validate_AcceptsRealWordRoundTripFixture()
    {
        var fixture = FindRepositoryFile(Path.Combine("docs", "AeroLink Technical Overview.docx"));

        var result = AeroLinkOoxmlProfile.ValidateFile(fixture);

        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, result.Result);
    }

    [Fact]
    public void Validate_AcceptsOrdinaryWordPartsAndSafeHyperlink()
    {
        var relationships = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.test/reference" TargetMode="External"/>
            </Relationships>
            """;
        var contentTypes = ContentTypes.Replace("</Types>", """
            <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
            </Types>
            """);

        var result = AeroLinkOoxmlProfile.Validate(Package(
            ("[Content_Types].xml", Text(contentTypes)),
            ("word/_rels/document.xml.rels", Text(relationships)),
            ("word/header1.xml", Text("<w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/></w:hdr>"))));

        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, result.Result);
    }

    [Fact]
    public void Validate_AcceptsSupportedTableContentControlHeadersFootersAndImage()
    {
        var document = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:sdt><w:sdtContent><w:p/></w:sdtContent></w:sdt><w:tbl><w:tr><w:tc><w:p/></w:tc></w:tr></w:tbl></w:body>
            </w:document>
            """;
        var relationships = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
            </Relationships>
            """;
        var contentTypes = ContentTypes.Replace("</Types>", """
            <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
            <Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>
            </Types>
            """);
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        png[19] = 16; png[23] = 16;

        var result = AeroLinkOoxmlProfile.Validate(Package(
            ("[Content_Types].xml", Text(contentTypes)), ("word/document.xml", Text(document)),
            ("word/_rels/document.xml.rels", Text(relationships)),
            ("word/header1.xml", Text("<w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/></w:hdr>")),
            ("word/footer1.xml", Text("<w:ftr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/></w:ftr>")),
            ("word/media/image1.png", png)));

        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, result.Result);
    }

    [Theory]
    [InlineData("../word/document.xml")]
    [InlineData("/word/document.xml")]
    [InlineData("word\\document.xml")]
    [InlineData("word/./document.xml")]
    [InlineData("word//document.xml")]
    public void Validate_RejectsUnsafePartNames(string name)
    {
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package((name, Text("unsafe")))));

        Assert.Equal("ooxml_part_name_invalid", error.Code);
    }

    [Fact]
    public void Validate_RejectsCaseEquivalentPartCollision()
    {
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(
            ("word/styles.xml", Text("<styles/>")),
            ("WORD/STYLES.XML", Text("<styles/>")))));

        Assert.Equal("ooxml_part_collision", error.Code);
    }

    [Fact]
    public void Validate_RejectsTooManyParts()
    {
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(
            ("word/one.xml", Text("<one/>")), ("word/two.xml", Text("<two/>"))),
            limits: new OoxmlProfileLimits(MaximumEntries: 4)));

        Assert.Equal("ooxml_entry_count_limit", error.Code);
    }

    [Fact]
    public void Validate_RejectsPerPartAndAggregateExpandedLimits()
    {
        var package = Package(("word/data.xml", Text("<data>1234567890</data>")));

        var perPart = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(package,
            limits: new OoxmlProfileLimits(MaximumEntryBytes: 32)));
        var aggregate = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(package,
            limits: new OoxmlProfileLimits(MaximumEntryBytes: 4096, MaximumExpandedBytes: 200)));

        Assert.Equal("ooxml_entry_size_limit", perPart.Code);
        Assert.Equal("ooxml_expanded_size_limit", aggregate.Code);
    }

    [Fact]
    public void Validate_RejectsCompressionBombRatio()
    {
        var repetitive = "<data>" + new string('A', 20_000) + "</data>";
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/data.xml", Text(repetitive))),
            limits: new OoxmlProfileLimits(MaximumEntryBytes: 32_000, MaximumExpandedBytes: 64_000, MaximumCompressionRatio: 5)));

        Assert.Equal("ooxml_compression_ratio_limit", error.Code);
    }

    [Fact]
    public void Validate_RejectsDtdAndExcessiveXmlDepth()
    {
        var dtd = Document.Replace("<w:document", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><w:document");
        var deep = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
            + string.Concat(Enumerable.Repeat("<w:p>", 10))
            + string.Concat(Enumerable.Repeat("</w:p>", 10)) + "</w:document>";

        var dtdError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(("word/document.xml", Text(dtd)))));
        var depthError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(("word/document.xml", Text(deep))),
            limits: new OoxmlProfileLimits(MaximumXmlDepth: 5)));

        Assert.Equal("ooxml_xml_invalid", dtdError.Code);
        Assert.Equal("ooxml_xml_limit", depthError.Code);
    }

    [Fact]
    public void Validate_RejectsExcessiveXmlAttributesAndCharacters()
    {
        var attributes = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" a=\"1\" b=\"2\"><w:body/></w:document>";
        var text = Document.Replace("Safe content", new string('x', 1024));

        var attributeError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(attributes))), limits: new OoxmlProfileLimits(MaximumAttributesPerElement: 2)));
        var textError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(text))), limits: new OoxmlProfileLimits(MaximumXmlCharacters: 512)));

        Assert.Equal("ooxml_xml_limit", attributeError.Code);
        Assert.Equal("ooxml_xml_limit", textError.Code);
    }

    [Fact]
    public void Validate_HonorsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => AeroLinkOoxmlProfile.Validate(Package(), cancelled.Token));
    }

    [Fact]
    public void Validate_EnforcesProcessingDeadline()
    {
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(),
            limits: new OoxmlProfileLimits(MaximumProcessingTime: TimeSpan.Zero)));

        Assert.Equal("ooxml_timeout", error.Code);
    }

    [Fact]
    public void Validate_RejectsMalformedCoreNamespace()
    {
        var wrongNamespace = Document.Replace(
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main", "urn:attacker:word");

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(wrongNamespace)))));

        Assert.Equal("ooxml_xml_invalid", error.Code);
    }

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "https://example.test/image.png")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate", "file:///C:/template.dotm")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", "https://user:secret@example.test/")]
    public void Validate_RejectsUnsafeExternalRelationships(string type, string target)
    {
        var rels = $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="{type}" Target="{target}" TargetMode="External"/>
            </Relationships>
            """;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/_rels/document.xml.rels", Text(rels)))));

        Assert.Equal("ooxml_relationship_external", error.Code);
    }

    [Fact]
    public void Validate_RejectsBrokenAndCyclicRelationships()
    {
        var broken = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="missing.xml"/>
            </Relationships>
            """;
        var cyclicDocument = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml" Target="child.xml"/>
            </Relationships>
            """;
        var cyclicChild = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXmlProps" Target="document.xml"/>
            </Relationships>
            """;

        var brokenError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/_rels/document.xml.rels", Text(broken)))));
        var cycleError = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(
            ("word/_rels/document.xml.rels", Text(cyclicDocument)),
            ("word/child.xml", Text("<child/>")),
            ("word/_rels/child.xml.rels", Text(cyclicChild)))));

        Assert.Equal("ooxml_relationship_broken", brokenError.Code);
        Assert.Equal("ooxml_relationship_cycle", cycleError.Code);
    }

    [Fact]
    public void Validate_RejectsUnknownInternalRelationshipType()
    {
        var relationships = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="urn:vendor:execute" Target="child.xml"/>
            </Relationships>
            """;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(Package(
            ("word/_rels/document.xml.rels", Text(relationships)), ("word/child.xml", Text("<child/>")))));

        Assert.Equal("ooxml_relationship_type_unsupported", error.Code);
    }

    [Fact]
    public void Validate_AllowsBenignUnreachableContentButRejectsOrphanRelationshipPart()
    {
        var contentTypes = ContentTypes.Replace("</Types>", """
            <Override PartName="/word/headerUnused.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
            </Types>
            """);
        var benign = AeroLinkOoxmlProfile.Validate(Package(("[Content_Types].xml", Text(contentTypes)),
            ("word/headerUnused.xml", Text("<w:hdr xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:p/></w:hdr>"))));
        var orphanRelationships = """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="document.xml"/>
            </Relationships>
            """;
        var orphan = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/_rels/ghost.xml.rels", Text(orphanRelationships)))));

        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, benign.Result);
        Assert.Equal("ooxml_relationship_broken", orphan.Code);
    }

    [Fact]
    public void Validate_RejectsUnsupportedContentTypeEvenWhenItsPartIsAbsent()
    {
        var contentTypes = ContentTypes.Replace("</Types>", """
            <Override PartName="/word/vbaProject.bin" ContentType="application/vnd.ms-office.vbaProject"/>
            </Types>
            """);

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("[Content_Types].xml", Text(contentTypes)))));

        Assert.Equal("ooxml_content_type_unsupported", error.Code);
    }

    [Theory]
    [InlineData("word/vbaProject.bin")]
    [InlineData("word/activeX/activeX1.bin")]
    [InlineData("word/embeddings/object1.bin")]
    [InlineData("customUI/customUI.xml")]
    [InlineData("word/afchunk1.html")]
    public void Validate_RejectsActiveOrEmbeddedPartNames(string name)
    {
        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package((name, Text("payload")))));

        Assert.Equal("ooxml_active_content", error.Code);
    }

    [Theory]
    [InlineData("altChunk")]
    [InlineData("object")]
    [InlineData("oleObject")]
    public void Validate_RejectsActiveWordElements(string element)
    {
        var document = $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:{element}/></w:body>
            </w:document>
            """;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(document)))));

        Assert.Equal("ooxml_active_content", error.Code);
    }

    [Theory]
    [InlineData("DDEAUTO calc.exe")]
    [InlineData("INCLUDETEXT https://example.test/payload")]
    [InlineData("DATABASE query")]
    public void Validate_RejectsDangerousSimpleFields(string instruction)
    {
        var document = $"""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p><w:fldSimple w:instr="{instruction}"/></w:p></w:body>
            </w:document>
            """;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(document)))));

        Assert.Equal("ooxml_dangerous_field", error.Code);
    }

    [Fact]
    public void Validate_RejectsDangerousCommandSplitAcrossRuns()
    {
        var document = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p>
                <w:r><w:fldChar w:fldCharType="begin"/></w:r>
                <w:r><w:instrText>INCLUDE</w:instrText></w:r>
                <w:r><w:instrText>TEXT https://example.test/payload</w:instrText></w:r>
                <w:r><w:fldChar w:fldCharType="end"/></w:r>
              </w:p></w:body>
            </w:document>
            """;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/document.xml", Text(document)))));

        Assert.Equal("ooxml_dangerous_field", error.Code);
    }

    [Fact]
    public void Validate_AcceptsOrdinaryPageField()
    {
        var document = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body><w:p>
                <w:r><w:fldChar w:fldCharType="begin"/></w:r>
                <w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r>
                <w:r><w:fldChar w:fldCharType="separate"/></w:r>
                <w:r><w:t>1</w:t></w:r>
                <w:r><w:fldChar w:fldCharType="end"/></w:r>
              </w:p></w:body>
            </w:document>
            """;

        var result = AeroLinkOoxmlProfile.Validate(Package(("word/document.xml", Text(document))));

        Assert.Equal(AeroLinkOoxmlProfile.AcceptedResult, result.Result);
    }

    [Fact]
    public void Validate_RejectsOversizedImageDimensions()
    {
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        png[16] = 0x00; png[17] = 0x00; png[18] = 0x4e; png[19] = 0x20;
        png[20] = 0x00; png[21] = 0x00; png[22] = 0x00; png[23] = 0x10;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/media/image1.png", png))));

        Assert.Equal("ooxml_media_limit", error.Code);
    }

    [Fact]
    public void Validate_RejectsOversizedMediaBeforeImageParsing()
    {
        var png = new byte[1024];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        png[19] = 16; png[23] = 16;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(
            Package(("word/media/image1.png", png)), limits: new OoxmlProfileLimits(MaximumMediaBytes: 512)));

        Assert.Equal("ooxml_media_limit", error.Code);
    }

    [Fact]
    public void ValidateFile_RejectsControlledSizeOrHashMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-ooxml-{Guid.NewGuid():N}.docx");
        try
        {
            var package = Package();
            File.WriteAllBytes(path, package);

            var size = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.ValidateFile(path, package.Length + 1));
            var hash = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.ValidateFile(path, package.Length, new string('0', 64)));

            Assert.Equal("ooxml_hash_mismatch", size.Code);
            Assert.Equal("ooxml_hash_mismatch", hash.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_RejectsMalformedFuzzCorpusWithoutEscapingStableErrors()
    {
        var random = new Random(505);
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var bytes = new byte[random.Next(1, 2048)];
            random.NextBytes(bytes);

            var error = Record.Exception(() => AeroLinkOoxmlProfile.Validate(bytes));

            var validation = Assert.IsType<OoxmlValidationException>(error);
            Assert.False(string.IsNullOrWhiteSpace(validation.Code));
        }
    }

    [Fact]
    public void Validate_RejectsCrcCorruption()
    {
        var package = PackageWithCompression(CompressionLevel.NoCompression);
        var marker = Text("Safe content");
        var offset = package.AsSpan().IndexOf(marker);
        Assert.True(offset >= 0, "The test package must store the marker bytes so its CRC can be corrupted deterministically.");
        package[offset] ^= 0x01;

        var error = Assert.Throws<OoxmlValidationException>(() => AeroLinkOoxmlProfile.Validate(package));

        Assert.Equal("ooxml_zip_unsupported", error.Code);
    }

    private static byte[] Package(params (string Name, byte[] Content)[] replacements)
        => PackageWithCompression(CompressionLevel.SmallestSize, replacements);

    private static byte[] PackageWithCompression(CompressionLevel compression, params (string Name, byte[] Content)[] replacements)
    {
        var parts = new List<(string Name, byte[] Content)>
        {
            ("[Content_Types].xml", Text(ContentTypes)),
            ("_rels/.rels", Text(RootRelationships)),
            ("word/document.xml", Text(Document))
        };
        foreach (var replacement in replacements)
        {
            var index = parts.FindIndex(part => string.Equals(part.Name, replacement.Name, StringComparison.Ordinal));
            if (index >= 0) parts[index] = replacement;
            else parts.Add(replacement);
        }

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in parts)
            {
                var entry = archive.CreateEntry(name, compression);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Repository fixture not found: {relativePath}");
    }
}
