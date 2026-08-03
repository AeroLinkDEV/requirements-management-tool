using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Detaches requirement changes held by a change request whose type forbids their level.
    ///
    /// A System change request governs System requirements; HLR and LLR work belongs to an SWCR. The domain
    /// has enforced that since #275, but enforcement only guards records being written. The persistent
    /// database kept <c>SCR-00032.00</c> carrying <c>HLR-000075.02</c>, and because that record is Deferred
    /// nothing in the ordinary workflow ever rewrote it. Correcting the seeder fixed new databases only.
    ///
    /// Expressed as a rule over the data rather than by identifier: what matters is that no such row survives
    /// anywhere, not that one known row is gone. Idempotent — a second run finds nothing to detach.
    ///
    /// The change request itself is preserved. Only the offending requirement change is detached, and a
    /// security audit event records what was removed, from where, and why, so the repair is attributable
    /// rather than a silent deletion of controlled content.
    /// </summary>
    public partial class RepairCrossLevelRequirementChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PostgreSQL only. SQLite databases are per-test and are created from the current model, so they
            // never contain a row that predates the rule.
            if (!migrationBuilder.ActiveProvider.Contains("Npgsql")) return;

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    offending RECORD;
                    detached INT := 0;
                BEGIN
                    FOR offending IN
                        SELECT change."Id"          AS change_id,
                               change."BaseNumber"  AS change_number,
                               change."Level"       AS change_level,
                               request."BaseNumber" AS request_number,
                               request."Revision"   AS request_revision,
                               request."Type"       AS request_type
                        FROM requirement_changes change
                        JOIN system_change_requests request ON request."Id" = change."ScrId"
                        WHERE (request."Type" = 'System' AND change."Level" <> 'System')
                           OR (request."Type" <> 'System' AND change."Level" = 'System')
                    LOOP
                        INSERT INTO security_audit_events
                            ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                        VALUES (
                            gen_random_uuid(),
                            'CrossLevelRequirementChangeDetached',
                            'migration',
                            'RequirementChange:' || offending.change_id,
                            'Success',
                            'Detached ' || offending.change_number || ' (' || offending.change_level ||
                            ') from ' || offending.request_type || ' change request ' || offending.request_number ||
                            '.' || LPAD(offending.request_revision::text, 2, '0') ||
                            '. A System change request governs System requirements only; HLR and LLR work belongs to an SWCR.',
                            'local',
                            NOW()
                        );

                        DELETE FROM requirement_changes WHERE "Id" = offending.change_id;
                        detached := detached + 1;
                    END LOOP;

                    IF detached > 0 THEN
                        RAISE NOTICE 'Detached % requirement change(s) whose level their change request forbids.', detached;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately irreversible. The rows removed here were invalid under a rule the domain now
            // enforces, so restoring them would recreate records the product refuses to write. The audit
            // events remain as the record of what was detached.
        }
    }
}
