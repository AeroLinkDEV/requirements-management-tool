using System.Data;
using System.Data.Common;
using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ExternalIdentityProviderView(Guid Id,string Key,string DisplayName,ExternalIdentityProtocol Protocol,string Issuer,string SubjectClaim,string GroupClaim,bool Enabled,string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? DisabledAt);
public sealed record ExternalGroupRoleMappingView(Guid Id,Guid ProviderId,string ExternalGroup,Guid ProgramId,ProgramRole Role,bool Enabled,string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? DisabledAt);

public sealed class ExternalIdentityAdministrationService(AeroLinkDbContext db)
{
    public async Task<IReadOnlyList<ExternalIdentityProviderView>> ListProvidersAsync(CancellationToken ct)
    {
        var rows=new List<ExternalIdentityProviderView>();
        await using var command=CreateCommand("SELECT id, key, display_name, protocol, issuer, subject_claim, group_claim, enabled, created_by, created_at, disabled_at FROM external_identity_providers ORDER BY key");
        await OpenAsync(command.Connection!,ct);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(ReadProvider(reader));
        return rows;
    }

    public async Task<IReadOnlyList<ExternalGroupRoleMappingView>> ListMappingsAsync(Guid? providerId,Guid? programId,CancellationToken ct)
    {
        var sql="SELECT id, provider_id, external_group, program_id, role, enabled, created_by, created_at, disabled_at FROM external_group_role_mappings WHERE 1=1";
        if(providerId is not null)sql+=" AND provider_id=@providerId";
        if(programId is not null)sql+=" AND program_id=@programId";
        sql+=" ORDER BY provider_id, program_id, external_group, role";
        var rows=new List<ExternalGroupRoleMappingView>();
        await using var command=CreateCommand(sql);
        if(providerId is not null)Add(command,"providerId",providerId.Value);
        if(programId is not null)Add(command,"programId",programId.Value);
        await OpenAsync(command.Connection!,ct);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(ReadMapping(reader));
        return rows;
    }

