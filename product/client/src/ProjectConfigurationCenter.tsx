import { useCallback, useEffect, useMemo, useState } from "react";
import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import ApprovalConfigurationCenter from "./ApprovalConfigurationCenter";
import AssurancePolicyPanel from "./AssurancePolicyPanel";
import RepositoryConfigurationPanel from "./RepositoryConfigurationPanel";
import InceptionSourceProvenancePanel from "./InceptionSourceProvenancePanel";
import { apiRequest, operationError } from "./apiClient";
import { capabilityMask } from "./projectLadder";
import { useVerificationVocabulary } from "./verificationMethods";
import "./ProjectConfigurationCenter.css";

type Level = string;
type VerificationKind = "Case" | "Procedure";
type Step = { catalogueEntry: Level; position: number; capabilities: number; enabledArtifactKinds?: VerificationKind[] };
type Relationship = { parent: Level; child: Level };
type HistoryItem = { revision: number; actor: string; occurredAt: string; reason: string; canonicalSnapshot: string; snapshotHash: string };
type Consumer = { id: string; description: string; routed: boolean };
type CatalogueEntry = { catalogueEntry: Level; supportedCapabilities: number };
type Configuration = {
  projectId: string; configurationId: string; classification: string; state: string; version: number;
  activationManifestVersion?: string; activationManifestHash?: string;
  steps: Step[]; effectiveSteps: Step[]; effectiveRelationships: Relationship[]; relationships: Relationship[]; history: HistoryItem[];
  readiness: { version: string; hash: string; consumers: Consumer[]; missingOrUnrouted: Consumer[]; isReady: boolean };
  catalogue: CatalogueEntry[]; canManage: boolean;
};
type ConfigurationResponse = Omit<Configuration, "steps" | "effectiveSteps" | "effectiveRelationships" | "catalogue"> & {
  steps: (Omit<Step, "capabilities"> & { capabilities: unknown })[];
  effectiveSteps?: (Omit<Step, "capabilities"> & { capabilities: unknown })[];
  effectiveRelationships?: Relationship[];
  catalogue: (Omit<CatalogueEntry, "supportedCapabilities"> & { supportedCapabilities: unknown })[];
};

const capabilityLabels = ["Change control", "Verification", "Requirements document", "Code traceability"];

function displayLevel(level: Level) {
  return ({
    System: "System",
    HighLevel: "High-Level software",
    LowLevel: "Low-Level software",
    Customer: "Customer",
    Interface: "Interface",
  } as Record<string, string>)[level] ?? level;
}

function defaultVerificationKinds(level: Level, capabilities: number): VerificationKind[] {
  if ((capabilities & (1 << 1)) === 0) return [];
  if (level === "System") return ["Procedure"];
  if (level === "HighLevel" || level === "LowLevel") return ["Case", "Procedure"];
  return [];
}

function supportedCapabilitiesFor(level: Level, capabilities: number) {
  // Customer and Interface are legitimate ladder levels with their own supported non-verification
  // capabilities. A catalogue that accidentally carries the verification bit must not make this editor
  // offer a discipline the level does not support.
  return level === "Customer" || level === "Interface"
    ? capabilities & ~(1 << 1)
    : capabilities;
}

function normalizeConfiguration(value: ConfigurationResponse): Configuration {
  const normalizeStep = (step: ConfigurationResponse["steps"][number]): Step => ({
    ...step,
    capabilities: supportedCapabilitiesFor(step.catalogueEntry, capabilityMask(step.capabilities)),
    enabledArtifactKinds: (() => {
      const supported = supportedCapabilitiesFor(step.catalogueEntry, capabilityMask(step.capabilities));
      const selected = step.enabledArtifactKinds?.filter((kind): kind is VerificationKind => kind === "Case" || kind === "Procedure") ?? [];
      return selected.length ? selected : defaultVerificationKinds(step.catalogueEntry, supported);
    })(),
  });
  return {
    ...value,
    steps: value.steps.map(normalizeStep),
    effectiveSteps: (value.effectiveSteps ?? []).map(normalizeStep),
    effectiveRelationships: value.effectiveRelationships ?? [],
    catalogue: value.catalogue.map(entry => ({
      ...entry,
      supportedCapabilities: supportedCapabilitiesFor(entry.catalogueEntry, capabilityMask(entry.supportedCapabilities)),
    })),
  };
}

