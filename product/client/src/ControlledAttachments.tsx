import { useCallback, useEffect, useState, type FormEvent } from "react";
import { stateLabel } from "./presentation";
import "./ControlledAttachments.css";

/**
 * The files that sit beside a controlled artifact.
 *
 * These are not the figures inside the content — those are part of what the record says and live in the
 * authored content itself. These are the supplier datasheet, the analysis spreadsheet, the trade study: the
 * things somebody deliberately attached because an approver needs them to decide.
 *
 * Attachments are versioned and never destroyed. An approver may have signed having read one of these, and a
 * signature over a file that can no longer be produced is the one thing an assurance record must never
 * allow, so a superseded version stays retrievable rather than being replaced.
 */

type Attachment = {
  id: string;
  logicalId: string;
  version: number;
  label: string;
  description: string;
  originalFileName: string;
  contentType: string;
  size: number;
  sha256: string;
  state: string;
  uploadedBy: string;
  uploadedAt: string;
  integrityVerifiedAt?: string;
};

type Props = {
  api: string;
  projectId: string;
  artifactType: "Requirement" | "ChangeRequest" | "ProblemReport";
  artifactId: string;
  revisionId?: string;
  /** Attaching is an authoring act. A reader sees the files; they do not add to them. */
  canAttach: boolean;
};

export default function ControlledAttachments({
  api,
  projectId,
  artifactType,
  artifactId,
  revisionId,
  canAttach,
}: Props) {
  const [items, setItems] = useState<Attachment[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");

  const load = useCallback(async () => {
    const response = await fetch(
      `${api}/api/enterprise-hardening/attachments?projectId=${projectId}&artifactType=${artifactType}&artifactId=${artifactId}`,
    );
    if (response.ok) setItems((await response.json()) as Attachment[]);
  }, [api, artifactId, artifactType, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const upload = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const form = event.currentTarget;
    const body = new FormData(form);
    body.set("projectId", projectId);
    body.set("artifactType", artifactType);
    body.set("artifactId", artifactId);
    if (revisionId) body.set("revisionId", revisionId);
    setBusy(true);
    setError("");
    setMessage("");
    const response = await fetch(`${api}/api/enterprise-hardening/attachments`, { method: "POST", body });
    setBusy(false);
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "The file could not be stored.");
      return;
    }
    form.reset();
    setMessage("Stored, hashed, and attributed.");
    await load();
  };

  const newVersion = async (item: Attachment, file: File) => {
    const body = new FormData();
    body.set("projectId", projectId);
    body.set("artifactType", artifactType);
    body.set("artifactId", artifactId);
    if (revisionId) body.set("revisionId", revisionId);
    body.set("logicalId", item.logicalId);
    body.set("label", item.label);
    body.set("description", item.description);
    body.set("file", file);
    setError("");
    const response = await fetch(`${api}/api/enterprise-hardening/attachments`, { method: "POST", body });
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "The new version could not be stored.");
      return;
    }
    setMessage(`${item.label} advanced to the next controlled version.`);
    await load();
  };

  const verify = async (item: Attachment) => {
    const response = await fetch(`${api}/api/enterprise-hardening/attachments/${item.id}/verify`, { method: "POST" });
    const result = (await response.json()) as { valid: boolean };
    setMessage(
      result.valid
        ? `${item.label} still matches the SHA-256 recorded when it was stored.`
        : `${item.label} no longer matches its recorded digest.`,
    );
    await load();
  };

  return (
    <div className="attachmentPanel">
      {canAttach && (
        <form className="attachmentUpload" onSubmit={upload}>
          <label>
            Label
            <input name="label" placeholder="Supplier interface datasheet" required />
          </label>
          <label>
            Description
            <input name="description" placeholder="Why an approver needs this" />
          </label>
          <label className="attachmentFile">
            File
            <input type="file" name="file" required />
          </label>
          <button disabled={busy}>{busy ? "Checksumming…" : "Attach"}</button>
        </form>
      )}
      {error && (
        <p className="attachmentError" role="alert">
          {error}
        </p>
      )}
      {message && <p className="attachmentMessage">{message}</p>}

      {items.length === 0 ? (
        <p className="attachmentEmpty">
          No files are attached. {canAttach ? "Attach the evidence an approver needs to decide." : ""}
        </p>
      ) : (
        <ul className="attachmentList">
          {items.map((item) => (
            <li key={item.id} className={item.state.toLowerCase()}>
              <div className="attachmentIcon">{item.originalFileName.split(".").at(-1)?.toUpperCase()}</div>
              <div className="attachmentDetail">
                <b>
                  {item.label} <i>v{item.version}</i>
                </b>
                <p>
                  {item.originalFileName} · {(item.size / 1024).toFixed(1)} KB
                </p>
                {item.description && <p className="attachmentWhy">{item.description}</p>}
                <code>SHA-256 {item.sha256.slice(0, 24)}…</code>
                <small>
                  {stateLabel(item.state)} · {item.uploadedBy} · {new Date(item.uploadedAt).toLocaleDateString()}
                </small>
              </div>
              <div className="attachmentActions">
                <a href={`${api}/api/enterprise-hardening/attachments/${item.id}/download`}>Download</a>
                <button type="button" onClick={() => void verify(item)}>
                  {item.integrityVerifiedAt ? "Verified ✓" : "Verify integrity"}
                </button>
                {canAttach && item.state === "Active" && (
                  <label>
                    New version
                    <input
                      type="file"
                      onChange={(event) => {
                        const file = event.target.files?.[0];
                        event.target.value = "";
                        if (file) void newVersion(item, file);
                      }}
                    />
                  </label>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
