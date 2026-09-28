import { useEffect, useRef, useState } from "react";
import { apiRequest, operationError } from "./apiClient";
import { RichContentView } from "./RichContent";
import { useCategoryVocabulary } from "./problemReportCategories";
import "./ProblemReportImportPanel.css";
import "./IntegrityImportPanel.css";

type Mapping = {
  sourceSystem: string; columns: Record<string, string>; statuses: Record<string, string>; severities: Record<string, string>;
  priorities: Record<string, string>; categories: Record<string, string>; people: Record<string, string>; builds: Record<string, string>;
};
type Outcome = { path: string; kind: string; outcome: string; sourceName?: string };
type Row = {
  sourceKey: string; title: string; action: string; reason: string; existingReport?: string; landingState: string;
  problem: string; analysis: string; rootCause: string; correctiveAction: string;
  severity: string; priority: string; category?: string; targetBuildId?: string; raisedBy: string;
  sourceReportedBy: string; sourceState: string; responsibleEngineer: string;
  sourceDate: { raw: string; meaning: string; representation: string; instant?: string; finding?: string };
  findings: string[]; outcomes: Outcome[];
};
type Preview = {
  manifestHash: string; previewHash: string; sourceName: string; evidenceMode: string; fields: string[];
  distinctValues: Record<string, string[]>; rows: Row[]; create: number; skip: number; blocked: number; packageOutcomes: Outcome[];
  images?: Record<string, string>;
};
type Batch = { id: string; manifestHash: string; importedBy: string; importedAt: string; receipt: { created: number; skipped: number } };
const emptyMapping = (): Mapping => ({ sourceSystem: "", columns: {}, statuses: {}, severities: {}, priorities: {}, categories: {}, people: {}, builds: {} });
const dateMeaning: Record<string, string> = {
  "explicit-offset": "Time with a known offset", "date-only": "Date only", "local-unknown-zone": "Local time; zone unknown",
  unreadable: "Preserved without interpretation", unrepresentable: "Preserved at original precision", blank: "Not supplied",
};
const outcomeLabel: Record<string, string> = {
  "native-and-preserved": "Copied into the report and preserved", preserved: "Preserved as source information",
  "source-reference": "Preserved as a source reference",
};
const fields = [
  ["title", "Title", "Summary"], ["problem", "Problem statement", "Description"], ["status", "Status", "State"],
  ["severity", "Severity", "Severity"], ["priority", "Priority", "Priority"], ["category", "Category", "Category"],
  ["reportedBy", "Source reporter", "Reporter"], ["responsibleEngineer", "Responsible engineer", "Owner"],
  ["createdAt", "Source created date", "Created"], ["targetBuild", "Target build", "Target Build"],
  ["analysis", "Analysis", "Analysis"], ["rootCause", "Root cause", "Root Cause"], ["correctiveAction", "Corrective action", "Corrective Action"],
];

