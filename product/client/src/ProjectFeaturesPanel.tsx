import { useEffect, useState } from "react";
import { apiRequest, operationError } from "./apiClient";
import { FEATURE_DESCRIPTIONS as descriptions, featureDependencyNote as dependencyNote, type ProjectFeature, type ProjectFeatureProjection } from "./projectFeatures";

/**
 * Project Configuration → Features (#1113). A feature holding records cannot be switched off; switching
 * one on is always allowed. Command Center and My Work are always present.
 */
export default function ProjectFeaturesPanel({ api, projectId, onChanged }: {
  api: string;
  projectId: string;
  onChanged: (enabled: ProjectFeature[]) => void;
}) {
  const [data, setData] = useState<ProjectFeatureProjection>();
  const [draft, setDraft] = useState<Set<ProjectFeature>>(new Set());
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [saving, setSaving] = useState(false);
  const accept = (next: ProjectFeatureProjection) => { setData(next); setDraft(new Set(next.enabled)); };
  useEffect(() => {
    let live = true;
    fetch(`${api}/api/projects/${projectId}/features`)
      .then(async (response) => { if (!response.ok) throw new Error(); return (await response.json()) as ProjectFeatureProjection; })
      .then((next) => { if (live) accept(next); })
      .catch(() => { if (live) setError("The project's features could not be loaded. Refresh to try again."); });
    return () => { live = false; };
  }, [api, projectId]);
  if (!data) return error ? <p className="projectConfigurationError">{error}</p> : <p>Loading features…</p>;
  const dirty = data.enabled.length !== draft.size || data.enabled.some((x) => !draft.has(x));
  const note = dependencyNote(draft);
  const toggle = (id: ProjectFeature) => {
    const next = new Set(draft);
    if (next.has(id)) next.delete(id); else next.add(id);
    setDraft(next); setNotice(""); setError("");
  };
  const save = async () => {
    setSaving(true); setError(""); setNotice("");
    try {
      const body = await apiRequest<ProjectFeatureProjection>(`${api}/api/projects/${projectId}/features`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ expectedVersion: data.version, reason, enabled: data.features.map((x) => x.id).filter((x) => draft.has(x)) }),
      });
      accept(body);
      setReason("");
      setNotice("Saved. The navigation now shows only the enabled features.");
      onChanged(body.enabled);
    } catch (failure) {
      setError(operationError(failure, "The features could not be saved."));
    } finally {
      setSaving(false);
    }
  };
  return (
    <div className="projectFeatures">
      <div className="projectConfigurationPanelHeader">
        <div>
          <h2>Features</h2>
          <p>Choose which parts of AeroLink this project uses. Command Center and My Work are always present and show what the enabled features contribute. A feature that already holds records cannot be switched off.</p>
        </div>
        <span className="projectConfigurationPill">{dirty ? "Unsaved changes" : data.persisted ? `Version ${data.version}` : "Default"}</span>
      </div>
      <ul className="projectFeatureList">
        {data.features.map((feature) => {
          const on = draft.has(feature.id);
          const locked = !data.canManage || (feature.enabled && feature.hasRecords);
          return (
            <li key={feature.id} className={on ? "on" : "off"}>
              <label>
                <input type="checkbox" checked={on} disabled={locked} onChange={() => toggle(feature.id)} aria-describedby={`feature-${feature.id}`} />
                <b>{feature.label}</b>
              </label>
              <small id={`feature-${feature.id}`}>
                {descriptions[feature.id]}
                {feature.enabled && feature.hasRecords && <em> Holds records, so it stays on.</em>}
              </small>
            </li>
          );
        })}
      </ul>
      {note && <p className="projectConfigurationError" role="alert">{note}</p>}
      {data.canManage ? (
        <div className="ladderActions">
          <label>Reason<input value={reason} aria-label="Reason for changing features" onChange={(event) => setReason(event.target.value)} placeholder="Why is this project's feature set changing?" /></label>
          <button type="button" className="primaryProjectConfigurationAction" disabled={saving || !dirty || !reason.trim() || !!note} onClick={() => void save()}>Save features</button>
        </div>
      ) : (
        <p className="projectConfigurationNotice">You can read this project's features. A Configuration Manager, Program Manager or Administrator changes them.</p>
      )}
      {error && <p className="projectConfigurationError" role="alert">{error}</p>}
      {notice && <p className="projectConfigurationNotice" role="status">{notice}</p>}
      {data.history.length > 0 && (
        <table className="configurationHistory">
          <thead><tr><th>Version</th><th>Actor</th><th>When</th><th>Enabled</th><th>Reason</th></tr></thead>
          <tbody>
            {data.history.map((row) => (
              <tr key={row.version}>
                <td>{row.version}</td><td>{row.actor}</td><td>{new Date(row.occurredAt).toLocaleString()}</td>
                <td>{row.enabled.map((id) => data.features.find((x) => x.id === id)?.label ?? id).join(", ") || "None"}</td>
                <td>{row.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
