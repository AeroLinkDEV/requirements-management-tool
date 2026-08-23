using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

/// <summary>
/// Forward-only relabelling of the three active Test Change Request families. The replacement is derived from
/// the current executable verification artifact (SYSTP + CR, HLRTC + CR, and LLRTC + CR). The old sequence rows
/// remain as non-allocating tombstones so an old backup or caller cannot restart a retired family at one.
/// </summary>
[DbContext(typeof(AeroLinkDbContext))]
[Migration("20260822180000_RenameTestChangeRequestPrefixes")]
public partial class RenameTestChangeRequestPrefixes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            DECLARE
                marker text := 'VerificationIdentityMigration.TestChangeRequests.v1';
                system_watermark bigint;
                high_watermark bigint;
                low_watermark bigint;
            BEGIN
                IF EXISTS (SELECT 1 FROM "security_audit_events"
                           WHERE "EventType" = marker || '.Completed'
                             AND "Target" = 'test-change-request-identities') THEN
                    RETURN;
                END IF;

                -- Parse JSON before rewriting. Invalid JSON remains byte-for-byte unchanged. The recursive
                -- walk only treats domain-owned identity properties as identities; prose, saved-search text,
                -- and generic number fields are deliberately not a bag of replaceable strings. Arrays under
                -- an identity property are supported, but each scalar still has to be an exact TCR identity.
                CREATE OR REPLACE FUNCTION aerolink_rewrite_tcr_jsonb(node jsonb, property_name text DEFAULT NULL) RETURNS jsonb
                LANGUAGE plpgsql IMMUTABLE AS $tcr_jsonb$
                DECLARE result jsonb;
                BEGIN
                    CASE jsonb_typeof(node)
                        WHEN 'object' THEN
                            SELECT COALESCE(jsonb_object_agg(key, aerolink_rewrite_tcr_jsonb(value, key)), '{}'::jsonb)
                                INTO result FROM jsonb_each(node);
                        WHEN 'array' THEN
                            SELECT COALESCE(jsonb_agg(aerolink_rewrite_tcr_jsonb(value, property_name)), '[]'::jsonb)
                                INTO result FROM jsonb_array_elements(node);
                        WHEN 'string' THEN
                            IF lower(COALESCE(property_name, '')) IN (
                                'basenumber', 'displaynumber', 'subjectdisplaynumber', 'changerequestnumber',
                                'testchangerequestnumber', 'testchangerequestdisplaynumber', 'artifactrevision',
                                'artifactidentity', 'sourcetestchangerequest', 'sourcechangerequestnumber', 'package')
                                AND node #>> '{}' ~ '^(SYSTCR|HLRTCR|LLRTCR)-[0-9]+(\.[0-9]+)?$' THEN
                                result := to_jsonb(regexp_replace(regexp_replace(regexp_replace(node #>> '{}',
                                    '^SYSTCR-', 'SYSTPCR-'), '^HLRTCR-', 'HLRTCCR-'), '^LLRTCR-', 'LLRTCCR-'));
                            ELSE
                                result := node;
                            END IF;
                        ELSE
                            result := node;
                    END CASE;
                    RETURN result;
                END $tcr_jsonb$;
                CREATE OR REPLACE FUNCTION aerolink_rewrite_tcr_json(value text) RETURNS text
                LANGUAGE plpgsql IMMUTABLE AS $tcr_json$
                DECLARE parsed jsonb; rewritten jsonb;
                BEGIN
                    IF value IS NULL THEN RETURN NULL; END IF;
                    BEGIN parsed := value::jsonb; EXCEPTION WHEN others THEN RETURN value; END;
                    rewritten := aerolink_rewrite_tcr_jsonb(parsed);
                    IF rewritten = parsed THEN RETURN value; END IF;
                    RETURN rewritten::text;
                END $tcr_json$;

                -- Capture a defensive per-family watermark before changing any authoritative identity. The
                -- scan includes current records, baseline selections, structured snapshots, drafts/merges,
                -- imports/jobs, notifications, and the retired allocator row. A number seen only in a saved
                -- payload is still reserved: it must never be handed out after the rename.
                SELECT GREATEST(
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^SYSTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_change_reviews" WHERE "BaseNumber" ~ '^SYSTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("TestChangeRequestDisplayNumber", 'SYSTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "baseline_test_change_request_selections" WHERE "TestChangeRequestDisplayNumber" ~ 'SYSTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("SourceChangeRequestsJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "test_procedure_revisions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("QueryJson", '') || '|' || COALESCE("ColumnsJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "saved_procedure_views") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_edit_sessions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_draft_snapshots") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("BaseJson", '') || '|' || COALESCE("LocalJson", '') || '|' || COALESCE("RemoteJson", '') || '|' || COALESCE("ResolutionJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_merge_conflicts") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("RequestJson", '') || '|' || COALESCE("ResultJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "enterprise_operation_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("MappingJson", '') || '|' || COALESCE("RowsJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "requirement_interchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("Title", '') || '|' || COALESCE("Detail", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "user_notifications") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DisplayNumber", '') || '|' || COALESCE("DeepLink", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "managed_document_links") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("ManifestJson", '') || '|' || COALESCE("CheckpointJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "reqif_exchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("PayloadJson", ''), 'SYSTCR-([0-9]+)', 'g') AS capture
                            FROM "integration_events" WHERE "State" = 'Pending') matches), 1),
                    COALESCE((SELECT "NextValue" FROM "identifier_sequences" WHERE "Scope" = 'SYSTCR'), 1)
                ) INTO system_watermark;
                SELECT GREATEST(
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^HLRTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_change_reviews" WHERE "BaseNumber" ~ '^HLRTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("TestChangeRequestDisplayNumber", 'HLRTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "baseline_test_change_request_selections" WHERE "TestChangeRequestDisplayNumber" ~ 'HLRTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("SourceChangeRequestsJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "test_procedure_revisions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("QueryJson", '') || '|' || COALESCE("ColumnsJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "saved_procedure_views") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_edit_sessions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_draft_snapshots") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("BaseJson", '') || '|' || COALESCE("LocalJson", '') || '|' || COALESCE("RemoteJson", '') || '|' || COALESCE("ResolutionJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_merge_conflicts") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("RequestJson", '') || '|' || COALESCE("ResultJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "enterprise_operation_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("MappingJson", '') || '|' || COALESCE("RowsJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "requirement_interchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("Title", '') || '|' || COALESCE("Detail", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "user_notifications") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DisplayNumber", '') || '|' || COALESCE("DeepLink", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "managed_document_links") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("ManifestJson", '') || '|' || COALESCE("CheckpointJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "reqif_exchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("PayloadJson", ''), 'HLRTCR-([0-9]+)', 'g') AS capture
                            FROM "integration_events" WHERE "State" = 'Pending') matches), 1),
                    COALESCE((SELECT "NextValue" FROM "identifier_sequences" WHERE "Scope" = 'HLRTCR'), 1)
                ) INTO high_watermark;
                SELECT GREATEST(
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^LLRTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_change_reviews" WHERE "BaseNumber" ~ '^LLRTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("TestChangeRequestDisplayNumber", 'LLRTCR-([0-9]+)'))[1]::bigint) + 1
                        FROM "baseline_test_change_request_selections" WHERE "TestChangeRequestDisplayNumber" ~ 'LLRTCR-[0-9]+'), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("SourceChangeRequestsJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "test_procedure_revisions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("QueryJson", '') || '|' || COALESCE("ColumnsJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "saved_procedure_views") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_edit_sessions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_draft_snapshots") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("BaseJson", '') || '|' || COALESCE("LocalJson", '') || '|' || COALESCE("RemoteJson", '') || '|' || COALESCE("ResolutionJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "artifact_merge_conflicts") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("RequestJson", '') || '|' || COALESCE("ResultJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "enterprise_operation_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("MappingJson", '') || '|' || COALESCE("RowsJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "requirement_interchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("Title", '') || '|' || COALESCE("Detail", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "user_notifications") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DisplayNumber", '') || '|' || COALESCE("DeepLink", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "managed_document_links") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("ManifestJson", '') || '|' || COALESCE("CheckpointJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "reqif_exchange_jobs") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("PayloadJson", ''), 'LLRTCR-([0-9]+)', 'g') AS capture
                            FROM "integration_events" WHERE "State" = 'Pending') matches), 1),
                    COALESCE((SELECT "NextValue" FROM "identifier_sequences" WHERE "Scope" = 'LLRTCR'), 1)
                ) INTO low_watermark;

                UPDATE "test_change_reviews" SET "BaseNumber" = replace(replace(replace("BaseNumber", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-')
                    WHERE "BaseNumber" LIKE 'SYSTCR-%' OR "BaseNumber" LIKE 'HLRTCR-%' OR "BaseNumber" LIKE 'LLRTCR-%';
                UPDATE "baseline_test_change_request_selections" SET "TestChangeRequestDisplayNumber" = replace(replace(replace("TestChangeRequestDisplayNumber", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-')
                    WHERE "TestChangeRequestDisplayNumber" LIKE '%SYSTCR-%' OR "TestChangeRequestDisplayNumber" LIKE '%HLRTCR-%' OR "TestChangeRequestDisplayNumber" LIKE '%LLRTCR-%';

                -- These fields are structured identity projections. Audit/security history and immutable
                -- signed rows are intentionally not rewritten; their old bytes remain evidence.
                UPDATE "test_procedure_revisions" SET "SourceChangeRequestsJson" = aerolink_rewrite_tcr_json("SourceChangeRequestsJson")
                    WHERE "SourceChangeRequestsJson" LIKE '%SYSTCR-%' OR "SourceChangeRequestsJson" LIKE '%HLRTCR-%' OR "SourceChangeRequestsJson" LIKE '%LLRTCR-%';
                UPDATE "saved_procedure_views" SET "QueryJson" = aerolink_rewrite_tcr_json("QueryJson"), "ColumnsJson" = aerolink_rewrite_tcr_json("ColumnsJson")
                    WHERE "QueryJson" LIKE '%SYSTCR-%' OR "QueryJson" LIKE '%HLRTCR-%' OR "QueryJson" LIKE '%LLRTCR-%' OR "ColumnsJson" LIKE '%SYSTCR-%' OR "ColumnsJson" LIKE '%HLRTCR-%' OR "ColumnsJson" LIKE '%LLRTCR-%';
                UPDATE "artifact_edit_sessions" SET "DraftJson" = aerolink_rewrite_tcr_json("DraftJson")
                    WHERE "DraftJson" LIKE '%SYSTCR-%' OR "DraftJson" LIKE '%HLRTCR-%' OR "DraftJson" LIKE '%LLRTCR-%';
                UPDATE "artifact_draft_snapshots" SET "DraftJson" = aerolink_rewrite_tcr_json("DraftJson")
                    WHERE "DraftJson" LIKE '%SYSTCR-%' OR "DraftJson" LIKE '%HLRTCR-%' OR "DraftJson" LIKE '%LLRTCR-%';
                UPDATE "artifact_merge_conflicts" SET "BaseJson" = aerolink_rewrite_tcr_json("BaseJson"), "LocalJson" = aerolink_rewrite_tcr_json("LocalJson"), "RemoteJson" = aerolink_rewrite_tcr_json("RemoteJson"), "ResolutionJson" = CASE WHEN "ResolutionJson" IS NULL THEN NULL ELSE aerolink_rewrite_tcr_json("ResolutionJson") END
                    WHERE "BaseJson" LIKE '%SYSTCR-%' OR "BaseJson" LIKE '%HLRTCR-%' OR "BaseJson" LIKE '%LLRTCR-%' OR "LocalJson" LIKE '%SYSTCR-%' OR "LocalJson" LIKE '%HLRTCR-%' OR "LocalJson" LIKE '%LLRTCR-%' OR "RemoteJson" LIKE '%SYSTCR-%' OR "RemoteJson" LIKE '%HLRTCR-%' OR "RemoteJson" LIKE '%LLRTCR-%' OR "ResolutionJson" LIKE '%SYSTCR-%' OR "ResolutionJson" LIKE '%HLRTCR-%' OR "ResolutionJson" LIKE '%LLRTCR-%';
                UPDATE "enterprise_operation_jobs" SET "RequestJson" = aerolink_rewrite_tcr_json("RequestJson"), "ResultJson" = aerolink_rewrite_tcr_json("ResultJson")
                    WHERE "RequestJson" LIKE '%SYSTCR-%' OR "RequestJson" LIKE '%HLRTCR-%' OR "RequestJson" LIKE '%LLRTCR-%' OR "ResultJson" LIKE '%SYSTCR-%' OR "ResultJson" LIKE '%HLRTCR-%' OR "ResultJson" LIKE '%LLRTCR-%';
                UPDATE "requirement_interchange_jobs" SET "MappingJson" = aerolink_rewrite_tcr_json("MappingJson"), "RowsJson" = aerolink_rewrite_tcr_json("RowsJson")
                    WHERE "MappingJson" LIKE '%SYSTCR-%' OR "MappingJson" LIKE '%HLRTCR-%' OR "MappingJson" LIKE '%LLRTCR-%' OR "RowsJson" LIKE '%SYSTCR-%' OR "RowsJson" LIKE '%HLRTCR-%' OR "RowsJson" LIKE '%LLRTCR-%';
                UPDATE "user_notifications" SET "Title" = replace(replace(replace("Title", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-'), "Detail" = replace(replace(replace("Detail", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-')
                    WHERE "Title" LIKE '%SYSTCR-%' OR "Title" LIKE '%HLRTCR-%' OR "Title" LIKE '%LLRTCR-%' OR "Detail" LIKE '%SYSTCR-%' OR "Detail" LIKE '%HLRTCR-%' OR "Detail" LIKE '%LLRTCR-%';
                UPDATE "user_notifications" SET "Route" = replace(replace(replace("Route", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-')
                    WHERE "Route" LIKE '%SYSTCR-%' OR "Route" LIKE '%HLRTCR-%' OR "Route" LIKE '%LLRTCR-%';
                UPDATE "artifact_assignments" SET "ArtifactType" = CASE "ArtifactType" WHEN 'SYSTCR' THEN 'SYSTPCR' WHEN 'HLRTCR' THEN 'HLRTCCR' WHEN 'LLRTCR' THEN 'LLRTCCR' ELSE "ArtifactType" END
                    WHERE "ArtifactType" IN ('SYSTCR', 'HLRTCR', 'LLRTCR');
                UPDATE "artifact_comments" SET "ArtifactType" = CASE "ArtifactType" WHEN 'SYSTCR' THEN 'SYSTPCR' WHEN 'HLRTCR' THEN 'HLRTCCR' WHEN 'LLRTCR' THEN 'LLRTCCR' ELSE "ArtifactType" END
                    WHERE "ArtifactType" IN ('SYSTCR', 'HLRTCR', 'LLRTCR');
                UPDATE "artifact_watches" SET "ArtifactType" = CASE "ArtifactType" WHEN 'SYSTCR' THEN 'SYSTPCR' WHEN 'HLRTCR' THEN 'HLRTCCR' WHEN 'LLRTCR' THEN 'LLRTCCR' ELSE "ArtifactType" END
                    WHERE "ArtifactType" IN ('SYSTCR', 'HLRTCR', 'LLRTCR');
                UPDATE "managed_document_links" SET "ArtifactType" = CASE "ArtifactType" WHEN 'SYSTCR' THEN 'SYSTPCR' WHEN 'HLRTCR' THEN 'HLRTCCR' WHEN 'LLRTCR' THEN 'LLRTCCR' ELSE "ArtifactType" END,
                    "DisplayNumber" = replace(replace(replace("DisplayNumber", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-'),
                    "DeepLink" = replace(replace(replace("DeepLink", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-')
                    WHERE "ArtifactType" IN ('SYSTCR', 'HLRTCR', 'LLRTCR') OR "DisplayNumber" LIKE '%SYSTCR-%' OR "DisplayNumber" LIKE '%HLRTCR-%' OR "DisplayNumber" LIKE '%LLRTCR-%' OR "DeepLink" LIKE '%SYSTCR-%' OR "DeepLink" LIKE '%HLRTCR-%' OR "DeepLink" LIKE '%LLRTCR-%';
                UPDATE "reqif_exchange_jobs" SET "ManifestJson" = aerolink_rewrite_tcr_json("ManifestJson"), "CheckpointJson" = aerolink_rewrite_tcr_json("CheckpointJson")
                    WHERE "ManifestJson" LIKE '%SYSTCR-%' OR "ManifestJson" LIKE '%HLRTCR-%' OR "ManifestJson" LIKE '%LLRTCR-%' OR "CheckpointJson" LIKE '%SYSTCR-%' OR "CheckpointJson" LIKE '%HLRTCR-%' OR "CheckpointJson" LIKE '%LLRTCR-%';
                UPDATE "integration_events" SET "PayloadJson" = aerolink_rewrite_tcr_json("PayloadJson")
                    WHERE "State" = 'Pending' AND ("PayloadJson" LIKE '%SYSTCR-%' OR "PayloadJson" LIKE '%HLRTCR-%' OR "PayloadJson" LIKE '%LLRTCR-%');

                -- New families start at the greatest old claim or structured occurrence. Retired rows are kept
                -- as tombstones; IdentifierAllocator rejects them before it can claim a value.
                INSERT INTO "identifier_sequences" ("Id", "Scope", "NextValue", "ConcurrencyStamp") VALUES
                    (gen_random_uuid(), 'SYSTPCR', system_watermark, 0),
                    (gen_random_uuid(), 'HLRTCCR', high_watermark, 0),
                    (gen_random_uuid(), 'LLRTCCR', low_watermark, 0)
                ON CONFLICT ("Scope") DO UPDATE SET "NextValue" = GREATEST("identifier_sequences"."NextValue", EXCLUDED."NextValue");
                UPDATE "identifier_sequences" SET "NextValue" = GREATEST("NextValue", CASE "Scope" WHEN 'SYSTCR' THEN system_watermark WHEN 'HLRTCR' THEN high_watermark WHEN 'LLRTCR' THEN low_watermark ELSE "NextValue" END), "ConcurrencyStamp" = "ConcurrencyStamp" + 1
                    WHERE "Scope" IN ('SYSTCR', 'HLRTCR', 'LLRTCR');

                -- Only a current, reconstructible review-cycle signature is handed to the runtime authority.
                -- Earlier cycles and signatures remain immutable historical evidence when their original
                -- canonical bytes can no longer be reconstructed honestly.
                INSERT INTO "security_audit_events" ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                SELECT gen_random_uuid(), 'VerificationIdentityMigration.SignatureSuperseded', 'aerolink-migration', 'ElectronicSignature:' || s."Id"::text, 'Superseded',
                    json_build_object('migration', marker, 'oldArtifactIdentity', s."ArtifactRevision", 'oldSignatureId', s."Id", 'oldSignatureHash', s."ContentHash", 'newArtifactIdentity', replace(replace(replace(s."ArtifactRevision", 'SYSTCR-', 'SYSTPCR-'), 'HLRTCR-', 'HLRTCCR-'), 'LLRTCR-', 'LLRTCCR-'), 'newContentHash', NULL, 'reason', 'Canonical review bytes must be regenerated by the governed migration authority; no signature was fabricated.')::text,
                    '', now()
                FROM "electronic_signatures" s
                JOIN "test_change_reviews" r ON s."ArtifactType" = 'TestChangeRequest' AND r."Id" = s."ArtifactId"
                JOIN "review_cycles" c ON c."TestChangeReviewId" = r."Id" AND c."SnapshotHash" = s."ContentHash"
                WHERE r."State" IN ('InReview', 'Approved') AND c."State" IN ('Active', 'Approved')
                  AND c."Sequence" = (SELECT MAX(latest."Sequence") FROM "review_cycles" latest WHERE latest."TestChangeReviewId" = r."Id")
                  AND (s."ReviewCycle" IS NULL OR s."ReviewCycle" = c."Sequence")
                  AND (s."ReviewStepId" IS NULL
                    OR EXISTS (SELECT 1 FROM "approval_steps" signed_step
                               WHERE signed_step."Id" = s."ReviewStepId"
                                 AND signed_step."ReviewCycleId" = c."Id"
                                 AND signed_step."State" = 'Approved'))
                  AND (s."ArtifactRevision" LIKE 'SYSTCR-%' OR s."ArtifactRevision" LIKE 'HLRTCR-%' OR s."ArtifactRevision" LIKE 'LLRTCR-%');

                INSERT INTO "security_audit_events" ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                VALUES (gen_random_uuid(), marker || '.Pending', 'aerolink-migration', 'test-change-request-identities', 'Pending',
                    json_build_object('version', marker, 'systemWatermark', system_watermark, 'highWatermark', high_watermark, 'lowWatermark', low_watermark, 'forwardOnly', true, 'signaturePolicy', 'Original human signatures and hashes remain unchanged; startup must record real replacement hashes before completion.')::text,
                    '', now());
                DROP FUNCTION aerolink_rewrite_tcr_json(text);
                DROP FUNCTION aerolink_rewrite_tcr_jsonb(jsonb, text);
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Test Change Request prefix migration is forward-only; restore a qualified backup to recover the retired identifiers.");
}
