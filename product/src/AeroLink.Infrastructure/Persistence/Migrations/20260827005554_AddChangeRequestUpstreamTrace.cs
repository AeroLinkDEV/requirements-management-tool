using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeRequestUpstreamTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InheritedAt",
                table: "system_change_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InheritedFromChangeRequestId",
                table: "system_change_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InheritedUpstreamContextJson",
                table: "system_change_requests",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoUpstreamRationale",
                table: "system_change_requests",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NoUpstreamStatedAt",
                table: "system_change_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoUpstreamStatedBy",
                table: "system_change_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpstreamAnswerAffirmed",
                table: "system_change_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpstreamAnswerAffirmedAt",
                table: "system_change_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpstreamAnswerAffirmedBy",
                table: "system_change_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotContractVersion",
                table: "review_cycles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "review_cycles",
                type: "character varying(200000)",
                maxLength: 200000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "change_request_upstream_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UpstreamLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpstreamChangeRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpstreamDisplayNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UpstreamBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpstreamBuildVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_request_upstream_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_change_request_upstream_history_system_change_requests_Chan~",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "change_request_upstream_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamChangeRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamDisplayNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    UpstreamBuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpstreamBuildVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_request_upstream_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_change_request_upstream_links_software_releases_UpstreamBui~",
                        column: x => x.UpstreamBuildId,
                        principalTable: "software_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_change_request_upstream_links_system_change_requests_Change~",
                        column: x => x.ChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_change_request_upstream_links_system_change_requests_Upstre~",
                        column: x => x.UpstreamChangeRequestId,
                        principalTable: "system_change_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_change_request_upstream_history_ChangeRequestId_OccurredAt",
                table: "change_request_upstream_history",
                columns: new[] { "ChangeRequestId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_change_request_upstream_history_UpstreamLinkId",
                table: "change_request_upstream_history",
                column: "UpstreamLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_change_request_upstream_links_ChangeRequestId_UpstreamChang~",
                table: "change_request_upstream_links",
                columns: new[] { "ChangeRequestId", "UpstreamChangeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_change_request_upstream_links_UpstreamBuildId",
                table: "change_request_upstream_links",
                column: "UpstreamBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_change_request_upstream_links_UpstreamChangeRequestId",
                table: "change_request_upstream_links",
                column: "UpstreamChangeRequestId");

            // SQLite fixtures exercise the domain and relational shape; PostgreSQL also protects the
            // trace answer at the provider boundary. Active link rows are immutable, every Draft link
            // removal must have a matching append-only history event, and the scalar answer cannot be
            // rewritten after the change request leaves Draft.
            if (migrationBuilder.ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION aerolink_guard_upstream_link_identity()
                    RETURNS trigger LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF TG_OP = 'INSERT' THEN
                            IF NOT EXISTS (
                                SELECT 1 FROM "system_change_requests"
                                WHERE "Id" = NEW."ChangeRequestId" AND "State" = 'Draft') THEN
                                RAISE EXCEPTION 'A change-request upstream link can be inserted only while its owner is Draft.';
                            END IF;
                            RETURN NEW;
                        END IF;
                        IF NOT EXISTS (
                            SELECT 1 FROM "system_change_requests"
                            WHERE "Id" = OLD."ChangeRequestId" AND "State" = 'Draft') THEN
                            RAISE EXCEPTION 'A change-request upstream link can change only while its owner is Draft.';
                        END IF;
                        IF TG_OP = 'UPDATE' THEN
                            RAISE EXCEPTION 'The identity and rationale of a change-request upstream link are immutable.';
                        END IF;
                        RETURN OLD;
                    END;
                    $function$;
                    CREATE TRIGGER aerolink_guard_upstream_link_identity
                    BEFORE INSERT OR UPDATE OR DELETE ON "change_request_upstream_links"
                    FOR EACH ROW EXECUTE FUNCTION aerolink_guard_upstream_link_identity();
                    """);
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION aerolink_require_upstream_link_delete_history()
                    RETURNS trigger LANGUAGE plpgsql AS $function$
                    BEGIN
                        -- Parent Draft deletion intentionally removes its trace while the parent trigger is
                        -- running. By deferred-check time the parent is absent, so that cascade remains valid.
                        IF NOT EXISTS (
                            SELECT 1 FROM "system_change_requests"
                            WHERE "Id" = OLD."ChangeRequestId") THEN
                            RETURN OLD;
                        END IF;
                        IF NOT EXISTS (
                            SELECT 1 FROM "change_request_upstream_history"
                            WHERE "ChangeRequestId" = OLD."ChangeRequestId"
                              AND "UpstreamLinkId" = OLD."Id"
                              AND "Action" IN ('Removed', 'Changed', 'NoUpstreamReplaced')) THEN
                            RAISE EXCEPTION 'A Draft upstream link can be deleted only with matching controlled history.';
                        END IF;
                        RETURN OLD;
                    END;
                    $function$;
                    CREATE CONSTRAINT TRIGGER aerolink_require_upstream_link_delete_history
                    AFTER DELETE ON "change_request_upstream_links"
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION aerolink_require_upstream_link_delete_history();
                    """);
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION aerolink_guard_upstream_history_immutable()
                    RETURNS trigger LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF TG_OP = 'INSERT' THEN
                            IF NOT EXISTS (
                                SELECT 1 FROM "system_change_requests"
                                WHERE "Id" = NEW."ChangeRequestId" AND "State" = 'Draft') THEN
                                RAISE EXCEPTION 'Change-request upstream history can be appended only while its owner is Draft.';
                            END IF;
                            RETURN NEW;
                        END IF;
                        -- A parent Draft may be deleted as ordinary abandoned work. Its parent-table trigger
                        -- removes these rows while the Draft is still visible, at nested trigger depth. A
                        -- direct history DELETE remains depth 1 and is refused.
                        IF TG_OP = 'DELETE' AND pg_trigger_depth() > 1 AND EXISTS (
                            SELECT 1 FROM "system_change_requests"
                            WHERE "Id" = OLD."ChangeRequestId" AND "State" = 'Draft') THEN
                            RETURN OLD;
                        END IF;
                        RAISE EXCEPTION 'Change-request upstream history is immutable.';
                    END;
                    $function$;
                    CREATE TRIGGER aerolink_guard_upstream_history_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON "change_request_upstream_history"
                    FOR EACH ROW EXECUTE FUNCTION aerolink_guard_upstream_history_immutable();
                    """);
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION aerolink_guard_upstream_answer_scalars()
                    RETURNS trigger LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF ROW(
                            NEW."NoUpstreamRationale", NEW."NoUpstreamStatedBy", NEW."NoUpstreamStatedAt",
                            NEW."InheritedUpstreamContextJson", NEW."InheritedFromChangeRequestId", NEW."InheritedAt",
                            NEW."UpstreamAnswerAffirmed", NEW."UpstreamAnswerAffirmedBy", NEW."UpstreamAnswerAffirmedAt")
                           IS DISTINCT FROM ROW(
                            OLD."NoUpstreamRationale", OLD."NoUpstreamStatedBy", OLD."NoUpstreamStatedAt",
                            OLD."InheritedUpstreamContextJson", OLD."InheritedFromChangeRequestId", OLD."InheritedAt",
                            OLD."UpstreamAnswerAffirmed", OLD."UpstreamAnswerAffirmedBy", OLD."UpstreamAnswerAffirmedAt")
                           AND (OLD."State" <> 'Draft' OR NEW."State" <> 'Draft') THEN
                            RAISE EXCEPTION 'The upstream answer cannot be changed after its owner leaves Draft.';
                        END IF;
                        RETURN NEW;
                    END;
                    $function$;
                    CREATE TRIGGER aerolink_guard_upstream_answer_scalars
                    BEFORE UPDATE ON "system_change_requests"
                    FOR EACH ROW EXECUTE FUNCTION aerolink_guard_upstream_answer_scalars();
                    """);
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION aerolink_remove_draft_upstream_trace()
                    RETURNS trigger LANGUAGE plpgsql AS $function$
                    BEGIN
                        IF OLD."State" = 'Draft' THEN
                            DELETE FROM "change_request_upstream_history"
                            WHERE "ChangeRequestId" = OLD."Id";
                            DELETE FROM "change_request_upstream_links"
                            WHERE "ChangeRequestId" = OLD."Id";
                        END IF;
                        RETURN OLD;
                    END;
                    $function$;
                    CREATE TRIGGER aerolink_remove_draft_upstream_trace
                    BEFORE DELETE ON "system_change_requests"
                    FOR EACH ROW EXECUTE FUNCTION aerolink_remove_draft_upstream_trace();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS aerolink_remove_draft_upstream_trace ON \"system_change_requests\";");
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_remove_draft_upstream_trace();");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS aerolink_guard_upstream_answer_scalars ON \"system_change_requests\";");
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_guard_upstream_answer_scalars();");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS aerolink_require_upstream_link_delete_history ON \"change_request_upstream_links\";");
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_require_upstream_link_delete_history();");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS aerolink_guard_upstream_link_identity ON \"change_request_upstream_links\";");
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_guard_upstream_link_identity();");
                migrationBuilder.Sql("DROP TRIGGER IF EXISTS aerolink_guard_upstream_history_immutable ON \"change_request_upstream_history\";");
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS aerolink_guard_upstream_history_immutable();");
            }

            migrationBuilder.DropTable(
                name: "change_request_upstream_history");

            migrationBuilder.DropTable(
                name: "change_request_upstream_links");

            migrationBuilder.DropColumn(
                name: "InheritedAt",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "InheritedFromChangeRequestId",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "InheritedUpstreamContextJson",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "NoUpstreamRationale",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "NoUpstreamStatedAt",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "NoUpstreamStatedBy",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "UpstreamAnswerAffirmed",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "UpstreamAnswerAffirmedAt",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "UpstreamAnswerAffirmedBy",
                table: "system_change_requests");

            migrationBuilder.DropColumn(
                name: "SnapshotContractVersion",
                table: "review_cycles");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "review_cycles");
        }
    }
}
