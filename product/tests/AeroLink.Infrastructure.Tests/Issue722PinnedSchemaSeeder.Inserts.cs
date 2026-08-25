using AeroLink.Domain.Baselines;
using AeroLink.Domain.ChangeControl;
using AeroLink.Domain.Identity;
using AeroLink.Domain.Programs;
using AeroLink.Domain.Requirements;
using AeroLink.Domain.Traceability;
using AeroLink.Domain.Verification;
using AeroLink.Infrastructure.Persistence;
using Npgsql;

namespace AeroLink.Infrastructure.Tests;

/// <summary>
/// Parameterized pinned-schema INSERTs for <see cref="Issue722PinnedSchemaSeeder"/>. Every statement names
/// only columns that exist at 20260822153030_AddNeutralVerificationIdentity — verified against the pinned
/// database's information_schema — so a future column added to today's model cannot break the fixture here.
/// </summary>
internal static partial class Issue722PinnedSchemaSeeder
{
    private static NpgsqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }

    private static Task InsertProgramAsync(NpgsqlConnection connection, ProgramRecord program) =>
        ExecuteAsync(connection, "INSERT INTO \"programs\" (\"Id\", \"Name\", \"Code\") VALUES (@id, @name, @code)",
            P("id", program.Id), P("name", program.Name), P("code", program.Code));

    private static Task InsertProjectAsync(NpgsqlConnection connection, ProjectRecord project) =>
        ExecuteAsync(connection,
            "INSERT INTO \"projects\" (\"Id\", \"ProgramId\", \"Name\", \"SoftwareProduct\") VALUES (@id, @program, @name, @product)",
            P("id", project.Id), P("program", project.ProgramId), P("name", project.Name), P("product", project.SoftwareProduct));

    private static Task InsertReleaseAsync(NpgsqlConnection connection, SoftwareRelease release) =>
        ExecuteAsync(connection,
            "INSERT INTO \"software_releases\" (\"Id\", \"ProjectId\", \"Version\", \"IsReleased\") VALUES (@id, @project, @version, @released)",
            P("id", release.Id), P("project", release.ProjectId), P("version", release.Version), P("released", release.IsReleased));

    private static Task InsertBaselineAsync(NpgsqlConnection connection, CandidateBaseline baseline, DateTimeOffset now) =>
        ExecuteAsync(connection,
            "INSERT INTO \"candidate_baselines\" (\"Id\", \"BaseNumber\", \"Revision\", \"ProjectId\", \"ReleaseId\", \"Name\", \"CreatedAt\", \"State\", \"UpdatedAt\", \"Version\") " +
            "VALUES (@id, @baseNumber, @revision, @project, @release, @name, @createdAt, 'Draft', @updatedAt, 1)",
            P("id", baseline.Id), P("baseNumber", baseline.BaseNumber), P("revision", baseline.Revision),
            P("project", baseline.ProjectId), P("release", baseline.ReleaseId), P("name", baseline.Name),
            P("createdAt", baseline.CreatedAt), P("updatedAt", now));

    private static Task InsertProcedureAsync(NpgsqlConnection connection, TestProcedure procedure) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_procedures\" (\"Id\", \"ProjectId\", \"BaseNumber\", \"Title\", \"OwnerId\", \"CreatedAt\", \"Level\", \"UpdatedAt\", \"Version\", \"ArtifactDiscipline\", \"ArtifactKind\") " +
            "VALUES (@id, @project, @baseNumber, @title, @owner, @createdAt, @level, @updatedAt, 1, @discipline, @kind)",
            P("id", procedure.Id), P("project", procedure.ProjectId), P("baseNumber", procedure.BaseNumber),
            P("title", procedure.Title), P("owner", procedure.OwnerId), P("createdAt", procedure.CreatedAt),
            P("level", procedure.Level.ToString()), P("updatedAt", procedure.UpdatedAt),
            P("discipline", procedure.ArtifactDiscipline.ToString()), P("kind", procedure.ArtifactKind.ToString()));

    private static Task InsertRevisionAsync(NpgsqlConnection connection, TestProcedureRevision revision) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_procedure_revisions\" (\"Id\", \"ProcedureId\", \"Revision\", \"Objective\", \"Preconditions\", \"Steps\", \"ExpectedResult\", \"State\", \"AuthorId\", \"CreatedAt\", \"SourceChangeRequestsJson\") " +
            "VALUES (@id, @procedure, @revision, @objective, @preconditions, @steps, @expected, @state, @author, @createdAt, @sourceChangeRequests)",
            P("id", revision.Id), P("procedure", revision.ProcedureId), P("revision", revision.Revision),
            P("objective", revision.Objective), P("preconditions", revision.Preconditions), P("steps", revision.Steps),
            P("expected", revision.ExpectedResult), P("state", revision.State.ToString()), P("author", revision.AuthorId),
            P("createdAt", revision.CreatedAt), P("sourceChangeRequests", revision.SourceChangeRequestsJson));

    private static Task InsertControlledDocumentAsync(NpgsqlConnection connection, ControlledDocument document) =>
        ExecuteAsync(connection,
            "INSERT INTO \"controlled_documents\" (\"Id\", \"ProjectId\", \"ReleaseId\", \"BaselineId\", \"Type\", \"DocumentNumber\", \"Title\", \"Revision\", \"ContentHash\", \"ArtifactCount\", \"GeneratedAt\") " +
            "VALUES (@id, @project, @release, @baseline, @type, @number, @title, @revision, @hash, @count, @generatedAt)",
            P("id", document.Id), P("project", document.ProjectId), P("release", document.ReleaseId),
            P("baseline", document.BaselineId), P("type", document.Type.ToString()), P("number", document.DocumentNumber),
            P("title", document.Title), P("revision", document.Revision), P("hash", document.ContentHash),
            P("count", document.ArtifactCount), P("generatedAt", document.GeneratedAt));

    private static Task InsertProcedureDocumentAsync(NpgsqlConnection connection, TestProcedureDocument document) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_procedure_documents\" (\"Id\", \"ProjectId\", \"DocumentNumber\", \"Title\", \"Level\", \"Description\", \"CreatedBy\", \"CreatedAt\", \"UpdatedAt\", \"Version\") " +
            "VALUES (@id, @project, @number, @title, @level, @description, @createdBy, @createdAt, @updatedAt, 1)",
            P("id", document.Id), P("project", document.ProjectId), P("number", document.DocumentNumber),
            P("title", document.Title), P("level", document.Level.ToString()), P("description", document.Description),
            P("createdBy", document.CreatedBy), P("createdAt", document.CreatedAt), P("updatedAt", document.UpdatedAt));

    private static Task InsertProcedureDocumentNodeAsync(NpgsqlConnection connection, TestProcedureDocumentNode node) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_procedure_document_nodes\" (\"Id\", \"DocumentId\", \"ParentId\", \"Position\", \"Type\", \"Heading\", \"ProcedureId\", \"CreatedBy\", \"CreatedAt\") " +
            "VALUES (@id, @document, @parent, @position, @type, @heading, @procedure, @createdBy, @createdAt)",
            P("id", node.Id), P("document", node.DocumentId), P("parent", node.ParentId), P("position", node.Position),
            P("type", node.Type.ToString()), P("heading", node.Heading), P("procedure", node.ProcedureId),
            P("createdBy", node.CreatedBy), P("createdAt", node.CreatedAt));

    private static Task InsertEditSessionAsync(NpgsqlConnection connection, ArtifactEditSession session, DateTimeOffset now) =>
        ExecuteAsync(connection,
            "INSERT INTO \"artifact_edit_sessions\" (\"Id\", \"ProjectId\", \"ArtifactType\", \"ArtifactId\", \"RevisionId\", \"BaseSnapshotHash\", \"DraftJson\", \"UserName\", \"State\", \"OpenedAt\", \"UpdatedAt\", \"Version\", \"ExpiresAt\", \"IsExclusive\") " +
            "VALUES (@id, @project, @artifactType, @artifact, @revision, @hash, @draft, @user, 'Active', @openedAt, @updatedAt, 1, @expiresAt, false)",
            P("id", session.Id), P("project", session.ProjectId), P("artifactType", session.ArtifactType),
            P("artifact", session.ArtifactId), P("revision", session.RevisionId), P("hash", session.BaseSnapshotHash),
            P("draft", session.DraftJson), P("user", session.UserName), P("openedAt", session.OpenedAt),
            P("updatedAt", session.UpdatedAt), P("expiresAt", now.AddMinutes(15)));

    private static Task InsertNotificationAsync(NpgsqlConnection connection, UserNotification notification) =>
        ExecuteAsync(connection,
            "INSERT INTO \"user_notifications\" (\"Id\", \"ProjectId\", \"Recipient\", \"Type\", \"Title\", \"Detail\", \"Route\", \"ArtifactId\", \"State\", \"CreatedAt\") " +
            "VALUES (@id, @project, @recipient, @type, @title, @detail, @route, @artifact, @state, @createdAt)",
            P("id", notification.Id), P("project", notification.ProjectId), P("recipient", notification.Recipient),
            P("type", notification.Type), P("title", notification.Title), P("detail", notification.Detail),
            P("route", notification.Route), P("artifact", notification.ArtifactId),
            P("state", notification.State.ToString()), P("createdAt", notification.CreatedAt));

    private static Task InsertSourceChangeRequestAsync(NpgsqlConnection connection, SystemChangeRequest change) =>
        ExecuteAsync(connection,
            "INSERT INTO \"system_change_requests\" (\"Id\", \"BaseNumber\", \"Revision\", \"ProjectId\", \"TargetReleaseId\", \"Title\", \"Problem\", \"Analysis\", \"Solution\", \"AuthorId\", \"State\", \"CreatedAt\", \"UpdatedAt\", \"Version\", \"Type\", \"ProblemRich\", \"AnalysisRich\", \"SolutionRich\", \"SoftwareLevel\") " +
            "VALUES (@id, @baseNumber, @revision, @project, @release, @title, @problem, @analysis, @solution, @author, 'Draft', @createdAt, @updatedAt, 1, @type, @problemRich, @analysisRich, @solutionRich, @softwareLevel)",
            P("id", change.Id), P("baseNumber", change.BaseNumber), P("revision", change.Revision),
            P("project", change.ProjectId), P("release", change.TargetReleaseId), P("title", change.Title),
            P("problem", change.Problem), P("analysis", change.Analysis), P("solution", change.Solution),
            P("author", change.AuthorId), P("createdAt", change.CreatedAt), P("updatedAt", change.UpdatedAt),
            P("type", change.Type.ToString()), P("problemRich", change.ProblemRich),
            P("analysisRich", change.AnalysisRich), P("solutionRich", change.SolutionRich),
            P("softwareLevel", change.SoftwareLevel.ToString()));

    private static Task InsertReviewAsync(NpgsqlConnection connection, TestChangeReview review) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_change_reviews\" (\"Id\", \"ProjectId\", \"ReleaseId\", \"ChangeRequestId\", \"Discipline\", \"SourceChangeRequestNumber\", \"State\", \"AssignedEngineerId\", \"SubmittedBy\", \"SubmittedAt\", \"ApprovalRationale\", \"CreatedAt\", \"UpdatedAt\", \"Version\", \"BaseNumber\", \"Revision\", \"NoChangeRationale\", \"Outcome\", \"Analysis\", \"AnalysisRich\", \"Problem\", \"ProblemRich\", \"Solution\", \"SolutionRich\", \"Title\", \"CaseContractVersion\", \"AuthorId\", \"DecidedBy\", \"DecidedAt\", \"SelectedApproverId\") " +
            "VALUES (@id, @project, @release, @changeRequest, @discipline, @sourceNumber, @state, @assignedEngineer, @submittedBy, @submittedAt, '', @createdAt, @updatedAt, 1, @baseNumber, @revision, '', @outcome, @analysis, @analysisRich, @problem, @problemRich, @solution, @solutionRich, @title, @caseContractVersion, @author, @decidedBy, @decidedAt, @selectedApprover)",
            P("id", review.Id), P("project", review.ProjectId), P("release", review.ReleaseId),
            P("changeRequest", review.ChangeRequestId), P("discipline", review.Discipline.ToString()),
            P("sourceNumber", review.SourceChangeRequestNumber), P("state", review.State.ToString()),
            P("assignedEngineer", review.AssignedEngineerId), P("submittedBy", review.SubmittedBy),
            P("submittedAt", review.SubmittedAt), P("createdAt", review.CreatedAt), P("updatedAt", review.UpdatedAt),
            P("baseNumber", review.BaseNumber), P("revision", review.Revision), P("outcome", review.Outcome.ToString()),
            P("analysis", review.Analysis), P("analysisRich", review.AnalysisRich), P("problem", review.Problem),
            P("problemRich", review.ProblemRich), P("solution", review.Solution), P("solutionRich", review.SolutionRich),
            P("title", review.Title), P("caseContractVersion", review.CaseContractVersion), P("author", review.AuthorId),
            P("decidedBy", review.DecidedBy), P("decidedAt", review.DecidedAt),
            P("selectedApprover", review.SelectedApproverId));

    private static Task InsertProcedureChangeAsync(NpgsqlConnection connection, TestProcedureChange change) =>
        ExecuteAsync(connection,
            "INSERT INTO \"test_procedure_changes\" (\"Id\", \"TestChangeReviewId\", \"BaseNumber\", \"Revision\", \"Level\", \"Kind\", \"Objective\", \"Preconditions\", \"Steps\", \"ExpectedResult\", \"Rationale\", \"DrivingRequirementRevisionIdsJson\", \"Title\", \"RemovedRequirementRevisionIdsJson\") " +
            "VALUES (@id, @review, @baseNumber, @revision, @level, @kind, @objective, @preconditions, @steps, @expected, @rationale, @driving, @title, @removed)",
            P("id", change.Id), P("review", change.TestChangeReviewId), P("baseNumber", change.BaseNumber),
            P("revision", change.Revision), P("level", change.Level.ToString()), P("kind", change.Kind.ToString()),
            P("objective", change.Objective), P("preconditions", change.Preconditions), P("steps", change.Steps),
            P("expected", change.ExpectedResult), P("rationale", change.Rationale),
            P("driving", change.DrivingRequirementRevisionIdsJson), P("title", change.Title),
            P("removed", change.RemovedRequirementRevisionIdsJson));

    private static Task InsertReviewCycleAsync(NpgsqlConnection connection, ReviewCycle cycle) =>
        ExecuteAsync(connection,
            "INSERT INTO \"review_cycles\" (\"Id\", \"Sequence\", \"SnapshotHash\", \"State\", \"StartedAt\", \"Mode\", \"TestChangeReviewId\") " +
            "VALUES (@id, @sequence, @hash, @state, @startedAt, @mode, @review)",
            P("id", cycle.Id), P("sequence", cycle.Sequence), P("hash", cycle.SnapshotHash),
            P("state", cycle.State.ToString()), P("startedAt", cycle.StartedAt), P("mode", cycle.Mode.ToString()),
            P("review", cycle.TestChangeReviewId));

    private static Task InsertApprovalStepAsync(NpgsqlConnection connection, ApprovalStep step) =>
        ExecuteAsync(connection,
            "INSERT INTO \"approval_steps\" (\"Id\", \"ReviewCycleId\", \"Position\", \"ApproverId\", \"ApproverName\", \"State\", \"StageKind\") " +
            "VALUES (@id, @cycle, @position, @approver, @approverName, @state, @stageKind)",
            P("id", step.Id), P("cycle", step.ReviewCycleId), P("position", step.Position),
            P("approver", step.ApproverId), P("approverName", step.ApproverName), P("state", step.State.ToString()),
            P("stageKind", step.StageKind.ToString()));

    private static Task InsertSignatureAsync(NpgsqlConnection connection, ElectronicSignature signature) =>
        ExecuteAsync(connection,
            "INSERT INTO \"electronic_signatures\" (\"Id\", \"UserId\", \"UserName\", \"DisplayName\", \"ProgramId\", \"ArtifactType\", \"ArtifactId\", \"ArtifactRevision\", \"Action\", \"Meaning\", \"ContentHash\", \"IpAddress\", \"SignedAt\", \"Rationale\", \"ReviewCycle\", \"ReviewStepId\", \"ReviewStepPosition\") " +
            "VALUES (@id, @user, @userName, @displayName, @program, @artifactType, @artifact, @artifactRevision, @action, @meaning, @hash, @ip, @signedAt, @rationale, @reviewCycle, @reviewStep, @reviewStepPosition)",
            P("id", signature.Id), P("user", signature.UserId), P("userName", signature.UserName),
            P("displayName", signature.DisplayName), P("program", signature.ProgramId),
            P("artifactType", signature.ArtifactType), P("artifact", signature.ArtifactId),
            P("artifactRevision", signature.ArtifactRevision), P("action", signature.Action),
            P("meaning", signature.Meaning), P("hash", signature.ContentHash), P("ip", signature.IpAddress),
            P("signedAt", signature.SignedAt), P("rationale", signature.Rationale),
            P("reviewCycle", signature.ReviewCycle), P("reviewStep", signature.ReviewStepId),
            P("reviewStepPosition", signature.ReviewStepPosition));

    private static Task InsertDocumentArtifactAsync(NpgsqlConnection connection, ControlledDocumentArtifact artifact) =>
        ExecuteAsync(connection,
            "INSERT INTO \"controlled_document_artifacts\" (\"Id\", \"DocumentId\", \"Format\", \"StorageKey\", \"OriginalFileName\", \"ContentType\", \"Size\", \"Sha256\", \"RenderedAt\") " +
            "VALUES (@id, @document, @format, @key, @fileName, @contentType, @size, @sha, @renderedAt)",
            P("id", artifact.Id), P("document", artifact.DocumentId), P("format", artifact.Format),
            P("key", artifact.StorageKey), P("fileName", artifact.OriginalFileName),
            P("contentType", artifact.ContentType), P("size", artifact.Size), P("sha", artifact.Sha256),
            P("renderedAt", artifact.RenderedAt));

    private static Task InsertCommentAsync(NpgsqlConnection connection, ArtifactComment comment) =>
        ExecuteAsync(connection,
            "INSERT INTO \"artifact_comments\" (\"Id\", \"ProjectId\", \"ArtifactType\", \"ArtifactId\", \"RevisionId\", \"Body\", \"MentionsJson\", \"State\", \"CreatedBy\", \"CreatedAt\") " +
            "VALUES (@id, @project, @artifactType, @artifact, @revision, @body, @mentions, 'Open', @createdBy, @createdAt)",
            P("id", comment.Id), P("project", comment.ProjectId), P("artifactType", comment.ArtifactType),
            P("artifact", comment.ArtifactId), P("revision", comment.RevisionId), P("body", comment.Body),
            P("mentions", comment.MentionsJson), P("createdBy", comment.CreatedBy), P("createdAt", comment.CreatedAt));

    // At the pinned schema the baseline membership table is baseline_test_procedures; the rename to
    // baseline_test_procedure_selections happens in a later migration the fixture runs afterwards.
    private static Task InsertBaselineSelectionAsync(NpgsqlConnection connection, Guid baselineId, Guid procedureId, Guid revisionId) =>
        ExecuteAsync(connection,
            "INSERT INTO \"baseline_test_procedures\" (\"Id\", \"BaselineId\", \"ProcedureId\", \"RevisionId\") VALUES (@id, @baseline, @artifact, @revision)",
            P("id", Guid.NewGuid()), P("baseline", baselineId), P("artifact", procedureId), P("revision", revisionId));

    private static Task InsertIdentifierSequenceAsync(NpgsqlConnection connection, string scope, long nextValue) =>
        ExecuteAsync(connection,
            "INSERT INTO \"identifier_sequences\" (\"Id\", \"Scope\", \"NextValue\", \"ConcurrencyStamp\") VALUES (@id, @scope, @next, 0)",
            P("id", Guid.NewGuid()), P("scope", scope), P("next", nextValue));
}
