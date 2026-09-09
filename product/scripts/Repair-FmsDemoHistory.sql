-- Operator-only #1006 correction. psql supplies apply_correction after backup verification.
-- Normal application connections never execute this maintenance exception.
BEGIN;
SET LOCAL lock_timeout = '10s';
-- Hold a consistent dependency manifest and unchanged-row proof. No concurrent writer can enter
-- the brief trigger exception; timeout refuses rather than waiting indefinitely on a live operator.
DO $locks$
DECLARE t record;
BEGIN
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename LOOP
    EXECUTE format('LOCK TABLE public.%I IN ACCESS EXCLUSIVE MODE', t.tablename);
  END LOOP;
END $locks$;

CREATE TEMP TABLE correction_scope (project_id uuid PRIMARY KEY, program_id uuid NOT NULL) ON COMMIT DROP;
INSERT INTO correction_scope SELECT pr."Id", p."Id" FROM programs p
JOIN projects pr ON pr."ProgramId"=p."Id" WHERE p."Code"='FMSLIVE';
DO $guard$
BEGIN
  IF (SELECT count(*) FROM correction_scope) <> 1 THEN
    RAISE EXCEPTION 'Exactly one FMSLIVE Project is required.';
  END IF;
  IF EXISTS (SELECT 1 FROM requirements r JOIN correction_scope s ON s.project_id=r."ProjectId"
             WHERE r."Level" IN ('Customer','Interface')) THEN
    RAISE EXCEPTION 'Unexpected Customer/Interface requirements require a separately reviewed dependency manifest.';
  END IF;
  IF EXISTS (SELECT 1 FROM system_change_requests c JOIN correction_scope s ON s.project_id=c."ProjectId"
      WHERE c."Type"='Interface' AND (c."State" <> 'Withdrawn' OR NOT EXISTS (
        SELECT 1 FROM showcase_upgrade_steps m WHERE m."ProgramId"=s.program_id
          AND m."StepKey" LIKE 'scenario-richness/interface/%' AND m."Detail"=c."Id"::text))) THEN
    RAISE EXCEPTION 'Unowned or active Interface work requires a separately reviewed correction.';
  END IF;
END $guard$;

CREATE TEMP TABLE correction_targets(table_name text, id uuid, PRIMARY KEY(table_name,id)) ON COMMIT DROP;
-- Derived lookup tables can have composite keys. Their full unchanged row supplies a transient
-- manifest fingerprint; this is never written as an application or controlled artifact identifier.
CREATE FUNCTION pg_temp.correction_row_id(value jsonb) RETURNS uuid LANGUAGE sql IMMUTABLE AS
$$ SELECT CASE WHEN value->>'Id' ~ '^[0-9a-fA-F-]{36}$' THEN (value->>'Id')::uuid ELSE md5(value::text)::uuid END $$;
INSERT INTO correction_targets SELECT 'system_change_requests', c."Id" FROM system_change_requests c
JOIN correction_scope s ON s.project_id=c."ProjectId" WHERE c."Type"='Interface';
INSERT INTO correction_targets SELECT 'showcase_upgrade_steps', m."Id" FROM showcase_upgrade_steps m
JOIN correction_scope s ON s.program_id=m."ProgramId"
WHERE m."StepKey" LIKE 'scenario-richness/interface/%';

