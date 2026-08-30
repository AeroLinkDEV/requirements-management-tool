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

### Email delivery operability

Email remains an outbox delivery channel over the attributable in-product notification. It never authorizes,
approves, or signs a controlled record: a mail link opens AeroLink, where authentication and electronic-signature
confirmation still apply. Configure SMTP only through protected service environment/configuration, never source
control:

- `Notifications__Smtp__Host`, with optional `Port`, `From`, `UseStartTls`, `UserName`, and `Password`;
- `Notifications__BaseUrl` as an absolute HTTP(S) origin with no credentials, query, or fragment;
- `Notifications__UnsubscribeSecret` as a protected value of at least 32 characters.

Use `START_AEROLINK_EMAIL_DEMO.bat` for the local, non-production proof path. It starts pinned
`smtp4dev` 3.15.0 under `%LOCALAPPDATA%\AeroLink\smtp4dev\3.15.0`, configures only loopback SMTP on
port 2525, and starts AeroLink with the loopback link origin. Open `http://127.0.0.1:5000` to inspect the
captured message. `START_AEROLINK_SMTP4DEV.bat`, `AEROLINK_SMTP4DEV_STATUS.bat`, and
`STOP_AEROLINK_SMTP4DEV.bat` control only that owned catcher; they do not touch `product/.local`, PostgreSQL,
or controlled records. The first start installs the pinned free tool into the current user's LocalAppData and
requires ordinary NuGet/network access; no Docker, tunnel, or secret is required.

Global administrators inspect non-secret SMTP/link posture and the newest bounded delivery states in
**System Operations → Notifications**. It intentionally shows no credentials, message body, or unredacted
recipient address. The **Send my transport test** operation can deliver only to the signed-in administrator's
account through the already configured relay; it accepts no SMTP host, recipient, or message from the browser.
`Pending`, `Sent`, `Failed`, and `Suppressed` remain durable outbox evidence. A missing address, opt-out, or
superseded queued review obligation is `Suppressed` with a deliberate reason, not a fabricated send.

`START_AEROLINK_SHARED.bat` now derives its LAN origin before API startup so its mail links use that exact
`http://LAN:5080` address; it still never opens the Windows firewall and remains plaintext demo-only. Protected
remote demo passes its configured HTTPS `PublicUrl` into the production launcher. If a pre-existing local API
has an unknown mail-link origin, remote start refuses rather than sending loopback links to remote recipients.
Successful startup records the exact local API listener process and public origin outside the repository; status
and repeated recovery accept that proof only while the same process still owns the listener. Restarting the API
therefore invalidates the proof instead of letting an old tunnel state claim that new mail links are reachable.

### Root Windows launcher inventory

The root launchers are deliberate operator compatibility surfaces. There are **19 root `.bat` launchers and
zero root `.cmd` launchers**; the only `.cmd` implementation is `product/scripts/launch.cmd`. A launcher may be a
very small wrapper, but its exact path can be held by a
Task Scheduler task, desktop shortcut, remote-demo recovery configuration, or another Windows machine that Git
cannot discover. Moving one for cosmetic root cleanup has no meaningful benefit and is not safe without a
separately proven transition plan.

