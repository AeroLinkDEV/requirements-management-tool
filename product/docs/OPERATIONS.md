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

Logs are under the installation's `logs` directory (see *Installation identity* below). The authoritative
database is `aerolink`; controlled evidence is under `%LOCALAPPDATA%\AeroLink\evidence`.

### Installation identity: source is not data

A checkout of this repository is **source**. The persistent AeroLink **installation** — the PostgreSQL
binaries and the `pgdata` cluster behind `127.0.0.1:54329/aerolink`, backups, restore working space,
dependency stamps and operator logs — is a separate thing that a checkout points at.

For an ordinary clone the two coincide: the installation is that checkout's `product/.local`, exactly as it
always was. A checkout may instead carry `product/.local/installation.json` naming another installation root,
and then it uses that one. `product/.local` is Git-ignored, so the pointer is never repository content and
cannot make a canonical source posture dirty.

This exists so HOME can have a second checkout without acquiring a second AeroLink. A pointer naming a path
that does not exist is refused rather than falling back, because the fallback is precisely the failure being
guarded against: a second, empty installation that starts perfectly and holds none of the operator's data.

`AEROLINK_INSTALLATION_ROOT` overrides both, and is for disposable qualification only.

Each installation may declare who it is, in `instance.json` under the installation root:

```json
{ "label": "HOME CANONICAL", "classification": "HomeCanonical" }
```

The launchers pass that to the API, which publishes it at `/health/identity`, and the client shows it beside
the wordmark on every screen. Canonical status is **declared, never inferred from the hostname**: an
installation that has declared nothing is labelled `LOCAL DEVELOPMENT` or `LOCAL PRODUCTION` and classified
`Undeclared`. Set it with `Set-AeroLinkInstanceConfig` from `product/scripts/AeroLinkInstallation.psm1`.

### Runtime identity: what /health/ready cannot tell you

`/health/ready` proves a process can reach a database. It says nothing about which source that process was
built from, and treating it as sufficient is how a healthy API from an older revision survived a repository
update in #816 while the client moved on.

`/health/identity` is the non-secret answer: source SHA, source identity, launcher mode, instance label and
classification, database **name** (never host, port, user or password), snapshot origin and age where one
exists, and the latest applied schema migration. It is anonymous, because a launcher has to be able to ask
who is listening before it holds a session.

A launcher reuses an existing process only when ownership, mode and exact source identity all agree and it is
ready. Otherwise it stops **only** a process it positively owns and starts the right one. A process AeroLink
does not own on an AeroLink port is a refusal naming the PID — never a casualty. For a dirty development tree
the identity folds in a bounded fingerprint of the changed and untracked files, so editing a file invalidates
a running process instead of a commit SHA pretending nothing changed.

### The three supported operating modes

| Mode | Entry point | Source | Database |
| --- | --- | --- | --- |
| HOME canonical / production | `START_AEROLINK_PRODUCTION.bat` on HOME | dedicated production checkout, clean canonical `main` | HOME canonical `aerolink` |
| Work-laptop local development | `START_AEROLINK.bat` on the laptop | that laptop's development checkout, any deliberate branch | that laptop's own `aerolink` |
| Protected remote demo | `START_AEROLINK_REMOTE_DEMO.bat` on HOME, or its recovery task | dedicated production checkout | HOME canonical `aerolink` |

A remote-demo browser session is a view of HOME. The work-laptop repository and work-laptop database are
irrelevant to it, and it never writes to them.

### Dedicated HOME production source

HOME production and the protected remote demo run from a **separate clone**, used by nothing else:

```text
C:\Sean Project\Requirements Management Tool   development: any branch, dirty WIP, agents, Playwright
C:\Sean Project\AeroLink Production            production only: clean source at an approved origin/main
```

