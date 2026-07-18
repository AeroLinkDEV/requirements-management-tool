using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenDigitalThreadFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integration_events_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_service_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScopesJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integration_service_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_integration_service_identities_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    EventTypesJson = table.Column<string>(type: "text", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_subscriptions_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntegrationEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResponseStatusCode = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_integration_events_IntegrationEventId",
                        column: x => x.IntegrationEventId,
                        principalTable: "integration_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_ProjectId_IdempotencyKey",
                table: "integration_events",
                columns: new[] { "ProjectId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_ProjectId_OccurredAt",
                table: "integration_events",
                columns: new[] { "ProjectId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_events_State_OccurredAt",
                table: "integration_events",
                columns: new[] { "State", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_service_identities_ClientId",
                table: "integration_service_identities",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integration_service_identities_ProjectId_State",
                table: "integration_service_identities",
                columns: new[] { "ProjectId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_IntegrationEventId_SubscriptionId",
                table: "webhook_deliveries",
                columns: new[] { "IntegrationEventId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_ProjectId_CreatedAt",
                table: "webhook_deliveries",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_State_NextAttemptAt",
                table: "webhook_deliveries",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId",
                table: "webhook_deliveries",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_ProjectId_IsEnabled",
                table: "webhook_subscriptions",
                columns: new[] { "ProjectId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_ProjectId_Name",
                table: "webhook_subscriptions",
                columns: new[] { "ProjectId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_service_identities");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "integration_events");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");
        }
    }
}
