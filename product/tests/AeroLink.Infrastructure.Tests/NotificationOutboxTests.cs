using AeroLink.Domain.Identity;
using AeroLink.Domain.Notifications;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Notifications;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// A notification that reaches nobody is indistinguishable from one that was never raised, so what is
/// asserted here is that queueing cannot be forgotten, cannot outlive a rollback, and cannot fail the work
/// it announces.
/// </summary>
public sealed class NotificationOutboxTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Managed_document_notifications_link_to_the_project_wide_open_resolver()
    {
        var (links, _) = Support();
        var id = "11111111-1111-1111-1111-111111111111";
        Assert.Equal($"/open/managed-document/{id}", NotificationLinkBuilder.PathFor($"managed-document:{id}"));
        Assert.Equal($"https://aerolink.example.test/open/managed-document/{id}", links.LinkFor($"managed-document:{id}"));
    }

    private static ReviewEmailFacts Facts(string title = "Reduce flight-plan reload latency",
        int introduced = 2, int modified = 5, int retired = 1) =>
        new("SRCR-00039.00", title, "Independent assurance · stage 2 of 3", "Software Quality Analyst",
            "Sequential · cycle 2", "Maya Patel", new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero),
            introduced, modified, retired, new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_subject_leads_with_the_identifier_and_the_ask()
    {
        // "AeroLink: Review SRCR-00039.00" buried both in a list of subjects that all start the same way.
        Assert.Equal("SRCR-00039.00 is ready for your review — AeroLink", ReviewEmailTemplate.Subject(Facts()));
    }

    [Fact]
    public void A_requirement_title_cannot_break_out_of_the_html_it_is_rendered_into()
    {
        var html = ReviewEmailTemplate.Html(Facts(title: "Reload <script>alert(1)</script> & resync"), null, null);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("&amp; resync", html);
    }

    [Fact]
    public void A_package_with_no_requirement_changes_says_so_rather_than_reading_as_a_fault()
    {
        var empty = ReviewEmailTemplate.PlainText(Facts(introduced: 0, modified: 0, retired: 0), null, null);
        var partial = ReviewEmailTemplate.PlainText(Facts(introduced: 0, modified: 3, retired: 0), null, null);

        Assert.Contains("No requirement changes", empty);
        Assert.DoesNotContain("0 introduced", empty);
        Assert.Contains("3 modified", partial);
        Assert.DoesNotContain("0 introduced", partial);
    }

    [Fact]
    public void The_html_and_the_plain_text_carry_the_same_facts_and_the_same_link()
    {
        const string link = "https://aerolink.example.test/open/scr/9f31c2a4-77b0-4c1e-9a2e-3f61d0b8e512";
        var facts = Facts();

        var html = ReviewEmailTemplate.Html(facts, link, null);
        var text = ReviewEmailTemplate.PlainText(facts, link, null);

        foreach (var fact in new[] { "SRCR-00039.00", "Independent assurance", "Software Quality Analyst", "Maya Patel" })
        {
            Assert.Contains(fact, html);
            Assert.Contains(fact, text);
        }
        // The text form is not a stub. A reader whose client strips HTML still gets the address.
        Assert.Contains(link, html);
        Assert.Contains(link, text);
    }

    [Fact]
    public void With_no_public_address_neither_form_prints_a_broken_link()
    {
        var html = ReviewEmailTemplate.Html(Facts(), null, null);
        var text = ReviewEmailTemplate.PlainText(Facts(), null, null);

        Assert.DoesNotContain("<a href", html);
        Assert.Contains("Sign in to AeroLink", html);
        Assert.Contains("Sign in to AeroLink", text);
    }

    [Fact]
    public void Test_change_request_notifications_resolve_to_the_package_rather_than_the_workspace_root()
    {
        var (links, _) = Support();
        var id = "22222222-2222-2222-2222-222222222222";

        Assert.Equal($"/open/test-change-request/{id}", NotificationLinkBuilder.PathFor($"test-change-request:{id}"));
        Assert.Equal($"https://aerolink.example.test/open/test-change-request/{id}",
            links.LinkFor($"test-change-request:{id}"));
        // The route these notifications used to carry had no identifier at all, so it never reached the
        // switch: PathFor found no colon and returned the root. That is the shape being guarded against.
        Assert.Equal("/", NotificationLinkBuilder.PathFor("verification"));
    }

    [Fact]
    public void A_message_with_no_html_sends_exactly_as_it_always_did()
    {
        using var mail = SmtpEmailSender.BuildMail("aerolink@fms.test",
            new EmailMessage("approver@example.test", "AeroLink: Review SRCR-00031.00", "Open it here:\nhttps://x/y"));

        Assert.False(mail.IsBodyHtml);
        Assert.Equal("Open it here:\nhttps://x/y", mail.Body);
        Assert.Empty(mail.AlternateViews);
    }

    [Fact]
    public void An_html_message_carries_the_plain_text_first_so_a_text_only_client_is_not_left_empty()
    {
        using var mail = SmtpEmailSender.BuildMail("aerolink@fms.test",
            new EmailMessage("approver@example.test", "SRCR-00039.00 is ready for your review — AeroLink",
                "SRCR-00039.00 is ready for your review.", "<html><body><h1>SRCR-00039.00</h1></body></html>"));

        // Order is the assertion. A client renders the last view it understands, so plain text must be
        // offered before HTML or every recipient silently receives the fallback instead of the message.
        Assert.Collection(mail.AlternateViews,
            first => Assert.Equal("text/plain", first.ContentType.MediaType),
            second => Assert.Equal("text/html", second.ContentType.MediaType));
    }

    [Fact]
    public async Task Composing_a_notification_still_produces_a_text_body_that_says_everything()
    {
        var (links, tokens) = Support();
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var notification = Notification(seed.ProjectId);
            var delivery = new NotificationDelivery(notification.Id, NotificationChannel.Email,
                "approver.user", "approver@example.test", Now);

            var message = NotificationOutbox.Compose(notification, delivery, links, tokens);

            Assert.Contains("SRCR-00031.00", message.PlainTextBody);
            Assert.Contains("https://aerolink.example.test/open/scr/", message.PlainTextBody);
        }
        finally { File.Delete(seed.Path); }
    }

    private sealed class RecordingSender(bool configured = true) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public Exception? Throw { get; set; }
        public bool IsConfigured { get; } = configured;
        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            if (Throw is not null) return Task.FromException(Throw);
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (NotificationLinkBuilder Links, UnsubscribeTokenService Tokens) Support(bool withBaseUrl = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Notifications:UnsubscribeSecret"] = "unsubscribe-secret-0123456789-abcdefghij",
        };
        if (withBaseUrl) settings["Notifications:BaseUrl"] = "https://aerolink.example.test";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return (new NotificationLinkBuilder(configuration), new UnsubscribeTokenService(configuration));
    }

    private static async Task<(DbContextOptions<AeroLinkDbContext> Options, Guid ProjectId, string Path)> SeedAsync(
        string email = "approver@example.test")
    {
        var path = Path.Combine(Path.GetTempPath(), $"aerolink-notify-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using var db = new AeroLinkDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var program = new ProgramRecord("Notify Program", "NTP");
        var project = new ProjectRecord(program.Id, "Software", "Notify Software");
        var account = new UserAccount("approver.user", "Approver User", email,
            IdentityService.HashPassword("AeroLink!Test2026"), Now);
        db.AddRange(program, project, account);
        await db.SaveChangesAsync();
        return (options, project.Id, path);
    }

    private static UserNotification Notification(Guid projectId, string recipient = "approver.user") =>
        new(projectId, recipient, "ReviewActivated", "Review SRCR-00031.00",
            "You are now authorized to review SRCR-00031.00: Oceanic routing.", "scr:11111111-1111-1111-1111-111111111111", null, Now);

    [Fact]
    public async Task Raising_a_notification_queues_its_delivery_without_anyone_asking()
    {
        var seed = await SeedAsync();
        try
        {
            await using (var db = new AeroLinkDbContext(seed.Options))
            {
                // Nothing here mentions email. The endpoint that raises a notification should not have to.
                db.UserNotifications.Add(Notification(seed.ProjectId));
                await db.SaveChangesAsync();
            }

            await using var assert = new AeroLinkDbContext(seed.Options);
            var delivery = await assert.NotificationDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(NotificationDeliveryState.Pending, delivery.State);
            Assert.Equal("approver.user", delivery.Recipient);
            Assert.Equal("approver@example.test", delivery.Address);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_rolled_back_approval_announces_nothing()
    {
        var seed = await SeedAsync();
        try
        {
            await using (var db = new AeroLinkDbContext(seed.Options))
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                db.UserNotifications.Add(Notification(seed.ProjectId));
                await db.SaveChangesAsync();
                await transaction.RollbackAsync();
            }

            // The delivery was written in the same unit of work, so it goes back with it. Sending before
            // commit would have told somebody about work that no longer exists.
            await using var assert = new AeroLinkDbContext(seed.Options);
            Assert.Empty(await assert.NotificationDeliveries.AsNoTracking().ToListAsync());
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Dispatch_sends_the_notice_with_a_deep_link_and_an_unsubscribe_link()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            var sender = new RecordingSender();
            var (links, tokens) = Support();
            var result = await new NotificationOutbox(db).DispatchPendingAsync(sender, links, tokens, 50, 5, Now, default);

            Assert.Equal(1, result.Sent);
            var message = Assert.Single(sender.Sent);
            Assert.Equal("approver@example.test", message.To);
            Assert.Contains("SRCR-00031.00", message.Subject);
            Assert.Contains("https://aerolink.example.test/open/scr/11111111-1111-1111-1111-111111111111", message.PlainTextBody);
            Assert.Contains("/api/notifications/unsubscribe", message.PlainTextBody);
            Assert.Equal(NotificationDeliveryState.Sent, (await db.NotificationDeliveries.AsNoTracking().SingleAsync()).State);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task Without_a_public_address_the_message_explains_itself_instead_of_carrying_a_broken_link()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            var sender = new RecordingSender();
            var (links, tokens) = Support(withBaseUrl: false);
            await new NotificationOutbox(db).DispatchPendingAsync(sender, links, tokens, 50, 5, Now, default);

            var message = Assert.Single(sender.Sent);
            Assert.DoesNotContain("http", message.PlainTextBody);
            Assert.Contains("No public address is configured", message.PlainTextBody);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task An_opted_out_recipient_is_recorded_as_suppressed_rather_than_left_out()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            var preference = new NotificationPreference("approver.user", Now);
            preference.SetEmailEnabled(false, Now);
            db.NotificationPreferences.Add(preference);
            await db.SaveChangesAsync();

            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            // Writing nothing would leave no evidence that somebody was meant to be told and deliberately
            // was not, and that evidence is exactly what an assurance record is for.
            var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(NotificationDeliveryState.Suppressed, delivery.State);
            Assert.Contains("turned off", delivery.LastError);

            var sender = new RecordingSender();
            var (links, tokens) = Support();
            await new NotificationOutbox(db).DispatchPendingAsync(sender, links, tokens, 50, 5, Now, default);
            Assert.Empty(sender.Sent);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_recipient_with_no_address_is_suppressed_with_the_reason_recorded()
    {
        var seed = await SeedAsync(email: "");
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            var delivery = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(NotificationDeliveryState.Suppressed, delivery.State);
            Assert.Contains("no email address", delivery.LastError);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task A_refused_send_is_retried_and_only_abandoned_after_the_attempt_limit()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            var sender = new RecordingSender { Throw = new InvalidOperationException("The relay is unavailable.") };
            var (links, tokens) = Support();
            var outbox = new NotificationOutbox(db);

            // The ordinary failure is a relay that is briefly down, not a wrong address. Abandoning on the
            // first refusal would lose the notice.
            await outbox.DispatchPendingAsync(sender, links, tokens, 50, 3, Now, default);
            var afterFirst = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(NotificationDeliveryState.Pending, afterFirst.State);
            Assert.Equal(1, afterFirst.Attempts);
            Assert.Contains("relay is unavailable", afterFirst.LastError);

            await outbox.DispatchPendingAsync(sender, links, tokens, 50, 3, Now, default);
            await outbox.DispatchPendingAsync(sender, links, tokens, 50, 3, Now, default);
            var abandoned = await db.NotificationDeliveries.AsNoTracking().SingleAsync();
            Assert.Equal(NotificationDeliveryState.Failed, abandoned.State);
            Assert.Equal(3, abandoned.Attempts);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public async Task With_no_mail_relay_configured_the_queue_is_retained_rather_than_discarded()
    {
        var seed = await SeedAsync();
        try
        {
            await using var db = new AeroLinkDbContext(seed.Options);
            db.UserNotifications.Add(Notification(seed.ProjectId));
            await db.SaveChangesAsync();

            var (links, tokens) = Support();
            var result = await new NotificationOutbox(db)
                .DispatchPendingAsync(new RecordingSender(configured: false), links, tokens, 50, 5, Now, default);

            Assert.Equal(0, result.Sent);
            // Pending and inspectable. Dropping them quietly is how an approval goes unnoticed for a week.
            Assert.Equal(NotificationDeliveryState.Pending,
                (await db.NotificationDeliveries.AsNoTracking().SingleAsync()).State);
        }
        finally { File.Delete(seed.Path); }
    }

    [Fact]
    public void An_unsubscribe_token_is_specific_to_its_recipient()
    {
        var (_, tokens) = Support();
        var issued = tokens.Issue("approver.user")!;
        Assert.True(tokens.Validate("approver.user", issued));
        Assert.True(tokens.Validate("APPROVER.USER", issued));
        // Otherwise one person could silence another person's approval notices.
        Assert.False(tokens.Validate("someone.else", issued));
        Assert.False(tokens.Validate("approver.user", "not-the-token"));
    }

    [Fact]
    public void Without_a_configured_secret_no_unsubscribe_link_is_offered_at_all()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Notifications:UnsubscribeSecret"] = "too-short" }).Build();
        var tokens = new UnsubscribeTokenService(configuration);
        Assert.False(tokens.IsConfigured);
        Assert.Null(tokens.Issue("approver.user"));
        Assert.False(tokens.Validate("approver.user", "anything"));
    }

    /// <summary>
    /// These used to expect paths such as /systems/change-requests/{id}, which the client router cannot
    /// resolve — it accepts application routes only beneath /programs/{p}/projects/{pr}/releases/{r}/. The
    /// assertions were green for as long as the links were broken, because comparing a generated string to
    /// an expected string proves the two agree and nothing about whether either one opens anything.
    ///
    /// Artifact routes now address the resolver, which owns the context lookup. `open-link.spec.ts` follows
    /// one of these through a running product, which is the assertion that could have caught the original.
    /// </summary>
    [Theory]
    [InlineData("scr:abc", "/open/scr/abc")]
    [InlineData("swcr:abc", "/open/swcr/abc")]
    [InlineData("requirement:xyz", "/open/requirement/xyz")]
    [InlineData("case:xyz", "/open/case/xyz")]
    [InlineData("verification-impact:1", "/system-verification")]
    [InlineData("problem-report:7", "/open/problem-report/7")]
    [InlineData("something-new:1", "/")]
    [InlineData("", "/")]
    public void Routes_resolve_to_client_paths_and_never_to_nothing(string route, string expected) =>
        Assert.Equal(expected, NotificationLinkBuilder.PathFor(route));
}
