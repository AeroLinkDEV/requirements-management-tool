using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record OidcAuthorizationRequestState(Guid AttemptId,Guid ProviderId,string ReturnPath,string State,string Nonce,string CodeVerifier,string CodeChallenge,DateTimeOffset ExpiresAt);
public sealed record OidcConsumedAuthorizationState(Guid ProviderId,string ReturnPath,string Nonce,string CodeVerifier);

public sealed class OidcAuthorizationStateService(AeroLinkDbContext db,IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector protector=dataProtection.CreateProtector("AeroLink.Identity.Oidc.AuthorizationState.v1");

    public async Task<OidcAuthorizationRequestState> CreateAsync(Guid providerId,string? returnPath,DateTimeOffset now,CancellationToken ct)
    {
        if(providerId==Guid.Empty)throw new ArgumentException("providerId is required.",nameof(providerId));
        var safeReturn=SafeReturnPath(returnPath);
        var attemptId=Guid.NewGuid();
        var nonce=RandomToken(32);
        var verifier=RandomToken(64);
        var stateId=RandomToken(32);
        var expiresAt=now.AddMinutes(10);
        var payload=JsonSerializer.Serialize(new ProtectedState(attemptId,providerId,stateId,safeReturn,expiresAt));
        var state=protector.Protect(payload);
        await ExecuteAsync("INSERT INTO oidc_authentication_attempts (id,provider_id,state_hash,nonce_hash,code_verifier_protected,return_path,created_at,expires_at,consumed_at) VALUES (@id,@providerId,@stateHash,@nonceHash,@verifier,@returnPath,@createdAt,@expiresAt,NULL)",ct,
            ("id",attemptId),("providerId",providerId),("stateHash",Digest(stateId)),("nonceHash",Digest(nonce)),("verifier",protector.Protect(verifier)),("returnPath",safeReturn),("createdAt",now),("expiresAt",expiresAt));
        return new(attemptId,providerId,safeReturn,state,nonce,verifier,PkceChallenge(verifier),expiresAt);
    }

    public async Task<OidcConsumedAuthorizationState?> ConsumeAsync(string? protectedState,string? nonce,DateTimeOffset now,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(protectedState)||string.IsNullOrWhiteSpace(nonce))return null;
        ProtectedState payload;
        try{payload=JsonSerializer.Deserialize<ProtectedState>(protector.Unprotect(protectedState))??throw new InvalidOperationException();}
        catch{return null;}
        if(payload.ExpiresAt<=now||payload.AttemptId==Guid.Empty||payload.ProviderId==Guid.Empty)return null;

        await using var transaction=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
        await using var command=CreateCommand("SELECT provider_id,state_hash,nonce_hash,code_verifier_protected,return_path,expires_at,consumed_at FROM oidc_authentication_attempts WHERE id=@id");
        Add(command,"id",payload.AttemptId);await OpenAsync(command.Connection!,ct);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        var providerId=ReadGuid(reader,0);
        var stateHash=reader.GetString(1);var nonceHash=reader.GetString(2);var verifierProtected=reader.GetString(3);var returnPath=reader.GetString(4);var expiresAt=ReadDate(reader,5);var consumed=!reader.IsDBNull(6);
        await reader.DisposeAsync();
        if(consumed||expiresAt<=now||providerId!=payload.ProviderId||!FixedEquals(stateHash,Digest(payload.StateId))||!FixedEquals(nonceHash,Digest(nonce)))return null;
        var updated=await ExecuteAsync("UPDATE oidc_authentication_attempts SET consumed_at=@now WHERE id=@id AND consumed_at IS NULL",ct,("now",now),("id",payload.AttemptId));
        if(updated!=1)return null;
        await transaction.CommitAsync(ct);
        string verifier;try{verifier=protector.Unprotect(verifierProtected);}catch{return null;}
        return new(providerId,SafeReturnPath(returnPath),nonce,verifier);
    }

    public Task<int> RemoveExpiredAsync(DateTimeOffset now,CancellationToken ct)=>ExecuteAsync("DELETE FROM oidc_authentication_attempts WHERE expires_at<@now OR consumed_at IS NOT NULL",ct,("now",now));

    public static string SafeReturnPath(string? value)
    {
        var path=string.IsNullOrWhiteSpace(value)?"/":value.Trim();
        if(!path.StartsWith('/')||path.StartsWith("//",StringComparison.Ordinal)||Uri.TryCreate(path,UriKind.Absolute,out _))return "/";
        return path.Length>500?"/":path;
    }
    public static string PkceChallenge(string verifier)=>Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string RandomToken(int bytes)=>Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Digest(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left,string right)=>CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left),Encoding.ASCII.GetBytes(right));
    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private sealed record ProtectedState(Guid AttemptId,Guid ProviderId,string StateId,string ReturnPath,DateTimeOffset ExpiresAt);

    private System.Data.Common.DbCommand CreateCommand(string sql){var command=db.Database.GetDbConnection().CreateCommand();command.CommandText=sql;command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();return command;}
    private static void Add(System.Data.Common.DbCommand command,string name,object value){var parameter=command.CreateParameter();parameter.ParameterName="@"+name;parameter.Value=value;command.Parameters.Add(parameter);}
    private static async Task OpenAsync(System.Data.Common.DbConnection connection,CancellationToken ct){if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(ct);}
    private async Task<int> ExecuteAsync(string sql,CancellationToken ct,params (string Name,object Value)[] values){await using var command=CreateCommand(sql);foreach(var value in values)Add(command,value.Name,value.Value);await OpenAsync(command.Connection!,ct);return await command.ExecuteNonQueryAsync(ct);}
    private static Guid ReadGuid(System.Data.Common.DbDataReader reader,int ordinal){var value=reader.GetValue(ordinal);return value is Guid guid?guid:Guid.Parse(value.ToString()!);}
    private static DateTimeOffset ReadDate(System.Data.Common.DbDataReader reader,int ordinal){var value=reader.GetValue(ordinal);return value is DateTimeOffset dto?dto:new DateTimeOffset(Convert.ToDateTime(value));}
}
