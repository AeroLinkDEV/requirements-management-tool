using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroLink.Infrastructure.Persistence;

namespace AeroLink.Infrastructure.Tests;

public sealed class ReqIfPackageIntegrityTests
{
    [Fact]
    public void Embedded_binary_hashes_are_verified_and_tampering_is_reported()
    {
        var binary=Encoding.UTF8.GetBytes("controlled attachment bytes");var expected=Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
        using var package=BuildPackage(binary,expected);var valid=ReqIfPackageIntegrity.Inspect(package,"exchange.reqifz");
        Assert.Single(valid.Binaries);Assert.Equal(1,valid.VerifiedBinaryCount);Assert.True(valid.Binaries[0].Verified);Assert.Empty(valid.Warnings);

        using var tampered=BuildPackage(Encoding.UTF8.GetBytes("tampered bytes"),expected);var invalid=ReqIfPackageIntegrity.Inspect(tampered,"exchange.reqifz");
        Assert.False(invalid.Binaries[0].Verified);Assert.Contains(invalid.Warnings,x=>x.Contains("does not match",StringComparison.OrdinalIgnoreCase));
    }

    private static MemoryStream BuildPackage(byte[] binary,string expected)
    {
        var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            var reqif=zip.CreateEntry("exchange.reqif");using(var writer=new StreamWriter(reqif.Open(),Encoding.UTF8,1024,leaveOpen:false))writer.Write("<REQ-IF xmlns='http://www.omg.org/spec/ReqIF/20110401/reqif.xsd'/>");
            var attachment=zip.CreateEntry("attachments/evidence.txt");using(var target=attachment.Open())target.Write(binary);
            var manifest=zip.CreateEntry("aerolink-manifest.json");using var manifestStream=manifest.Open();JsonSerializer.Serialize(manifestStream,new{sha256=new Dictionary<string,string>{{"attachments/evidence.txt",expected}}});
        }
        output.Position=0;return output;
    }
}
