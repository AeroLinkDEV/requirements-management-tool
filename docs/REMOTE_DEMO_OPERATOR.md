# AeroLink Protected Remote Demo Operator Mode

This is a **local demonstration convenience**, not a production deployment. It
keeps the canonical AeroLink installation and PostgreSQL on the always-on Windows
host and uses an outbound ngrok HTTPS tunnel with an edge-level Basic Auth gate so
a separate machine can reach the real AeroLink login page without opening any
inbound firewall or router port.

## Security posture (requirements, not suggestions)

- AeroLink stays bound to `127.0.0.1:5080`; PostgreSQL stays bound to
  `127.0.0.1:54329`. Neither binds to `0.0.0.0`.
- Shared mode is never used for this mode.
- No Windows Firewall or router ports are opened.
- AeroLink `AllowedHosts` is never widened; the ngrok Traffic Policy rewrites the
  upstream `Host` header to `127.0.0.1`.
- The ngrok authtoken stays in ngrok's own user configuration
  (`%LOCALAPPDATA%\ngrok\ngrok.yml`). The Basic Auth password stays in the ngrok
  Vault (`aerolink-demo` / `basic-auth-password` by name). Neither value is ever
  written into the repository, operator configuration, process command lines,
  scheduled-task arguments, or logs.
- The per-user configuration file contains only non-secret values: ngrok
  executable path, public URL, Traffic Policy path, upstream URL, local API URL,
  AeroLink root, log/state paths, and optional Vault/secret NAMES.

## Operator commands (repository root)

| Command | What it does |
|---|---|
| `START_AEROLINK_REMOTE_DEMO.bat` | Starts local production AeroLink if needed, starts the policy-backed ngrok tunnel, proves the public endpoint returns 401, and prints `AEROLINK REMOTE DEMO READY`. Idempotent. |
| `AEROLINK_REMOTE_DEMO_STATUS.bat` | Read-only component status with a final `AEROLINK REMOTE DEMO READY` / `AEROLINK REMOTE DEMO NOT READY` verdict. |
| `STOP_AEROLINK_REMOTE_DEMO.bat` | Stops only the AeroLink-owned ngrok tunnel, then stops the local AeroLink stack and repository-owned PostgreSQL. Never deletes configuration, evidence, database content, or credentials. |
| `CONFIGURE_AEROLINK_REMOTE_DEMO.bat` | Scheduled-recovery management: `Preview`, `Install`, `Status`, or `Remove`. No argument defaults to `Preview`. |

The underlying implementation is `product\scripts\AeroLinkRemoteDemo.ps1`
(operator CLI) and `product\scripts\AeroLinkRemoteDemo.psm1` (tested core). It
reuses the existing `Start-AeroLinkProduction.ps1`, `Stop-AeroLink.ps1`,
`Get-AeroLinkDiagnostics.ps1`, and the repository-owned PostgreSQL runtime.

## Per-user configuration

Create `%LOCALAPPDATA%\AeroLink\RemoteDemo\remote-demo.config.psd1`. A sample
with placeholders is at `product\scripts\AeroLinkRemoteDemo.config.sample.psd1`.

```powershell
@{
    NgrokExecutable   = 'C:\Users\<you>\AppData\Local\Programs\ngrok\ngrok.exe'
    PublicUrl         = 'https://your-endpoint.ngrok-free.dev'
    TrafficPolicyPath = 'C:\Users\<you>\AppData\Local\ngrok\aerolink-demo\traffic-policy.yml'
    Upstream          = 'http://127.0.0.1:5080'
    LocalApiBaseUri   = 'http://127.0.0.1:5080'
    AeroLinkRoot      = 'C:\path\to\requirements-management-tool'
    VaultName           = 'aerolink-demo'
    BasicAuthSecretName = 'basic-auth-password'
}
```

Missing, malformed, unknown-key, or secret-looking configuration fails closed.

## Traffic Policy

The operator mode consumes an external user-owned policy file (or a non-secret
repository template). The supported policy requires Basic Auth backed by an ngrok
Vault reference, removes the outer `Authorization` header after authentication,
and rewrites the upstream `Host` to `127.0.0.1`:

```yaml
on_http_request:
  - actions:
      - type: basic-auth
        config:
          realm: "AeroLink Demo"
          credentials:
            - "aerolink-demo:${secrets.get('aerolink-demo', 'basic-auth-password')}"
          enforce: true
      - type: remove-headers
        config:
          headers:
            - Authorization
      - type: add-headers
        config:
          headers:
            host: "127.0.0.1"
```

The repository never creates or retrieves the Vault secret value.

## Start behavior (fail closed)

1. Confirm local `/health/ready` = 200 and `/` serves the built client; if not,
   invoke the supported production launcher first.
2. Verify the configured ngrok executable and Traffic Policy file exist.
3. Verify no unexpected ngrok process exists (ownership = exact executable plus
   public URL, upstream, and Traffic Policy in the command line).
4. Decide idempotently: already-owned-and-protected means READY with no new
   process; owned-but-unprotected or a foreign responder means NOT READY with no
   replacement.
5. Start the ngrok tunnel with the configured URL, upstream, and Traffic Policy.
6. Probe the public endpoint unauthenticated with
   `ngrok-skip-browser-warning: 1`; require `401`. Any 2xx, AeroLink 400, 404, or
   other responder tears down only the just-started tunnel and reports NOT READY.
7. Re-check local AeroLink readiness and record the exact owned PID/contract in
   `%LOCALAPPDATA%\AeroLink\RemoteDemo\state\remote-demo-state.json`.

## Status components

Status reuses the credentialless diagnostics (database listener, API liveness and
readiness, built client, applied migrations, backup recency) and adds:

- owned protected ngrok process (exact executable + URL + upstream + policy);
- public endpoint 401 protection probe;
- automatic-recovery task installation state.

`AEROLINK REMOTE DEMO READY` is printed only when every component is healthy.

## Automatic recovery (current-user Scheduled Task)

`CONFIGURE_AEROLINK_REMOTE_DEMO.bat Install` creates the current-user task
`AeroLinkRemoteDemoRecovery` (no administrator rights, no password):

- trigger: at user logon, with Start-When-Available enabled;
- action: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File
  <AeroLinkRoot>\product\scripts\AeroLinkRemoteDemo.ps1 -Action Start -Scheduled`;
- multiple instances policy `IgnoreNew`, so repeated logons/triggers cannot
  create duplicate AeroLink or ngrok processes;
- the task XML and its arguments contain no secrets.

`Status` inspects the task; `Remove` deletes it; `Preview` prints the exact XML
that would be installed without creating anything.

## Qualification evidence

Deterministic regression coverage lives in
`product\scripts\AeroLinkRemoteDemo.Tests.ps1` and runs in CI under Windows
PowerShell 5.1 (configuration validation, launch-command construction, process
ownership matching, idempotent decisions, 401 classification, task XML without
secrets). ngrok itself cannot be CI-qualified; attended Windows qualification
against the real public endpoint is recorded per handover and reports the exact
unauthenticated 401 result, local readiness, and owned process details without
credentials.

## Explicit non-goals

Cloud hosting, database replication, unprotected public access, replacement of
ngrok with a general production reverse proxy, corporate SSO/TLS/reverse-proxy
design, and wildcard/public-host `AllowedHosts` are all out of scope. This mode
is a protected demonstration topology only.