    public async Task<ExternalIdentityProviderView> CreateProviderAsync(string key,string displayName,ExternalIdentityProtocol protocol,string issuer,string subjectClaim,string groupClaim,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var provider=new ExternalIdentityProvider(key,displayName,protocol,issuer,subjectClaim,groupClaim,actor,now);
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            await ExecuteAsync("INSERT INTO external_identity_providers (id,key,display_name,protocol,issuer,subject_claim,group_claim,enabled,created_by,created_at,disabled_at) VALUES (@id,@key,@displayName,@protocol,@issuer,@subjectClaim,@groupClaim,@enabled,@createdBy,@createdAt,NULL)",ct,
                ("id",provider.Id),("key",provider.Key),("displayName",provider.DisplayName),("protocol",provider.Protocol.ToString()),("issuer",provider.Issuer),("subjectClaim",provider.SubjectClaim),("groupClaim",provider.GroupClaim),("enabled",provider.Enabled),("createdBy",provider.CreatedBy),("createdAt",provider.CreatedAt));
            db.SecurityAuditEvents.Add(new("ExternalIdentityProviderCreated",actor,provider.Key,"Success",$"Created {provider.Protocol} provider '{provider.DisplayName}'.",ip,now));
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
            return ToView(provider);
        }
        catch(DbException ex){await transaction.RollbackAsync(ct);throw new InvalidOperationException("An identity provider with the same key or issuer already exists.",ex);}
    }

    public async Task<ExternalGroupRoleMappingView> CreateMappingAsync(Guid providerId,string externalGroup,Guid programId,ProgramRole role,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var mapping=new ExternalGroupRoleMapping(providerId,externalGroup,programId,role,actor,now);
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        try
        {
            if(!await ExistsAsync("SELECT 1 FROM external_identity_providers WHERE id=@id",ct,("id",providerId)))throw new KeyNotFoundException("Identity provider was not found.");
            if(!await db.Programs.AsNoTracking().AnyAsync(x=>x.Id==programId,ct))throw new KeyNotFoundException("Program was not found.");
            await ExecuteAsync("INSERT INTO external_group_role_mappings (id,provider_id,external_group,program_id,role,enabled,created_by,created_at,disabled_at) VALUES (@id,@providerId,@externalGroup,@programId,@role,@enabled,@createdBy,@createdAt,NULL)",ct,
                ("id",mapping.Id),("providerId",mapping.ProviderId),("externalGroup",mapping.ExternalGroup),("programId",mapping.ProgramId),("role",mapping.Role.ToString()),("enabled",mapping.Enabled),("createdBy",mapping.CreatedBy),("createdAt",mapping.CreatedAt));
            db.SecurityAuditEvents.Add(new("ExternalGroupRoleMappingCreated",actor,mapping.Id.ToString(),"Success",$"Mapped group '{mapping.ExternalGroup}' to {mapping.Role} for Program {mapping.ProgramId}.",ip,now));
            await db.SaveChangesAsync(ct);await transaction.CommitAsync(ct);
            return ToView(mapping);
        }
        catch(DbException ex){await transaction.RollbackAsync(ct);throw new InvalidOperationException("That provider, group, Program and role mapping already exists.",ex);}
    }

    public Task<bool> SetProviderEnabledAsync(Guid id,bool enabled,string actor,string ip,DateTimeOffset now,CancellationToken ct)
        =>SetEnabledAsync("external_identity_providers",id,enabled,"ExternalIdentityProvider",actor,ip,now,ct);
    public Task<bool> SetMappingEnabledAsync(Guid id,bool enabled,string actor,string ip,DateTimeOffset now,CancellationToken ct)
        =>SetEnabledAsync("external_group_role_mappings",id,enabled,"ExternalGroupRoleMapping",actor,ip,now,ct);

    public async Task<IReadOnlyList<ProgramRole>> ResolveRolesAsync(Guid providerId,string issuer,IEnumerable<string> externalGroups,Guid programId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(issuer)||externalGroups is null)return [];
        var groups=externalGroups.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if(groups.Count==0)return [];
        var provider=await ReadSingleProviderAsync(providerId,ct);
        if(provider is null||!provider.Enabled||!UriEquals(provider.Issuer,issuer))return [];
        var mappings=await ListMappingsAsync(providerId,programId,ct);
        return mappings.Where(x=>x.Enabled&&groups.Contains(x.ExternalGroup,StringComparer.Ordinal)).Select(x=>x.Role).Distinct().Order().ToList();
    }

    private async Task<bool> SetEnabledAsync(string table,Guid id,bool enabled,string eventPrefix,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var affected=await ExecuteAsync($"UPDATE {table} SET enabled=@enabled, disabled_at=@disabledAt WHERE id=@id AND enabled<>@enabled",ct,("enabled",enabled),("disabledAt",enabled?null:now),("id",id));
        if(affected==0)return await ExistsAsync($"SELECT 1 FROM {table} WHERE id=@id",ct,("id",id));
        db.SecurityAuditEvents.Add(new($"{eventPrefix}{(enabled?"Enabled":"Disabled")}",actor,id.ToString(),"Success",$"{(enabled?"Enabled":"Disabled")} {eventPrefix}.",ip,now));
        await db.SaveChangesAsync(ct);return true;
    }

    private async Task<ExternalIdentityProviderView?> ReadSingleProviderAsync(Guid id,CancellationToken ct)
    {
        await using var command=CreateCommand("SELECT id, key, display_name, protocol, issuer, subject_claim, group_claim, enabled, created_by, created_at, disabled_at FROM external_identity_providers WHERE id=@id");Add(command,"id",id);await OpenAsync(command.Connection!,ct);await using var reader=await command.ExecuteReaderAsync(ct);return await reader.ReadAsync(ct)?ReadProvider(reader):null;
    }
    private DbCommand CreateCommand(string sql){var command=db.Database.GetDbConnection().CreateCommand();command.CommandText=sql;command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();return command;}
    private async Task<int> ExecuteAsync(string sql,CancellationToken ct,params (string Name,object? Value)[] values){await using var command=CreateCommand(sql);foreach(var value in values)Add(command,value.Name,value.Value);await OpenAsync(command.Connection!,ct);return await command.ExecuteNonQueryAsync(ct);}
    private async Task<bool> ExistsAsync(string sql,CancellationToken ct,params (string Name,object? Value)[] values){await using var command=CreateCommand(sql);foreach(var value in values)Add(command,value.Name,value.Value);await OpenAsync(command.Connection!,ct);return await command.ExecuteScalarAsync(ct) is not null;}
    private static void Add(DbCommand command,string name,object? value){var parameter=command.CreateParameter();parameter.ParameterName="@"+name;parameter.Value=value??DBNull.Value;command.Parameters.Add(parameter);}
    private static Task OpenAsync(DbConnection connection,CancellationToken ct)=>connection.State==ConnectionState.Open?Task.CompletedTask:connection.OpenAsync(ct);
    private static ExternalIdentityProviderView ReadProvider(DbDataReader r)=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),Enum.Parse<ExternalIdentityProtocol>(r.GetString(3)),r.GetString(4),r.GetString(5),r.GetString(6),r.GetBoolean(7),r.GetString(8),r.GetFieldValue<DateTimeOffset>(9),r.IsDBNull(10)?null:r.GetFieldValue<DateTimeOffset>(10));
    private static ExternalGroupRoleMappingView ReadMapping(DbDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetGuid(3),Enum.Parse<ProgramRole>(r.GetString(4)),r.GetBoolean(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?null:r.GetFieldValue<DateTimeOffset>(8));
    private static ExternalIdentityProviderView ToView(ExternalIdentityProvider x)=>new(x.Id,x.Key,x.DisplayName,x.Protocol,x.Issuer,x.SubjectClaim,x.GroupClaim,x.Enabled,x.CreatedBy,x.CreatedAt,x.DisabledAt);
    private static ExternalGroupRoleMappingView ToView(ExternalGroupRoleMapping x)=>new(x.Id,x.ProviderId,x.ExternalGroup,x.ProgramId,x.Role,x.Enabled,x.CreatedBy,x.CreatedAt,x.DisabledAt);
    private static bool UriEquals(string left,string right)=>Uri.TryCreate(right.Trim(),UriKind.Absolute,out var parsed)&&string.Equals(new Uri(left).AbsoluteUri.TrimEnd('/'),parsed.AbsoluteUri.TrimEnd('/'),StringComparison.OrdinalIgnoreCase);
}
