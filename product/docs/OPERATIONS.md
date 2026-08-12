# AeroLink Operations and Recovery

## Service lifecycle

There are two launchers, and which one to use is not a matter of taste.

`START_AEROLINK_PRODUCTION.bat` builds the client and serves it **from the API** on a single origin at
`http://127.0.0.1:5080` — one process, one port, no CORS policy joining two servers. This is the shape an
on-premises install has, and the only path that runs the built client. Use it for any demonstration, and for
checking what a deployment will actually behave like. It waits on `/health/ready` and then confirms the
document it serves references a built bundle, so it cannot report success over a database it cannot reach or a
client it is not serving. `-SkipClientBuild` reuses the existing build when nothing in the client changed.

`START_AEROLINK.bat` is the development launcher. It starts PostgreSQL on `127.0.0.1:54329`, applies pending
Entity Framework migrations through the API, starts the API on `127.0.0.1:5080`, and starts the Vite **dev**
server on `127.0.0.1:5173`. The dev server recompiles on save, which is what makes it right for development and
wrong for anything anybody else is watching.

Both are safe to run again while AeroLink is already running.

Use `STOP_AEROLINK.bat` for a controlled stop. The script only stops listeners whose command line resolves to this repository, then stops the repository-owned PostgreSQL instance. Use `AEROLINK_DIAGNOSTICS.bat` to check PostgreSQL, API health, a real local sign-in, client response, applied migrations, disk space, backup age, and evidence storage.

Logs are under `product/.local/logs`. The authoritative database is `aerolink`; controlled evidence is under `%LOCALAPPDATA%\AeroLink\evidence`.

## Microsoft Word desktop connector

Run `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat` once for each Windows user who edits managed documents. It
publishes the small connector under `%LOCALAPPDATA%\AeroLink\DocumentConnector` and registers the `aerolink://`
protocol in `HKCU`, so administrator rights are not required. Microsoft Word is required for editing and final
PDF conversion. Re-run the installer after upgrading AeroLink to update the connector.

If **Open in Word** does nothing, confirm the connector is installed for the signed-in Windows account and that
the browser is allowed to open the AeroLink protocol. An abandoned checkout expires automatically; a
configuration manager or administrator may force-unlock it with a recorded reason. Connector working files are
local conveniences, not authoritative storage; a completed check-in must appear in AeroLink's Versions tab.

Liveness is available at `/health/live`. Deployment orchestrators should use `/health/ready`, which returns `503` until the database can be reached.

## Requirement proposal integrity reconciliation

After upgrading, create an Enterprise integrity checkpoint from **Enterprise Control**. A checkpoint in
`Attention` reports failed enterprise jobs or unresolved merge conflicts; `Failed` reports missing, altered or
unreadable controlled attachment content. Author impact dispositions are no longer an integrity invariant
(DEC-071), so a checkpoint does not report or block on their absence. Historical disposition JSON remains
untouched.

An authenticated Program member can read
`GET /api/authoring/attribute-gaps?projectId=<project-guid>` to inventory proposals missing the standard
`criticality` or `owner` authored attributes. This report is deliberately read-only. For a Draft, follow its
returned `scr:<guid>` reference, check it out, enter the attributable values, and check it in. Never infer an
owner or rewrite an approved record. For approved history, create a controlled successor revision and record the
reconciliation rationale there.

## Review-step authority migration

Migration `20260728141151_PreserveReviewStepAuthority` adds the frozen authority field to approval steps. Existing
steps retain their stored approver account and name; because historical authority cannot be reconstructed
honestly, an empty legacy value is shown as `Authority unresolved`. New and restarted review cycles resolve and
store current Program authority at assignment time.

## Open Digital Thread configuration

Production permits no cross-origin browser callers unless each trusted origin is configured as `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1`, and so on. Never use a wildcard origin with credentialed browser sessions.

The service API is rooted at `/api/v1`. Machine credentials and webhook signing secrets are created in **Administration → Integration Center** and are displayed once. Store them in the calling system's secret manager; do not place them in source control, operator transcripts, or shared configuration files.

Relevant production settings are:

- `Integrations__ApiRateLimitPerMinute` — per-credential request budget; default `240`.
- `Integrations__DeliveryPollSeconds` — webhook dispatcher interval; default `3`.
- `Integrations__AllowInsecureWebhookTargets` — must remain `false` outside isolated development.
- `Integrations__AllowPrivateWebhookTargets` — must remain `false` unless a reviewed private-network integration requires it.