On 2026-09-03 the HOME PC rebooted while the only checkout on the machine was mid-feature with dirty and
untracked work. Recovery ran, PostgreSQL came up, and the canonical-source guard correctly refused to
exercise the canonical database from it — so port 5080 never opened, ngrok was never started, and the demo
answered `ERR_NGROK_3200`. The guard was right; the architecture was wrong.

A clone rather than a worktree, deliberately: one repository cannot have `main` checked out in two worktrees,
and the development checkout must stay free to use `main`. The clone carries an installation pointer to the
canonical HOME installation, so **source is separated and data is not** — same cluster, same evidence, same
backups.

Set it up once per HOME machine:

```text
CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Preview
CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Install
CONFIGURE_AEROLINK_REMOTE_DEMO.bat Install
```

Configuration lives in `%LOCALAPPDATA%\AeroLink\Production\production-source.config.psd1` and holds no
secret. `Status` reports where the production source is and whether it is canonical; `Update` fast-forwards
it. A production source that has acquired a tracked modification, an untracked file, a local-only commit, a
feature branch, or divergence is reported and refused **exactly as found** — never stashed, reset, rebased or
cleaned. When GitHub is unreachable a previously verified clean cached `main` runs with an explicit "the
latest remote revision could not be verified" diagnostic.

Once a dedicated source is configured, **the production launchers refuse to run from any other checkout** —
`START_AEROLINK_PRODUCTION.bat`, `START_AEROLINK_SHARED.bat` and `START_AEROLINK_EMAIL_DEMO.bat` alike. The
canonical-main gate already refuses a dirty or feature-branch development checkout, so this is not about
unreviewed code; it is about which working tree the resulting long-lived process executes out of. A
development checkout that happens to be on clean `main` passes every gate, serves the demo, and is then one
`git checkout` away from having its assemblies and client bundle replaced underneath a running process. The
refusal names both paths and the command to run instead, and starts nothing. `-AllowNonDedicatedSource`
exists for qualifying the launcher itself and for a machine where the dedicated source is genuinely
unavailable. On a machine with no dedicated source configured — every work laptop, and any HOME machine set
up before this — nothing changes.

Recovery is registered against that source in both of the places the 2026-09-03 amendment named: the source
root it launches, and the `AeroLinkRemoteDemo.ps1` path the Scheduled Task executes. Registration refuses any
checkout not marked as dedicated production source, so it cannot be aimed back at the development checkout.

Two tasks are installed, both under the operator's own account, neither as SYSTEM (ngrok's configuration and
credentials are per-user), and neither carrying a secret:

* `AeroLinkRemoteDemoRecovery` — boot trigger with a one-minute delay, plus a logon trigger as a second
  chance. `MultipleInstancesPolicy=IgnoreNew` and an idempotent start mean both firing produces no duplicate
  API and no duplicate tunnel. The principal is S4U, so a reboot recovers without an interactive sign-in.

  **Run `CONFIGURE_AEROLINK_REMOTE_DEMO.bat Install` once from an elevated PowerShell.** Windows will not
  register a boot trigger or an S4U principal for a non-elevated caller — measured on the HOME machine,
  where both were refused with "Access is denied" while logon and time triggers under an interactive token
  registered fine. Without elevation the installer falls back to the logon-only shape, which recovers after
  you sign in and **not** after a reboot with nobody logged in; it prints that in as many words, and
  `UnattendedBootRecovery` in its result and `Configure Status` report which shape is installed. Everything
  else about the task is unchanged by the fallback, including its binding to the dedicated production source.
* `AeroLinkProductionSourceReconcile` — every 30 minutes, so a machine that never reboots does not run last
  week's `main`. It does nothing unless `origin/main` actually moved. When it has, it **inspects, stops,
  advances, then starts**: a fetch touches only remote-tracking refs, which nothing running reads, so the
  decision is safe with the demo up; the running production process is stopped before the working tree it is
  executing out of is rewritten; and the restart goes through the ordinary start path, which re-proves the
  protected endpoint. Advancing first and restarting afterwards would leave a live process serving the demo
  out of assemblies, migrations and a client bundle that had already been replaced on disk. If the advance is
  refused after the stop — `origin/main` moved again in between, say — production is still restarted on the
  revision that is on disk rather than left down. Polling rather than a webhook: an inbound public endpoint
  to learn about a merge would be a far larger security surface than the problem justifies.

