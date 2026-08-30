using System.Text.Json;
using AeroLink.Domain.Common;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Api;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapAeroLinkOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group=app.MapGroup("/api/operations");
        group.MapGet("/overview",OverviewAsync);
        group.MapGet("/notifications",NotificationOperationsAsync);
        group.MapPost("/notifications/transport-test",QueueNotificationTransportTestAsync);
        group.MapPost("/qualification-runs",RecordQualificationAsync);
        group.MapPost("/restore-drills",RecordRestoreDrillAsync);
        group.MapPost("/retention-policies",RecordRetentionPolicyAsync);
        group.MapPost("/upgrade-evidence",RecordUpgradeEvidenceAsync);
        group.MapPost("/alerts",OpenAlertAsync);
        group.MapPost("/alerts/{id:guid}/resolve",ResolveAlertAsync);
        app.MapAeroLinkExternalIdentityAdminEndpoints();
        app.MapAeroLinkVerificationImpactEndpoints();
        return app;
    }

    // SMTP configuration and delivery history are installation operations, not project data. A global
    // administrator is the narrow existing authority that may inspect another person's notification address
    // and a relay's failure classification. Do this check before querying deliveries.
    private static async Task<IResult> NotificationOperationsAsync(HttpContext http, AeroLinkDbContext db,
        IConfiguration configuration, IEmailSender sender, NotificationLinkBuilder links, CancellationToken ct)
    {
        if (!http.UserAccount().IsAdministrator) return Results.Forbid();
        // Sequence is the provider-safe chronological queue key. SQLite cannot translate DateTimeOffset ORDER BY.
        var deliveryCounts = await db.NotificationDeliveries.AsNoTracking()
            .GroupBy(x => x.State).Select(x => new { State = x.Key, Count = x.Count() }).ToListAsync(ct);
        var deliveries = await db.NotificationDeliveries.AsNoTracking().OrderByDescending(x => x.Sequence)
            .Take(100).ToListAsync(ct);
        var configuredBaseUrl = configuration["Notifications:BaseUrl"];
        var baseUrl = links.BaseUrl;
        var validBaseUrl = baseUrl is not null;
        var smtpHost = configuration["Notifications:Smtp:Host"]?.Trim();
        var smtpPort = int.TryParse(configuration["Notifications:Smtp:Port"], out var configuredPort)
            && configuredPort is > 0 and <= 65535 ? configuredPort : 25;
        var hasCredentials = !string.IsNullOrWhiteSpace(configuration["Notifications:Smtp:UserName"])
            && !string.IsNullOrWhiteSpace(configuration["Notifications:Smtp:Password"]);
        return Results.Ok(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            smtp = new
            {
                configured = sender.IsConfigured,
                hostConfigured = !string.IsNullOrWhiteSpace(smtpHost),
                port = smtpPort,
                useStartTls = !string.Equals(configuration["Notifications:Smtp:UseStartTls"], "false", StringComparison.OrdinalIgnoreCase),
                credentialsConfigured = hasCredentials,
                // A from address is not a secret, but do not echo arbitrary configuration in this operation.
                fromConfigured = !string.IsNullOrWhiteSpace(configuration["Notifications:Smtp:From"])
            },
            links = new { configured = !string.IsNullOrWhiteSpace(configuredBaseUrl), valid = validBaseUrl, baseUrl = validBaseUrl ? baseUrl : null },
            totals = new
            {
                pending = DeliveryCount(NotificationDeliveryState.Pending),
                sent = DeliveryCount(NotificationDeliveryState.Sent),
                failed = DeliveryCount(NotificationDeliveryState.Failed),
                suppressed = DeliveryCount(NotificationDeliveryState.Suppressed)
            },
            deliveries = deliveries.Select(x => new
            {
                x.Id, x.NotificationId, x.Recipient, address = RedactAddress(x.Address),
                channel = x.Channel.ToString(), state = x.State.ToString(), x.Attempts,
                // Relay exceptions can contain operational detail; cap their exposed form to a bounded,
                // non-body status while the full server-side delivery record remains controlled evidence.
                detail = SafeDeliveryDetail(x.State, x.LastError), x.CreatedAt, x.UpdatedAt, x.CompletedAt
            })
        });

        int DeliveryCount(NotificationDeliveryState state) => deliveryCounts.SingleOrDefault(x => x.State == state)?.Count ?? 0;
    }

    private static async Task<IResult> QueueNotificationTransportTestAsync(NotificationTransportTestRequest request,
        HttpContext http, AeroLinkDbContext db, CancellationToken ct)
    {
        var actor = http.UserAccount();
        if (!actor.IsAdministrator) return Results.Forbid();
        if (!await db.Projects.AsNoTracking().AnyAsync(x => x.Id == request.ProjectId, ct)) return Results.NotFound();
        // The destination and relay settings never come from the caller. This is an operational proof of the
        // configured deployment, not an SMTP proxy or address-discovery API.
        var now = DateTimeOffset.UtcNow;
        var notification = new UserNotification(request.ProjectId, actor.UserName, "NotificationTransportTest",
            "AeroLink email delivery test", "This is a configured AeroLink notification transport test.", "", null, now);
        db.UserNotifications.Add(notification);
        await db.SaveChangesAsync(ct);
        var delivery = await db.NotificationDeliveries.AsNoTracking()
            .SingleAsync(x => x.NotificationId == notification.Id, ct);
        return Results.Accepted("/api/operations/notifications", new
        {
            delivery.Id, state = delivery.State.ToString(), delivery.Attempts,
            detail = SafeDeliveryDetail(delivery.State, delivery.LastError), delivery.CreatedAt
        });
    }

    private static string? SafeDeliveryDetail(NotificationDeliveryState state, string detail)
    {
        var trimmed = (detail ?? "").Trim();
        if (trimmed.Length == 0) return null;
        // Only the application's deliberate suppression reasons are fit for display. Relay exceptions can
        // contain recipient and server implementation detail; the retained record remains available to an
        // operator through protected server diagnostics without turning this API into a message proxy.
        if (state != NotificationDeliveryState.Suppressed) return "SMTP delivery failed; inspect protected server diagnostics.";
        return trimmed.Length <= 240 ? trimmed : trimmed[..240] + "…";
    }

    private static string RedactAddress(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0 || at == address.Length - 1) return "not-recorded";
        return $"{address[0]}***@{address[(at + 1)..]}";
    }

    private static async Task<IResult> OverviewAsync(Guid projectId,HttpContext http,AeroLinkDbContext db,CancellationToken ct)
    {
        if(!await http.HasProjectAccessAsync(db,projectId,ct))return Results.Forbid();var checkpoint=(await db.EnterpriseIntegrityCheckpoints.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.CreatedAt).FirstOrDefault();var jobs=await db.EnterpriseOperationJobs.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);var attachments=await db.ControlledAttachments.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct);
        var qualifications=(await db.WorkloadQualificationEvidence.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.ExecutedAt).ToList();var restoreDrills=(await db.BackupRestoreDrillEvidence.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.ExecutedAt).ToList();var retention=(await db.RetentionPolicyEvidence.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.ConfiguredAt).ToList();var upgrades=(await db.UpgradeAssuranceEvidence.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.ExecutedAt).ToList();var alerts=(await db.OperationalAlerts.AsNoTracking().Where(x=>x.ProjectId==projectId).ToListAsync(ct)).OrderByDescending(x=>x.OpenedAt).Take(100).ToList();var productionQualification=qualifications.FirstOrDefault(x=>x.MeetsProductionTarget);
        return Results.Ok(new{generatedAt=DateTimeOffset.UtcNow,health=new{database=await db.Database.CanConnectAsync(ct)?"ready":"not_ready",integrity=checkpoint?.State.ToString()??"not_checked",latestManifest=checkpoint?.ManifestHash,checkpointAt=checkpoint?.CreatedAt,openAlerts=alerts.Count(x=>x.State!=OperationalAlertState.Resolved)},operations=new{runningJobs=jobs.Count(x=>x.State==EnterpriseJobState.Running),failedJobs=jobs.Count(x=>x.State==EnterpriseJobState.Failed),attachments=attachments.Count,attachmentBytes=attachments.Sum(x=>x.Size),unverifiedAttachments=attachments.Count(x=>x.IntegrityVerifiedAt is null)},qualification=new{scaleTargetRequirements=50_000,concurrentUserTarget=150,qualified=productionQualification is not null,evidence=productionQualification is null?null:new{productionQualification.Id,productionQualification.Environment,productionQualification.RequirementCount,productionQualification.ConcurrentUsers,productionQualification.DurationMinutes,productionQualification.ReportHash,productionQualification.ExecutedBy,productionQualification.ExecutedAt}},restoreAssurance=restoreDrills.Take(20).Select(x=>new{x.Id,state=x.State.ToString(),x.TargetRpoMinutes,x.ActualRpoMinutes,x.TargetRtoMinutes,x.ActualRtoMinutes,x.BackupLocation,x.BackupHash,x.EvidenceHash,x.ExecutedAt}),retentionPolicies=retention.GroupBy(x=>x.RecordType).Select(x=>x.First()).Select(x=>new{x.Id,x.RecordType,x.RetentionDays,x.LegalHold,x.Rationale,x.ConfiguredAt}),upgradeEvidence=upgrades.Take(20).Select(x=>new{x.Id,x.FromVersion,x.ToVersion,state=x.State.ToString(),x.EvidenceHash,x.ExecutedAt}),alerts=alerts.Select(x=>new{x.Id,x.Severity,x.Signal,x.Detail,x.RunbookUrl,state=x.State.ToString(),x.OpenedAt,x.ResolvedAt})});
    }

    private static async Task<IResult> RecordQualificationAsync(RecordQualificationRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await Authorized(request.ProjectId,http,db,identity,ct))return Results.Forbid();try{using var _=JsonDocument.Parse(request.ResultsJson);var item=new WorkloadQualificationEvidence(request.ProjectId,request.Environment,request.RequirementCount,request.ConcurrentUsers,request.DurationMinutes,request.ResultsJson,request.ReportHash,request.AllPassed,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.WorkloadQualificationEvidence.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/operations/qualification-runs/{item.Id}",new{item.Id,state=item.State.ToString(),item.MeetsProductionTarget,item.ReportHash});}catch(JsonException){return Results.BadRequest(new{error="Qualification results must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> RecordRestoreDrillAsync(RecordRestoreDrillRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await Authorized(request.ProjectId,http,db,identity,ct))return Results.Forbid();try{var item=new BackupRestoreDrillEvidence(request.ProjectId,request.BackupLocation,request.BackupHash,request.BackupCreatedAt,request.OffsiteVerifiedAt,request.TargetRpoMinutes,request.TargetRtoMinutes,request.ActualRpoMinutes,request.ActualRtoMinutes,request.RestoreEnvironment,request.EvidenceHash,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.BackupRestoreDrillEvidence.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/operations/restore-drills/{item.Id}",new{item.Id,state=item.State.ToString(),item.ActualRpoMinutes,item.ActualRtoMinutes,item.EvidenceHash});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> RecordRetentionPolicyAsync(RecordRetentionPolicyRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await Authorized(request.ProjectId,http,db,identity,ct))return Results.Forbid();try{var item=new RetentionPolicyEvidence(request.ProjectId,request.RecordType,request.RetentionDays,request.LegalHold,request.Rationale,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.RetentionPolicyEvidence.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/operations/retention-policies/{item.Id}",new{item.Id,item.RecordType,item.RetentionDays,item.LegalHold});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> RecordUpgradeEvidenceAsync(RecordUpgradeEvidenceRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await Authorized(request.ProjectId,http,db,identity,ct))return Results.Forbid();try{using var _=JsonDocument.Parse(request.PreflightJson);using var __=JsonDocument.Parse(request.PostCheckJson);var item=new UpgradeAssuranceEvidence(request.ProjectId,request.FromVersion,request.ToVersion,request.PreflightJson,request.PostCheckJson,request.EvidenceHash,request.Passed,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.UpgradeAssuranceEvidence.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/operations/upgrade-evidence/{item.Id}",new{item.Id,state=item.State.ToString(),item.EvidenceHash});}catch(JsonException){return Results.BadRequest(new{error="Upgrade preflight and post-check evidence must be valid JSON."});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> OpenAlertAsync(OpenOperationalAlertRequest request,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {if(!await Authorized(request.ProjectId,http,db,identity,ct))return Results.Forbid();try{var item=new OperationalAlert(request.ProjectId,request.Severity,request.Signal,request.Detail,request.RunbookUrl,http.UserAccount().UserName,DateTimeOffset.UtcNow);db.OperationalAlerts.Add(item);await db.SaveChangesAsync(ct);return Results.Created($"/api/operations/alerts/{item.Id}",new{item.Id,state=item.State.ToString()});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static async Task<IResult> ResolveAlertAsync(Guid id,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)
    {var item=await db.OperationalAlerts.SingleOrDefaultAsync(x=>x.Id==id,ct);if(item is null)return Results.NotFound();if(!await Authorized(item.ProjectId,http,db,identity,ct))return Results.Forbid();try{item.Resolve(http.UserAccount().UserName,DateTimeOffset.UtcNow);await db.SaveChangesAsync(ct);return Results.Ok(new{item.Id,state=item.State.ToString(),item.ResolvedAt});}catch(DomainException ex){return Results.BadRequest(new{error=ex.Message});}}
    private static Task<bool> Authorized(Guid projectId,HttpContext http,AeroLinkDbContext db,IdentityService identity,CancellationToken ct)=>http.HasProjectRoleAsync(db,identity,projectId,ct,ProgramRole.ConfigurationManager,ProgramRole.ProgramManager,ProgramRole.Administrator);
}

public sealed record RecordQualificationRequest(Guid ProjectId,string Environment,int RequirementCount,int ConcurrentUsers,int DurationMinutes,string ResultsJson,string ReportHash,bool AllPassed);
public sealed record RecordRestoreDrillRequest(Guid ProjectId,string BackupLocation,string BackupHash,DateTimeOffset BackupCreatedAt,DateTimeOffset OffsiteVerifiedAt,int TargetRpoMinutes,int TargetRtoMinutes,int ActualRpoMinutes,int ActualRtoMinutes,string RestoreEnvironment,string EvidenceHash);
public sealed record RecordRetentionPolicyRequest(Guid ProjectId,string RecordType,int RetentionDays,bool LegalHold,string Rationale);
public sealed record RecordUpgradeEvidenceRequest(Guid ProjectId,string FromVersion,string ToVersion,string PreflightJson,string PostCheckJson,string EvidenceHash,bool Passed);
public sealed record OpenOperationalAlertRequest(Guid ProjectId,string Severity,string Signal,string Detail,string RunbookUrl);
public sealed record NotificationTransportTestRequest(Guid ProjectId);