Webhook requests include `X-AeroLink-Event`, `X-AeroLink-Delivery`, `X-AeroLink-Timestamp`, and `X-AeroLink-Signature`. Consumers must reject stale timestamps and verify the `v1=<hex>` HMAC-SHA256 over `<timestamp>.<raw request body>` before parsing the payload. Multi-instance deployments must use a shared, protected ASP.NET Core Data Protection key ring so encrypted webhook secrets and browser mutation tokens remain valid across instances.

## Production first-install administrator

Production does not seed identities. Before the first API start against an empty database, set `Identity__BootstrapSecret` in the service environment to a randomly generated value of at least 32 characters. Do not place it in `appsettings.json`, source control, a command-line argument, or an operator transcript. `GET /api/setup/status` reports only whether bootstrap is required and enabled.

While the API is reachable only from the administrative network, open the website. An empty, bootstrap-enabled deployment automatically presents the one-time activation screen; enter the protected bootstrap secret and choose the administrator display name, email, and password. The administrator username is always `admin`. The password must contain at least 14 characters with uppercase, lowercase, numeric, and symbol characters. The equivalent API procedure is retained below for headless installations.

```powershell
$secureBootstrapSecret = Read-Host 'Bootstrap secret from the protected service configuration' -AsSecureString
$bootstrapSecret = [System.Net.NetworkCredential]::new('', $secureBootstrapSecret).Password
$securePassword = Read-Host 'New AeroLink administrator password' -AsSecureString
$administratorPassword = [System.Net.NetworkCredential]::new('', $securePassword).Password
$headers = @{ 'X-AeroLink-Bootstrap-Secret' = $bootstrapSecret }
$body = @{ displayName = 'AeroLink Administrator'; email = 'admin@example.org'; password = $administratorPassword } | ConvertTo-Json
Invoke-RestMethod 'https://aerolink.example.org/api/setup/bootstrap' -Method Post -Headers $headers -ContentType 'application/json' -Body $body
Remove-Variable secureBootstrapSecret, bootstrapSecret, securePassword, administratorPassword, body
```

The operation uses a serializable zero-user transaction and permanently closes after the first account exists. Confirm `GET /api/setup/status` returns `bootstrapRequired: false`, remove `Identity__BootstrapSecret` from the service environment, restart the API, and sign in with the chosen password. If the database already contains any user, recovery requires the approved database/identity recovery procedure; bootstrap cannot overwrite or add an administrator.

Demo identity seeding fails closed outside the `Development` environment. `Identity__AllowDemoAccounts=true` is an explicit escape hatch only for an isolated, non-production showcase and must never be used for an operational deployment.

## Local credentials warning

The deterministic demonstration password is `AeroLink!2026`. Useful users include `admin`, `systems.author`, `software.author`, `systems.reviewer`, and `release.manager`. These credentials are for the disconnected local development installation only. Replace them, remove credential prefill, configure TLS, and establish organizational identity and privileged-access policy before operational use.

## Repairing an existing showcase database

A database seeded by an older build does not receive invariants added since. The seeder used to return early
whenever a showcase Program already existed, so an installation created before verification impact shipped
kept approved FMS 1.6 change requests alongside an empty impact queue — a state the product describes as
impossible, and one no amount of restarting would fix.

Reconciliation is now ordered, keyed and idempotent, and runs on every start. Two commands let an operator
see and force it. Both need an administrator session and neither reseeds or replaces anything.

**Read what has been applied, and whether the invariants hold:**

```bash
curl -s --cookie cookies.txt http://127.0.0.1:5080/api/showcase/upgrade-state
```

`steps` lists each reconciliation step with what it changed and when. `invariants` is checked independently of
those steps: `healthy` false means the database is wrong regardless of what the steps claim to have done, and
each entry names the count it expected against the count it found.

**Apply anything outstanding:**

```bash
curl -s --cookie cookies.txt -X POST http://127.0.0.1:5080/api/showcase/upgrade
```

Safe to run repeatedly and safe to run again after an interrupted attempt: a step records itself only after
its own work commits, so an upgrade resumes at the step it stopped on rather than repeating the ones that
already succeeded. Records created by users are never touched.

If `healthy` is still false afterwards, read the failing invariant's detail rather than reseeding — dropping
and recreating the database is documented below and discards everything anybody has authored locally.

## FMS-only local showcase cleanup

The Development seed creates only `Flight Management System Live Program` (`FMSLIVE`) with released version 1.5 and in-work version 1.6. Playwright uses a disposable SQLite database, so browser journeys do not add Programs to the live PostgreSQL selector.

