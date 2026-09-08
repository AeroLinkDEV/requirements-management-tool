using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveryClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttemptHistoryJson",
                table: "webhook_deliveries",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimExpiresAt",
                table: "webhook_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClaimToken",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "webhook_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "webhook_deliveries",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "webhook_deliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_State_ClaimExpiresAt",
                table: "webhook_deliveries",
                columns: new[] { "State", "ClaimExpiresAt" });

            // Rows written by the pre-claim worker could be left in Delivering forever. They have no
            // trustworthy owner or expiry, so return them to the retry queue with their delivery/event IDs
            // unchanged. This is deliberately an additive, one-time repair of only the ambiguous legacy state;
            // new claims always carry a token and a lease.
            migrationBuilder.Sql(
                "UPDATE \"webhook_deliveries\" SET \"State\" = CASE WHEN \"AttemptCount\" >= 5 THEN 'DeadLettered' ELSE 'RetryScheduled' END, \"NextAttemptAt\" = CURRENT_TIMESTAMP, \"AttemptHistoryJson\" = json_build_array(json_build_object('Attempt', \"AttemptCount\", 'ClaimToken', NULL, 'Worker', NULL, 'StartedAt', NULL, 'FinishedAt', CURRENT_TIMESTAMP, 'Outcome', 'LegacyRecovered', 'ResponseStatusCode', \"ResponseStatusCode\", 'Error', \"LastError\"))::text, \"LastError\" = substr(CASE WHEN COALESCE(\"LastError\", '') = '' THEN 'Recovered legacy webhook delivery without a durable claim.' ELSE \"LastError\" || ' Recovered legacy webhook delivery without a durable claim.' END, 1, 2000), \"UpdatedAt\" = CURRENT_TIMESTAMP WHERE \"State\" = 'Delivering';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_webhook_deliveries_State_ClaimExpiresAt",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "AttemptHistoryJson",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimExpiresAt",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "webhook_deliveries");
        }
    }
}
