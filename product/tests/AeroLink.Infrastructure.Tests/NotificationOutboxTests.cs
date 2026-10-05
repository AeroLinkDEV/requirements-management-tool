using System.Net;
using System.Net.Sockets;
using System.Text;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AeroLink.Infrastructure.Tests;

// Test-audit disposition for #1482: retain the atomic-save, uncertain-save deduplication, preference,
// address and exact-obligation contracts below. Retire rich title/person/change-count/five-day template
// expectations: identifier-only notices deliberately supersede that content, and the old Compose and
// IEmailSender dispatch have no production callers. Real TLS/retry/provider owners are separate fixtures.
// These owners use the real save pipeline, typed contexts, production DI and an owned Capture relay.
public sealed class NotificationOutboxTests
{
    [Fact]
    public async Task Raising_a_bound_notification_queues_a_held_root_atomically_without_plaintext_contact()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        Guid noticeId;
        await using (var db = fixture.NewDb())
        {
            var (requirement, assignment, notice) = fixture.Assignment();
            noticeId = notice.Id;
            db.AddRange(requirement, assignment, notice);
            await db.SaveChangesAsync();
        }
        await using var asserted = fixture.NewDb();
        var root = await asserted.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(noticeId, root.BoundNotificationId);
        Assert.Equal(NotificationDeliveryState.HeldAdmission, root.State);
        Assert.Equal("approver.user", root.Recipient);
        Assert.Equal("", root.Address);
        Assert.Null(root.AdmissionEpochId);
        Assert.Empty(await asserted.NotificationDeliveryGenerations.ToListAsync());
        Assert.Single(await asserted.NotificationContexts.ToListAsync());
    }

    private sealed class FailOnceSaveInterceptor : SaveChangesInterceptor
    {
        private int failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref failed, 1, 0) == 0)
                throw new DbUpdateException("Injected uncertain notification save.");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Retrying_an_uncertain_save_does_not_append_a_second_root_or_context()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        await using var db = fixture.NewDb(new FailOnceSaveInterceptor());
        var (requirement, assignment, notice) = fixture.Assignment();
        db.AddRange(requirement, assignment, notice);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await db.SaveChangesAsync();
        await using var asserted = fixture.NewDb();
        Assert.Single(await asserted.UserNotifications.ToListAsync());
        Assert.Single(await asserted.NotificationContexts.ToListAsync());
        Assert.Single(await asserted.NotificationDeliveries.ToListAsync());
    }

    [Fact]
    public async Task A_rolled_back_business_save_announces_nothing()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        await using (var db = fixture.NewDb())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var (requirement, assignment, notice) = fixture.Assignment();
            db.AddRange(requirement, assignment, notice);
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var asserted = fixture.NewDb();
        Assert.Empty(await asserted.UserNotifications.ToListAsync());
        Assert.Empty(await asserted.NotificationContexts.ToListAsync());
        Assert.Empty(await asserted.NotificationDeliveries.ToListAsync());
    }

    [Theory]
    [InlineData(false, "approver@example.test", "turned off")]
    [InlineData(true, "", "no email address")]
    public async Task A_recipient_refusal_is_recorded_without_erasing_the_in_app_notice(bool emailEnabled, string email, string reason)
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync(email);
        await using var db = fixture.NewDb();
        var preference = new NotificationPreference("approver.user", DateTimeOffset.UtcNow);
        preference.SetEmailEnabled(emailEnabled, DateTimeOffset.UtcNow);
        db.NotificationPreferences.Add(preference);
        await db.SaveChangesAsync();
        var (requirement, assignment, notice) = fixture.Assignment();
        db.AddRange(requirement, assignment, notice);
        await db.SaveChangesAsync();
        var root = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(NotificationDeliveryState.Suppressed, root.State);
        Assert.Contains(reason, root.LastError);
        Assert.Single(await db.UserNotifications.ToListAsync());
        Assert.Empty(await db.NotificationDeliveryGenerations.ToListAsync());
    }

    [Fact]
    public async Task Capture_dispatch_persists_one_synthetic_physical_receipt_without_repeating_accepted_mail()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        await using var relay = new OwnedNotificationCaptureRelay();
        await fixture.ActivateCaptureAsync(relay.Port);
        Guid noticeId;
        await using (var db = fixture.NewDb())
        {
            var (requirement, assignment, notice) = fixture.Assignment();
            noticeId = notice.Id;
            db.AddRange(requirement, assignment, notice);
            await db.SaveChangesAsync();
            Assert.NotNull((await db.NotificationDeliveries.SingleAsync()).AdmissionEpochId);
        }
        var first = await fixture.DispatchAsync();
        var message = await relay.Message.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, first.Sent);
        Assert.Equal("AeroLink transport diagnostic", message.Subject);
        Assert.Equal("capture@aerolink.invalid", Assert.Single(message.To.Mailboxes).Address);
        Assert.Contains("synthetic", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private fixture statement", message.TextBody);
        Assert.DoesNotContain("Private assignment prose", message.HtmlBody);
        Assert.DoesNotContain("/unsubscribe", message.TextBody);
        Assert.Empty(message.Attachments);
        var second = await fixture.DispatchAsync();
        Assert.Equal(0, second.Sent);
        await using var asserted = fixture.NewDb();
        var generation = await asserted.NotificationDeliveryGenerations.SingleAsync();
        var attempt = await asserted.NotificationPhysicalAttempts.SingleAsync();
        Assert.Equal(NotificationGenerationState.Captured, generation.State);
        Assert.Equal(NotificationAttemptOutcome.Captured, attempt.Outcome);
        Assert.Equal(1, generation.Attempts);
        Assert.True(attempt.TransportDisposed);
        Assert.Equal(generation.MessageId, message.MessageId);
        Assert.Equal(generation.BodyHash, NotificationInstallationAuthority.Hash(
            fixture.Protection.Unprotect(generation.ProtectedMime)));
        Assert.Equal(noticeId, (await asserted.NotificationDeliveries.SingleAsync()).NotificationId);
    }

    [Fact]
    public async Task A_completed_assignment_does_not_become_active_when_the_same_person_receives_a_new_assignment()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        NotificationContext oldContext;
        NotificationContext newContext;
        await using (var db = fixture.NewDb())
        {
            var (requirement, assignment, notice) = fixture.Assignment();
            oldContext = notice.Context!;
            db.AddRange(requirement, assignment, notice);
            await db.SaveChangesAsync();
            Assert.True((await fixture.EligibleAsync(oldContext)).Eligible);
            assignment.Complete("approver.user", assignment.Version, DateTimeOffset.UtcNow);
            var replacement = new ArtifactAssignment(fixture.ProjectId, "Requirement", requirement.Id, null,
                "approver.user", "Private replacement title", "Private replacement prose", null, "author.user", DateTimeOffset.UtcNow);
            var later = new UserNotification(fixture.ProjectId, "approver.user", "RequirementAssignment",
                "Later assignment", "Later private prose", $"requirement:{requirement.Id}", requirement.Id, DateTimeOffset.UtcNow);
            later.BindContext(NotificationContext.RequirementAssignment(later, requirement, replacement));
            newContext = later.Context!;
            db.AddRange(replacement, later);
            await db.SaveChangesAsync();
        }
        var ended = await fixture.EligibleAsync(oldContext);
        Assert.False(ended.Eligible);
        Assert.Equal("OriginalObligationEnded", ended.SafeCode);
        Assert.True((await fixture.EligibleAsync(newContext)).Eligible);
        Assert.NotEqual(oldContext.SourceId, newContext.SourceId);
    }

    [Fact]
    public async Task An_old_change_request_cycle_is_not_reassigned_to_a_later_cycle_for_the_same_person()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        NotificationContext original;
        NotificationContext later;
        await using (var db = fixture.NewDb())
        {
            var record = fixture.ChangeRequest();
            var cycle = record.SubmitForReview("author.user", [new("approver.user", "Approver User")], DateTimeOffset.UtcNow);
            var notice = fixture.ChangeNotice(record, cycle);
            original = notice.Context!;
            db.AddRange(record, notice);
            await db.SaveChangesAsync();
            Assert.True((await fixture.EligibleAsync(original)).Eligible);
            record.RequestChanges("approver.user", "Revise it.", DateTimeOffset.UtcNow);
            var next = record.SubmitForReview("author.user", [new("approver.user", "Approver User")], DateTimeOffset.UtcNow);
            var newNotice = fixture.ChangeNotice(record, next);
            later = newNotice.Context!;
            db.UserNotifications.Add(newNotice);
            await db.SaveChangesAsync();
        }
        Assert.False((await fixture.EligibleAsync(original)).Eligible);
        Assert.True((await fixture.EligibleAsync(later)).Eligible);
        Assert.NotEqual(original.ReviewCycleId, later.ReviewCycleId);
        Assert.Equal(1, original.Cycle);
        Assert.Equal(2, later.Cycle);
    }

    [Fact]
    public async Task An_unnumbered_test_assessment_keeps_its_exact_frozen_approval_step_identity()
    {
        using var fixture = await NotificationInfrastructureFixture.CreateAsync();
        NotificationContext context;
        await using (var db = fixture.NewDb())
        {
            var source = fixture.ChangeRequest();
            var assessment = new TestChangeReview(fixture.ProjectId, fixture.ReleaseId, source.Id,
                TestChangeReviewDiscipline.System, source.DisplayNumber, DateTimeOffset.UtcNow, authorId: "author.user");
            assessment.RecordNoTestChangeRequired("author.user", "No procedure is affected.", DateTimeOffset.UtcNow);
            var workflow = NotificationInfrastructureFixture.ApprovalWorkflow();
            var cycle = assessment.SubmitForReview("author.user",
                [new ApproverSelection("approver.user", "Approver User", ProgramRole.SystemTestEngineer)],
                everyItemResolved: true, now: DateTimeOffset.UtcNow, workflow: workflow);
            var notice = new UserNotification(fixture.ProjectId, "approver.user", "TestChangeRequestApprovalRequested",
                "Assessment approval requested", "Private assessment detail", $"test-change-request:{assessment.Id}", assessment.Id, DateTimeOffset.UtcNow);
            notice.BindContext(NotificationContext.TestChangeStep(notice, assessment, cycle, Assert.Single(cycle.Steps)));
            context = notice.Context!;
            db.AddRange(source, assessment, notice);
            await db.SaveChangesAsync();
        }
        Assert.Equal("Approval", context.Stage);
        Assert.Equal(NotificationSourceFamily.TCRStep, context.SourceFamily);
        Assert.NotNull(context.ApprovalStepId);
        Assert.True((await fixture.EligibleAsync(context)).Eligible);
        Assert.DoesNotContain("SYSTPCR-", context.Identifier);
    }

    [Fact]
    public void An_unsubscribe_token_is_recipient_bound_and_cannot_be_forged()
    {
        var tokens = new UnsubscribeTokenService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Notifications:UnsubscribeSecret"] = "unsubscribe-secret-0123456789-abcdefghij" }).Build());
        var issued = tokens.Issue("approver.user")!;
        Assert.True(tokens.Validate("APPROVER.USER", issued));
        Assert.False(tokens.Validate("someone.else", issued));
        Assert.False(tokens.Validate("approver.user", "not-the-token"));
    }

    [Fact]
    public void Without_a_valid_unsubscribe_secret_no_capability_is_offered()
    {
        var tokens = new UnsubscribeTokenService(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Notifications:UnsubscribeSecret"] = "too-short" }).Build());
        Assert.False(tokens.IsConfigured);
        Assert.Null(tokens.Issue("approver.user"));
        Assert.False(tokens.Validate("approver.user", "anything"));
    }
}