If an older development database contains obsolete sample or browser-test Programs, first preview the dependency-aware cleanup from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Prune-LocalShowcasePrograms.ps1
```

After reviewing the exact keep/delete list, apply it with `-Apply`. The command validates the single FMS Program and its exact 1.5/1.6 release pair, creates and verifies a full pre-purge backup, detects cross-Program dependency overlap, then deletes the obsolete Program graphs in one transaction. The live `aerolink` database cannot bypass its backup.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Prune-LocalShowcasePrograms.ps1 -Apply
```

## Backup and verification

Run `BACKUP_AEROLINK.bat`. The output under `product/.local/backups` contains a PostgreSQL custom-format dump, the exact runtime-configured evidence root, runtime configuration, a database-derived attachment inventory, `manifest.json`, and a ZIP SHA-256 sidecar. `Evidence__Root` has environment precedence, then the active appsettings environment, then appsettings, then the LocalAppData default. Backup fails before publication if a referenced object is missing or does not match its size/SHA-256, if attachment metadata changes during capture, or if a pending/repair-required storage operation or partial candidate/released set exists. Retention defaults to 30 days.

Run `VERIFY_AEROLINK_BACKUP.bat <absolute-or-repository-backup-zip>`. Verification supports an intentionally relocated ZIP when its adjacent sidecar travels with it, checks the sidecar, rejects unsafe ZIP, manifest, and storage-key paths, verifies every declared file, and independently reconciles every attachment inventory row to the archived evidence size/hash. Orphan objects are reported separately and cannot substitute for a missing referenced object.

### Automatic daily backup

For the current single-workstation installation, run `SCHEDULE_AEROLINK_BACKUP.bat` once. It registers the
current Windows user to run the existing complete backup and verification flow at 02:00 local time each day.
The task also runs while the workstation is locked. If the computer is off or the user is signed out at the
scheduled time, **Start when available** runs it after that user next signs in. Overlapping runs are ignored.

The default schedule retains archives for 30 days. Configure a different time or retention without editing the
task by hand:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Configure-AeroLinkBackupSchedule.ps1 `
  -Action Install -DailyAt 03:30 -RetentionDays 45
```

Inspect or remove the schedule with the same command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Configure-AeroLinkBackupSchedule.ps1 -Action Status
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Configure-AeroLinkBackupSchedule.ps1 -Action Remove
```

`-Action Preview` validates and displays the exact executable, arguments, user, working directory, time and
retention without changing Task Scheduler. Each scheduled run appends to
`product/.local/logs/scheduled-backup.log`, and the task's last result remains visible through `-Action Status`.
The scheduler deliberately invokes `Backup-AeroLink.ps1` and then `Verify-AeroLinkBackup.ps1`; it does not own a
second backup implementation or create another database. A future sub-daily trigger can reuse the same runner.

This current-user convenience task is suitable for the local workstation. An operational deployment should run
the same script under a dedicated service identity whether anyone is signed in, retain task history, alert on
nonzero exit, and copy archives to separately protected storage. A 24-hour backup target does not replace an
organization-approved RPO/RTO.

## Isolated restore drill

Run `RESTORE_AEROLINK.bat <backup-zip> aerolink_restore_validation`. The target name must contain `restore`, `validation`, or `test`. The command verifies the archive, recreates only that isolated database, binds validation to evidence below `product/.local/restore-validation`, compares the restored database inventory with the signed archive inventory, rejects pending/partial lifecycle state, and independently verifies every restored object before reporting Program, attachment, object, and byte counts. It never selects `aerolink` by default. Production restore resolves the same runtime evidence root as the API, retains the prior evidence directory and a pre-restore backup, and does not restart AeroLink unless full database-to-filesystem validation succeeds.

Validate a pending migration on the restored copy with the design-time connection made explicit. Design-time
EF commands fail closed: `AEROLINK_MIGRATIONS_CONNECTION` must be set, and the factory never falls back to the
persistent `aerolink` database.

```powershell
$env:AEROLINK_MIGRATIONS_CONNECTION='Host=127.0.0.1;Port=54329;Database=aerolink_restore_validation;Username=postgres'
dotnet ef database update --project product\src\AeroLink.Infrastructure\AeroLink.Infrastructure.csproj --startup-project product\src\AeroLink.Api\AeroLink.Api.csproj --configuration Release
Remove-Item Env:\AEROLINK_MIGRATIONS_CONNECTION
```

`ConnectionStrings__AeroLink` is an application runtime setting and does not redirect `dotnet ef`. For
migration experimentation, prefer a genuinely disposable PostgreSQL cluster (for example a temporary
`initdb`/`pg_ctl` instance on a non-default port) rather than a database on the persistent server, and never
point design-time EF at `aerolink` itself. Ordinary AeroLink startup applies runtime migrations normally via
`Database.MigrateAsync()`; design-time EF is a separate, explicitly connected workflow.

## Attended production restore

1. Confirm the selected archive, its date, and its `.sha256` sidecar; copy both to `product/.local/backups` if necessary.
2. Run archive verification and an isolated restore drill first.
3. Record the incident/change authority and notify users of downtime.
4. Invoke `product\scripts\Restore-AeroLink.ps1` with `-TargetDatabase aerolink -AllowProductionRestore -Confirmation RESTORE-AEROLINK`.
5. The script creates a new pre-restore safety backup, stops repository services, restores the database and evidence, and restarts AeroLink.
6. Run credentialless diagnostics, sign in separately, inspect FMS 1.5 and 1.6, and reconcile any external records created after the restored point.

Never use production restore as an automated test. Keep the pre-restore archive until recovery is formally accepted.

## Safe diagnostics

`product\scripts\Get-AeroLinkDiagnostics.ps1` checks API liveness, API/database readiness, the client, direct
database connectivity, migration posture, backup recency, disk capacity, and evidence storage. The standard
mode is credentialless and never creates a browser session:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Get-AeroLinkDiagnostics.ps1
```