export default function ProjectConfigurationCenter({ user, api, projectId, projectName, initialSection = "ladder", onBackToBuilds, onOpenApprovalConfiguration, onActivated, onSignOut }: {
  user: AuthUser; api: string; projectId: string; projectName: string; onBackToBuilds: () => void;
  initialSection?: "ladder" | "assurance" | "history" | "readiness" | "approvals" | "verification" | "repository" | "inceptionSource";
  onOpenApprovalConfiguration: () => void; onActivated: (configuration: Configuration) => void; onSignOut: () => void;
}) {
  const [configuration, setConfiguration] = useState<Configuration>();
  const [steps, setSteps] = useState<Step[]>([]);
  const [relationships, setRelationships] = useState<Relationship[]>([]);
  const [reason, setReason] = useState("");
  const [section, setSection] = useState<"ladder" | "assurance" | "history" | "readiness" | "approvals" | "verification" | "repository" | "inceptionSource">(initialSection);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [saving, setSaving] = useState(false);
  const [rememberedVerificationProfiles, setRememberedVerificationProfiles] = useState<Record<string, VerificationKind[]>>({});

  // The project's permitted verification methods (#701). Edited here because this decides what every future
  // submission will accept, which is the same authority the ladder above already carries. The stored values
  // that do not match are reported beside it and never rewritten: reconciling one is a controlled change to
  // the record that says it, not a side effect of opening this screen.
  const verification = useVerificationVocabulary(api, projectId);
  const { vocabulary, loading: vocabularyLoading, error: vocabularyError, reload: reloadVocabulary } = verification;
  const [methods, setMethods] = useState<string[]>([]);
  const [newMethod, setNewMethod] = useState("");
  const [vocabularyReason, setVocabularyReason] = useState("");
  useEffect(() => { if (vocabulary) setMethods([...vocabulary.methods]); }, [vocabulary]);
  const vocabularyDirty = !!vocabulary && JSON.stringify(methods) !== JSON.stringify(vocabulary.methods);
  const canManageVocabulary = !!vocabulary?.canManage;
  const moveMethod = (index: number, delta: number) => {
    const next = [...methods]; const target = index + delta; if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]]; setMethods(next);
  };
  const addMethod = () => { if (!newMethod.trim()) return; setMethods([...methods, newMethod.trim()]); setNewMethod(""); };
  const saveVocabulary = async () => {
    // The version and the methods being saved must belong to the project being saved to. The hook keeps the
    // two together; this is the assertion that nothing between here and there separated them (#701).
    if (!vocabulary || verification.projectId !== projectId) {
      setError("The permitted verification methods for this project are still loading. Try again in a moment.");
      return;
    }
    if (!vocabularyReason.trim()) { setError("A meaningful reason is required for every configuration edit."); return; }
    setSaving(true); setError(""); setNotice("");
    try {
      await apiRequest(`${api}/api/projects/${projectId}/verification-methods`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedVersion: vocabulary.version, reason: vocabularyReason, methods }),
      });
      setVocabularyReason("");
      setNotice(`Permitted verification methods saved: ${methods.join(", ")}.`);
      await reloadVocabulary();
    } catch (failure) { setError(operationError(failure, "The verification vocabulary edit was refused.")); }
    finally { setSaving(false); }
  };

  const load = useCallback(async () => {
    try {
      const normalized = normalizeConfiguration(await apiRequest<ConfigurationResponse>(`${api}/api/projects/${projectId}/configuration`));
      setConfiguration(normalized); setSteps(normalized.steps); setRelationships(normalized.relationships); setError("");
    } catch (failure) { setError(operationError(failure, "The project configuration could not be loaded.")); }
  }, [api, projectId]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { setSection(initialSection); }, [initialSection]);

  const dirty = useMemo(() => configuration && (JSON.stringify(steps) !== JSON.stringify(configuration.steps)
    || JSON.stringify(relationships) !== JSON.stringify(configuration.relationships)), [configuration, steps, relationships]);
  const canAuthor = !!configuration?.canManage && configuration.state !== "Active";

  const updateStep = (index: number, patch: Partial<Step>) => setSteps(current => current.map((step, i) => i === index ? { ...step, ...patch } : step));
  const reorder = (index: number, delta: number) => {
    const next = [...steps]; const target = index + delta; if (target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]];
    setSteps(next.map((step, i) => ({ ...step, position: i + 1 })));
  };
  const addStep = () => {
    if (!configuration) return;
    const available = configuration.catalogue.find(entry => !steps.some(step => step.catalogueEntry === entry.catalogueEntry));
    if (!available) return;
    setSteps([...steps, {
      catalogueEntry: available.catalogueEntry,
      position: steps.length + 1,
      capabilities: available.supportedCapabilities,
      enabledArtifactKinds: rememberedVerificationProfiles[available.catalogueEntry]
        ?? defaultVerificationKinds(available.catalogueEntry, available.supportedCapabilities),
    }]);
  };
  const removeStep = (index: number) => {
    const removedStep = steps[index];
    const removed = removedStep.catalogueEntry;
    if (removedStep) {
      setRememberedVerificationProfiles(current => ({
        ...current,
        [removed]: removedStep.enabledArtifactKinds ?? defaultVerificationKinds(removed, removedStep.capabilities),
      }));
    }
    setSteps(steps.filter((_, i) => i !== index).map((step, i) => ({ ...step, position: i + 1 })));
    setRelationships(relationships.filter(edge => edge.parent !== removed && edge.child !== removed));
  };
  const addRelationship = () => {
    if (!configuration) return;
    const existing = new Set(relationships.map(edge => `${edge.parent}>${edge.child}`));
    const candidate = steps.flatMap(parent => steps.map(child => ({ parent: parent.catalogueEntry, child: child.catalogueEntry })))
      .find(edge => edge.parent !== edge.child
        && steps.find(step => step.catalogueEntry === edge.parent)!.position < steps.find(step => step.catalogueEntry === edge.child)!.position
        && !existing.has(`${edge.parent}>${edge.child}`));
    if (!candidate) { setError("Choose two distinct ladder steps in top-to-bottom order for a new relationship."); return; }
    setRelationships([...relationships, candidate]); setError("");
  };
  const save = async () => {
    if (!configuration || !reason.trim()) { setError("A meaningful reason is required for every configuration edit."); return; }
    setSaving(true); setError(""); setNotice("");
    try {
      const value = await apiRequest<ConfigurationResponse>(`${api}/api/projects/${projectId}/configuration`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedVersion: configuration.version, reason, steps, relationships }),
      });
      const normalized = normalizeConfiguration(value);
      setConfiguration(normalized); setSteps(normalized.steps); setRelationships(normalized.relationships); setReason(""); setNotice("Draft configuration saved with immutable history evidence.");
    } catch (failure) { setError(operationError(failure, "The configuration edit was refused.")); }
    finally { setSaving(false); }
  };
  const activate = async () => {
    if (!configuration || !reason.trim()) { setError("A meaningful reason is required for an activation attempt."); return; }
    setSaving(true); setError(""); setNotice("");
    try {
      const value = await apiRequest<ConfigurationResponse>(`${api}/api/projects/${projectId}/configuration/activate`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ expectedVersion: configuration.version, reason })
      });
      const normalized = normalizeConfiguration(value);
      setConfiguration(normalized); setSteps(normalized.steps); setRelationships(normalized.relationships); setReason("");
      setNotice("Ladder activated. Runtime surfaces now use the stored effective ladder.");
      onActivated(normalized);
    }
    catch (failure) { setError(operationError(failure, "Activation was refused.")); }
    finally { setSaving(false); }
  };

  return <div className="projectConfigurationPage">
    <PortalHeader user={user} onSignOut={onSignOut}/>
    <main className="projectConfigurationMain">
      <nav className="projectConfigurationBreadcrumb" aria-label="Breadcrumb"><button type="button" onClick={onBackToBuilds}>Software Builds</button><span aria-hidden="true">/</span><strong>Project configuration</strong></nav>
      <header className="projectConfigurationHeading"><div><p className="projectConfigurationEyebrow">PROJECT CONFIGURATION / {projectName}</p><h1>Project configuration</h1><p>Author the project-owned requirement ladder, review its evidence, and see exactly why activation is or is not ready.</p></div><button type="button" onClick={onBackToBuilds}>← Software Builds</button></header>
      {error && <p className="projectConfigurationError" role="alert">{error}</p>}
      {notice && <p className="projectConfigurationNotice" role="status">{notice}</p>}
      {!configuration ? <p>Loading the stored project ladder…</p> : <div className="projectConfigurationLayout">
        <nav className="projectConfigurationRail" aria-label="Configuration sections">
          <button className={section === "ladder" ? "selected" : ""} onClick={() => setSection("ladder")}>Requirement ladder<small>{configuration.state}</small></button>
          <button className={section === "assurance" ? "selected" : ""} onClick={() => setSection("assurance")}>Assurance policy<small>Declared posture and deviations</small></button>
          <button className={section === "history" ? "selected" : ""} onClick={() => setSection("history")}>History<small>{configuration.history.length} attributed edits</small></button>
          <button className={section === "readiness" ? "selected" : ""} onClick={() => setSection("readiness")}>Activation readiness<small>{configuration.readiness.isReady ? "Ready" : `${configuration.readiness.missingOrUnrouted.length} blockers`}</small></button>
          <button className={section === "approvals" ? "selected" : ""} onClick={() => { setSection("approvals"); onOpenApprovalConfiguration(); }}>Approval configuration<small>Nested project policy</small></button>
          <button className={section === "verification" ? "selected" : ""} onClick={() => setSection("verification")}>Verification methods<small>{vocabularyLoading ? "Loading" : vocabulary ? `${vocabulary.methods.length} permitted${vocabulary.nonConforming.length > 0 ? ` · ${vocabulary.nonConforming.length} off-vocabulary` : ""}` : "Unavailable"}</small></button>
          <button className={section === "repository" ? "selected" : ""} onClick={() => setSection("repository")}>Repository setup<small>Connection and verification</small></button>
          <button className={section === "inceptionSource" ? "selected" : ""} onClick={() => setSection("inceptionSource")}>Source provenance<small>Inherited source facts</small></button>
        </nav>
        <section className="projectConfigurationPanel">
          {section === "approvals" && <ApprovalConfigurationCenter embedded user={user} api={api} projectId={projectId} projectName={projectName} onBackToBuilds={onBackToBuilds} onSignOut={onSignOut} />}
          {section === "repository" && <RepositoryConfigurationPanel api={api} projectId={projectId} projectName={projectName} />}
          {section === "inceptionSource" && <InceptionSourceProvenancePanel api={api} projectId={projectId} projectName={projectName} />}
          {section === "assurance" && <AssurancePolicyPanel api={api} projectId={projectId} />}
          {section === "verification" && <>
            <div className="projectConfigurationPanelHeader"><div><h2>Verification methods</h2><p>The controlled vocabulary requirement authoring offers and review enforces. A change request declaring anything else is refused at submission, naming these values.</p></div><span className="projectConfigurationPill">{vocabularyDirty ? "Unsaved changes" : "Saved"}</span></div>
            {vocabularyLoading && <p>Loading the permitted verification methods…</p>}
            {!!vocabularyError && <p className="projectConfigurationError" role="alert">{vocabularyError}</p>}
            {!vocabularyLoading && !vocabularyError && vocabulary && <>
              <p>Version {vocabulary.version} · {vocabulary.persisted ? "configured for this project" : "not configured yet; these are the methods the product is founded on"}.</p>
              <ol className="ladderRows">{methods.map((method, index) => <li key={`method-${index}`} className="ladderRow"><span className="ladderPosition">{index + 1}</span><label>Method<input value={method} aria-label={`Verification method ${index + 1}`} disabled={!canManageVocabulary} onChange={event => setMethods(methods.map((current, i) => i === index ? event.target.value : current))} /></label><div className="ladderRowActions">{canManageVocabulary && <><button type="button" onClick={() => moveMethod(index, -1)} disabled={index === 0}>↑</button><button type="button" onClick={() => moveMethod(index, 1)} disabled={index === methods.length - 1}>↓</button><button type="button" onClick={() => setMethods(methods.filter((_, i) => i !== index))} disabled={methods.length <= 1}>Remove</button></>}</div></li>)}</ol>
              {canManageVocabulary ? <div className="ladderActions"><label>New method<input value={newMethod} aria-label="New verification method" onChange={event => setNewMethod(event.target.value)} placeholder="e.g. Similarity" onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); addMethod(); } }} /></label><button type="button" onClick={addMethod} disabled={!newMethod.trim()}>Add method</button><label>Reason<input value={vocabularyReason} aria-label="Verification vocabulary reason" onChange={event => setVocabularyReason(event.target.value)} placeholder="Why is this vocabulary changing?" /></label><button type="button" className="primaryProjectConfigurationAction" disabled={saving || !vocabularyDirty} onClick={() => void saveVocabulary()}>Save vocabulary</button></div> : <p className="projectConfigurationNotice">You have read access to this vocabulary. A Configuration Manager, Program Manager, or Administrator must change what this project permits.</p>}
              <div className="relationshipEditor"><h3>Stored values outside the vocabulary</h3><p>Reported for a deliberate decision. Nothing here is rewritten by this screen, by a migration, or by submitting a change request — a historical record keeps saying what it says until a controlled correction changes it.</p>{vocabulary.nonConforming.length === 0 ? <p className="projectConfigurationNotice">Every stored verification method in this project matches the configured vocabulary.</p> : <table className="configurationHistory"><thead><tr><th>Stored value</th><th>Proposals</th><th>Revisions</th><th>Total</th><th>Example requirements</th></tr></thead><tbody>{vocabulary.nonConforming.map(row => <tr key={row.value}><td><code>{row.value}</code></td><td>{row.changeCount}</td><td>{row.revisionCount}</td><td>{row.totalCount}</td><td>{row.examples.join(", ") || "—"}</td></tr>)}</tbody></table>}</div>
            </>}
          </>}
          {section === "ladder" && <>
            <div className="projectConfigurationPanelHeader"><div><h2>Requirement ladder</h2><p>Version {configuration.version} · {configuration.classification} · {configuration.state}. Authored edits remain drafts until the sole activation gate succeeds.</p></div><span className="projectConfigurationPill">{dirty ? "Unsaved changes" : "Saved"}</span></div>
            <ol className="ladderRows">{steps.map((step, index) => {
              const catalogue = configuration.catalogue.find(entry => entry.catalogueEntry === step.catalogueEntry);
              const supported = catalogue?.supportedCapabilities ?? 0;
              const profile = step.enabledArtifactKinds ?? defaultVerificationKinds(step.catalogueEntry, step.capabilities);
              return <li key={`${step.catalogueEntry}-${index}`} className="ladderRow">
                <span className="ladderPosition">{index + 1}</span>
                <label>Level<select value={step.catalogueEntry} disabled={!canAuthor} onChange={event => {
                  const nextCatalogue = configuration.catalogue.find(entry => entry.catalogueEntry === event.target.value);
                  updateStep(index, {
                    catalogueEntry: event.target.value as Level,
                    capabilities: nextCatalogue?.supportedCapabilities ?? 0,
                    enabledArtifactKinds: defaultVerificationKinds(event.target.value, nextCatalogue?.supportedCapabilities ?? 0),
                  });
                }}>{configuration.catalogue.map(entry => <option key={entry.catalogueEntry} value={entry.catalogueEntry}>{displayLevel(entry.catalogueEntry)}</option>)}</select></label>
                <div className="ladderRowOptions">
                  <fieldset disabled={!canAuthor}><legend>Capabilities</legend>{capabilityLabels.map((label, capabilityIndex) => {
                    const allowed = (supported & (1 << capabilityIndex)) !== 0;
                    return <label key={label}><input type="checkbox" checked={allowed && (step.capabilities & (1 << capabilityIndex)) !== 0} disabled={!allowed} onChange={event => updateStep(index, { capabilities: event.target.checked ? step.capabilities | (1 << capabilityIndex) : step.capabilities & ~(1 << capabilityIndex) })}/>{label}</label>
                  })}</fieldset>
                  {(step.catalogueEntry === "HighLevel" || step.catalogueEntry === "LowLevel") && (supported & (1 << 1)) !== 0 && <fieldset className="verificationProfile" disabled={!canAuthor}><legend>Verification profile</legend><select aria-label={`${displayLevel(step.catalogueEntry)} verification profile`} value={profile.includes("Procedure") ? "Case+Procedure" : "Case"} onChange={event => updateStep(index, { enabledArtifactKinds: event.target.value === "Case+Procedure" ? ["Case", "Procedure"] : ["Case"] })}><option value="Case">Case-only</option><option value="Case+Procedure">Case + Procedure</option></select><small>Choose Case-only or Case + Procedure.</small></fieldset>}
                  {step.catalogueEntry === "System" && (supported & (1 << 1)) !== 0 && <p className="verificationProfileFixed">Procedure profile (System)</p>}
                  {(step.catalogueEntry === "Customer" || step.catalogueEntry === "Interface") && <p className="verificationProfileFixed">No verification profile; this level keeps its supported non-verification capabilities.</p>}
                </div>
                <div className="ladderRowActions">{canAuthor && <><button type="button" onClick={() => reorder(index, -1)} disabled={index === 0}>↑</button><button type="button" onClick={() => reorder(index, 1)} disabled={index === steps.length - 1}>↓</button><button type="button" onClick={() => removeStep(index)}>Remove</button></>}</div>
              </li>
            })}</ol>
            {canAuthor ? <div className="ladderActions"><button type="button" onClick={addStep} disabled={steps.length >= configuration.catalogue.length}>Add level</button><label>Reason<input value={reason} onChange={event => setReason(event.target.value)} placeholder="Why is this ladder changing?" /></label><button type="button" className="primaryProjectConfigurationAction" disabled={saving || !dirty} onClick={() => void save()}>Save draft</button><button type="button" disabled={saving} onClick={() => void activate()}>Attempt activation</button></div> : <p className="projectConfigurationNotice">{configuration.state === "Active" ? "This ladder is active and immutable. Its stored manifest is now the runtime authority; author a new configuration revision through the project configuration workflow." : "You have read access to this project configuration. A Configuration Manager, Program Manager, or Administrator must author changes."}</p>}
            <div className="relationshipEditor"><h3>Allowed upstream relationships</h3>{relationships.map((edge, index) => <div className="relationshipRow" key={`${edge.parent}-${edge.child}-${index}`}><select value={edge.parent} disabled={!canAuthor} onChange={event => setRelationships(relationships.map((current, i) => i === index ? { ...current, parent: event.target.value as Level } : current))}>{steps.map(step => <option key={step.catalogueEntry} value={step.catalogueEntry}>{displayLevel(step.catalogueEntry)}</option>)}</select><span>→</span><select value={edge.child} disabled={!canAuthor} onChange={event => setRelationships(relationships.map((current, i) => i === index ? { ...current, child: event.target.value as Level } : current))}>{steps.map(step => <option key={step.catalogueEntry} value={step.catalogueEntry}>{displayLevel(step.catalogueEntry)}</option>)}</select>{canAuthor && <button type="button" onClick={() => setRelationships(relationships.filter((_, i) => i !== index))}>Remove</button>}</div>)}{canAuthor && <button type="button" onClick={addRelationship}>Add relationship</button>}</div>
          </>}
          {section === "history" && <><h2>Immutable edit history</h2><p>Each successful edit records its actor, reason, exact canonical snapshot and hash.</p><table className="configurationHistory"><thead><tr><th>Revision</th><th>Actor</th><th>When</th><th>Reason</th><th>Snapshot</th></tr></thead><tbody>{configuration.history.map(item => <tr key={item.revision}><td>{item.revision}</td><td>{item.actor}</td><td>{new Date(item.occurredAt).toLocaleString()}</td><td>{item.reason}</td><td><details><summary><code>{item.snapshotHash.slice(0, 16)}…</code></summary><code>{item.canonicalSnapshot}</code></details></td></tr>)}</tbody></table></>}
          {section === "readiness" && <><h2>Activation readiness</h2><p>Manifest <code>{configuration.readiness.version}</code> · <code>{configuration.readiness.hash.slice(0, 16)}…</code></p><p className={configuration.readiness.isReady ? "projectConfigurationNotice" : "projectConfigurationError"}>{configuration.readiness.isReady ? "All stable ladder consumers are routed. Activation records the effective manifest atomically." : "Activation remains blocked until every stable consumer is routed."}</p><ul className="readinessList">{configuration.readiness.consumers.map(consumer => <li key={consumer.id}><strong>{consumer.id}</strong><span>{consumer.routed ? "Routed" : "Unrouted"}</span><small>{consumer.description}</small></li>)}</ul></>}
        </section>
      </div>}
    </main>
  </div>;
}
