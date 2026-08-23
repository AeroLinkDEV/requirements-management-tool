using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroLink.Infrastructure.Persistence.Migrations;

/// <summary>
/// Forward-only relabelling of the existing software verification aggregate.
///
/// The aggregate remains in the compatibility TestProcedure tables so its complete revision body and
/// provenance survive this vocabulary change. Only identity-bearing projections are rewritten. Signed bytes
/// are not silently re-signed: the security-audit rows below retain the old signature hash and identify the
/// migration that requires a governed regenerated rendition before a new signature can be made.
/// </summary>
[DbContext(typeof(AeroLinkDbContext))]
[Migration("20260822170000_RenameSoftwareVerificationArtifactsToCases")]
public partial class RenameSoftwareVerificationArtifactsToCases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            DECLARE
                marker text := 'VerificationIdentityMigration.SoftwareCases.v1';
                high_watermark bigint;
                low_watermark bigint;
            BEGIN
                IF EXISTS (SELECT 1 FROM "security_audit_events" WHERE "EventType" = marker || '.Completed' AND "Target" = 'software-verification-identities') THEN
                    RETURN;
                END IF;

                -- JSON columns are parsed before rewriting. Invalid/user-authored prose is returned byte-for-byte
                -- unchanged; no audit sentence or revision body is treated as a bag of replaceable text.
                CREATE OR REPLACE FUNCTION aerolink_rewrite_case_jsonb(node jsonb, property_name text DEFAULT NULL) RETURNS jsonb
                LANGUAGE plpgsql IMMUTABLE AS $case_jsonb$
                DECLARE result jsonb;
                BEGIN
                    CASE jsonb_typeof(node)
                        WHEN 'object' THEN
                            SELECT COALESCE(jsonb_object_agg(key, aerolink_rewrite_case_jsonb(value, key)), '{}'::jsonb)
                                INTO result FROM jsonb_each(node);
                        WHEN 'array' THEN
                            SELECT COALESCE(jsonb_agg(aerolink_rewrite_case_jsonb(value, property_name)), '[]'::jsonb)
                                INTO result FROM jsonb_array_elements(node);
                        WHEN 'string' THEN
                            result := CASE WHEN lower(property_name) IN ('basenumber', 'displaynumber', 'subjectdisplaynumber',
                                'artifactrevision', 'artifactidentity', 'procedurenumber', 'procedureidentity')
                                THEN to_jsonb(regexp_replace(regexp_replace(node #>> '{}', 'HLRTP-', 'HLRTC-', 'g'), 'LLRTP-', 'LLRTC-', 'g'))
                                ELSE node END;
                        ELSE result := node;
                    END CASE;
                    RETURN result;
                END $case_jsonb$;
                CREATE OR REPLACE FUNCTION aerolink_rewrite_case_json(value text) RETURNS text
                LANGUAGE plpgsql IMMUTABLE AS $case_json$
                DECLARE parsed jsonb;
                BEGIN
                    BEGIN parsed := value::jsonb; EXCEPTION WHEN others THEN RETURN value; END;
                    RETURN aerolink_rewrite_case_jsonb(parsed)::text;
                END $case_json$;

                -- Capture the defensive watermark before changing the legacy identity. The search includes
                -- every structured identity store rewritten below, not only the denormalised review/impact
                -- identity sites. A saved draft or view can be the only surviving occurrence of a number;
                -- reserving it is safer than allowing the new Case allocator to collide with that payload.
                SELECT GREATEST(
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^(?:HLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_procedures" WHERE "BaseNumber" ~ '^(?:HLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^(?:HLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_procedure_changes" WHERE "BaseNumber" ~ '^(?:HLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("SubjectDisplayNumber", '^(?:HLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "verification_impact_items" WHERE "SubjectDisplayNumber" ~ '^(?:HLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("ArtifactRevision", 'HLRTP-([0-9]+)'))[1]::bigint) + 1
                        FROM "electronic_signatures" WHERE "ArtifactRevision" ~ 'HLRTP-[0-9]+'), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("SourceChangeRequestsJson", ''), 'HLRTP-([0-9]+)', 'g') AS capture
                            FROM "test_procedure_revisions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("QueryJson", '') || '|' || COALESCE("ColumnsJson", ''), 'HLRTP-([0-9]+)', 'g') AS capture
                            FROM "saved_procedure_views") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'HLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_edit_sessions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'HLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_draft_snapshots") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("BaseJson", '') || '|' || COALESCE("LocalJson", '') || '|' || COALESCE("RemoteJson", '') || '|' || COALESCE("ResolutionJson", ''), 'HLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_merge_conflicts") matches), 1),
                    COALESCE((SELECT "NextValue" FROM "identifier_sequences" WHERE "Scope" = 'HLRTP'), 1)
                ) INTO high_watermark;
                SELECT GREATEST(
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^(?:LLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_procedures" WHERE "BaseNumber" ~ '^(?:LLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("BaseNumber", '^(?:LLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "test_procedure_changes" WHERE "BaseNumber" ~ '^(?:LLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("SubjectDisplayNumber", '^(?:LLRTP)-([0-9]+)'))[1]::bigint) + 1
                        FROM "verification_impact_items" WHERE "SubjectDisplayNumber" ~ '^(?:LLRTP)-[0-9]+'), 1),
                    COALESCE((SELECT MAX((regexp_match("ArtifactRevision", 'LLRTP-([0-9]+)'))[1]::bigint) + 1
                        FROM "electronic_signatures" WHERE "ArtifactRevision" ~ 'LLRTP-[0-9]+'), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("SourceChangeRequestsJson", ''), 'LLRTP-([0-9]+)', 'g') AS capture
                            FROM "test_procedure_revisions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("QueryJson", '') || '|' || COALESCE("ColumnsJson", ''), 'LLRTP-([0-9]+)', 'g') AS capture
                            FROM "saved_procedure_views") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'LLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_edit_sessions") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("DraftJson", ''), 'LLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_draft_snapshots") matches), 1),
                    COALESCE((SELECT MAX((matches.capture[1])::bigint) + 1
                        FROM (SELECT regexp_matches(COALESCE("BaseJson", '') || '|' || COALESCE("LocalJson", '') || '|' || COALESCE("RemoteJson", '') || '|' || COALESCE("ResolutionJson", ''), 'LLRTP-([0-9]+)', 'g') AS capture
                            FROM "artifact_merge_conflicts") matches), 1),
                    COALESCE((SELECT "NextValue" FROM "identifier_sequences" WHERE "Scope" = 'LLRTP'), 1)
                ) INTO low_watermark;

                -- Current software rows are Cases. System SYSTP rows are deliberately not touched.
                UPDATE "test_procedures" SET "BaseNumber" = regexp_replace("BaseNumber", '^HLRTP-', 'HLRTC-')
                    WHERE "BaseNumber" ~ '^HLRTP-' AND "Level" = 'HighLevel';
                UPDATE "test_procedures" SET "BaseNumber" = regexp_replace("BaseNumber", '^LLRTP-', 'LLRTC-')
                    WHERE "BaseNumber" ~ '^LLRTP-' AND "Level" = 'LowLevel';
                UPDATE "test_procedure_changes" SET "BaseNumber" = regexp_replace("BaseNumber", '^HLRTP-', 'HLRTC-')
                    WHERE "BaseNumber" ~ '^HLRTP-';
                UPDATE "test_procedure_changes" SET "BaseNumber" = regexp_replace("BaseNumber", '^LLRTP-', 'LLRTC-')
                    WHERE "BaseNumber" ~ '^LLRTP-';
                UPDATE "verification_impact_items" SET "SubjectDisplayNumber" = regexp_replace("SubjectDisplayNumber", '^HLRTP-', 'HLRTC-')
                    WHERE "SubjectDisplayNumber" ~ '^HLRTP-';
                UPDATE "verification_impact_items" SET "SubjectDisplayNumber" = regexp_replace("SubjectDisplayNumber", '^LLRTP-', 'LLRTC-')
                    WHERE "SubjectDisplayNumber" ~ '^LLRTP-';

                -- Notifications are live product links, not immutable audit events. Relabel the software
                -- artifact and its resolver route so a queued or unread notice opens the current Case surface.
                UPDATE "user_notifications" notification
                    SET "Type" = CASE WHEN notification."Type" = 'TestProcedureComment' THEN 'TestCaseComment' ELSE notification."Type" END,
                        "Title" = replace(replace(notification."Title", 'HLRTP-', 'HLRTC-'), 'LLRTP-', 'LLRTC-'),
                        "Detail" = replace(replace(notification."Detail", 'HLRTP-', 'HLRTC-'), 'LLRTP-', 'LLRTC-'),
                        "Route" = 'case:' || artifact."Id"::text
                    FROM "test_procedures" artifact
                    WHERE notification."ArtifactId" = artifact."Id"
                      AND artifact."Level" IN ('HighLevel', 'LowLevel')
                      AND (lower(notification."Route") LIKE 'testprocedure:%'
                           OR lower(notification."Route") LIKE 'procedure:%'
                           OR lower(notification."Route") LIKE 'case:%');

                UPDATE "artifact_comments" comment
                    SET "ArtifactType" = 'TestCase'
                    FROM "test_procedures" artifact
                    WHERE comment."ArtifactId" = artifact."Id"
                      AND comment."ArtifactType" = 'TestProcedure'
                      AND artifact."Level" IN ('HighLevel', 'LowLevel');

                -- These are structured identity/reference payloads. The legacy body columns on
                -- test_procedure_revisions are intentionally excluded, preserving every character of the
                -- approved revision body and its provenance.
                UPDATE "test_procedure_revisions" SET "SourceChangeRequestsJson" = aerolink_rewrite_case_json("SourceChangeRequestsJson")
                    WHERE "SourceChangeRequestsJson" LIKE '%HLRTP-%' OR "SourceChangeRequestsJson" LIKE '%LLRTP-%';
                UPDATE "saved_procedure_views" SET "QueryJson" = aerolink_rewrite_case_json("QueryJson"),
                    "ColumnsJson" = aerolink_rewrite_case_json("ColumnsJson")
                    WHERE "QueryJson" LIKE '%HLRTP-%' OR "QueryJson" LIKE '%LLRTP-%' OR "ColumnsJson" LIKE '%HLRTP-%' OR "ColumnsJson" LIKE '%LLRTP-%';
                UPDATE "artifact_edit_sessions" SET "DraftJson" = aerolink_rewrite_case_json("DraftJson")
                    WHERE "DraftJson" LIKE '%HLRTP-%' OR "DraftJson" LIKE '%LLRTP-%';
                UPDATE "artifact_draft_snapshots" SET "DraftJson" = aerolink_rewrite_case_json("DraftJson")
                    WHERE "DraftJson" LIKE '%HLRTP-%' OR "DraftJson" LIKE '%LLRTP-%';
                UPDATE "artifact_merge_conflicts" SET "BaseJson" = aerolink_rewrite_case_json("BaseJson"),
                    "LocalJson" = aerolink_rewrite_case_json("LocalJson"),
                    "RemoteJson" = aerolink_rewrite_case_json("RemoteJson"),
                    "ResolutionJson" = CASE WHEN "ResolutionJson" IS NULL THEN NULL ELSE aerolink_rewrite_case_json("ResolutionJson") END
                    WHERE "BaseJson" LIKE '%HLRTP-%' OR "BaseJson" LIKE '%LLRTP-%' OR "LocalJson" LIKE '%HLRTP-%' OR "LocalJson" LIKE '%LLRTP-%' OR "RemoteJson" LIKE '%HLRTP-%' OR "RemoteJson" LIKE '%LLRTP-%' OR "ResolutionJson" LIKE '%HLRTP-%' OR "ResolutionJson" LIKE '%LLRTP-%';
                -- Append-only audit/security history and hash-governed problem-report snapshots are not
                -- rewritten. Their old bytes remain historical evidence; the pending migration event below
                -- carries the explicit old/new identity explanation without mutating signed history.

                -- Existing software controlled-document rows represent the current Case output. The old enum
                -- members remain in code solely to explain historical rows that cannot be classified here.
                UPDATE "controlled_documents" SET "Type" = 'HighLevelTestCases',
                    "Title" = replace(replace("Title", 'Test Procedures', 'Test Cases'), 'Test Procedure', 'Test Case')
                    WHERE "Type" = 'HighLevelTestProcedures';
                UPDATE "controlled_documents" SET "Type" = 'LowLevelTestCases',
                    "Title" = replace(replace("Title", 'Test Procedures', 'Test Cases'), 'Test Procedure', 'Test Case')
                    WHERE "Type" = 'LowLevelTestProcedures';
                UPDATE "review_workflows" SET "AppliesTo" = 'HighLevelSoftwareCase'
                    WHERE "AppliesTo" = 'HighLevelSoftwareTest';
                UPDATE "review_workflows" SET "AppliesTo" = 'LowLevelSoftwareCase'
                    WHERE "AppliesTo" = 'LowLevelSoftwareTest';
                UPDATE "test_procedure_documents"
                    SET "Title" = replace(replace("Title", 'Test Procedures', 'Test Cases'), 'Test Procedure', 'Test Case'),
                        "Description" = CASE
                            WHEN "Description" = 'Controlled high-level software test procedures document for this project.'
                                THEN 'Controlled high-level software test cases document for this project.'
                            WHEN "Description" = 'Controlled low-level software test procedures document for this project.'
                                THEN 'Controlled low-level software test cases document for this project.'
                            ELSE "Description"
                        END
                    WHERE "Level" IN ('HighLevel', 'LowLevel');
                UPDATE "test_procedure_document_nodes" node
                    SET "Heading" = 'Unsectioned cases'
                    FROM "test_procedure_documents" document
                    WHERE node."DocumentId" = document."Id"
                      AND node."Type" = 'Section'
                      AND node."Heading" = 'Unsectioned procedures'
                      AND document."Level" IN ('HighLevel', 'LowLevel');

                -- Retain both families at one defensive watermark. The new Case allocator and the reserved
                -- future Procedure allocator therefore cannot restart or collide after this split.
                INSERT INTO "identifier_sequences" ("Id", "Scope", "NextValue", "ConcurrencyStamp")
                    VALUES (gen_random_uuid(), 'HLRTC', high_watermark, 0)
                    ON CONFLICT ("Scope") DO UPDATE SET "NextValue" = GREATEST("identifier_sequences"."NextValue", EXCLUDED."NextValue");
                INSERT INTO "identifier_sequences" ("Id", "Scope", "NextValue", "ConcurrencyStamp")
                    VALUES (gen_random_uuid(), 'HLRTP', high_watermark, 0)
                    ON CONFLICT ("Scope") DO UPDATE SET "NextValue" = GREATEST("identifier_sequences"."NextValue", EXCLUDED."NextValue");
                INSERT INTO "identifier_sequences" ("Id", "Scope", "NextValue", "ConcurrencyStamp")
                    VALUES (gen_random_uuid(), 'LLRTC', low_watermark, 0)
                    ON CONFLICT ("Scope") DO UPDATE SET "NextValue" = GREATEST("identifier_sequences"."NextValue", EXCLUDED."NextValue");
                INSERT INTO "identifier_sequences" ("Id", "Scope", "NextValue", "ConcurrencyStamp")
                    VALUES (gen_random_uuid(), 'LLRTP', low_watermark, 0)
                    ON CONFLICT ("Scope") DO UPDATE SET "NextValue" = GREATEST("identifier_sequences"."NextValue", EXCLUDED."NextValue");

                -- Human signatures remain immutable. This durable security event is the migration supersession
                -- record; it contains the original evidence identity/hash and the governed replacement slot,
                -- and explicitly refuses to claim a human signed rewritten bytes.
                INSERT INTO "security_audit_events" ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                SELECT gen_random_uuid(), 'VerificationIdentityMigration.SignatureSuperseded', 'aerolink-migration',
                    'ElectronicSignature:' || s."Id"::text, 'Superseded',
                    json_build_object('migration', marker, 'oldArtifactIdentity', s."ArtifactRevision",
                        'oldSignatureId', s."Id", 'oldSignatureHash', s."ContentHash",
                        'newArtifactIdentity', replace(replace(s."ArtifactRevision", 'HLRTP-', 'HLRTC-'), 'LLRTP-', 'LLRTC-'),
                        'newContentHash', NULL, 'reason', 'Controlled output must be regenerated and re-signed by a human; no signature was fabricated.')::text,
                    '', now()
                FROM "electronic_signatures" s
                LEFT JOIN "test_change_reviews" r ON s."ArtifactType" = 'TestChangeRequest' AND r."Id" = s."ArtifactId"
                WHERE s."ArtifactType" = 'TestChangeRequest'
                  AND r."Discipline" IN ('HighLevelSoftware', 'LowLevelSoftware')
                  -- A signature is pending only when its hash names the latest active/approved
                  -- review-cycle snapshot that the governed authority can reconstruct. Earlier
                  -- cycles remain immutable evidence; a TCR row merely containing a renamed case
                  -- is not enough to select every signature on that request.
                  AND r."State" IN ('InReview', 'Approved')
                  AND EXISTS (
                      SELECT 1
                      FROM "review_cycles" current_cycle
                      WHERE current_cycle."TestChangeReviewId" = r."Id"
                        AND current_cycle."State" IN ('Active', 'Approved')
                        AND current_cycle."Sequence" = (
                            SELECT MAX(latest_cycle."Sequence")
                            FROM "review_cycles" latest_cycle
                            WHERE latest_cycle."TestChangeReviewId" = r."Id")
                        AND current_cycle."SnapshotHash" = s."ContentHash"
                  )
                  AND (EXISTS (SELECT 1 FROM "test_procedure_changes" c
                               WHERE c."TestChangeReviewId" = r."Id"
                                 AND c."BaseNumber" ~ '^(?:HLRTC|LLRTC)-[0-9]+')
                       OR EXISTS (SELECT 1 FROM "verification_impact_items" i
                                  WHERE i."TestChangeReviewId" = r."Id"
                                    AND i."SubjectDisplayNumber" ~ '^(?:HLRTC|LLRTC)-[0-9]+'));

                -- Controlled-document signatures cover bytes/content bases that this migration regenerates.
                -- Preserve each human signature and create the same explicit supersession hand-off used for
                -- TestChangeRequests; startup records the real replacement hash after governed regeneration.
                INSERT INTO "security_audit_events" ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                SELECT gen_random_uuid(), 'VerificationIdentityMigration.SignatureSuperseded', 'aerolink-migration',
                    'ElectronicSignature:' || s."Id"::text, 'Superseded',
                    json_build_object('migration', marker, 'oldArtifactIdentity', s."ArtifactRevision",
                        'oldSignatureId', s."Id", 'oldSignatureHash', s."ContentHash",
                        'newArtifactIdentity', replace(replace(s."ArtifactRevision", 'HLRTP-', 'HLRTC-'), 'LLRTP-', 'LLRTC-'),
                        'newContentHash', NULL, 'reason', 'Controlled output must be regenerated and re-signed by a human; no signature was fabricated.')::text,
                    '', now()
                FROM "electronic_signatures" s
                JOIN "controlled_document_artifacts" artifact
                  ON s."ArtifactType" = 'ControlledDocumentArtifact' AND artifact."Id" = s."ArtifactId"
                JOIN "controlled_documents" document ON document."Id" = artifact."DocumentId"
                WHERE document."Type" IN ('HighLevelTestCases', 'LowLevelTestCases')
                UNION ALL
                SELECT gen_random_uuid(), 'VerificationIdentityMigration.SignatureSuperseded', 'aerolink-migration',
                    'ElectronicSignature:' || s."Id"::text, 'Superseded',
                    json_build_object('migration', marker, 'oldArtifactIdentity', s."ArtifactRevision",
                        'oldSignatureId', s."Id", 'oldSignatureHash', s."ContentHash",
                        'newArtifactIdentity', replace(replace(s."ArtifactRevision", 'HLRTP-', 'HLRTC-'), 'LLRTP-', 'LLRTC-'),
                        'newContentHash', NULL, 'reason', 'Controlled output must be regenerated and re-signed by a human; no signature was fabricated.')::text,
                    '', now()
                FROM "electronic_signatures" s
                JOIN "controlled_documents" document
                  ON s."ArtifactType" = 'ControlledDocument' AND document."Id" = s."ArtifactId"
                WHERE document."Type" IN ('HighLevelTestCases', 'LowLevelTestCases');

                INSERT INTO "security_audit_events" ("Id", "EventType", "ActorId", "Target", "Outcome", "Detail", "IpAddress", "OccurredAt")
                    VALUES (gen_random_uuid(), marker || '.Pending', 'aerolink-migration', 'software-verification-identities', 'Pending',
                        json_build_object('version', marker, 'highWatermark', high_watermark, 'lowWatermark', low_watermark,
                            'systemPrefixPreserved', true, 'forwardOnly', true,
                            'signaturePolicy', 'Original human signatures remain unchanged; startup must regenerate stored bytes and record real replacement hashes before completion.')::text,
                        '', now());
                DROP FUNCTION aerolink_rewrite_case_json(text);
                DROP FUNCTION aerolink_rewrite_case_jsonb(jsonb, text);
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The original signed bytes are not reconstructible from a renamed identity and a hash. A truthful
        // Down would invent bytes and invalidate the audit trail, so rollback is intentionally blocked.
        throw new NotSupportedException("Software Case identity migration is forward-only; restore a qualified backup to recover the pre-rename signed bytes.");
    }
}