`remote-demo-state.json` is advisory operational metadata, not process truth. A recorded PID from before a
reboot can never block a fresh start; live ownership, port, runtime identity and readiness decide.

If the production launcher exits with a terminal refusal, recovery reports that refusal within seconds and
quotes the reason the launcher gave. It no longer waits out the readiness timeout for a port that cannot open.

### Database upgrade posture, before the web server

Both launchers ask what this build would do to this database **before** building a client or starting an API:

```text
dotnet run --project product/src/AeroLink.Api -- maintenance analyze [--json]
dotnet run --project product/src/AeroLink.Api -- maintenance upgrade [--apply]
dotnet run --project product/src/AeroLink.Api -- maintenance resolve  ...
```

This is a mode of the application host, not a separate tool, so it uses the same domain, the same persistence
configuration and the same migration authorities that startup uses. `analyze` writes nothing. Exit codes are
the launcher's contract: `0` current, `10` deterministic upgrade required, `20` conflict, `30` unreachable.

When an upgrade is pending and deterministic, the launcher takes a verified backup, restores an **isolated
copy** through the supported `Restore-AeroLink.ps1` path, applies this build's upgrade to that copy, proves
the copy is then current, proves current AeroLink can actually serve it, and only then upgrades the real
database. The ordering is the safety property: a failure at any earlier step leaves the persistent database
and evidence untouched because nothing had reached them.

**Isolated means the evidence store too.** A maintenance run pointed at a clone by connection string alone
still resolved the live `Evidence:Root`, and one of the semantic authorities in this upgrade set rewrites
controlled renditions through it — so validating a clone wrote new objects into the canonical evidence tree,
where no database rollback can reach them. An isolated database target now **requires** an isolated evidence
root and is refused without one; the clone and the HOME snapshot staging copy each get their own tree under
`restore-validation/<database>/evidence`.

The post-upgrade proof runs in the **restore-validation read-only** boundary, not an ordinary web host.
That matters on a copy of production data: an ordinary host would migrate, seed, and start every hosted
worker — notification, managed-document integrity, enterprise job, webhook, Jira — so a copied outbox row
could send real mail or real webhook traffic because somebody asked whether an upgrade was safe. Read-only
mode removes every hosted service, performs no startup mutation, and refuses a database with pending
migrations. Within it the proof is: `/health/ready`, an **anonymous request refused**, a wrong token refused,
and byte-exact authenticated controlled reads. An anonymous `200` is the shape of a broken authentication
boundary and fails validation rather than counting as "authentication responding".

When the database presents a genuine ambiguity — the #816 legacy leadership backup that no longer holds the
position's required base role is the modelled case — every conflict in the database is reported at once, in
seconds, naming the program, the person, the role required and the role held, with the supported decisions
and which of them would grant authority somebody does not have today. AeroLink makes no such decision itself.

To act on one:

```text
dotnet run --project product/src/AeroLink.Api -- maintenance resolve ^
  --conflict project-leadership.legacy-backup-base-role-missing ^
  --choice retire-legacy-backup ^
  --program <guid> --position SoftwareEngineeringLead --person <guid> ^
  --legacy-backup <guid> --expect-primary <guid|none> ^
  --operator "Sean, issue #816" [--apply]
```

Dry run without `--apply`. Preconditions are re-read inside the transaction immediately before the write, so
a decision reviewed against state that has since moved cannot land. History is ended, never deleted, and a
decision that changes state writes a maintenance audit event under one formal attribution
(`aerolink-maintenance`). A **refusal writes nothing at all** — an audit row is a write, and "this changed
nothing" has to be literally true to be worth relying on.

