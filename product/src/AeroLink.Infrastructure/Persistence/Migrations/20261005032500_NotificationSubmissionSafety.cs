using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationSubmissionSafety : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdmissionEpochId",
                table: "notification_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BoundNotificationId",
                table: "notification_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentStewardAssignmentId",
                table: "managed_documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentResponsibleAssignmentId",
                table: "managed_document_revisions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_managed_document_review_steps_Id_RevisionId",
                table: "managed_document_review_steps",
                columns: new[] { "Id", "RevisionId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_managed_document_events_Id_DocumentId",
                table: "managed_document_events",
                columns: new[] { "Id", "DocumentId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_managed_document_assignments_Id_DocumentId",
                table: "managed_document_assignments",
                columns: new[] { "Id", "DocumentId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_approval_steps_Id_ReviewCycleId",
                table: "approval_steps",
                columns: new[] { "Id", "ReviewCycleId" });

            migrationBuilder.CreateTable(
                name: "notification_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Actor = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Family = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    OperationKey = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    GenerationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preference_confirmations",
                columns: table => new
                {
                    ChallengeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preference_confirmations", x => x.ChallengeHash);
                });

            migrationBuilder.CreateTable(
                name: "notification_settings_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Sender = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    BaseUrl = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    ProtectedCredential = table.Column<string>(type: "text", nullable: false),
                    AllowedEventTypesJson = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_settings_revisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_contexts",
                columns: table => new
                {
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipient = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SourceFamily = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    TestChangeReviewId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewCycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovalStepId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuditEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentStepId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArtifactAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Cycle = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Stage = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_contexts", x => x.NotificationId);
                    table.CheckConstraint("CK_notification_contexts_typed_source", "\"SourceSchemaVersion\" = 1 AND ((\"SourceFamily\" = 'CRStep' AND \"SourceId\" = \"ApprovalStepId\" AND \"ChangeRequestId\" IS NOT NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NOT NULL AND \"ApprovalStepId\" IS NOT NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NULL AND \"DocumentRevisionId\" IS NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'TCRStep' AND \"SourceId\" = \"ApprovalStepId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NOT NULL AND \"ReviewCycleId\" IS NOT NULL AND \"ApprovalStepId\" IS NOT NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NULL AND \"DocumentRevisionId\" IS NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'DocStep' AND \"SourceId\" = \"DocumentStepId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NULL AND \"ApprovalStepId\" IS NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NOT NULL AND \"DocumentRevisionId\" IS NOT NULL AND \"DocumentStepId\" IS NOT NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'CRAudit' AND \"SourceId\" = \"AuditEventId\" AND \"ChangeRequestId\" IS NOT NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NOT NULL AND \"ApprovalStepId\" IS NOT NULL AND \"AuditEventId\" IS NOT NULL AND \"DocumentId\" IS NULL AND \"DocumentRevisionId\" IS NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'DocEvent' AND \"SourceId\" = \"DocumentEventId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NULL AND \"ApprovalStepId\" IS NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NOT NULL AND \"DocumentRevisionId\" IS NOT NULL AND \"DocumentStepId\" IS NOT NULL AND \"DocumentEventId\" IS NOT NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'RequirementAssignment' AND \"SourceId\" = \"ArtifactAssignmentId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NULL AND \"ApprovalStepId\" IS NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NULL AND \"DocumentRevisionId\" IS NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NOT NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'DocumentAssignment' AND \"SourceId\" = \"DocumentAssignmentId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NULL AND \"ApprovalStepId\" IS NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NOT NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NOT NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NULL) OR (\"SourceFamily\" = 'DiagnosticOperation' AND \"SourceId\" = \"OperationId\" AND \"ChangeRequestId\" IS NULL AND \"TestChangeReviewId\" IS NULL AND \"ReviewCycleId\" IS NULL AND \"ApprovalStepId\" IS NULL AND \"AuditEventId\" IS NULL AND \"DocumentId\" IS NULL AND \"DocumentRevisionId\" IS NULL AND \"DocumentStepId\" IS NULL AND \"DocumentEventId\" IS NULL AND \"DocumentAssignmentId\" IS NULL AND \"ArtifactAssignmentId\" IS NULL AND \"OperationId\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_notification_contexts_approval_steps_ApprovalStepId_ReviewC~",
                        columns: x => new { x.ApprovalStepId, x.ReviewCycleId },
                        principalTable: "approval_steps",
                        principalColumns: new[] { "Id", "ReviewCycleId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_artifact_assignments_ArtifactAssignme~",
                        column: x => x.ArtifactAssignmentId,
                        principalTable: "artifact_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_audit_events_AuditEventId",
                        column: x => x.AuditEventId,
                        principalTable: "audit_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_managed_document_assignments_Document~",
                        columns: x => new { x.DocumentAssignmentId, x.DocumentId },
                        principalTable: "managed_document_assignments",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_managed_document_events_DocumentEvent~",
                        columns: x => new { x.DocumentEventId, x.DocumentId },
                        principalTable: "managed_document_events",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_managed_document_review_steps_Documen~",
                        columns: x => new { x.DocumentStepId, x.DocumentRevisionId },
                        principalTable: "managed_document_review_steps",
                        principalColumns: new[] { "Id", "RevisionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_managed_document_revisions_DocumentRe~",
                        column: x => x.DocumentRevisionId,
                        principalTable: "managed_document_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_managed_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "managed_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_notification_operations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "notification_operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_review_cycles_ReviewCycleId",
                        column: x => x.ReviewCycleId,
                        principalTable: "review_cycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_system_change_requests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_test_change_reviews_TestChangeReviewId",
                        column: x => x.TestChangeReviewId,
                        principalTable: "test_change_reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_contexts_user_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "user_notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_admission_epochs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SettingsRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SendGeneration = table.Column<Guid>(type: "uuid", nullable: false),
                    CutoffSequence = table.Column<long>(type: "bigint", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_admission_epochs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_admission_epochs_notification_settings_revisio~",
                        column: x => x.SettingsRevisionId,
                        principalTable: "notification_settings_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_delivery_generations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialDeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DuplicateRiskAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    AdmissionEpochId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalAdmissionEpochId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalSendGeneration = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalDeadlineTicks = table.Column<long>(type: "bigint", nullable: false),
                    SettingsRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SendGeneration = table.Column<Guid>(type: "uuid", nullable: false),
                    PredecessorId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<string>(type: "text", nullable: false),
                    MessageDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProtectedAddress = table.Column<string>(type: "text", nullable: false),
                    AddressHash = table.Column<string>(type: "text", nullable: false),
                    ProtectedMime = table.Column<string>(type: "text", nullable: false),
                    BodyHash = table.Column<string>(type: "text", nullable: false),
                    MessageConfigurationHash = table.Column<string>(type: "text", nullable: false),
                    BlockedEffectiveSettingsHash = table.Column<string>(type: "text", nullable: false),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    ContentPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilTicks = table.Column<long>(type: "bigint", nullable: false),
                    DueTicks = table.Column<long>(type: "bigint", nullable: false),
                    DeadlineTicks = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    TransientFailures = table.Column<int>(type: "integer", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaximumPhysicalAttempts = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    SafeCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_delivery_generations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_delivery_generations_notification_admission_ep~",
                        column: x => x.AdmissionEpochId,
                        principalTable: "notification_admission_epochs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_delivery_generations_notification_admission_e~1",
                        column: x => x.OriginalAdmissionEpochId,
                        principalTable: "notification_admission_epochs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_delivery_generations_notification_deliveries_D~",
                        column: x => x.DeliveryId,
                        principalTable: "notification_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_delivery_generations_notification_delivery_gen~",
                        column: x => x.PredecessorId,
                        principalTable: "notification_delivery_generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_delivery_generations_notification_settings_rev~",
                        column: x => x.SettingsRevisionId,
                        principalTable: "notification_settings_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_installation_states",
                columns: table => new
                {
                    InstallationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SettingsRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdmissionEpochId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    SendingEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_installation_states", x => x.InstallationId);
                    table.ForeignKey(
                        name: "FK_notification_installation_states_notification_admission_epo~",
                        column: x => x.AdmissionEpochId,
                        principalTable: "notification_admission_epochs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_installation_states_notification_settings_revi~",
                        column: x => x.SettingsRevisionId,
                        principalTable: "notification_settings_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_physical_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: false),
                    SettingsRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyHash = table.Column<string>(type: "text", nullable: false),
                    ProtectedSettingsSnapshot = table.Column<string>(type: "text", nullable: false),
                    EffectiveSettingsHash = table.Column<string>(type: "text", nullable: false),
                    HostIdentity = table.Column<string>(type: "text", nullable: false),
                    ProcessId = table.Column<int>(type: "integer", nullable: false),
                    ProcessStartTicks = table.Column<long>(type: "bigint", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TransmissionStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Phase = table.Column<string>(type: "text", nullable: false),
                    SmtpStatus = table.Column<int>(type: "integer", nullable: true),
                    SafeCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    TransportDisposed = table.Column<bool>(type: "boolean", nullable: false),
                    CleanupWarning = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_physical_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_physical_attempts_notification_delivery_genera~",
                        column: x => x.GenerationId,
                        principalTable: "notification_delivery_generations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_notification_physical_attempts_notification_settings_revisi~",
                        column: x => x.SettingsRevisionId,
                        principalTable: "notification_settings_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_BoundNotificationId_Channel",
                table: "notification_deliveries",
                columns: new[] { "BoundNotificationId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_documents_CurrentStewardAssignmentId",
                table: "managed_documents",
                column: "CurrentStewardAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_managed_document_revisions_CurrentResponsibleAssignmentId",
                table: "managed_document_revisions",
                column: "CurrentResponsibleAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_admission_epochs_SettingsRevisionId",
                table: "notification_admission_epochs",
                column: "SettingsRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_ApprovalStepId_ReviewCycleId",
                table: "notification_contexts",
                columns: new[] { "ApprovalStepId", "ReviewCycleId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_ArtifactAssignmentId",
                table: "notification_contexts",
                column: "ArtifactAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_AuditEventId",
                table: "notification_contexts",
                column: "AuditEventId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_ChangeRequestId",
                table: "notification_contexts",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_DocumentAssignmentId_DocumentId",
                table: "notification_contexts",
                columns: new[] { "DocumentAssignmentId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_DocumentEventId_DocumentId",
                table: "notification_contexts",
                columns: new[] { "DocumentEventId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_DocumentId",
                table: "notification_contexts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_DocumentRevisionId",
                table: "notification_contexts",
                column: "DocumentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_DocumentStepId_DocumentRevisionId",
                table: "notification_contexts",
                columns: new[] { "DocumentStepId", "DocumentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_OperationId",
                table: "notification_contexts",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_ProjectId_SourceFamily_SourceId_Event~",
                table: "notification_contexts",
                columns: new[] { "ProjectId", "SourceFamily", "SourceId", "EventType", "Recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_ReviewCycleId",
                table: "notification_contexts",
                column: "ReviewCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_contexts_TestChangeReviewId",
                table: "notification_contexts",
                column: "TestChangeReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_AdmissionEpochId",
                table: "notification_delivery_generations",
                column: "AdmissionEpochId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_DeliveryId",
                table: "notification_delivery_generations",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_InitialDeliveryId",
                table: "notification_delivery_generations",
                column: "InitialDeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_MessageId",
                table: "notification_delivery_generations",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_OriginalAdmissionEpochId",
                table: "notification_delivery_generations",
                column: "OriginalAdmissionEpochId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_PredecessorId",
                table: "notification_delivery_generations",
                column: "PredecessorId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_SettingsRevisionId",
                table: "notification_delivery_generations",
                column: "SettingsRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_delivery_generations_State_DueTicks",
                table: "notification_delivery_generations",
                columns: new[] { "State", "DueTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_installation_states_AdmissionEpochId",
                table: "notification_installation_states",
                column: "AdmissionEpochId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_installation_states_SettingsRevisionId",
                table: "notification_installation_states",
                column: "SettingsRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_operations_InstallationId_Actor_Family_Operati~",
                table: "notification_operations",
                columns: new[] { "InstallationId", "Actor", "Family", "OperationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_physical_attempts_ClaimToken",
                table: "notification_physical_attempts",
                column: "ClaimToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_physical_attempts_GenerationId",
                table: "notification_physical_attempts",
                column: "GenerationId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_physical_attempts_SettingsRevisionId",
                table: "notification_physical_attempts",
                column: "SettingsRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_settings_revisions_InstallationId_Version",
                table: "notification_settings_revisions",
                columns: new[] { "InstallationId", "Version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_managed_document_revisions_managed_document_assignments_Cur~",
                table: "managed_document_revisions",
                column: "CurrentResponsibleAssignmentId",
                principalTable: "managed_document_assignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_managed_documents_managed_document_assignments_CurrentStewa~",
                table: "managed_documents",
                column: "CurrentStewardAssignmentId",
                principalTable: "managed_document_assignments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Historical duplicates and IDs remain intact; unresolved pending mail is never admitted.
            migrationBuilder.Sql("UPDATE notification_deliveries SET \"State\" = 'LegacyUnbound' WHERE \"State\" = 'Pending' AND \"BoundNotificationId\" IS NULL;");
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    CREATE FUNCTION aerolink_notification_source_integrity() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                      IF NOT EXISTS (SELECT 1 FROM user_notifications n WHERE n."Id"=NEW."NotificationId" AND n."ProjectId"=NEW."ProjectId" AND n."Recipient"=NEW."Recipient" AND n."Type"=NEW."EventType" AND n."ArtifactId" IS NOT DISTINCT FROM NEW."RecordId") THEN
                        RAISE EXCEPTION 'Notification context does not match its original notice';
                      END IF;
                      IF NEW."SourceFamily" IN ('CRStep','CRAudit') AND NOT EXISTS
                        (SELECT 1 FROM system_change_requests r JOIN review_cycles c ON c."ChangeRequestId"=r."Id"
                         WHERE r."Id"=NEW."ChangeRequestId" AND r."ProjectId"=NEW."ProjectId" AND r."Id"=NEW."RecordId" AND c."Id"=NEW."ReviewCycleId") THEN
                        RAISE EXCEPTION 'Notification change request source ownership mismatch';
                      ELSIF NEW."SourceFamily"='TCRStep' AND NOT EXISTS
                        (SELECT 1 FROM test_change_reviews r JOIN review_cycles c ON c."TestChangeReviewId"=r."Id"
                         WHERE r."Id"=NEW."TestChangeReviewId" AND r."ProjectId"=NEW."ProjectId" AND r."Id"=NEW."RecordId" AND c."Id"=NEW."ReviewCycleId") THEN
                        RAISE EXCEPTION 'Notification test change source ownership mismatch';
                      ELSIF NEW."SourceFamily" IN ('DocStep','DocEvent','DocumentAssignment') AND NOT EXISTS
                        (SELECT 1 FROM managed_documents d WHERE d."Id"=NEW."DocumentId" AND d."ProjectId"=NEW."ProjectId" AND d."Id"=NEW."RecordId"
                         AND (NEW."DocumentRevisionId" IS NULL OR EXISTS (SELECT 1 FROM managed_document_revisions r WHERE r."Id"=NEW."DocumentRevisionId" AND r."DocumentId"=d."Id"))) THEN
                        RAISE EXCEPTION 'Notification document source ownership mismatch';
                      ELSIF NEW."SourceFamily"='RequirementAssignment' AND NOT EXISTS
                        (SELECT 1 FROM artifact_assignments a JOIN requirements r ON r."Id"=a."ArtifactId"
                         WHERE a."Id"=NEW."ArtifactAssignmentId" AND a."ProjectId"=NEW."ProjectId" AND a."ArtifactType"='Requirement'
                           AND r."ProjectId"=NEW."ProjectId" AND r."Id"=NEW."RecordId" AND a."AssignedTo"=NEW."Recipient") THEN
                        RAISE EXCEPTION 'Notification task source ownership mismatch';
                      ELSIF NEW."SourceFamily"='DiagnosticOperation' AND NOT EXISTS
                        (SELECT 1 FROM notification_operations o WHERE o."Id"=NEW."OperationId" AND o."Actor"=NEW."Recipient" AND o."Family"='TransportTest') THEN
                        RAISE EXCEPTION 'Notification diagnostic source ownership mismatch';
                      END IF;
                      IF NEW."SourceFamily"='CRAudit' AND NOT EXISTS (SELECT 1 FROM audit_events a WHERE a."Id"=NEW."AuditEventId" AND a."AggregateId"=NEW."ChangeRequestId" AND a."EventType"='ChangesRequested') THEN
                        RAISE EXCEPTION 'Notification return event ownership mismatch';
                      END IF;
                      RETURN NEW;
                    END $$;
                    CREATE CONSTRAINT TRIGGER notification_source_integrity AFTER INSERT ON notification_contexts DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION aerolink_notification_source_integrity();
                    CREATE FUNCTION aerolink_notification_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN RAISE EXCEPTION 'Notification original evidence is immutable'; END $$;
                    CREATE TRIGGER notification_context_immutable BEFORE UPDATE OR DELETE ON notification_contexts FOR EACH ROW EXECUTE FUNCTION aerolink_notification_immutable();
                    CREATE TRIGGER notification_settings_immutable BEFORE UPDATE OR DELETE ON notification_settings_revisions FOR EACH ROW EXECUTE FUNCTION aerolink_notification_immutable();
                    CREATE TRIGGER notification_admission_immutable BEFORE UPDATE OR DELETE ON notification_admission_epochs FOR EACH ROW EXECUTE FUNCTION aerolink_notification_immutable();
                    CREATE TRIGGER notification_operation_immutable BEFORE UPDATE OR DELETE ON notification_operations FOR EACH ROW EXECUTE FUNCTION aerolink_notification_immutable();
                    CREATE TRIGGER notification_preference_receipt_immutable BEFORE UPDATE OR DELETE ON notification_preference_confirmations FOR EACH ROW EXECUTE FUNCTION aerolink_notification_immutable();
                    CREATE FUNCTION aerolink_notification_attempt_integrity() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                      IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Physical submission evidence cannot be deleted'; END IF;
                      IF (to_jsonb(NEW) - ARRAY['TransmissionStartedAt','Outcome','Phase','SmtpStatus','SafeCode','TransportDisposed','CompletedAt','DisposedAt','CleanupWarning']) IS DISTINCT FROM
                         (to_jsonb(OLD) - ARRAY['TransmissionStartedAt','Outcome','Phase','SmtpStatus','SafeCode','TransportDisposed','CompletedAt','DisposedAt','CleanupWarning'])
                         OR (OLD."TransmissionStartedAt" IS NOT NULL AND NEW."TransmissionStartedAt" IS DISTINCT FROM OLD."TransmissionStartedAt")
                         OR (OLD."Outcome" <> 'InProgress' AND (to_jsonb(NEW) - ARRAY['TransportDisposed','DisposedAt','CleanupWarning']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['TransportDisposed','DisposedAt','CleanupWarning']))
                         OR (OLD."TransportDisposed" AND (NOT NEW."TransportDisposed" OR NEW."DisposedAt" IS DISTINCT FROM OLD."DisposedAt"))
                         OR (OLD."CleanupWarning" AND NOT NEW."CleanupWarning") THEN
                        RAISE EXCEPTION 'Physical submission identity and terminal outcome are immutable';
                      END IF;
                      RETURN NEW;
                    END $$;
                    CREATE TRIGGER notification_attempt_integrity BEFORE UPDATE OR DELETE ON notification_physical_attempts FOR EACH ROW EXECUTE FUNCTION aerolink_notification_attempt_integrity();
                    CREATE FUNCTION aerolink_notification_generation_integrity() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                      IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Concrete notification evidence cannot be deleted'; END IF;
                      IF (to_jsonb(NEW) - ARRAY['AdmissionEpochId','SendGeneration','ProtectedMime','BodyHash','MessageConfigurationHash','BlockedEffectiveSettingsHash','DuplicateRiskAcknowledged','State','ClaimToken','CurrentAttemptId','LeaseUntilTicks','DueTicks','DeadlineTicks','Attempts','TransientFailures','MaximumAttempts','MaximumPhysicalAttempts','Version','SafeCode']) IS DISTINCT FROM
                         (to_jsonb(OLD) - ARRAY['AdmissionEpochId','SendGeneration','ProtectedMime','BodyHash','MessageConfigurationHash','BlockedEffectiveSettingsHash','DuplicateRiskAcknowledged','State','ClaimToken','CurrentAttemptId','LeaseUntilTicks','DueTicks','DeadlineTicks','Attempts','TransientFailures','MaximumAttempts','MaximumPhysicalAttempts','Version','SafeCode'])
                         OR (OLD."ProtectedMime" <> '' AND (NEW."ProtectedMime",NEW."BodyHash",NEW."MessageConfigurationHash") IS DISTINCT FROM (OLD."ProtectedMime",OLD."BodyHash",OLD."MessageConfigurationHash")) THEN
                        RAISE EXCEPTION 'Concrete notification identity, destination and prepared MIME are immutable';
                      END IF;
                      RETURN NEW;
                    END $$;
                    CREATE TRIGGER notification_generation_integrity BEFORE UPDATE OR DELETE ON notification_delivery_generations FOR EACH ROW EXECUTE FUNCTION aerolink_notification_generation_integrity();
                    """);
            }

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_notification_source_integrity() CASCADE; DROP FUNCTION IF EXISTS aerolink_notification_immutable() CASCADE; DROP FUNCTION IF EXISTS aerolink_notification_attempt_integrity() CASCADE; DROP FUNCTION IF EXISTS aerolink_notification_generation_integrity() CASCADE;");
            migrationBuilder.DropForeignKey(
                name: "FK_managed_document_revisions_managed_document_assignments_Cur~",
                table: "managed_document_revisions");

            migrationBuilder.DropForeignKey(
                name: "FK_managed_documents_managed_document_assignments_CurrentStewa~",
                table: "managed_documents");

            migrationBuilder.DropTable(
                name: "notification_contexts");

            migrationBuilder.DropTable(
                name: "notification_installation_states");

            migrationBuilder.DropTable(
                name: "notification_physical_attempts");

            migrationBuilder.DropTable(
                name: "notification_preference_confirmations");

            migrationBuilder.DropTable(
                name: "notification_operations");

            migrationBuilder.DropTable(
                name: "notification_delivery_generations");

            migrationBuilder.DropTable(
                name: "notification_admission_epochs");

            migrationBuilder.DropTable(
                name: "notification_settings_revisions");

            migrationBuilder.DropIndex(
                name: "IX_notification_deliveries_BoundNotificationId_Channel",
                table: "notification_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_managed_documents_CurrentStewardAssignmentId",
                table: "managed_documents");

            migrationBuilder.DropIndex(
                name: "IX_managed_document_revisions_CurrentResponsibleAssignmentId",
                table: "managed_document_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_managed_document_review_steps_Id_RevisionId",
                table: "managed_document_review_steps");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_managed_document_events_Id_DocumentId",
                table: "managed_document_events");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_managed_document_assignments_Id_DocumentId",
                table: "managed_document_assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_approval_steps_Id_ReviewCycleId",
                table: "approval_steps");

            migrationBuilder.DropColumn(
                name: "AdmissionEpochId",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "BoundNotificationId",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "CurrentStewardAssignmentId",
                table: "managed_documents");

            migrationBuilder.DropColumn(
                name: "CurrentResponsibleAssignmentId",
                table: "managed_document_revisions");
        }
    }
}
