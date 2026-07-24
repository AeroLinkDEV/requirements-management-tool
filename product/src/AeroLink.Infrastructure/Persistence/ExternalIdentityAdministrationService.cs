using AeroLink.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Persistence;

public sealed record ExternalIdentityProviderView(Guid Id,string Key,string DisplayName,ExternalIdentityProtocol Protocol,string Issuer,string SubjectClaim,string GroupClaim,bool Enabled,string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? DisabledAt);
public sealed record ExternalGroupRoleMappingView(Guid Id,Guid ProviderId,string ExternalGroup,Guid ProgramId,ProgramRole Role,bool Enabled,string CreatedBy,DateTimeOffset CreatedAt,DateTimeOffset? DisabledAt);

/// <summary>
/// Server-authoritative administration of trusted external identity providers and their Program-scoped
/// group-to-role authority. Every mutation and its security audit event are saved in one unit of work, so
/// an authority change can never be recorded without its evidence. Authority decisions are delegated to
/// the domain records rather than reimplemented here.
/// </summary>
public sealed class ExternalIdentityAdministrationService(AeroLinkDbContext db)
{
    public async Task<IReadOnlyList<ExternalIdentityProviderView>> ListProvidersAsync(CancellationToken ct)
    {
        var providers=await db.ExternalIdentityProviders.AsNoTracking().OrderBy(x=>x.Key).ToListAsync(ct);
        return providers.Select(ToView).ToList();
    }

    public async Task<IReadOnlyList<ExternalGroupRoleMappingView>> ListMappingsAsync(Guid? providerId,Guid? programId,CancellationToken ct)
    {
        var query=db.ExternalGroupRoleMappings.AsNoTracking();
        if(providerId is not null)query=query.Where(x=>x.ProviderId==providerId.Value);
        if(programId is not null)query=query.Where(x=>x.ProgramId==programId.Value);
        var mappings=await query.OrderBy(x=>x.ProviderId).ThenBy(x=>x.ProgramId).ThenBy(x=>x.ExternalGroup).ThenBy(x=>x.Role).ToListAsync(ct);
        return mappings.Select(ToView).ToList();
    }

    public async Task<ExternalIdentityProviderView> CreateProviderAsync(string key,string displayName,ExternalIdentityProtocol protocol,string issuer,string subjectClaim,string groupClaim,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        ExternalIdentityProvider provider;
        try { provider=new(key,displayName,protocol,issuer,subjectClaim,groupClaim,actor,now); }
        catch(ArgumentException ex) { await AuditDeniedAsync("ExternalIdentityProviderCreateRejected",actor,key,"Validation",ex.Message,ip,now,ct); throw; }

        if(await db.ExternalIdentityProviders.AnyAsync(x=>x.Key==provider.Key||x.Issuer==provider.Issuer,ct))
            throw await ConflictAsync("ExternalIdentityProviderCreateRejected",actor,provider.Key,"An identity provider with the same key or issuer already exists.",ip,now,ct);

        db.ExternalIdentityProviders.Add(provider);
        db.SecurityAuditEvents.Add(new("ExternalIdentityProviderCreated",actor,provider.Key,"Success",$"Created {provider.Protocol} provider '{provider.DisplayName}' anchored to issuer {provider.Issuer}.",ip,now));
        try { await db.SaveChangesAsync(ct); }
        catch(DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            throw await ConflictAsync("ExternalIdentityProviderCreateRejected",actor,provider.Key,"An identity provider with the same key or issuer already exists.",ip,now,ct,ex);
        }
        return ToView(provider);
    }

    public async Task<ExternalGroupRoleMappingView> CreateMappingAsync(Guid providerId,string externalGroup,Guid programId,ProgramRole role,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        ExternalGroupRoleMapping mapping;
        try { mapping=new(providerId,externalGroup,programId,role,actor,now); }
        catch(ArgumentException ex) { await AuditDeniedAsync("ExternalGroupRoleMappingCreateRejected",actor,$"{providerId}/{programId}","Validation",ex.Message,ip,now,ct); throw; }

        if(!await db.ExternalIdentityProviders.AnyAsync(x=>x.Id==providerId,ct))
            throw await NotFoundAsync("ExternalGroupRoleMappingCreateRejected",actor,$"{providerId}/{programId}","Identity provider was not found.",ip,now,ct);
        if(!await db.Programs.AsNoTracking().AnyAsync(x=>x.Id==programId,ct))
            throw await NotFoundAsync("ExternalGroupRoleMappingCreateRejected",actor,$"{providerId}/{programId}","Program was not found.",ip,now,ct);
        if(await db.ExternalGroupRoleMappings.AnyAsync(x=>x.ProviderId==providerId&&x.ExternalGroup==mapping.ExternalGroup&&x.ProgramId==programId&&x.Role==role,ct))
            throw await ConflictAsync("ExternalGroupRoleMappingCreateRejected",actor,mapping.Id.ToString(),"That provider, group, Program and role mapping already exists.",ip,now,ct);

        db.ExternalGroupRoleMappings.Add(mapping);
        db.SecurityAuditEvents.Add(new("ExternalGroupRoleMappingCreated",actor,mapping.Id.ToString(),"Success",$"Mapped group '{mapping.ExternalGroup}' to {mapping.Role} for Program {mapping.ProgramId}.",ip,now));
        try { await db.SaveChangesAsync(ct); }
        catch(DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            throw await ConflictAsync("ExternalGroupRoleMappingCreateRejected",actor,mapping.Id.ToString(),"That provider, group, Program and role mapping already exists.",ip,now,ct,ex);
        }
        return ToView(mapping);
    }