DO $discover$
DECLARE relation record; prior_count bigint; next_count bigint;
BEGIN
  LOOP
    SELECT count(*) INTO prior_count FROM correction_targets;
    FOR relation IN
      SELECT tc.table_name AS child_table, kcu.column_name AS child_column, ccu.table_name AS parent_table
      FROM information_schema.table_constraints tc
      JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.constraint_schema=kcu.constraint_schema
      JOIN information_schema.referential_constraints rc ON tc.constraint_name=rc.constraint_name AND tc.constraint_schema=rc.constraint_schema
      JOIN information_schema.constraint_column_usage ccu ON rc.unique_constraint_name=ccu.constraint_name AND rc.unique_constraint_schema=ccu.constraint_schema
      WHERE tc.constraint_type='FOREIGN KEY' AND tc.table_schema='public'
    LOOP
      EXECUTE format('INSERT INTO correction_targets SELECT %L,pg_temp.correction_row_id(to_jsonb(c)) FROM public.%I c JOIN correction_targets p ON p.table_name=%L AND p.id=c.%I ON CONFLICT DO NOTHING',
        relation.child_table,relation.child_table,relation.parent_table,relation.child_column);
    END LOOP;
    -- Polymorphic artifact references have no database FK. Include their exact UUID, never text labels.
    FOR relation IN SELECT table_name,column_name FROM information_schema.columns
      WHERE table_schema='public' AND udt_name='uuid' AND column_name <> 'Id'
    LOOP
      EXECUTE format('INSERT INTO correction_targets SELECT %L,pg_temp.correction_row_id(to_jsonb(c)) FROM public.%I c JOIN correction_targets p ON p.id=c.%I ON CONFLICT DO NOTHING',
        relation.table_name,relation.table_name,relation.column_name);
    END LOOP;
    SELECT count(*) INTO next_count FROM correction_targets;
    EXIT WHEN prior_count=next_count;
  END LOOP;
  -- Fail closed at controlled boundaries which the audited eight synthetic CRs do not cross.
  IF EXISTS (SELECT 1 FROM correction_targets WHERE table_name IN
      ('requirements','requirement_revisions','baselines','baseline_requirements','candidate_baselines',
       'baseline_change_request_selections','test_change_reviews','controlled_attachments',
       'controlled_attachment_storage_operations','managed_document_links','certification_evidence_index')) THEN
    RAISE EXCEPTION 'The manifest reaches released material, external outputs or attachments; no cleanup is authorized by this exact correction.';
  END IF;
  IF EXISTS (SELECT 1 FROM system_change_requests c JOIN correction_targets t ON t.table_name='system_change_requests' AND t.id=c."Id"
    WHERE c."Type" <> 'Interface' OR c."ProjectId" NOT IN (SELECT project_id FROM correction_scope)) THEN
    RAISE EXCEPTION 'The manifest crosses the exact FMS Interface aggregate boundary.';
  END IF;
END $discover$;

