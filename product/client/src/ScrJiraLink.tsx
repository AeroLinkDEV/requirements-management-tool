import { useCallback, useEffect, useState } from "react";
import "./ScrJiraLink.css";

/**
 * The change request's issue in the programme's tracker.
 *
 * Pushing is deliberately an act somebody takes rather than something that happens on approval. Not every
 * change request is programme-tracked work, and creating an issue for every draft would fill a board with
 * things nobody agreed to. Pushing twice is harmless: the server returns the issue that already exists.
 *
 * Nothing here can change the change request. Jira holds the work item; AeroLink holds the controlled
 * record and its approvals, and neither is authoritative for what the other is for.
 */

type Link = {
  issueKey: string;
  issueUrl: string;
  issueStatus: string;
  state: "Pending" | "Linked" | "Failed";
  lastError?: string;
  statusReadAt?: string;
};

export default function ScrJiraLink({
  api,
  scrId,
  displayNumber,
}: {
  api: string;
  scrId: string;
  displayNumber: string;
}) {
  const [configured, setConfigured] = useState(false);
  const [link, setLink] = useState<Link>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    const response = await fetch(`${api}/api/change-requests/${scrId}/jira`);
    if (!response.ok) return;
    const body = (await response.json()) as { configured: boolean; link: Link | null };
    setConfigured(body.configured);
    setLink(body.link ?? undefined);
  }, [api, scrId]);

  useEffect(() => {
    void load();
  }, [load]);

  // A project with no tracker connected sees nothing. An empty panel telling somebody about a capability
  // their programme has not adopted is noise on the page they came to read.
  if (!configured) return null;

  const push = async () => {
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/change-requests/${scrId}/jira`, { method: "POST" });
    setBusy(false);
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "The tracker did not accept the issue.");
    }
    await load();
  };

  return (
    <section className="workspaceCard scrJira">
      <div className="workspaceTitle">
        <div>
          <h2>Work tracking</h2>
          <p>The issue this change is tracked under in the programme&rsquo;s tracker</p>
        </div>
        {link?.state !== "Linked" && (
          <button className="outline" type="button" disabled={busy} onClick={() => void push()}>
            {busy ? "Creating…" : link ? "Try again" : "Create Jira issue"}
          </button>
        )}
      </div>

      {error && (
        <p className="scrJiraError" role="alert">
          {error}
        </p>
      )}

      {link?.state === "Linked" ? (
        <div className="scrJiraLinked">
          <a href={link.issueUrl} target="_blank" rel="noreferrer">
            {link.issueKey}
          </a>
          <div>
            <strong>{link.issueStatus || "Status not read yet"}</strong>
            <small>
              {link.statusReadAt
                ? `Read from the tracker ${new Date(link.statusReadAt).toLocaleString()}`
                : "AeroLink reads status on a timer; the controlled record is unaffected by it."}
            </small>
          </div>
        </div>
      ) : (
        <p className="scrJiraEmpty">
          {link?.lastError
            ? link.lastError
            : `${displayNumber} is not tracked in Jira yet.`}
        </p>
      )}
    </section>
  );
}