    public async Task<bool> SetProviderEnabledAsync(Guid id,bool enabled,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var provider=await db.ExternalIdentityProviders.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(provider is null)
        {
            await AuditDeniedAsync("ExternalIdentityProviderStateChangeRejected",actor,id.ToString(),"NotFound","Identity provider was not found.",ip,now,ct);
            return false;
        }
        if(provider.Enabled==enabled)return true;
        if(enabled)provider.Enable();else provider.Disable(now);
        db.SecurityAuditEvents.Add(new($"ExternalIdentityProvider{(enabled?"Enabled":"Disabled")}",actor,id.ToString(),"Success",$"{(enabled?"Enabled":"Disabled")} identity provider '{provider.Key}'.",ip,now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetMappingEnabledAsync(Guid id,bool enabled,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var mapping=await db.ExternalGroupRoleMappings.FirstOrDefaultAsync(x=>x.Id==id,ct);
        if(mapping is null)
        {
            await AuditDeniedAsync("ExternalGroupRoleMappingStateChangeRejected",actor,id.ToString(),"NotFound","Group role mapping was not found.",ip,now,ct);
            return false;
        }
        if(mapping.Enabled==enabled)return true;
        if(enabled)mapping.Enable();else mapping.Disable(now);
        db.SecurityAuditEvents.Add(new($"ExternalGroupRoleMapping{(enabled?"Enabled":"Disabled")}",actor,id.ToString(),"Success",$"{(enabled?"Enabled":"Disabled")} mapping of group '{mapping.ExternalGroup}' to {mapping.Role} for Program {mapping.ProgramId}.",ip,now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Resolves the Program roles a set of directory groups grants through one trusted provider. Resolution
    /// is fail-closed: an unknown or disabled provider, a mismatched issuer, malformed group input and
    /// disabled mappings all yield no authority. Both grants and denials are recorded as security evidence.
    /// </summary>
    public async Task<IReadOnlyList<ProgramRole>> ResolveRolesAsync(Guid providerId,string? issuer,IEnumerable<string>? externalGroups,Guid programId,string actor,string ip,DateTimeOffset now,CancellationToken ct)
    {
        var target=$"{providerId}/{programId}";
        var groups=new List<string>();
        foreach(var candidate in externalGroups??[])
            if(ExternalGroupRoleMapping.TryNormalizeGroup(candidate,out var normalized)&&!groups.Contains(normalized,StringComparer.Ordinal))
                groups.Add(normalized);

        var provider=await db.ExternalIdentityProviders.AsNoTracking().FirstOrDefaultAsync(x=>x.Id==providerId,ct);
        var refusal=provider is null?"The identity provider was not found."
            :!provider.Enabled?"The identity provider is disabled."
            :!provider.MatchesIssuer(issuer)?"The presented issuer does not match the trusted anchor."
            :groups.Count==0?"No usable directory group was presented."
            :null;
        if(refusal is not null)
        {
            await AuditAsync("ExternalIdentityRoleResolutionDenied",actor,target,"Denied",refusal,ip,now,ct);
            return [];
        }

        var mappings=await db.ExternalGroupRoleMappings.AsNoTracking()
            .Where(x=>x.ProviderId==providerId&&x.ProgramId==programId&&x.Enabled).ToListAsync(ct);
        var roles=mappings.Where(x=>groups.Any(group=>x.Matches(providerId,group))).Select(x=>x.Role).Distinct().Order().ToList();
        await AuditAsync("ExternalIdentityRolesResolved",actor,target,roles.Count>0?"Success":"Denied",
            roles.Count>0?$"Resolved {string.Join(", ",roles)} from {groups.Count} presented group(s)."
                :$"No mapping granted authority for the {groups.Count} presented group(s).",ip,now,ct);
        return roles;
    }

    private async Task<InvalidOperationException> ConflictAsync(string eventType,string actor,string target,string message,string ip,DateTimeOffset now,CancellationToken ct,Exception? inner=null)
    {
        await AuditDeniedAsync(eventType,actor,target,"Conflict",message,ip,now,ct);
        return new InvalidOperationException(message,inner);
    }

    private async Task<KeyNotFoundException> NotFoundAsync(string eventType,string actor,string target,string message,string ip,DateTimeOffset now,CancellationToken ct)
    {
        await AuditDeniedAsync(eventType,actor,target,"NotFound",message,ip,now,ct);
        return new KeyNotFoundException(message);
    }

    private Task AuditDeniedAsync(string eventType,string actor,string target,string reason,string detail,string ip,DateTimeOffset now,CancellationToken ct)
        =>AuditAsync(eventType,actor,target,"Denied",$"{reason}: {detail}",ip,now,ct);

    private async Task AuditAsync(string eventType,string actor,string target,string outcome,string detail,string ip,DateTimeOffset now,CancellationToken ct)
    {
        db.SecurityAuditEvents.Add(new(eventType,actor,string.IsNullOrWhiteSpace(target)?"external-identity":target,outcome,detail,ip,now));
        await db.SaveChangesAsync(ct);
    }

    private static ExternalIdentityProviderView ToView(ExternalIdentityProvider x)=>new(x.Id,x.Key,x.DisplayName,x.Protocol,x.Issuer,x.SubjectClaim,x.GroupClaim,x.Enabled,x.CreatedBy,x.CreatedAt,x.DisabledAt);
    private static ExternalGroupRoleMappingView ToView(ExternalGroupRoleMapping x)=>new(x.Id,x.ProviderId,x.ExternalGroup,x.ProgramId,x.Role,x.Enabled,x.CreatedBy,x.CreatedAt,x.DisabledAt);
}
