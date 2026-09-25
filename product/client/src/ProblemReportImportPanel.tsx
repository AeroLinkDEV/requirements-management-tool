import { useState } from "react";
import { apiRequest, operationError } from "./apiClient";
import { useCategoryVocabulary } from "./problemReportCategories";
import "./ProblemReportImportPanel.css";

type Row = { row: number; sourceKey: string; title: string; action: string; reason?: string | null; landingState?: string | null; severity?: string | null; responsibleEngineer?: string | null };
type Preview = { sourceHash: string; previewHash: string; headers: string[]; distinctValues: Record<string, string[]>; rows: Row[]; create: number; skip: number };
type Mapping = {
  sourceSystem: string;
  columns: Record<string, string>;
  statuses: Record<string, string>;
  severities: Record<string, string>;
  priorities: Record<string, string>;
  categories: Record<string, string>;
  people: Record<string, string>;
  builds: Record<string, string>;
};

const fields: { id: string; label: string; required?: boolean; guesses: string[] }[] = [
  { id: "sourceKey", label: "Source key", required: true, guesses: ["key", "id", "issue key", "number"] },
  { id: "title", label: "Title", required: true, guesses: ["summary", "title", "headline"] },
  { id: "problem", label: "Problem statement", required: true, guesses: ["description", "problem", "details"] },
  { id: "status", label: "Status", guesses: ["status", "state"] },
  { id: "severity", label: "Severity", guesses: ["severity"] },
  { id: "priority", label: "Priority", guesses: ["priority"] },
  { id: "category", label: "Category", guesses: ["category", "type", "issue type"] },
  { id: "reportedBy", label: "Reported by", guesses: ["reporter", "reported by", "creator", "raised by"] },
  { id: "responsibleEngineer", label: "Responsible engineer", guesses: ["assignee", "owner", "responsible"] },
  { id: "createdAt", label: "Created", guesses: ["created", "created date", "date"] },
  { id: "targetBuild", label: "Target build", guesses: ["fix version", "version", "fix version/s", "target"] },
  { id: "analysis", label: "Analysis", guesses: ["analysis"] },
  { id: "rootCause", label: "Root cause", guesses: ["root cause"] },
  { id: "correctiveAction", label: "Corrective action", guesses: ["resolution", "corrective action", "fix"] },
];
const landingStates = [
  ["Draft", "Draft"], ["ReadyForSccb", "Ready for SCCB"], ["Open", "Open"], ["Implementing", "Implementing"],
  ["Verifying", "Verifying"], ["ClosedInSource", "Closed in source (read-only)"], ["Skip", "Skip these rows"],
];
const severities = ["Critical", "High", "Major", "Minor", "Trivial"];
const priorities = ["Urgent", "High", "Normal", "Low"];

/**
 * Problem Report import from another tool's CSV/XLSX export (#1114). Every row is previewed as Create or
 * Skip with its reason before anything is written; the import is then signed with the importer's password
 * over the exact file and mapping that was previewed.
 */
