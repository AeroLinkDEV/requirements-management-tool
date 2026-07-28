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

Liveness is available at `/health/live`. Deployment orchestrators should use `/health/ready`, which returns `503` until the database can be reached.

## Requirement proposal integrity reconciliation

After upgrading, create an Enterprise integrity checkpoint from **Enterprise Control**. A checkpoint in
`Attention` reports the count of legacy requirement proposals whose five impact dispositions are missing,
malformed, unknown, or still Pending. Those records cannot enter review, be selected into a candidate baseline,
freeze, or materialize until corrected.

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

Run `BACKUP_AEROLINK.bat`. The output under `product/.local/backups` contains a PostgreSQL custom-format dump, evidence, runtime configuration, `manifest.json`, and a ZIP SHA-256 sidecar. Retention defaults to 30 days.

Run `VERIFY_AEROLINK_BACKUP.bat <absolute-or-repository-backup-zip>`. Verification accepts only archives in `product/.local/backups`, checks the sidecar, rejects unsafe ZIP and manifest paths, and verifies every declared file's size and hash.

For Windows Task Scheduler, create a daily task under a dedicated service identity: program `powershell.exe`; arguments `-NoProfile -ExecutionPolicy Bypass -File "<repository>\product\scripts\Backup-AeroLink.ps1" -RetentionDays 30`; start in `<repository>`. Configure it to run whether the user is logged on or not, retain task history, alert on nonzero exit, and copy archives to separately protected storage. A 24-hour backup target does not replace an organization-approved RPO/RTO.

## Isolated restore drill

Run `RESTORE_AEROLINK.bat <backup-zip> aerolink_restore_validation`. The target name must contain `restore`, `validation`, or `test`. The command verifies the archive, recreates only that isolated database, restores evidence only below `product/.local/restore-validation`, and reports the restored Program count. It never selects `aerolink` by default.

Validate a pending migration on the restored copy with:

```powershell
$env:AEROLINK_MIGRATIONS_CONNECTION='Host=127.0.0.1;Port=54329;Database=aerolink_restore_validation;Username=postgres'
dotnet ef database update --project product\src\AeroLink.Infrastructure\AeroLink.Infrastructure.csproj --startup-project product\src\AeroLink.Api\AeroLink.Api.csproj --configuration Release
Remove-Item Env:\AEROLINK_MIGRATIONS_CONNECTION
```

The design-time migration factory uses `AEROLINK_MIGRATIONS_CONNECTION`; `ConnectionStrings__AeroLink` is an application runtime setting and does not redirect `dotnet ef`.

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