// Shared only by these owned infrastructure contract tests. All calls below are production boundaries.
internal sealed class NotificationInfrastructureFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"aerolink-notification-owner-{Guid.NewGuid():N}");
    internal ServiceProvider Services { get; private set; } = null!;
    internal Guid ProjectId { get; private set; }
    internal Guid ReleaseId { get; private set; }
    internal string DatabasePath => Path.Combine(root, "owner.db");
    internal NotificationContentProtection Protection => Services.GetRequiredService<NotificationContentProtection>();
    private NotificationInfrastructureFixture() { }
    internal static async Task<NotificationInfrastructureFixture> CreateAsync(string email = "approver@example.test")
    {
        var fixture = new NotificationInfrastructureFixture();
        Directory.CreateDirectory(fixture.root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:AeroLink"] = $"Data Source={fixture.DatabasePath};Pooling=False",
            ["Instance:InstanceId"] = Guid.NewGuid().ToString("D"),
            ["DataProtection:KeyRingPath"] = Path.Combine(fixture.root, "keys"),
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config);
        services.AddAeroLinkInfrastructure(config);
        fixture.Services = services.BuildServiceProvider();
        await using var db = fixture.NewDb();
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Notify Program", "NTP");
        var project = new ProjectRecord(program.Id, "Software", "Notify Software");
        var release = new SoftwareRelease(project.Id, "1.0", true);
        var account = new UserAccount("approver.user", "Approver User", email,
            IdentityService.HashPassword("AeroLink!Test2026"), DateTimeOffset.UtcNow);
        db.AddRange(program, project, release, account,
            new ProgramMembership(account.Id, program.Id, ProgramRole.Engineer, "admin", DateTimeOffset.UtcNow),
            new ProgramMembership(account.Id, program.Id, ProgramRole.Approver, "admin", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        fixture.ProjectId = project.Id; fixture.ReleaseId = release.Id;
        return fixture;
    }
    internal AeroLinkDbContext NewDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={DatabasePath};Pooling=False");
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AeroLinkDbContext(options.Options);
    }
    internal (RequirementArtifact Requirement, ArtifactAssignment Assignment, UserNotification Notice) Assignment()
    {
        var now = DateTimeOffset.UtcNow;
        var requirement = new RequirementArtifact(ProjectId, "SYSR-00000001", RequirementLevel.System, now);
        var assignment = new ArtifactAssignment(ProjectId, "Requirement", requirement.Id, null, "approver.user",
            "Private fixture statement", "Private assignment prose", null, "author.user", now);
        var notice = new UserNotification(ProjectId, "approver.user", "RequirementAssignment",
            "Private fixture statement", "Private assignment prose", $"requirement:{requirement.Id}", requirement.Id, now);
        notice.BindContext(NotificationContext.RequirementAssignment(notice, requirement, assignment));
        return (requirement, assignment, notice);
    }
    internal SystemChangeRequest ChangeRequest()
    {
        var record = new SystemChangeRequest("SRCR-00031", 0, ProjectId, ReleaseId, "Private change title",
            "P", "A", "S", "author.user", DateTimeOffset.UtcNow);
        record.AddRequirementChange("author.user", "SYSR-00000001", 0, RequirementLevel.System,
            RequirementChangeKind.Introduce, "Private fixture statement", "Prevent stale mail.", "Review", DateTimeOffset.UtcNow);
        return record;
    }
    internal UserNotification ChangeNotice(SystemChangeRequest record, ReviewCycle cycle)
    {
        var step = Assert.Single(cycle.Steps.Where(x => x.State == ApprovalStepState.Active));
        var notice = new UserNotification(ProjectId, "approver.user",
            step.StageKind == ReviewStageKind.Approval ? "ApprovalActivated" : "ReviewActivated",
            "Private title", "Private detail", $"scr:{record.Id}", record.Id, DateTimeOffset.UtcNow);
        notice.BindContext(NotificationContext.ChangeRequestStep(notice, record, cycle, step));
        return notice;
    }
    internal static ReviewWorkflowSpecification ApprovalWorkflow() => new(Guid.NewGuid(), Guid.NewGuid(),
        "Release approval", 1, ReviewMode.Sequential,
        [new ReviewStageRequirement(0, "Approval", ProgramRole.SystemTestEngineer, ReviewStageKind.Approval)]);
    internal async Task<NotificationEligibilityResult> EligibleAsync(NotificationContext context)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationEligibility>().EvaluateAsync(context, default);
    }
    internal async Task ActivateCaptureAsync(int port)
    {
        using var scope = Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationOperationsService>();
        var saved = await service.SaveSettingsAsync("admin", new(Guid.NewGuid(), 0, NotificationMode.Capture,
            "127.0.0.1", port, "fixture@example.test", "Owned capture", "https://fixture.invalid", null), default);
        await service.ControlAsync("admin", new(Guid.NewGuid(), saved.Result.GetProperty("version").GetInt64(),
            "Activate", NotificationMode.Capture), default);
    }
    internal async Task<NotificationDispatchResult> DispatchAsync()
    {
        using var scope = Services.CreateScope();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(25, timeout.Token);
    }
    public void Dispose()
    {
        Services?.Dispose();
        // This exact GUID root was created by this fixture, and contains no caller-supplied paths.
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

internal sealed class OwnedNotificationCaptureRelay : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
    private readonly Task<MimeMessage> completion;
    internal int Port { get; }
    internal Task<MimeMessage> Message => completion;
    internal OwnedNotificationCaptureRelay()
    {
        listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        completion = ReceiveAsync();
    }
    private async Task<MimeMessage> ReceiveAsync()
    {
        using var client = await listener.AcceptTcpClientAsync(stop.Token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 capture.local ready");
        var data = new StringBuilder(); var inData = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(stop.Token);
            if (line is null) throw new IOException("Capture connection ended before QUIT.");
            if (inData)
            {
                if (line == ".") { inData = false; await writer.WriteLineAsync("250 2.0.0 accepted"); }
                else data.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
                continue;
            }
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("250 capture.local");
            else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) || line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                await writer.WriteLineAsync("250 2.1.0 accepted");
            else if (line == "DATA") { inData = true; await writer.WriteLineAsync("354 send data"); }
            else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
            else throw new IOException("Unexpected Capture SMTP command.");
        }
        using var mime = new MemoryStream(Encoding.ASCII.GetBytes(data.ToString()));
        return await MimeMessage.LoadAsync(mime, stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        try { await completion; } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