A third category is reported but never applied automatically: **showcase content upgrade steps**. Unlike
schema migrations and semantic authorities these do not run at startup — the seeder returns early for a
database that already has the showcase program, because existing showcase content is operator-owned state a
restart must not rewrite. So a database seeded before a step shipped stays behind indefinitely, and analysis
used to answer `DATABASE CURRENT`: true about the schema, misleading about everything on screen. `analyze`
now names the outstanding steps and says plainly that nothing applies them on its own. They do not make the
database "upgrade required", so no start routes demo content through backup and clone validation; the
explicit showcase upgrade command, which takes a verified backup first, is how they are applied.

The maintenance and upgrade qualification runs against a **disposable PostgreSQL** in the `postgresql-smoke`
CI lane the merge gate waits on, with `AEROLINK_REQUIRE_POSTGRES_QUALIFICATION` set so a missing connection
fails the lane rather than skipping into a green tick. Locally, point `AEROLINK_MIGRATIONS_CONNECTION` at a
throwaway server; never at a persistent installation.

### Declaring which installation this is

An installation says what it is; nothing infers it from the machine name. The declaration lives in
`instance.json` under the installation root, the header badge renders it, and the destructive guards read it:
a `HomeCanonical` installation refuses a HOME-to-laptop snapshot import and refuses a development launch.

Two supported actions establish it on their own — `CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat Install` declares
`HOME CANONICAL`, and the first `REFRESH_AEROLINK_FROM_HOME.bat` import declares `WORK-LAPTOP LOCAL`, since
an installation that has just accepted a HOME snapshot over its database is not anything else. For every
other case, including a laptop that never imports a snapshot:

```text
DECLARE_AEROLINK_INSTANCE.bat
DECLARE_AEROLINK_INSTANCE.bat Preview WorkLaptopLocal
DECLARE_AEROLINK_INSTANCE.bat Declare WorkLaptopLocal
```

`Status` and `Preview` change nothing, on disk or anywhere else. Reclassifying an installation that already
declares something needs `-Force`, because it moves which destructive guards apply to real data. No database,
evidence, attachment or backup is touched by any of it.

Each installation also carries a stable `instanceId` — a bare GUID, minted once, describing nothing about the
machine or the network. It answers what a label cannot: two installations can both be labelled
`WORK-LAPTOP LOCAL`, and a restored snapshot carries the source's label with it. It is minted by the
launchers and by explicit setup, never by a read: `Preview` paths do not create it.

### Explicit HOME to work-laptop snapshot refresh

One-way, operator-initiated, and never part of startup. HOME being unreachable does not affect a work-laptop
launch, and there is no bidirectional synchronization of any kind.

Take a supported backup on HOME with `BACKUP_AEROLINK.bat`, carry the archive to the laptop by whatever means
already carries controlled data there, then:

```text
REFRESH_AEROLINK_FROM_HOME.bat "D:\transfer\aerolink-20260903-120000.zip"
REFRESH_AEROLINK_FROM_HOME.bat "D:\transfer\aerolink-20260903-120000.zip" Import REFRESH-FROM-HOME
```

The archive is verified, the laptop's current state is backed up first, the snapshot is restored to an
isolated staging copy, upgraded with this build and proven current — and only then activated through the
supported production restore, which keeps its own rollback contract. Snapshot origin and age are recorded in
`instance.json` and shown in the instance badge. Refused outright on an installation declared
`HomeCanonical`.

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

The root launchers are deliberate operator compatibility surfaces. There are **21 root `.bat` launchers and
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
| `CONFIGURE_AEROLINK_PRODUCTION_SOURCE.bat` | Dedicated HOME production source setup | `product/scripts/Configure-AeroLinkProductionSource.ps1` with forwarded action/arguments | This document; `docs/REMOTE_DEMO_OPERATOR.md` | Run once per HOME machine; the resulting configuration is per-user and outside source control. Keep stable root entry point. |
| `REFRESH_AEROLINK_FROM_HOME.bat` | Explicit one-way HOME to work-laptop snapshot refresh | `product/scripts/Import-AeroLinkHomeSnapshot.ps1` | This document | Work-laptop operator action only; never invoked by startup, and refused on a HOME canonical installation. Keep stable root entry point. |
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