Deployment endpoints and storage locations are parameters, so the same script can target a local workstation,
shared demonstration host, or production topology without relying on development ports. Use `Get-Help
product\scripts\Get-AeroLinkDiagnostics.ps1 -Detailed` to inspect them.

An optional authenticated probe targets only the service API health route. It requires a service identity with
the `integrations:read` scope and reads its API key as a `SecureString`; it never logs in as a person, creates a
browser session, or writes the key to output:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File product\scripts\Get-AeroLinkDiagnostics.ps1 `
  -ApiBaseUri https://aerolink.example.test -ClientUri https://aerolink.example.test `
  -AuthenticatedProbe
```

The script prompts for the key as a secure value. An automation host that already owns a `SecureString` may
invoke the script in-process with `-ServiceApiKey`; never put the plain key in a command-line argument.

The authenticated probe proves only that a least-privilege service credential is accepted. It is not evidence
that interactive login, MFA, delegated authority, or every governed workflow is operational.

## Verification commands

```powershell
dotnet test product\AeroLink.slnx --configuration Release
Set-Location product\client
npm.cmd run lint
npm.cmd run build
npm.cmd run test:production
npm.cmd run test:e2e
```

Playwright starts disposable APIs and SQLite databases and does not reuse the live API. `test:production`
serves the compiled client and API on one origin, performs protected writes, and verifies their durable server
state; `test:e2e` exercises the wider development-client journey matrix.

### Attended workflow qualification

Use the persistent `aerolink` database only when the created records are intentional engineering demonstration
records that will remain visible after the qualification. A disposable probe, temporary baseline, destructive
boundary case, or record whose only purpose is to prove an endpoint belongs in the isolated Playwright/API
environment on port 5082, never the live development API on port 5080.

Before an attended live workflow creates or changes controlled configuration, create and verify a backup and
name every artifact as real engineering work. Do not use `test`, `probe`, or throwaway identifiers in FMSLIVE.
If the scenario cannot safely leave every created record in the programme, stop and move it to the disposable
environment. This is an operator guard for exploratory work; the automated suites already enforce isolation.

## Controlled numbering: scope and gaps

Controlled numbers are issued from `identifier_sequences`, one row per prefix, by a single atomic increment.
Two things follow that operators should expect rather than investigate:

- **Numbering is repository-wide per prefix, not per Program or Project.** `SYSR-000123` is unique across the
  whole database; two Projects share one `SYSR` run. This is what the unique indexes on the base numbers have
  always enforced.
- **Gaps are normal.** A number is spent the moment it is handed out, so a create that is abandoned or fails
  after allocation consumes its number permanently. Nothing in the product infers meaning from contiguity, and
  reissuing a number that a failed attempt may already have displayed or exported would be worse than a gap.
  A missing number is not evidence of a deleted record.

On first use after upgrading, each prefix seeds its sequence from the highest identifier already recorded, so
an existing database continues its numbering rather than restarting it. No operator action is required.