CREATE TEMP TABLE correction_before(table_name text PRIMARY KEY, digest text) ON COMMIT DROP;
CREATE TEMP TABLE correction_manifest(table_name text, id uuid, row_value jsonb) ON COMMIT DROP;
CREATE TEMP TABLE correction_triggers(table_name text, trigger_name text, enabled "char") ON COMMIT DROP;
INSERT INTO correction_triggers SELECT c.relname,t.tgname,t.tgenabled FROM pg_trigger t
JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname='public' AND NOT t.tgisinternal AND c.relname IN (SELECT table_name FROM correction_targets);
DO $proof$
DECLARE t record; digest text; unclassified boolean;
BEGIN
  FOR t IN SELECT DISTINCT table_name FROM correction_targets LOOP
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name=t.table_name AND column_name='ProjectId') THEN
      EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I r JOIN correction_targets c ON c.table_name=%L AND c.id=pg_temp.correction_row_id(to_jsonb(r))
        WHERE r."ProjectId" IS NOT NULL AND r."ProjectId" NOT IN (SELECT project_id FROM correction_scope))',t.table_name,t.table_name) INTO unclassified;
      IF unclassified THEN RAISE EXCEPTION 'Cross-Project ownership in %; correction refused.',t.table_name; END IF;
    END IF;
    EXECUTE format('INSERT INTO correction_manifest SELECT %L,pg_temp.correction_row_id(to_jsonb(r)),to_jsonb(r) FROM public.%I r
      JOIN correction_targets c ON c.table_name=%L AND c.id=pg_temp.correction_row_id(to_jsonb(r))',t.table_name,t.table_name,t.table_name);
  END LOOP;
  FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY tablename LOOP
    EXECUTE format('SELECT EXISTS (SELECT 1 FROM public.%I r WHERE NOT EXISTS
      (SELECT 1 FROM correction_targets c WHERE c.table_name=%L AND c.id=pg_temp.correction_row_id(to_jsonb(r)))
      AND EXISTS (SELECT 1 FROM correction_targets root WHERE root.table_name=''system_change_requests''
        AND strpos(lower(to_jsonb(r)::text),root.id::text)>0))',t.tablename,t.tablename) INTO unclassified;
    IF unclassified THEN RAISE EXCEPTION 'Unclassified exact Interface reference in %; review the dependency manifest.',t.tablename; END IF;
    -- Also handles schema metadata tables whose primary key is not the usual UUID Id.
    EXECUTE format('SELECT md5(coalesce(string_agg(j::text,chr(10) ORDER BY j::text),'''')) FROM
      (SELECT to_jsonb(r) j FROM public.%I r WHERE NOT EXISTS (SELECT 1 FROM correction_targets c
       WHERE c.table_name=%L AND c.id=pg_temp.correction_row_id(to_jsonb(r)))) rows',t.tablename,t.tablename) INTO digest;
    INSERT INTO correction_before VALUES(t.tablename,digest);
  END LOOP;
END $proof$;
SELECT table_name,count(*) AS rows_in_exact_manifest FROM correction_targets GROUP BY table_name ORDER BY table_name;
SELECT 'TARGET_COUNT:' || count(*) FROM correction_targets;
SELECT c."Id",c."BaseNumber",c."Revision",c."State" FROM system_change_requests c
JOIN correction_targets t ON t.table_name='system_change_requests' AND t.id=c."Id" ORDER BY c."BaseNumber";
SELECT 'MANIFEST:' || md5(coalesce(string_agg(table_name||id::text||row_value::text,chr(10) ORDER BY table_name,id),'')) FROM correction_manifest;

\if :apply_correction
CREATE TEMP TABLE correction_expected(value text) ON COMMIT DROP;
INSERT INTO correction_expected VALUES (:'expected_manifest');
DO $apply$
DECLARE t record; prior_count bigint := -1; next_count bigint; digest text;
BEGIN
  SELECT md5(coalesce(string_agg(table_name||id::text||row_value::text,chr(10) ORDER BY table_name,id),'')) INTO digest FROM correction_manifest;
  IF digest IS DISTINCT FROM (SELECT value FROM correction_expected) THEN
    RAISE EXCEPTION 'The reviewed manifest changed after preview; correction refused.';
  END IF;
  -- Disable only named user triggers on affected tables while all writers are excluded. FK checks
  -- remain enabled, all original trigger modes are restored, and any failed assertion rolls back DDL too.
  FOR t IN SELECT * FROM correction_triggers WHERE enabled <> 'D' LOOP
    EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER %I',t.table_name,t.trigger_name);
  END LOOP;
  CREATE TEMP TABLE correction_pending AS SELECT * FROM correction_targets;
  LOOP
    SELECT count(*) INTO next_count FROM correction_pending;
    EXIT WHEN next_count=0;
    IF prior_count=next_count THEN RAISE EXCEPTION 'Dependency-safe correction made no progress; rollback required.'; END IF;
    prior_count := next_count;
    FOR t IN SELECT DISTINCT table_name FROM correction_pending ORDER BY table_name LOOP
      BEGIN
        EXECUTE format('DELETE FROM public.%I r USING correction_pending p WHERE p.table_name=%L AND pg_temp.correction_row_id(to_jsonb(r))=p.id',t.table_name,t.table_name);
        DELETE FROM correction_pending WHERE table_name=t.table_name;
      EXCEPTION WHEN foreign_key_violation THEN NULL;
      END;
    END LOOP;
  END LOOP;
  FOR t IN SELECT * FROM correction_triggers WHERE enabled <> 'D' LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE %s TRIGGER %I',t.table_name,
      CASE t.enabled WHEN 'A' THEN 'ALWAYS' WHEN 'R' THEN 'REPLICA' ELSE '' END,t.trigger_name);
  END LOOP;
  SET CONSTRAINTS ALL IMMEDIATE;
  FOR t IN SELECT * FROM correction_before ORDER BY table_name LOOP
    EXECUTE format('SELECT md5(coalesce(string_agg(j::text,chr(10) ORDER BY j::text),'''')) FROM
      (SELECT to_jsonb(r) j FROM public.%I r) rows',t.table_name) INTO digest;
    IF digest IS DISTINCT FROM t.digest THEN RAISE EXCEPTION 'Unaffected-row proof failed for %; rolling back.',t.table_name; END IF;
  END LOOP;
  IF EXISTS (SELECT 1 FROM correction_triggers expected JOIN pg_class c ON c.relname=expected.table_name
      JOIN pg_namespace n ON n.oid=c.relnamespace AND n.nspname='public'
      LEFT JOIN pg_trigger actual ON actual.tgrelid=c.oid AND actual.tgname=expected.trigger_name
      WHERE actual.tgenabled IS DISTINCT FROM expected.enabled) THEN
    RAISE EXCEPTION 'Trigger restoration proof failed; rolling back.';
  END IF;
END $apply$;
SELECT 'All rows outside the exact manifest and all trigger modes are unchanged.' AS acceptance;
COMMIT;
\else
ROLLBACK;
\endif
