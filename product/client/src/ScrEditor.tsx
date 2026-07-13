import { useEffect, useMemo, useState } from "react";
import type { FormEvent } from "react";
import type { AuthUser } from "./IdentityCenter";
import "./ScrEditor.css";
import "./ScrEditorEnhancements.css";

type ChangeScope = "System" | "Software";
type RequirementLevel = "System" | "HighLevel" | "LowLevel";
type RequirementKind = "Introduce" | "Modify" | "Retire";
type RequirementDraft = {
  baseNumber: string;
  revision: number;
  level: RequirementLevel;
  kind: RequirementKind;
  statement: string;
  rationale: string;
  verificationMethod: string;
  isDerived: boolean;
};
type ExistingRequirement = RequirementDraft & {
  id: string;
  displayNumber: string;
  nextRevision: number;
  state: string;
};
type Context = {
  changeRequestNumber: string;
  author: { userName: string; displayName: string };
  requirementNumbers: Record<string, string>;
};
type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  scope: ChangeScope;
  user: AuthUser;
  onCancel: () => void;
  onSaved: (scrId: string) => void;
};
type SavedDraft = {
  title: string;
  problem: string;
  analysis: string;
  solution: string;
  changes: RequirementDraft[];
};

const prefixFor = (level: RequirementLevel) =>
  level === "System" ? "SYSR" : level === "HighLevel" ? "HLR" : "LLR";
const addToIdentifier = (identifier: string | undefined, offset: number) => {
  if (!identifier) return "Assigned when saved";
  const [prefix, raw] = identifier.split("-");
  return `${prefix}-${(Number(raw) + offset).toString().padStart(8, "0")}`;
};