export default function IntegrityImportPanel({ api, projectId, releases, onClose, onImported }: {
  api: string; projectId: string; releases: { id: string; version: string }[]; onClose: () => void; onImported: () => void;
}) {
  const vocabulary = useCategoryVocabulary(api);
  const [file, setFile] = useState<File>();
  const [manifestHash, setManifestHash] = useState("");
  const [mapping, setMapping] = useState<Mapping>(emptyMapping);
  const [preview, setPreview] = useState<Preview>();
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState("");
  const [batches, setBatches] = useState<Batch[]>([]);
  const [page, setPage] = useState(0);
  const operation = useRef(crypto.randomUUID());
  const root = `${api}/api/problem-reports/integrity-import`;
  const loadBatches = async () => {
    try { setBatches(await apiRequest<Batch[]>(`${root}/batches?projectId=${encodeURIComponent(projectId)}`)); }
    catch (failure) { setError(operationError(failure, "You cannot retrieve this project's preserved imports.")); }
  };
  useEffect(() => { void loadBatches(); }, [api, projectId]); // eslint-disable-line react-hooks/exhaustive-deps
  const changed = (next: Mapping) => {
    setMapping(next); setPreview(current => current && { ...current, previewHash: "" });
    operation.current = crypto.randomUUID(); setDone("");
  };
  const request = async <T,>(path: string, next: Mapping, extra: Record<string, string> = {}): Promise<T> => {
    const form = new FormData(); form.append("projectId", projectId); form.append("file", file!);
    form.append("manifestHash", manifestHash.trim()); form.append("mapping", JSON.stringify(next));
    for (const [key, value] of Object.entries(extra)) form.append(key, value);
    return apiRequest<T>(`${root}/${path}`, { method: "POST", body: form });
  };
  const read = async () => {
    if (!file) return; setBusy(true); setError(""); setPage(0);
    operation.current = crypto.randomUUID();
    try {
      let result = await request<Preview>("preview", mapping);
      if (Object.keys(mapping.columns).length === 0) {
        const columns: Record<string, string> = {};
        for (const [key, , suggested] of fields) {
          const match = result.fields.find(x => x.toLowerCase() === suggested.toLowerCase());
          if (match) columns[key] = match;
        }
        const next = { ...mapping, columns }; setMapping(next);
        result = await request<Preview>("preview", next);
      }
      setPreview(result);
    } catch (failure) { setError(operationError(failure, "The source package could not be verified.")); }
    finally { setBusy(false); }
  };
  const commit = async () => {
    if (!preview?.previewHash) return; setBusy(true); setError("");
    try {
      const receipt = await request<{ created: number; skipped: number }>("commit", mapping,
        { previewHash: preview.previewHash, operationId: operation.current, password });
      setDone(`Imported ${receipt.created} reports; ${receipt.skipped} were already imported. The preserved package and reconciliation are available below.`);
      setPassword(""); onImported(); await loadBatches();
    } catch (failure) { setError(operationError(failure, "The import was not confirmed. You can retry this same operation.")); }
    finally { setBusy(false); }
  };
  const values = (field: string) => preview?.distinctValues[field] ?? [];
  const select = (group: keyof Omit<Mapping, "sourceSystem" | "columns">, source: string, options: [string, string][]) =>
    <label key={`${group}-${source}`}>{source}<select aria-label={`Map ${group}: ${source}`} value={mapping[group][source] ?? ""}
      onChange={event => changed({ ...mapping, [group]: { ...mapping[group], [source]: event.target.value } })}>
      <option value="">Choose…</option>{options.map(([value, label]) => <option key={value} value={value}>{label}</option>)}
    </select></label>;
  const enumOptions = (values: string[]): [string, string][] => values.map(x => [x, x]);
  return <section className="prImport integrityImport" aria-label="Import from Integrity">
    <header><div><h2>Import from Integrity</h2><p>Review the reports and source information before confirming. Original history and signatures remain Integrity records.</p></div>
      <button type="button" onClick={onClose}>Close</button></header>
    {error && <p role="alert">{error}</p>}
    {done && <p role="status">{done}</p>}
    {!done && <>
      <div className="prImportStep">
        <label>Integrity source package<input type="file" accept=".zip" disabled={busy} onChange={event => {
          setFile(event.target.files?.[0]); setPreview(undefined); changed(emptyMapping());
        }} /></label>
        <label>Recorded manifest SHA-256<input value={manifestHash} spellCheck={false} onChange={event => {
          setManifestHash(event.target.value); changed(mapping);
        }} placeholder="Paste the hash recorded when the package was acquired" /></label>
        <button type="button" disabled={!file || !/^[a-f0-9]{64}$/i.test(manifestHash.trim()) || busy} onClick={() => void read()}>
          {preview ? "Review updated mapping" : "Read package"}</button>
      </div>
      {preview && <>
        <p><strong>{preview.sourceName}</strong>{preview.evidenceMode === "fixture" && " — synthetic fixture; no live Integrity validation"}</p>
        <fieldset className="prImportColumns"><legend>Fields</legend>{fields.map(([key, label]) => <label key={key}>{label}
          <select value={mapping.columns[key] ?? ""} onChange={event => changed({ ...mapping, columns: { ...mapping.columns, [key]: event.target.value } })}>
            <option value="">Not mapped</option>{preview.fields.map(name => <option key={name}>{name}</option>)}
          </select></label>)}</fieldset>
        <fieldset className="prImportColumns"><legend>Interpretation</legend>
          {values("status").map(x => select("statuses", x, [["Draft", "Draft"], ["ReadyForSccb", "Ready for SCCB"], ["Open", "Open"],
            ["Implementing", "Implementing"], ["Verifying", "Verifying"], ["ClosedInSource", "Closed in source (read-only)"]]))}
          {values("severity").map(x => select("severities", x, enumOptions(["Critical", "High", "Major", "Minor", "Trivial"])))}
          {values("priority").map(x => select("priorities", x, enumOptions(["Urgent", "High", "Normal", "Low"])))}
          {values("category").map(x => select("categories", x, vocabulary.map(category => [category.value, category.label])))}
          {[...new Set([...values("reportedBy"), ...values("responsibleEngineer")])].map(person => <label key={person}>{person}
            <input aria-label={`AeroLink account for ${person}`} value={mapping.people[person] ?? ""} placeholder="AeroLink username"
              onChange={event => changed({ ...mapping, people: { ...mapping.people, [person]: event.target.value } })} /></label>)}
          {values("targetBuild").map(x => select("builds", x, [["Unassigned", "Unassigned — preserve source build only"], ...releases.map(build => [build.id, build.version] as [string, string])]))}
        </fieldset>
        {!preview.previewHash && <p role="status">Mapping changed. Review the updated result before confirming.</p>}
        <p>{preview.create} to create · {preview.skip} already imported · {preview.blocked} need attention. All {preview.packageOutcomes.length} package members will be preserved.</p>
        <div style={{ overflowX: "auto" }}><table className="prImportRows"><thead><tr><th>Integrity ID</th><th>Report</th><th>Result</th><th>Source date</th><th>Review</th></tr></thead>
          <tbody>{preview.rows.slice(page * 50, page * 50 + 50).map(row => <tr key={row.sourceKey}>
            <td>{row.sourceKey}</td><td>{row.title}</td><td>{row.action} · {row.landingState}<br />{row.existingReport ?? row.reason}</td>
            <td>{row.sourceDate.raw || "Not supplied"}<br />{dateMeaning[row.sourceDate.meaning] ?? row.sourceDate.meaning}{row.sourceDate.instant && <><br />Stored instant: {row.sourceDate.instant}</>}</td>
            <td><details><summary>Content and findings</summary>
              <p>Severity: {row.severity} · Priority: {row.priority} · Category: {row.category || "Not classified"}</p>
              <p>Target build: {releases.find(build => build.id === row.targetBuildId)?.version || "Unassigned"}</p>
              <p>Raised by: {row.raisedBy} · Source reporter: {row.sourceReportedBy || "Not supplied"}</p>
              <p>Source state: {row.sourceState || "Not supplied"} · Date representation: {row.sourceDate.representation}</p>
              {([["Problem", row.problem], ["Analysis", row.analysis], ["Root cause", row.rootCause], ["Corrective action", row.correctiveAction]] as const)
                .map(([label, value]) => <div key={label}><strong>{label}</strong><RichContentView api={api} value={value} previewImages={preview.images} /></div>)}
              <p>Responsible engineer: {row.responsibleEngineer || "Not mapped"}</p>
              {row.findings.map(finding => <p key={finding}>{finding}</p>)}
              <details><summary>{row.outcomes.length} source outcomes</summary><ul>{row.outcomes.map((outcome, index) =>
                <li key={index}>{outcome.sourceName || outcome.path} — {outcomeLabel[outcome.outcome] ?? outcome.outcome}</li>)}</ul></details>
            </details></td></tr>)}</tbody></table></div>
        {preview.rows.length > 50 && <nav aria-label="Preview pages"><button disabled={page === 0} onClick={() => setPage(page - 1)}>Previous</button>
          <span>Page {page + 1} of {Math.ceil(preview.rows.length / 50)}</span><button disabled={(page + 1) * 50 >= preview.rows.length} onClick={() => setPage(page + 1)}>Next</button></nav>}
        <p>Confirming accepts this exact result, including the date interpretations and findings. Source approvals do not become AeroLink approvals. Preserved source content is restricted to project import-authority roles.</p>
        <label>Confirm with your password<input type="password" autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} /></label>
        <button type="button" disabled={busy || !password || !preview.previewHash || preview.blocked > 0} onClick={() => void commit()}>Confirm import</button>
      </>}
    </>}
    <details><summary>Preserved imports ({batches.length})</summary>{batches.map(batch => <article key={batch.id}>
      <p>{new Date(batch.importedAt).toLocaleString()} · {batch.importedBy} · {batch.receipt.created} created · {batch.receipt.skipped} skipped</p>
      <a href={`${root}/batches/${batch.id}/package`}>Download original package</a>{" · "}
      <a href={`${root}/batches/${batch.id}`} target="_blank" rel="noreferrer">View signed reconciliation</a>
    </article>)}</details>
  </section>;
}