| Root entry point | Purpose / classification | Implementation and delegation | Repository callers and documentation | External-path risk and final disposition |
| --- | --- | --- | --- | --- |
| `AEROLINK_DIAGNOSTICS.bat` | Diagnostics | `product/scripts/Get-AeroLinkDiagnostics.ps1` | `README.md`; this document; historical delivery report | Operator shortcut/task risk is not enumerable from Git; keep stable root entry point. |
| `AEROLINK_SMTP4DEV_STATUS.bat` | Local email-catcher status | `product/scripts/AeroLinkSmtp4dev.ps1 -Action Status` | This document | Local operator shortcut risk; status never exposes mail contents. |
| `AEROLINK_REMOTE_DEMO_STATUS.bat` | Protected remote-demo status | `product/scripts/AeroLinkRemoteDemo.ps1 -Action Status` | `docs/REMOTE_DEMO_OPERATOR.md` | Remote operator shortcut/recovery risk; keep stable root entry point. |
| `BACKUP_AEROLINK.bat` | Backup/recovery | `product/scripts/Backup-AeroLink.ps1` | `README.md`, this document, Managed Documentation; scheduled runner calls the PowerShell script directly | Backup automation and operator shortcuts may retain the path; keep stable root entry point. |
| `CONFIGURE_AEROLINK_REMOTE_DEMO.bat` | Protected remote-demo scheduled recovery setup | `product/scripts/AeroLinkRemoteDemo.ps1 -Action Configure` with forwarded action/arguments | `docs/REMOTE_DEMO_OPERATOR.md`; remote-demo module error guidance | Recovery setup instructions may be copied to another host; keep stable root entry point. |
| `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat` | Connector/install/setup | `product/scripts/Install-AeroLinkDocumentConnector.ps1` | This document; `MANAGED_DOCUMENTATION_CENTER.md` | Per-user installation instructions and shortcuts may retain the path; keep stable root entry point. |
| `RESTORE_AEROLINK.bat` | Backup/recovery and isolated restore validation | `product/scripts/Restore-AeroLink.ps1 -BackupArchive ... -TargetDatabase ...` | This document; usage text; historical delivery report | Recovery runbooks and desktop shortcuts may retain the path; keep stable root entry point. |
| `SCHEDULE_AEROLINK_BACKUP.bat` | Backup scheduler configuration | `product/scripts/Configure-AeroLinkBackupSchedule.ps1` | This document; historical handoffs | The installed task invokes `Invoke-AeroLinkScheduledBackup.ps1` directly, but operator setup paths remain externally visible; keep stable root entry point. |
| `START_AEROLINK_PRODUCTION.bat` | Production-style local/demo run | Sets `AEROLINK_SCRIPT=Start-AeroLinkProduction.ps1` and calls `product/scripts/launch.cmd` | `README.md`, this document, product/client README, showcase brief, DEC-052 | Desktop shortcuts and demonstration machines can target the exact path; keep stable root entry point. |
| `START_AEROLINK_EMAIL_DEMO.bat` | Local smtp4dev email proof | Starts the owned local catcher then calls `Start-AeroLinkProduction.ps1` with loopback mail settings | This document | Demonstration shortcut risk; no production credentials or external exposure. |
| `START_AEROLINK_SMTP4DEV.bat` | Start local email catcher | `product/scripts/AeroLinkSmtp4dev.ps1 -Action Start` | This document | Local operator shortcut risk; catcher state remains outside the repository. |
| `START_AEROLINK_REMOTE_DEMO.bat` | Protected remote demo start | `product/scripts/AeroLinkRemoteDemo.ps1 -Action Start` | `README.md`; `docs/REMOTE_DEMO_OPERATOR.md` | Remote-demo operator shortcuts can target the exact path; recovery task calls the PowerShell implementation directly; keep stable root entry point. |
| `START_AEROLINK_SHARED.bat` | Opt-in trusted-LAN demo | Sets `AEROLINK_SCRIPT=Start-AeroLinkProduction.ps1`, adds `-Shared`, and calls `product/scripts/launch.cmd` | `README.md`, product README, DEC-053, production launcher guidance | Shared-demo hosts and shortcuts may retain the path; keep stable root entry point. |
| `START_AEROLINK.bat` | Day-to-day development | Sets `AEROLINK_SCRIPT=Start-AeroLink.ps1` and calls `product/scripts/launch.cmd` | `README.md`, this document, product/client README, smoke test and API guidance | The dated #783 host audit observed a desktop shortcut targeting this exact path; keep stable root entry point. |
| `STOP_AEROLINK_REMOTE_DEMO.bat` | Protected remote-demo stop | `product/scripts/AeroLinkRemoteDemo.ps1 -Action Stop -IncludeLocalStack` | `docs/REMOTE_DEMO_OPERATOR.md` | Recovery runbooks and operator shortcuts may retain the path; keep stable root entry point. |
| `STOP_AEROLINK.bat` | Controlled local stop | `product/scripts/Stop-AeroLink.ps1` | `README.md`, this document, product README | Desktop/operator shutdown shortcuts may retain the path; keep stable root entry point. |
| `STOP_AEROLINK_SMTP4DEV.bat` | Stop owned local email catcher | `product/scripts/AeroLinkSmtp4dev.ps1 -Action Stop` | This document | Stops only the pinned LocalAppData process and retains captured messages. |
| `TEST_AEROLINK_CHANGED.bat` | Developer/testing changed-area planner | `product/scripts/Get-AeroLinkTestPlan.ps1` | `product/test-planner/README.md`; planner contract | Developer shortcuts and team runbooks may retain the path; keep stable root entry point. |
| `VERIFY_AEROLINK_BACKUP.bat` | Backup verification | `product/scripts/Verify-AeroLinkBackup.ps1` | This document; historical delivery report | Scheduled runner calls the PowerShell implementation directly, but recovery runbooks may retain this wrapper; keep stable root entry point. |

The email-demo launcher first invokes the pinned local catcher; otherwise the start wrappers delegate to
`product/scripts/launch.cmd` or their named PowerShell runner. The backup scheduler and remote-demo recovery tasks call their PowerShell runners
directly. In the dated 2026-08-26 #783 read-only host audit, a desktop shortcut was observed targeting
`START_AEROLINK.bat`; the scheduled tasks pointed directly to their deeper PowerShell runners. These observations
confirm compatibility risk but do not expose credentials or claim to cover other machines.

## Microsoft Word desktop connector