export default function ScrEditor({ api, projectId, releaseId, releaseVersion, scope, user, onCancel, onSaved }: Props) {
  const storageKey = `aerolink:new-${scope.toLowerCase()}-change:${projectId}:${releaseId}`;
  const restored = useMemo<SavedDraft | undefined>(() => {
    try { return JSON.parse(localStorage.getItem(storageKey) || "") as SavedDraft; } catch { return undefined; }
  }, [storageKey]);
  const defaultLevel: RequirementLevel = scope === "System" ? "System" : "HighLevel";
  const fresh = (level = defaultLevel): RequirementDraft => ({ baseNumber: "", revision: 0, level, kind: "Introduce", statement: "", rationale: "", verificationMethod: "Test", isDerived: false });
  const [context, setContext] = useState<Context>();
  const [title, setTitle] = useState(restored?.title || ""), [problem, setProblem] = useState(restored?.problem || ""), [analysis, setAnalysis] = useState(restored?.analysis || ""), [solution, setSolution] = useState(restored?.solution || "");
  const [changes, setChanges] = useState<RequirementDraft[]>(restored?.changes?.length ? restored.changes : [fresh()]);
  const [searches, setSearches] = useState<Record<number, string>>({}), [results, setResults] = useState<Record<number, ExistingRequirement[]>>({});
  const [saving, setSaving] = useState(false), [error, setError] = useState(""), [savedAt, setSavedAt] = useState<Date>();

  useEffect(() => {
    fetch(`${api}/api/authoring/context?projectId=${projectId}&type=${scope}`).then(async response => {
      if (!response.ok) throw new Error((await response.json()).error || "Authoring context unavailable.");
      setContext(await response.json());
    }).catch(reason => setError(reason.message));
  }, [api, projectId, scope]);

  useEffect(() => {
    const timer = setTimeout(() => {
      localStorage.setItem(storageKey, JSON.stringify({ title, problem, analysis, solution, changes } satisfies SavedDraft));
      setSavedAt(new Date());
    }, 450);
    return () => clearTimeout(timer);
  }, [storageKey, title, problem, analysis, solution, changes]);

  useEffect(() => {
    const timers = Object.entries(searches).map(([rawIndex, query]) => setTimeout(async () => {
      const index = Number(rawIndex);
      if (query.trim().length < 2) { setResults(current => ({ ...current, [index]: [] })); return; }
      const response = await fetch(`${api}/api/authoring/requirements?projectId=${projectId}&scope=${scope}&search=${encodeURIComponent(query)}&limit=8`);
      if (response.ok) { const rows:ExistingRequirement[]=await response.json(); setResults(current => ({ ...current, [index]: rows })); }
    }, 180));
    return () => timers.forEach(clearTimeout);
  }, [api, projectId, scope, searches]);

  const previewNumber = (change: RequirementDraft, index: number) => {
    if (change.kind !== "Introduce") return change.baseNumber || "Select an existing requirement";
    const prefix = prefixFor(change.level);
    const earlier = changes.slice(0, index).filter(item => item.kind === "Introduce" && prefixFor(item.level) === prefix).length;
    return addToIdentifier(context?.requirementNumbers[prefix], earlier);
  };
  const update = <K extends keyof RequirementDraft>(index: number, field: K, value: RequirementDraft[K]) => setChanges(items => items.map((item, position) => position === index ? { ...item, [field]: value } : item));
  const selectExisting = (index: number, item: ExistingRequirement) => {
    setChanges(current => current.map((change, position) => position === index ? { ...change, baseNumber: item.baseNumber, revision: item.nextRevision, level: item.level, statement: change.kind === "Retire" ? "" : item.statement, rationale: item.rationale, verificationMethod: item.verificationMethod } : change));
    setSearches(current => ({ ...current, [index]: item.baseNumber }));
    setResults(current => ({ ...current, [index]: [] }));
  };
  const changeKind = (index: number, kind: RequirementKind) => {
    setChanges(current => current.map((item, position) => position === index ? { ...item, kind, baseNumber: "", revision: 0, statement: kind === "Retire" ? "" : item.statement } : item));
    setSearches(current => ({ ...current, [index]: "" }));
  };
  const changeLevel = (index: number, level: RequirementLevel) => {
    setChanges(current => current.map((item, position) => position === index ? { ...item, level, baseNumber: item.kind === "Introduce" ? "" : item.baseNumber, revision: item.kind === "Introduce" ? 0 : item.revision } : item));
  };

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setSaving(true); setError("");
    if (changes.some(item => item.kind !== "Introduce" && !item.baseNumber)) { setError("Select an existing controlled requirement for every modification or retirement."); setSaving(false); return; }
    const response = await fetch(`${api}/api/scr-drafts`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ projectId, targetReleaseId: releaseId, title, problem, analysis, solution, type: scope, requirementChanges: changes.map((item, index) => ({ ...item, baseNumber: item.kind === "Introduce" ? previewNumber(item, index) : item.baseNumber })) }) });
    if (!response.ok) { setError((await response.json()).error || `Unable to save the ${scope === "System" ? "SCR" : "SWCR"} draft.`); setSaving(false); return; }
    const created = await response.json(); localStorage.removeItem(storageKey); onSaved(created.id);
  };

  const abbreviation = scope === "System" ? "SCR" : "SWCR";
  return <main className="editorPage">
    <header className="editorHeader"><div><button className="back" onClick={onCancel}>← Command Center</button><p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / NEW {abbreviation}</p><h1>Create {scope} Change Request</h1><p>{scope === "System" ? "Propose controlled System requirement changes for complete-package review." : "Propose controlled HLR and LLR changes in a dedicated Software workflow."}</p></div><div className="editorStatus"><span className="draftPill">DRAFT</span><small>{savedAt ? `Recovery copy saved ${savedAt.toLocaleTimeString()}` : "Auto-save ready"}</small></div></header>
    <form className="editorForm" onSubmit={submit}>
      <section className="editorCard"><div className="sectionTitle"><span>01</span><div><h2>Change request identity</h2><p>Server-assigned identity and authenticated ownership</p></div></div><div className="fields three">
        <label>{abbreviation} number<input value={context?.changeRequestNumber || "Calculating next number…"} readOnly/><small>Final number is assigned atomically when saved.</small></label>
        <label>Target release<input value={releaseVersion} readOnly/></label>
        <label>Author<input value={`${context?.author.displayName || user.displayName} (${context?.author.userName || user.userName})`} readOnly/><small>Derived from your authenticated session.</small></label>
        <label className="wide">Title<input value={title} onChange={event => setTitle(event.target.value)} placeholder="Concise description of the proposed change" required/></label>
      </div></section>
      <section className="editorCard"><div className="sectionTitle"><span>02</span><div><h2>Problem, analysis, and solution</h2><p>The complete case for change is reviewed as one exact snapshot</p></div></div><div className="pas">
        <label><b>Problem</b><textarea value={problem} onChange={event => setProblem(event.target.value)} placeholder="What operational, product, or assurance need exists?" required/></label>
        <label><b>Analysis</b><textarea value={analysis} onChange={event => setAnalysis(event.target.value)} placeholder="What is affected and what alternatives were considered?" required/></label>
        <label><b>Solution</b><textarea value={solution} onChange={event => setSolution(event.target.value)} placeholder="What controlled change is proposed?" required/></label>
      </div></section>
      <section className="editorCard"><div className="sectionTitle"><span>03</span><div><h2>Proposed {scope} requirement changes</h2><p>New identities and next revisions are generated automatically</p></div><button type="button" className="secondary" onClick={() => setChanges(items => [...items, fresh()])}>+ Add requirement</button></div>
        {changes.map((change, index) => <div className="requirement" key={index}><div className="requirementHead"><b>Requirement change {index + 1}</b>{changes.length > 1 && <button type="button" onClick={() => setChanges(items => items.filter((_, position) => position !== index))}>Remove</button>}</div><div className="fields reqgrid">
          <label>Change type<select value={change.kind} onChange={event => changeKind(index, event.target.value as RequirementKind)}><option value="Introduce">Introduce new</option><option value="Modify">Modify existing</option><option value="Retire">Retire existing</option></select></label>
          {scope === "Software" && <label>Software level<select value={change.level} onChange={event => changeLevel(index, event.target.value as RequirementLevel)}><option value="HighLevel">HLR</option><option value="LowLevel">LLR</option></select></label>}
          {change.kind === "Introduce" ? <label>Generated requirement number<input value={previewNumber(change, index)} readOnly/></label> : <label className="requirementLookup">Find requirement<input aria-label={`Find requirement ${index + 1}`} value={searches[index] || ""} onChange={event => setSearches(current => ({ ...current, [index]: event.target.value }))} placeholder="Type any part, e.g. 6832" autoComplete="off"/>{(results[index]?.length || 0) > 0 && <div className="lookupResults">{results[index].map(item => <button type="button" key={item.id} onClick={() => selectExisting(index, item)}><b>{item.displayNumber}</b><span>{item.level === "HighLevel" ? "HLR" : item.level === "LowLevel" ? "LLR" : "System"} · {item.state}</span><small>{item.statement}</small></button>)}</div>}</label>}
          <label>Proposed revision<input value={change.kind === "Introduce" ? "00" : change.baseNumber ? change.revision.toString().padStart(2, "0") : "Select requirement"} readOnly/></label>
          {scope === "Software" && <label className="derivedField"><span>Classification</span><button type="button" className={change.isDerived ? "derived active" : "derived"} onClick={() => update(index, "isDerived", !change.isDerived)}><i>{change.isDerived ? "✓" : "○"}</i><b>Derived software requirement</b><small>Not directly allocated from a higher-level requirement</small></button></label>}
          <label className="wide">Requirement statement<textarea value={change.statement} onChange={event => update(index, "statement", event.target.value)} readOnly={change.kind === "Retire"} placeholder={change.kind === "Retire" ? "Retirement retains the prior statement in immutable history." : "The controlled requirement statement"} required={change.kind !== "Retire"}/></label>
          <label className="half">Rationale<textarea value={change.rationale} onChange={event => update(index, "rationale", event.target.value)}/></label>
          <label className="half">Verification method<select value={change.verificationMethod} onChange={event => update(index, "verificationMethod", event.target.value)}><option>Test</option><option>Analysis</option><option>Inspection</option><option>Demonstration</option></select></label>
        </div></div>)}
      </section>
      {error && <div className="formError">{error}</div>}
      <footer className="editorActions"><p>Auto-save protects this browser recovery copy. Saving creates an attributable server-side Draft; it does not start review.</p><div><button type="button" className="secondary" onClick={onCancel}>Cancel</button><button disabled={saving || !context}>{saving ? "Saving Draft…" : `Save ${abbreviation} Draft`}</button></div></footer>
    </form>
  </main>;
}
