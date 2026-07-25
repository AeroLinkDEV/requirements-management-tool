import { useCallback, useEffect, useState, type FormEvent } from "react";
import "./JiraConnectorPanel.css";

/**
 * The project's link to its issue tracker.
 *
 * AeroLink pushes a change request to Jira and reflects back what Jira says about it. It does not become the
 * tracker, and the tracker never becomes authoritative for the controlled record — so nothing here lets a
 * Jira status change an AeroLink state.
 *
 * The stored credential is never returned by the server. Leaving the token field blank keeps the existing
 * one, so changing an issue type does not require pasting a secret.
 */

type Connection = {
  configured: boolean;
  baseUrl?: string;
  projectKey?: string;
  issueType?: string;
  userName?: string;
  isEnabled?: boolean;
  lastVerifiedAt?: string;
  lastError?: string;
};

type Link = {
  id: string;
  artifactNumber: string;
  issueKey: string;
  issueUrl: string;
  issueStatus: string;
  state: "Pending" | "Linked" | "Failed";
  lastError?: string;
  updatedAt: string;
  statusReadAt?: string;
};

const time = (value?: string) => (value ? new Date(value).toLocaleString() : "Never");

export default function JiraConnectorPanel({
  api,
  projectId,
  onError,
}: {
  api: string;
  projectId: string;
  onError: (message: string) => void;
}) {
  const [connection, setConnection] = useState<Connection>();
  const [links, setLinks] = useState<Link[]>([]);
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState("");
  const [message, setMessage] = useState("");

  const load = useCallback(async () => {
    const [c, l] = await Promise.all([
      fetch(`${api}/api/jira/connection?projectId=${projectId}`),
      fetch(`${api}/api/jira/links?projectId=${projectId}`),
    ]);
    if (c.ok) setConnection((await c.json()) as Connection);
    if (l.ok) setLinks((await l.json()) as Link[]);
  }, [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setBusy("Saving");
    setMessage("");
    const response = await fetch(`${api}/api/jira/connection`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        projectId,
        baseUrl: form.get("baseUrl"),
        projectKey: form.get("projectKey"),
        issueType: form.get("issueType"),
        userName: form.get("userName"),
        apiToken: form.get("apiToken") || null,
      }),
    });
    setBusy("");
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      onError(detail.error || "The connection could not be saved.");
      return;
    }
    setEditing(false);
    setMessage("Connection saved. Check it before somebody needs it.");
    await load();
  };

  const verify = async () => {
    setBusy("Checking");
    setMessage("");
    const response = await fetch(`${api}/api/jira/connection/verify?projectId=${projectId}`, { method: "POST" });
    setBusy("");
    const result = (await response.json().catch(() => ({}))) as { reachable?: boolean; detail?: string };
    setMessage(result.detail || "The tracker did not answer.");
    await load();
  };

  const refresh = async () => {
    setBusy("Reading");
    const response = await fetch(`${api}/api/jira/links/refresh?projectId=${projectId}`, { method: "POST" });
    setBusy("");
    const result = (await response.json().catch(() => ({}))) as { refreshed?: number };
    setMessage(`${result.refreshed ?? 0} issue status${result.refreshed === 1 ? "" : "es"} read from the tracker.`);
    await load();
  };

  const configured = connection?.configured === true;

  return (
    <article className="integrationPanel jiraPanel">
      <div className="integrationPanelHead">
        <div>
          <span>WORK TRACKING</span>
          <h2>Jira</h2>
          <p>
            Push a change request to the tracker and reflect its status back. The controlled record stays
            authoritative; Jira never changes an AeroLink state.
          </p>
        </div>
        <button onClick={() => setEditing(true)}>{configured ? "Reconfigure" : "Connect"}</button>
      </div>

      {message && <p className="jiraMessage">{message}</p>}

      {configured ? (
        <div className="jiraConnection">
          <div className={`jiraState ${connection?.lastError ? "attention" : "healthy"}`}>
            <i />
            <div>
              <b>
                {connection?.projectKey} · {connection?.issueType}
              </b>
              <code>{connection?.baseUrl}</code>
              <small>
                {connection?.lastError
                  ? connection.lastError
                  : `Last checked ${time(connection?.lastVerifiedAt)}`}
              </small>
            </div>
          </div>
          <div className="jiraActions">
            <button disabled={!!busy} onClick={() => void verify()}>
              {busy === "Checking" ? "Checking…" : "Check connection"}
            </button>
            <button disabled={!!busy || links.length === 0} onClick={() => void refresh()}>
              {busy === "Reading" ? "Reading…" : "Refresh statuses"}
            </button>
          </div>
        </div>
      ) : (
        <div className="integrationEmpty">
          <i>↗</i>
          <b>No tracker connected</b>
          <p>
            Connect the Jira project this programme tracks its work in, so a change request can carry its
            issue rather than being retyped into both.
          </p>
          <button onClick={() => setEditing(true)}>Connect Jira →</button>
        </div>
      )}

      {links.length > 0 && (
        <div className="jiraLinks">
          {links.map((link) => (
            <div key={link.id} className={link.state.toLowerCase()}>
              <div>
                <b>{link.artifactNumber}</b>
                {link.state === "Linked" ? (
                  <a href={link.issueUrl} target="_blank" rel="noreferrer">
                    {link.issueKey}
                  </a>
                ) : (
                  <em>{link.lastError || "Not pushed."}</em>
                )}
              </div>
              <div>
                {/* Jira's own wording, reflected as read. Mapping it onto an AeroLink state would invent a
                    correspondence that no two Jira projects agree on. */}
                <strong>{link.issueStatus || (link.state === "Linked" ? "Status not read yet" : link.state)}</strong>
                <time>{time(link.statusReadAt || link.updatedAt)}</time>
              </div>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <div
          className="integrationOverlay"
          role="presentation"
          onMouseDown={(event) => {
            if (event.target === event.currentTarget) setEditing(false);
          }}
        >
          <section role="dialog" aria-modal="true" aria-labelledby="jira-dialog-title">
            <button className="close" onClick={() => setEditing(false)} aria-label="Close">
              ×
            </button>
            <form onSubmit={save}>
              <p className="eyebrow">WORK TRACKING</p>
              <h2 id="jira-dialog-title">{configured ? "Reconfigure Jira" : "Connect Jira"}</h2>
              <p>
                AeroLink calls the tracker from the server. Nothing in the browser reaches Jira, so this works
                on a network that only the server can route out of.
              </p>
              <label>
                Jira address
                <input
                  name="baseUrl"
                  type="url"
                  defaultValue={connection?.baseUrl ?? ""}
                  placeholder="https://jira.example.com"
                  required
                />
              </label>
              <label>
                Project key
                <input name="projectKey" defaultValue={connection?.projectKey ?? ""} placeholder="FMS" required />
              </label>
              <label>
                Issue type
                <input name="issueType" defaultValue={connection?.issueType ?? "Task"} required />
              </label>
              <label>
                User name or email
                <input name="userName" defaultValue={connection?.userName ?? ""} placeholder="engineer@example.com" />
                <small>Leave blank for a Data Center instance using a personal access token.</small>
              </label>
              <label>
                API token
                <input
                  name="apiToken"
                  type="password"
                  placeholder={configured ? "Leave blank to keep the stored token" : ""}
                  required={!configured}
                />
                <small>Encrypted at rest. It is never displayed again, and no endpoint returns it.</small>
              </label>
              <button className="primaryAction" disabled={!!busy}>
                {busy || "Save connection"}
              </button>
            </form>
          </section>
        </div>
      )}
    </article>
  );
}
