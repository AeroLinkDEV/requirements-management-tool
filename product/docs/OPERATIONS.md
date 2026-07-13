# AeroLink Operations and Recovery

## Service lifecycle

From the repository root, double-click `START_AEROLINK.bat`, or run it from PowerShell. It starts PostgreSQL on `127.0.0.1:54329`, applies pending Entity Framework migrations through the API, starts the API on `127.0.0.1:5080`, and starts the React client on `127.0.0.1:5173`.

Use `STOP_AEROLINK.bat` for a controlled stop. The script only stops listeners whose command line resolves to this repository, then stops the repository-owned PostgreSQL instance. Use `AEROLINK_DIAGNOSTICS.bat` to check PostgreSQL, API health, a real local sign-in, client response, applied migrations, disk space, backup age, and evidence storage.

Logs are under `product/.local/logs`. The authoritative database is `aerolink`; controlled evidence is under `%LOCALAPPDATA%\AeroLink\evidence`.

## Local credentials warning

The deterministic demonstration password is `AeroLink!2026`. Useful users include `admin`, `systems.author`, `software.author`, `systems.reviewer`, and `release.manager`. These credentials are for the disconnected local development installation only. Replace them, remove credential prefill, configure TLS, and establish organizational identity and privileged-access policy before operational use.

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
6. Run diagnostics, sign in, inspect FMS 1.5 and 1.6, and reconcile any external records created after the restored point.

Never use production restore as an automated test. Keep the pre-restore archive until recovery is formally accepted.

## Verification commands

```powershell
dotnet test product\AeroLink.slnx --configuration Release
Set-Location product\client
npm.cmd run lint
npm.cmd run build
npm.cmd run test:e2e
```

Playwright starts its own API on port 5082 with a disposable SQLite database and does not reuse the live API.
