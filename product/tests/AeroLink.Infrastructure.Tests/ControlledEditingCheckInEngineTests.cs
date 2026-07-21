using System.Text;
using System.Text.Json;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AeroLink.Infrastructure.Tests;

public sealed class ControlledEditingCheckInEngineTests
{
    [Fact]
    public async Task Successful_check_in_applies_latest_autosave_and_atomically_closes_lease_with_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        var latest = Scenario.Draft("Latest autosaved title", "Latest problem");
        await scenario.AutosaveAsync(latest);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.True(result.Success);
        Assert.Equal(2, result.ResultingArtifactVersion);
        Assert.NotNull(result.ResultingHash);
        scenario.Db.ChangeTracker.Clear();
        var saved = await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id);
        var session = await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id);
        var evidence = await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId);
        Assert.Equal("Latest autosaved title", saved.Title);
        Assert.Equal("Latest problem", saved.Problem);
        Assert.Equal(EditSessionState.Committed, session.State);
        Assert.Null(session.LockKey);
        Assert.Equal(ControlledCheckInOutcome.Succeeded, evidence.Outcome);
        Assert.Equal("SystemChangeRequestControlledEditingAdapter", evidence.Adapter);
        Assert.Equal(1, evidence.AggregateVersionBefore);
        Assert.Equal(2, evidence.AggregateVersionAfter);
        Assert.Equal(result.ResultingHash, evidence.ResultingSnapshotHash);
        Assert.NotNull(evidence.DraftSnapshotId);
        Assert.Contains(await scenario.Db.AuditEvents.ToListAsync(), x =>
            x.AggregateId == scenario.Scr.Id && x.EventType == "ArtifactCheckedIn");
    }

    [Fact]
    public async Task Expected_session_version_mismatch_retains_active_session_and_does_not_mutate_artifact()
    {
        await using var scenario = await Scenario.CreateAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version + 1,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("edit_session_version_mismatch", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task Authoritative_change_after_checkout_returns_deterministic_stale_artifact_conflict()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Scr.UpdateDraft(scenario.Actor.UserName, "Changed elsewhere", "Problem", "Analysis",
            "Solution", [], scenario.Now.AddSeconds(5));
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("stale_artifact_version", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Changed elsewhere", (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).Title);
        Assert.Equal(EditSessionState.Active, (await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id)).State);
    }

    [Fact]
    public async Task Expired_lease_is_rejected_without_applying_draft()
    {
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await using var scenario = await Scenario.CreateAsync(openedAt, 2);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, DateTimeOffset.UtcNow, default);

        Assert.Equal(ControlledCheckInStatus.Conflict, result.Status);
        Assert.Equal("edit_session_expired", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal("Original title", (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).Title);
        var expired = await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id);
        Assert.Equal(EditSessionState.Expired, expired.State);
        Assert.Null(expired.LockKey);
    }

    [Fact]
    public async Task Wrong_user_is_forbidden_and_session_owner_keeps_the_lease()
    {
        await using var scenario = await Scenario.CreateAsync();
        var other = new AuthenticatedUser(Guid.NewGuid(), "other.engineer", "Other Engineer", "other@example.test",
            false, [new UserProgramAccess(scenario.Program.Id, [ProgramRole.Engineer.ToString()])]);

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            other, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Forbidden, result.Status);
        Assert.Equal("edit_session_owner_mismatch", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task User_without_project_authorization_is_forbidden()
    {
        await using var scenario = await Scenario.CreateAsync();
        var unauthorized = scenario.Actor with { Programs = [] };

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            unauthorized, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.Forbidden, result.Status);
        Assert.Equal("project_authorization_required", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
    }

    [Fact]
    public async Task Lifecycle_rejection_precedes_stale_snapshot_detection()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Scr.AddRequirementChange(scenario.Actor.UserName, "SYSR-000001", 0,
            RequirementLevel.System, RequirementChangeKind.Introduce, "Required behavior", "Rationale",
            "Test", scenario.Now.AddSeconds(1));
        scenario.Scr.SubmitForReview(scenario.Actor.UserName,
            [new ApproverSelection("approver", "Approver")], scenario.Now.AddSeconds(2));
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal("artifact_not_editable", result.Code);
        scenario.Db.ChangeTracker.Clear();
        Assert.Equal(ScrState.InReview, (await scenario.Db.SystemChangeRequests.SingleAsync(x => x.Id == scenario.Scr.Id)).State);
        Assert.Equal(EditSessionState.Active, (await scenario.Db.ArtifactEditSessions.SingleAsync(x => x.Id == scenario.Session.Id)).State);
    }

    [Fact]
    public async Task Malformed_latest_autosave_is_rejected_and_preserved_as_failure_evidence()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.AutosaveAsync("{ malformed");

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.InvalidDraft, result.Status);
        Assert.Equal("malformed_draft_json", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
        Assert.Equal(ControlledCheckInOutcome.Failed,
            (await scenario.Db.ControlledArtifactCheckInEvidence.SingleAsync(x => x.Id == result.EvidenceId)).Outcome);
    }

    [Fact]
    public async Task Aggregate_validation_failure_does_not_partially_persist_or_release_lease()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.AutosaveAsync(Scenario.Draft("", "Attempted mutation"));

        var result = await scenario.Engine.CheckInAsync(scenario.Session.Id, scenario.Session.Version,
            scenario.Actor, scenario.Now.AddMinutes(1), default);

        Assert.Equal(ControlledCheckInStatus.InvalidDraft, result.Status);
        Assert.Equal("aggregate_validation_failed", result.Code);
        await scenario.AssertUnchangedAndActiveAsync();
        Assert.Contains("SCR title", (await scenario.Db.ControlledArtifactCheckInEvidence
            .SingleAsync(x => x.Id == result.EvidenceId)).Reason);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private Scenario(AeroLinkDbContext db, ProgramRecord program, ProjectRecord project,
            SystemChangeRequest scr, ArtifactEditSession session, AuthenticatedUser actor,
            ControlledEditingCheckInEngine engine, DateTimeOffset now)
        { Db = db; Program = program; Project = project; Scr = scr; Session = session; Actor = actor; Engine = engine; Now = now; }

        public AeroLinkDbContext Db { get; }
        public ProgramRecord Program { get; }
        public ProjectRecord Project { get; }
        public SystemChangeRequest Scr { get; }
        public ArtifactEditSession Session { get; }
        public AuthenticatedUser Actor { get; }
        public ControlledEditingCheckInEngine Engine { get; }
        public DateTimeOffset Now { get; }

        public static async Task<Scenario> CreateAsync(DateTimeOffset? openedAt = null, int leaseMinutes = 15)
        {
            var options = new DbContextOptionsBuilder<AeroLinkDbContext>().UseSqlite("Data Source=:memory:").Options;
            var db = new AeroLinkDbContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            var now = openedAt ?? DateTimeOffset.UtcNow;
            var program = new ProgramRecord("Controlled Editing Program", $"CE{Guid.NewGuid():N}"[..12]);
            var project = new ProjectRecord(program.Id, "Controlled Product", "Flight Management System");
            var release = new SoftwareRelease(project.Id, "1.0", false);
            var scr = new SystemChangeRequest("SCR-00000001", 0, project.Id, release.Id, "Original title",
                "Original problem", "Original analysis", "Original solution", "engineer", now);
            db.AddRange(program, project, release, scr);
            await db.SaveChangesAsync();
            var adapter = new SystemChangeRequestControlledEditingAdapter(db);
            var artifact = await adapter.ResolveAsync(scr.Id, default) ?? throw new InvalidOperationException();
            var snapshot = adapter.CanonicalSnapshot(artifact);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(snapshot));
            var session = new ArtifactEditSession(project.Id, "ChangeRequest", scr.Id, null, hash,
                snapshot, "engineer", now, true, leaseMinutes);
            var draft = new ArtifactDraftSnapshot(project.Id, session.Id, "ChangeRequest", scr.Id, 1,
                snapshot, hash, "engineer", now);
            db.AddRange(session, draft);
            await db.SaveChangesAsync();
            var actor = new AuthenticatedUser(Guid.NewGuid(), "engineer", "Engineer", "engineer@example.test",
                false, [new UserProgramAccess(program.Id, [ProgramRole.Engineer.ToString()])]);
            var engine = new ControlledEditingCheckInEngine(db, new IdentityService(db), [adapter]);
            return new(db, program, project, scr, session, actor, engine, now);
        }

        public async Task AutosaveAsync(string draftJson)
        {
            Session.Save(draftJson, Session.Version, Now.AddSeconds(Session.Version), 15);
            var hash = EnterpriseRequirementsService.Hash(Encoding.UTF8.GetBytes(draftJson));
            Db.ArtifactDraftSnapshots.Add(new(Project.Id, Session.Id, "ChangeRequest", Scr.Id,
                Session.Version, draftJson, hash, Actor.UserName, Now.AddSeconds(Session.Version)));
            await Db.SaveChangesAsync();
        }

        public async Task AssertUnchangedAndActiveAsync()
        {
            Db.ChangeTracker.Clear();
            var scr = await Db.SystemChangeRequests.SingleAsync(x => x.Id == Scr.Id);
            var session = await Db.ArtifactEditSessions.SingleAsync(x => x.Id == Session.Id);
            Assert.Equal("Original title", scr.Title);
            Assert.Equal("Original problem", scr.Problem);
            Assert.Equal(EditSessionState.Active, session.State);
            Assert.NotNull(session.LockKey);
        }

        public static string Draft(string title, string problem) => JsonSerializer.Serialize(new
        {
            title, problem, analysis = "Latest analysis", solution = "Latest solution",
            requirementChanges = Array.Empty<object>()
        });

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