Run `INSTALL_AEROLINK_DOCUMENT_CONNECTOR.bat` once for each Windows user who edits managed documents. It
publishes the small connector under `%LOCALAPPDATA%\AeroLink\DocumentConnector` and registers the `aerolink://`
protocol in `HKCU`, so administrator rights are not required. Microsoft Word is required for editing and final
PDF conversion. Re-run the installer after upgrading AeroLink to update the connector.

Installation does not trust a server implicitly. In Documentation Center, select **Download connector trust**
while authenticated to the intended Project. Verify the exact origin, stable deployment ID, and SHA-256
public-key fingerprint through the organization's deployment channel, then enroll the downloaded file:

```powershell
product\.local\document-connector\AeroLink.DocumentConnector.exe --enroll C:\approved\aerolink-deployment-trust.json
```

Enrollment is per Windows user and is recorded in
`%LOCALAPPDATA%\AeroLink\DocumentConnector\trust\trust-audit.log`. Enrolling a new key for the same deployment
retires its prior active key. Revoke a compromised or retired key explicitly with
`AeroLink.DocumentConnector.exe --revoke <deployment-id> <key-id>`. HTTP is refused except when the manifest
explicitly identifies an exact loopback development origin. Production deployments must set a stable
`Connector__DeploymentId`, set the externally reachable exact `Connector__PublicOrigin`, and protect the ECDSA
P-256 private key named by `Connector__SigningKeyPath`; rotate
the key by changing that protected file and enrolling the newly issued manifest before retiring the old key.

Every custom-protocol launch contains only a five-minute signed envelope. The connector verifies the enrolled
deployment/key and exact origin before network access, refuses redirects, and checks that redemption cannot
change the Project, document, formal revision, mode, source attachment, size, or SHA-256. It streams to a new
deployment/Project/document/revision/grant-specific workspace, verifies length, hash, and the shared safe OOXML
profile, and applies the Windows intranet attachment zone before Word opens. Reusable browser credentials are
never sent to or stored by the connector.

If **Open in Word** does nothing, confirm the connector is installed for the signed-in Windows account and that
the browser is allowed to open the AeroLink protocol. An abandoned checkout expires automatically; a
configuration manager or administrator may force-unlock it with a recorded reason. Connector working files are
local conveniences, not authoritative storage; a completed check-in must appear in AeroLink's Versions tab.

If Word, Windows, or the network fails during editing, run the connector executable without arguments to open
**Recover local document work**. Select the retained workspace and authenticate in AeroLink. Resume and discard
are browser-authorized operations; the connector stores no reusable browser credential. Resume is allowed only
when the original user (or an authorized administrator) still has Project/document authority and the exact base
attachment remains current. Source conflicts, advanced revisions, authority loss, or another active checkout are
preserved for export and never uploaded automatically.

Do not delete `%LOCALAPPDATA%\AeroLink\DocumentConnector\working` during repair, upgrade, or uninstall. It can
contain unresolved local work. A signed cleanup command removes a successfully committed workspace only after
the connector independently matches AeroLink's accepted hashes and confirms Word is closed. Explicit discard
also refuses to remove an open or unsaved Word document. Conflict retention is marked for at least 90 days;
expired, abandoned, and force-unlocked work is marked for at least 30 days. These dates are operator review
targets, not unattended deletion jobs. Export retained work before any approved manual cleanup.

After an upgrade that changes the versioned safe OOXML profile, run the Documentation Center Project integrity
scan before controlled use. Existing DOCX attachment rows deliberately retain null validation-profile evidence;
the scan verifies their immutable bytes against the current profile without rewriting historical acceptance.
Any profile failure opens a deduplicated critical integrity incident and blocks download. Recover only the exact
recorded bytes from independent backup; if those exact bytes remain outside the current profile, retain them as
historical evidence and create a separately authorized safe successor rather than modifying the old attachment.

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

`Restore-AeroLink.ps1` restores into a shadow database and incoming evidence directory first. It requires the
signed attachment inventory to match the restored database, verifies every object, and starts a loopback-only,
token-authenticated read-only AeroLink process against that exact database/root. Every retained managed-document
attachment is downloaded through its production API route and independently size/hash checked. Production is
activated only after this passes: the current database and evidence root are renamed as a retained rollback pair,
the verified shadow pair is moved into place, and download/inventory checks run again before AeroLink restarts.
Any failure through activation or restart stops the service and restores the prior database/evidence pair. The
retained pair is deliberately not deleted automatically; an operator dispositions it only after the restored
deployment completes its site acceptance period.

For an isolated drill, use a database name containing `restore`, `validation`, or `test`; its evidence target must
remain under `product\.local\restore-validation`. `AeroLinkRestoreQualification.Tests.ps1` exercises the
destructive activation/fault matrix only on disposable PostgreSQL and refuses persistent port 54329.

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
