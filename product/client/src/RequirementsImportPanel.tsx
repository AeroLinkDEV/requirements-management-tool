import { useState } from "react";
import type { FormEvent } from "react";
import "./RequirementsImportPanel.css";

type ImportRow = {
  rowNumber: number;
  identifier: string;
  level: string;
  statement: string;
  verificationMethod: string;
  valid: boolean;
  errors: string[];
};

type ImportPreview = {
  id: string;
  fileName: string;
  sha256: string;
  validRows: number;
  invalidRows: number;
  rows: ImportRow[];
};

type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  scope: "System" | "Software";
  onCreated: (changeRequestId: string) => void;
};

export default function RequirementsImportPanel({ api, projectId, releaseId, scope, onCreated }: Props) {
  const [open, setOpen] = useState(false);
  const [preview, setPreview] = useState<ImportPreview>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const abbreviation = scope === "System" ? "SCR" : "SWCR";

  const close = () => {
    setOpen(false);
    setPreview(undefined);
    setError("");
  };

  const previewFile = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/enterprise-requirements/import/preview?projectId=${projectId}`, {
      method: "POST",
      body: new FormData(event.currentTarget),
    });
    setBusy(false);
    if (!response.ok) {
      const body = await response.json() as { error?: string };
      setError(body.error || "The source file could not be previewed.");
      return;
    }
    setPreview(await response.json());
  };

  const createDraft = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!preview) return;
    const form = new FormData(event.currentTarget);
    setBusy(true);
    setError("");
    const response = await fetch(`${api}/api/enterprise-requirements/import/${preview.id}/commit`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        targetReleaseId: releaseId,
        type: scope,
        title: form.get("title"),
        problem: form.get("problem"),
        analysis: form.get("analysis"),
        solution: form.get("solution"),
      }),
    });
    setBusy(false);
    if (!response.ok) {
      const body = await response.json() as { error?: string };
      setError(body.error || `The Draft ${abbreviation} could not be created.`);
      return;
    }
    const result = await response.json() as { id: string };
    onCreated(result.id);
  };

  return (
    <>
      <section className="changeIntakeTools" aria-label="Change intake options">
        <div><b>Starting with supplier or legacy data?</b><span>Validate the source here and convert it into a controlled Draft change package.</span></div>
        <button type="button" onClick={() => setOpen(true)}>Import into Draft {abbreviation}</button>
      </section>
      {open && (
        <div className="changeImportBackdrop" role="presentation" onMouseDown={(event) => event.target === event.currentTarget && close()}>
          <section className="changeImportPanel" role="dialog" aria-modal="true" aria-labelledby="change-import-title">
            <header>
              <div><p className="eyebrow">CHANGES / GOVERNED IMPORT</p><h2 id="change-import-title">Import requirements into a Draft {abbreviation}</h2><p>Rows are validated and previewed here. Commit creates proposals inside Changes; it never edits authoritative requirements directly.</p></div>
              <button type="button" aria-label="Close requirement import" onClick={close}>×</button>
            </header>
            {error && <div className="formError" role="alert">{error}</div>}
            {!preview ? (
              <form className="changeImportFile" onSubmit={previewFile}>
                <label>
                  <span>⇧</span><b>Select CSV or XLSX source</b><small>Identifier, Level, Statement, Rationale, VerificationMethod · 25 MB maximum</small>
                  <input aria-label="Requirements import file" type="file" name="file" accept=".csv,.xlsx" required />
                </label>
                <button disabled={busy}>{busy ? "Validating…" : "Validate and preview"}</button>
              </form>
            ) : (
              <div className="changeImportPreview">
                <div className="changeImportIdentity"><div><b>{preview.fileName}</b><small>SHA-256 {preview.sha256.slice(0, 20)}…</small></div><div><b>{preview.validRows}</b><span>valid</span><b className={preview.invalidRows ? "bad" : ""}>{preview.invalidRows}</b><span>invalid</span></div></div>
                <div className="changeImportRows">{preview.rows.slice(0, 8).map((row) => <article className={row.valid ? "valid" : "invalid"} key={row.rowNumber}><span>{row.rowNumber}</span><div><b>{row.identifier || "Missing identifier"}</b><p>{row.statement || "Missing statement"}</p><small>{row.valid ? `${row.level} · ${row.verificationMethod}` : row.errors.join(" · ")}</small></div></article>)}</div>
                {preview.invalidRows ? <div className="changeImportBlocked"><b>Import blocked</b><span>Correct every invalid row in the source file, then preview it again. No lifecycle data has changed.</span><button type="button" onClick={() => setPreview(undefined)}>Choose another file</button></div> : (
                  <form className="changeImportCase" onSubmit={createDraft}>
                    <h3>Describe the controlled change package</h3>
                    <label>Title<input name="title" defaultValue={`Import ${preview.validRows} ${scope.toLowerCase()} requirement proposals`} required /></label>
                    <div><label>Problem<textarea name="problem" required /></label><label>Analysis<textarea name="analysis" required /></label><label>Solution<textarea name="solution" required /></label></div>
                    <button disabled={busy}>{busy ? `Creating Draft ${abbreviation}…` : `Create Draft ${abbreviation} with ${preview.validRows} proposals`}</button>
                  </form>
                )}
              </div>
            )}
          </section>
        </div>
      )}
    </>
  );
}
