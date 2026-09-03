[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$KeepProgramCode = 'FMSLIVE',
    [string]$Database = 'aerolink',
    [switch]$Apply,
    [switch]$SkipBackup,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$productRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'AeroLinkInstallation.psm1') -Force
$postgresBin = (Get-AeroLinkInstallationPaths -ProductRoot $productRoot).PostgresBin
$psql = Join-Path $postgresBin 'psql.exe'

if ($KeepProgramCode -notmatch '^[A-Za-z0-9 _-]{1,30}$') { throw 'The retained Program code is unsafe.' }
if ($Database -notmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') { throw 'The database name is unsafe.' }
if ($SkipBackup -and $Database -eq 'aerolink') { throw 'The live local database cannot skip its pre-purge backup.' }
if (-not (Test-Path -LiteralPath $psql)) { throw 'The bundled PostgreSQL client is unavailable. Run Setup-Postgres.ps1 first.' }

& (Join-Path $PSScriptRoot 'Start-Postgres.ps1')
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL is not available.' }

function Invoke-AeroLinkSql {
    param([Parameter(Mandatory)][string]$Sql, [switch]$TuplesOnly)
    $arguments = @('-h', '127.0.0.1', '-p', '54329', '-U', 'postgres', '-d', $Database, '-v', 'ON_ERROR_STOP=1')
    if ($TuplesOnly) { $arguments += @('-A', '-t') }
    $output = $Sql | & $psql @arguments
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL command failed for database '$Database'." }
    return $output
}

$escapedCode = $KeepProgramCode.Replace("'", "''").ToUpperInvariant()
$previewSql = @"
SELECT p."Code", p."Name", COUNT(DISTINCT pr."Id") AS projects,
       COUNT(DISTINCT r."Id") AS releases,
       CASE WHEN p."Code" = '$escapedCode' THEN 'KEEP' ELSE 'DELETE' END AS action
FROM programs p
LEFT JOIN projects pr ON pr."ProgramId" = p."Id"
LEFT JOIN software_releases r ON r."ProjectId" = pr."Id"
GROUP BY p."Id", p."Code", p."Name"
ORDER BY action DESC, p."Name";
"@

Write-Host "Program cleanup preview for database '$Database':" -ForegroundColor Cyan
Invoke-AeroLinkSql -Sql $previewSql | Out-Host
if (-not $Apply) {
    Write-Host 'Preview only. Re-run with -Apply after reviewing the list.' -ForegroundColor Yellow
    return
}

if (-not $SkipBackup) {
    if ($Database -ne 'aerolink') { throw '-SkipBackup is required for an isolated validation database.' }
    & (Join-Path $PSScriptRoot 'Backup-AeroLink.ps1') -RetentionDays 30
    if ($LASTEXITCODE -ne 0) { throw 'The pre-purge backup did not complete successfully.' }
}

if (-not $Force -and -not $PSCmdlet.ShouldProcess("database '$Database'", "Delete every Program except '$KeepProgramCode'")) { return }

$purgeSql = @"
BEGIN;
LOCK TABLE programs IN SHARE ROW EXCLUSIVE MODE;

DO `$guard`$
DECLARE
    keep_program_id uuid;
    keep_project_id uuid;
    release_count integer;
BEGIN
    SELECT p."Id" INTO keep_program_id FROM programs p WHERE upper(p."Code") = '$escapedCode';
    IF keep_program_id IS NULL THEN
        RAISE EXCEPTION 'Required Program $escapedCode does not exist.';
    END IF;
    IF (SELECT COUNT(*) FROM programs p WHERE upper(p."Code") = '$escapedCode') <> 1 THEN
        RAISE EXCEPTION 'Required Program $escapedCode is not unique.';
    END IF;
    SELECT pr."Id" INTO keep_project_id FROM projects pr WHERE pr."ProgramId" = keep_program_id;
    IF keep_project_id IS NULL OR (SELECT COUNT(*) FROM projects pr WHERE pr."ProgramId" = keep_program_id) <> 1 THEN
        RAISE EXCEPTION 'Required Program $escapedCode must contain exactly one Project.';
    END IF;
    SELECT COUNT(*) INTO release_count FROM software_releases r WHERE r."ProjectId" = keep_project_id;
    IF release_count <> 2
       OR NOT EXISTS (SELECT 1 FROM software_releases r WHERE r."ProjectId" = keep_project_id AND r."Version" = '1.5')
       OR NOT EXISTS (SELECT 1 FROM software_releases r WHERE r."ProjectId" = keep_project_id AND r."Version" = '1.6') THEN
        RAISE EXCEPTION 'Required Program $escapedCode must contain exactly releases 1.5 and 1.6.';
    END IF;
END
`$guard`$;

CREATE TEMP TABLE purge_programs (id uuid PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE purge_projects (id uuid PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE keep_programs (id uuid PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE keep_projects (id uuid PRIMARY KEY) ON COMMIT DROP;
CREATE TEMP TABLE purge_targets (table_name text NOT NULL, id uuid NOT NULL, PRIMARY KEY (table_name, id)) ON COMMIT DROP;
CREATE TEMP TABLE keep_targets (table_name text NOT NULL, id uuid NOT NULL, PRIMARY KEY (table_name, id)) ON COMMIT DROP;

INSERT INTO keep_programs SELECT p."Id" FROM programs p WHERE upper(p."Code") = '$escapedCode';
INSERT INTO purge_programs SELECT p."Id" FROM programs p WHERE upper(p."Code") <> '$escapedCode';
INSERT INTO keep_projects SELECT pr."Id" FROM projects pr JOIN keep_programs p ON p.id = pr."ProgramId";
INSERT INTO purge_projects SELECT pr."Id" FROM projects pr JOIN purge_programs p ON p.id = pr."ProgramId";
INSERT INTO keep_targets SELECT 'programs', id FROM keep_programs;
INSERT INTO keep_targets SELECT 'projects', id FROM keep_projects;
INSERT INTO purge_targets SELECT 'programs', id FROM purge_programs;
INSERT INTO purge_targets SELECT 'projects', id FROM purge_projects;

DO `$discover`$
DECLARE
    association record;
    foreign_key record;
    previous_count bigint;
    current_count bigint;
BEGIN
    FOR association IN
        SELECT c.table_name, c.column_name
        FROM information_schema.columns c
        WHERE c.table_schema = 'public' AND c.column_name IN ('ProgramId', 'ProjectId')
    LOOP
        IF association.column_name = 'ProgramId' THEN
            EXECUTE format('INSERT INTO purge_targets SELECT %L, t."Id" FROM %I t JOIN purge_programs p ON p.id=t.%I ON CONFLICT DO NOTHING', association.table_name, association.table_name, association.column_name);
            EXECUTE format('INSERT INTO keep_targets SELECT %L, t."Id" FROM %I t JOIN keep_programs p ON p.id=t.%I ON CONFLICT DO NOTHING', association.table_name, association.table_name, association.column_name);
        ELSE
            EXECUTE format('INSERT INTO purge_targets SELECT %L, t."Id" FROM %I t JOIN purge_projects p ON p.id=t.%I ON CONFLICT DO NOTHING', association.table_name, association.table_name, association.column_name);
            EXECUTE format('INSERT INTO keep_targets SELECT %L, t."Id" FROM %I t JOIN keep_projects p ON p.id=t.%I ON CONFLICT DO NOTHING', association.table_name, association.table_name, association.column_name);
        END IF;
    END LOOP;

    LOOP
        SELECT (SELECT COUNT(*) FROM purge_targets) + (SELECT COUNT(*) FROM keep_targets) INTO previous_count;
        FOR foreign_key IN
            SELECT tc.table_name AS child_table, kcu.column_name AS child_column, ccu.table_name AS parent_table
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.constraint_schema=kcu.constraint_schema
            JOIN information_schema.referential_constraints rc ON tc.constraint_name=rc.constraint_name AND tc.constraint_schema=rc.constraint_schema
            JOIN information_schema.constraint_column_usage ccu ON rc.unique_constraint_name=ccu.constraint_name AND rc.unique_constraint_schema=ccu.constraint_schema
            WHERE tc.constraint_type='FOREIGN KEY' AND tc.table_schema='public'
        LOOP
            EXECUTE format('INSERT INTO purge_targets SELECT %L, c."Id" FROM %I c JOIN purge_targets p ON p.table_name=%L AND p.id=c.%I ON CONFLICT DO NOTHING', foreign_key.child_table, foreign_key.child_table, foreign_key.parent_table, foreign_key.child_column);
            EXECUTE format('INSERT INTO keep_targets SELECT %L, c."Id" FROM %I c JOIN keep_targets p ON p.table_name=%L AND p.id=c.%I ON CONFLICT DO NOTHING', foreign_key.child_table, foreign_key.child_table, foreign_key.parent_table, foreign_key.child_column);
        END LOOP;
        SELECT (SELECT COUNT(*) FROM purge_targets) + (SELECT COUNT(*) FROM keep_targets) INTO current_count;
        EXIT WHEN current_count = previous_count;
    END LOOP;

    IF EXISTS (SELECT 1 FROM purge_targets p JOIN keep_targets k ON k.table_name=p.table_name AND k.id=p.id) THEN
        RAISE EXCEPTION 'Cross-Program dependency overlap detected. No records were deleted.';
    END IF;
END
`$discover`$;

SELECT table_name, COUNT(*) AS rows_to_delete FROM purge_targets GROUP BY table_name ORDER BY table_name;

DO `$delete`$
DECLARE
    foreign_key record;
    target_table record;
    previous_count bigint := -1;
    current_count bigint;
BEGIN
    FOR foreign_key IN
        SELECT tc.table_name, kcu.column_name
        FROM information_schema.table_constraints tc
        JOIN information_schema.key_column_usage kcu ON tc.constraint_name=kcu.constraint_name AND tc.constraint_schema=kcu.constraint_schema
        JOIN information_schema.referential_constraints rc ON tc.constraint_name=rc.constraint_name AND tc.constraint_schema=rc.constraint_schema
        JOIN information_schema.constraint_column_usage ccu ON rc.unique_constraint_name=ccu.constraint_name AND rc.unique_constraint_schema=ccu.constraint_schema
        WHERE tc.constraint_type='FOREIGN KEY' AND tc.table_schema='public' AND tc.table_name=ccu.table_name
    LOOP
        EXECUTE format('UPDATE %I t SET %I=NULL FROM purge_targets child, purge_targets parent WHERE child.table_name=%L AND child.id=t."Id" AND parent.table_name=%L AND parent.id=t.%I', foreign_key.table_name, foreign_key.column_name, foreign_key.table_name, foreign_key.table_name, foreign_key.column_name);
    END LOOP;

    LOOP
        SELECT COUNT(*) INTO current_count FROM purge_targets;
        EXIT WHEN current_count = 0;
        IF current_count = previous_count THEN
            RAISE EXCEPTION 'Dependency-safe deletion could not make progress with % remaining records.', current_count;
        END IF;
        previous_count := current_count;
        FOR target_table IN SELECT DISTINCT table_name FROM purge_targets ORDER BY table_name
        LOOP
            BEGIN
                EXECUTE format('DELETE FROM %I t USING purge_targets p WHERE p.table_name=%L AND p.id=t."Id"', target_table.table_name, target_table.table_name);
                EXECUTE format('DELETE FROM purge_targets p WHERE p.table_name=%L AND NOT EXISTS (SELECT 1 FROM %I t WHERE t."Id"=p.id)', target_table.table_name, target_table.table_name);
            EXCEPTION WHEN integrity_constraint_violation THEN
                NULL;
            END;
        END LOOP;
    END LOOP;
END
`$delete`$;

DO `$verify`$
BEGIN
    IF (SELECT COUNT(*) FROM programs) <> 1 OR NOT EXISTS (SELECT 1 FROM programs p WHERE upper(p."Code")='$escapedCode') THEN
        RAISE EXCEPTION 'Post-purge Program verification failed.';
    END IF;
    IF (SELECT COUNT(*) FROM projects pr JOIN programs p ON p."Id"=pr."ProgramId" WHERE upper(p."Code")='$escapedCode') <> 1 THEN
        RAISE EXCEPTION 'Post-purge Project verification failed.';
    END IF;
    IF (SELECT COUNT(*) FROM software_releases r JOIN projects pr ON pr."Id"=r."ProjectId" JOIN programs p ON p."Id"=pr."ProgramId" WHERE upper(p."Code")='$escapedCode') <> 2 THEN
        RAISE EXCEPTION 'Post-purge release verification failed.';
    END IF;
END
`$verify`$;

COMMIT;

SELECT p."Code", p."Name", pr."Name" AS project,
       string_agg(r."Version", ', ' ORDER BY r."Version") AS releases
FROM programs p
JOIN projects pr ON pr."ProgramId"=p."Id"
JOIN software_releases r ON r."ProjectId"=pr."Id"
GROUP BY p."Id", p."Code", p."Name", pr."Name";
"@

Invoke-AeroLinkSql -Sql $purgeSql | Out-Host
Write-Host "Program cleanup completed. '$KeepProgramCode' is the only remaining Program." -ForegroundColor Green