export default function ProblemReportImportPanel({ api, projectId, releases, onClose, onImported }: {
  api: string;
  projectId: string;
  releases: { id: string; version: string }[];
  onClose: () => void;
  onImported: () => void;
}) {
  const categories = useCategoryVocabulary(api);
  const [file, setFile] = useState<File>();
  const [mapping, setMapping] = useState<Mapping>({ sourceSystem: "", columns: {}, statuses: {}, severities: {}, priorities: {}, categories: {}, people: {}, builds: {} });
  const [preview, setPreview] = useState<Preview>();
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [done, setDone] = useState("");

  const request = async (path: string, extra: Record<string, string>, next: Mapping) => {
    const form = new FormData();
    form.append("projectId", projectId);
    form.append("mapping", JSON.stringify(next));
    form.append("file", file!);
    for (const [key, value] of Object.entries(extra)) form.append(key, value);
    return apiRequest<Record<string, unknown>>(`${api}/api/problem-reports/import/${path}`, { method: "POST", body: form });
  };
  const runPreview = async (next = mapping) => {
    if (!file) return;
    setBusy(true); setError("");
    try {
      const result = (await request("preview", {}, next)) as unknown as Preview;
      // First read of a file: guess the obvious columns from its headers, then preview again with them.
      if (Object.keys(next.columns).length === 0) {
        const columns: Record<string, string> = {};
        for (const field of fields) {
          const hit = result.headers.find((header) => field.guesses.includes(header.trim().toLowerCase()));
          if (hit) columns[field.id] = hit;
        }
        if (Object.keys(columns).length > 0) {
          const guessed = { ...next, columns };
          setMapping(guessed);
          setPreview((await request("preview", {}, guessed)) as unknown as Preview);
          return;
        }
      }
      setPreview(result);
    } catch (failure) {
      setError(operationError(failure, "The file could not be previewed."));
    } finally {
      setBusy(false);
    }
  };
  const update = (next: Mapping) => { setMapping(next); setPreview((current) => current && { ...current, previewHash: "" }); };
  const setValue = (group: keyof Omit<Mapping, "sourceSystem" | "columns">, key: string, value: string) =>
    update({ ...mapping, [group]: { ...mapping[group], [key]: value } });
  const commit = async () => {
    if (!preview) return;
    setBusy(true); setError("");
    try {
      const result = await request("commit", { previewHash: preview.previewHash, password }, mapping);
      setDone(`Imported ${String(result.created)} Problem Reports; ${String(result.skipped)} rows were skipped with their reasons.`);
      setPassword("");
      onImported();
    } catch (failure) {
      setError(operationError(failure, "The import was refused."));
    } finally {
      setBusy(false);
    }
  };
  const distinct = (field: string) => preview?.distinctValues[field] ?? [];
  const valueSelect = (group: keyof Omit<Mapping, "sourceSystem" | "columns">, value: string, options: [string, string][]) => (
    <select aria-label={`Map ${value}`} value={mapping[group][value] ?? ""} onChange={(event) => setValue(group, value, event.target.value)}>
      <option value="">Choose…</option>
      {options.map(([id, label]) => <option key={id} value={id}>{label}</option>)}
    </select>
  );
  const stale = !!preview && !preview.previewHash;

  return (
    <section className="prImport" aria-label="Import Problem Reports">
      <header>
        <div>
          <h2>Import Problem Reports</h2>
          <p>Bring reports in from another tool's CSV or Excel export. Every row is previewed before anything is written. The source key, reporter, date and status are kept as source facts; a report the source had closed arrives read-only as Closed in source, with no AeroLink SQA closure.</p>
        </div>
        <button type="button" onClick={onClose}>Close</button>
      </header>
      {done ? <p className="prImportDone" role="status">{done}</p> : <>
        <div className="prImportStep">
          <label>Source system<input value={mapping.sourceSystem} placeholder="e.g. Jira" onChange={(event) => update({ ...mapping, sourceSystem: event.target.value })} /></label>
          <label>Export file (.csv or .xlsx)<input type="file" accept=".csv,.xlsx" onChange={(event) => { setFile(event.target.files?.[0]); setPreview(undefined); update({ ...mapping, columns: {} }); }} /></label>
          <button type="button" disabled={!file || busy} onClick={() => void runPreview()}>{preview ? "Preview again" : "Read the file"}</button>
        </div>

        {preview && <>
          <fieldset className="prImportColumns">
            <legend>Columns</legend>
            {fields.map((field) => (
              <label key={field.id}>{field.label}{field.required ? " *" : ""}
                <select value={mapping.columns[field.id] ?? ""} onChange={(event) => update({ ...mapping, columns: { ...mapping.columns, [field.id]: event.target.value } })}>
                  <option value="">Not in this file</option>
                  {preview.headers.map((header) => <option key={header} value={header}>{header}</option>)}
                </select>
              </label>
            ))}
          </fieldset>

          <fieldset className="prImportValues">
            <legend>Values</legend>
            {distinct("status").map((value) => <label key={`status-${value}`}>Status “{value}”{valueSelect("statuses", value, landingStates as [string, string][])}</label>)}
            {distinct("severity").filter((value) => !severities.some((name) => name.toLowerCase() === value.toLowerCase())).map((value) =>
              <label key={`severity-${value}`}>Severity “{value}”{valueSelect("severities", value, severities.map((name) => [name, name]))}</label>)}
            {distinct("priority").filter((value) => !priorities.some((name) => name.toLowerCase() === value.toLowerCase())).map((value) =>
              <label key={`priority-${value}`}>Priority “{value}”{valueSelect("priorities", value, priorities.map((name) => [name, name]))}</label>)}
            {distinct("category").map((value) => <label key={`category-${value}`}>Category “{value}”{valueSelect("categories", value, categories.map((item) => [item.value, `${item.code} ${item.label}`]))}</label>)}
            {[...new Set([...distinct("reportedBy"), ...distinct("responsibleEngineer")])].map((value) => (
              <label key={`person-${value}`}>Person “{value}”
                <input aria-label={`AeroLink user for ${value}`} value={mapping.people[value] ?? ""} placeholder="AeroLink user name (optional)" onChange={(event) => setValue("people", value, event.target.value)} />
              </label>
            ))}
            {distinct("targetBuild").map((value) => <label key={`build-${value}`}>Version “{value}”{valueSelect("builds", value, [...releases.map((release) => [release.id, `Build ${release.version}`] as [string, string]), ["Unassigned", "Unassigned"]])}</label>)}
          </fieldset>
          {stale && <p className="prImportNote">The mapping changed. Preview again to see the result.</p>}

          <table className="prImportRows">
            <caption>{preview.create} will be created · {preview.skip} will be skipped</caption>
            <thead><tr><th>Row</th><th>Source key</th><th>Title</th><th>Result</th></tr></thead>
            <tbody>
              {preview.rows.map((row) => (
                <tr key={row.row} className={row.action === "Create" ? "create" : "skip"}>
                  <td>{row.row}</td><td>{row.sourceKey}</td><td>{row.title}</td>
                  <td>{row.action === "Create" ? `Create · ${row.landingState === "ClosedInSource" ? "Closed in source" : row.landingState}` : `Skip · ${row.reason}`}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="prImportCommit">
            <label>Confirm with your password<input type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} /></label>
            <button type="button" disabled={busy || stale || preview.create === 0 || !password} onClick={() => void commit()}>
              Sign and import {preview.create} Problem Report{preview.create === 1 ? "" : "s"}
            </button>
          </div>
        </>}
      </>}
      {error && <p className="prImportError" role="alert">{error}</p>}
    </section>
  );
}