### Webhook egress and redirect policy

- Webhook endpoints must use HTTPS. `Integrations__AllowInsecureWebhookTargets=true` may permit plain HTTP only in isolated development. No other URI scheme is accepted, with or without the development overrides.
- In production, every address a webhook endpoint resolves to must be globally routable. IPv6 destinations outside the global unicast range `2000::/3` (reserved or otherwise unassigned space) fail closed, and the prohibition set inside it is a static snapshot of the IANA IPv4 and IPv6 special-purpose registries: loopback, private, link-local (including the cloud metadata service), carrier-grade NAT, discard-only, dummy-prefix, benchmarking, documentation, ORCHID, SRv6, local-use NAT64 translation, unique-local and site-local IPv6, multicast, reserved ranges, deprecated 6to4 relay space, and the deprecated IPv4-mapped and IPv4-compatible IPv6 forms (refused regardless of the embedded IPv4 address, while the well-known NAT64 prefix `64:ff9b::/96` takes its reachability from the embedded IPv4 destination it translates) are refused, whether configured as a literal address or reached through a hostname. An endpoint whose hostname returns any prohibited answer — including a mix of permitted and prohibited answers — or no answers at all, is refused.
- For every delivery the complete answer set is resolved and classified before the connection is attempted, and the connection is then pinned to an address from that validated set (`SocketsHttpHandler.ConnectCallback`). DNS cannot change between validation and connection to steer the delivery to an unvalidated address. The endpoint hostname, TLS certificate verification, and SNI still use the original hostname, not the pinned address.
- Webhook connections are never reused between deliveries (zero pooled connection lifetime, HTTP/1.1 over TLS only). A connection established for one delivery can therefore never serve a later delivery whose freshly validated address set no longer contains that connection's peer address; every delivery establishes a fresh pinned connection.
- `Integrations__AllowPrivateWebhookTargets=true` (isolated development only) exempts addresses from the prohibition check, but the endpoint must still resolve to at least one connectable address, the connection remains pinned to a validated address, and connections are still never reused across deliveries. It does not relax the HTTPS rule.
- Automatic redirects are disabled. A `3xx` response is recorded as a failed delivery attempt; AeroLink never connects to a redirect target.

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

Reconciliation is ordered, keyed and idempotent, but it does **not** run automatically against an existing FMS
Program. Ordinary startup and `/api/showcase/seed` leave an existing showcase unchanged. Updating that synthetic
showcase is an explicit administrator operation: first create and verify a supported AeroLink backup, positively
identify the intended `FMSLIVE` Program and its 1.5/1.6 release pair, then use the two commands below. The upgrade
adds only missing owned deterministic scenarios and durable step markers; it does not reseed the database or
rewrite user-authored records.

**Read what has been applied, and whether the invariants hold:**

```bash
curl -s --cookie cookies.txt http://127.0.0.1:5080/api/showcase/upgrade-state
```

`steps` lists each reconciliation step with what it changed and when. `invariants` is checked independently of
those steps: `healthy` false means the database is wrong regardless of what the steps claim to have done, and
each entry names the count it expected against the count it found.

**Apply anything outstanding only after the backup and target checks above:**

```bash
curl -s --cookie cookies.txt -X POST http://127.0.0.1:5080/api/showcase/upgrade
```

Safe to run repeatedly and safe to run again after an interrupted attempt: all outstanding steps commit in one
transaction, and concurrent API processes are serialized so only one operation applies a given deterministic
scenario. A failed operation rolls back its changes; the next explicit request starts again from the durable step
set. Records created by users are never touched.

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
