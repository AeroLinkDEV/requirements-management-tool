using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ReqIfBinaryReceipt(string Path,long Size,string Sha256,string? ExpectedSha256,bool Verified);
public sealed record ReqIfPackageIntegrityResult(string PackageSha256,string? ExpectedPackageSha256,bool PackageVerified,int EmbeddedBinaryCount,int VerifiedBinaryCount,IReadOnlyList<ReqIfBinaryReceipt> Binaries,IReadOnlyList<string> Warnings);

/// <summary>Validates the exact bytes carried in a ReqIFZ package without extracting untrusted paths.</summary>
public static class ReqIfPackageIntegrity
{
    public static ReqIfPackageIntegrityResult Inspect(Stream source,string fileName,string? expectedPackageSha256=null)
    {
        using var copy=new MemoryStream();source.CopyTo(copy);var bytes=copy.ToArray();var packageHash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var packageVerified=string.IsNullOrWhiteSpace(expectedPackageSha256)||string.Equals(packageHash,expectedPackageSha256,StringComparison.OrdinalIgnoreCase);
        if(!fileName.EndsWith(".reqifz",StringComparison.OrdinalIgnoreCase)&&!IsZip(bytes))
            return new(packageHash,expectedPackageSha256,packageVerified,0,0,[],packageVerified?[]:["The stored package hash does not match the exchange record."]);

        using var archiveStream=new MemoryStream(bytes,false);using var zip=new ZipArchive(archiveStream,ZipArchiveMode.Read,false);
        var warnings=new List<string>();var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var manifest=zip.Entries.FirstOrDefault(x=>x.FullName.Equals("aerolink-manifest.json",StringComparison.OrdinalIgnoreCase));
        if(manifest is not null)
        {
            using var manifestStream=manifest.Open();using var document=JsonDocument.Parse(manifestStream);
            if(document.RootElement.TryGetProperty("sha256",out var hashes)&&hashes.ValueKind==JsonValueKind.Object)
                foreach(var property in hashes.EnumerateObject())expected[property.Name.Replace('\\','/')]=property.Value.GetString()??"";
        }
        else warnings.Add("The package does not contain an AeroLink binary-integrity manifest; embedded binaries are inventoried but cannot be matched to declared hashes.");

        var receipts=new List<ReqIfBinaryReceipt>();
        foreach(var entry in zip.Entries.Where(x=>!x.FullName.EndsWith('/')&&!x.FullName.EndsWith(".reqif",StringComparison.OrdinalIgnoreCase)&&!x.FullName.Equals("aerolink-manifest.json",StringComparison.OrdinalIgnoreCase)))
        {
            ValidateEntry(entry.FullName);using var stream=entry.Open();var hash=Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();var normalized=entry.FullName.Replace('\\','/');expected.TryGetValue(normalized,out var expectedHash);var verified=!string.IsNullOrWhiteSpace(expectedHash)&&string.Equals(hash,expectedHash,StringComparison.OrdinalIgnoreCase);
            receipts.Add(new(normalized,entry.Length,hash,string.IsNullOrWhiteSpace(expectedHash)?null:expectedHash,verified));
            if(!string.IsNullOrWhiteSpace(expectedHash)&&!verified)warnings.Add($"Embedded binary '{normalized}' does not match its declared SHA-256 hash.");
        }
        foreach(var missing in expected.Keys.Except(receipts.Select(x=>x.Path),StringComparer.OrdinalIgnoreCase))warnings.Add($"Declared embedded binary '{missing}' is missing from the package.");
        if(!packageVerified)warnings.Add("The stored package hash does not match the exchange record.");
        return new(packageHash,expectedPackageSha256,packageVerified,receipts.Count,receipts.Count(x=>x.Verified),receipts,warnings);
    }

    private static bool IsZip(byte[] bytes)=>bytes.Length>=4&&bytes[0]==0x50&&bytes[1]==0x4b;
    private static void ValidateEntry(string name){var normalized=name.Replace('\\','/');if(normalized.StartsWith('/')||normalized.Contains("../",StringComparison.Ordinal)||Path.IsPathRooted(name))throw new InvalidDataException("The ReqIF package contains an unsafe entry path.");}
}
