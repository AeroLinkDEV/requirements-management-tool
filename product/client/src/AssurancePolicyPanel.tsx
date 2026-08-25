import { useCallback, useEffect, useMemo, useState } from "react";
import { apiRequest, operationError } from "./apiClient";

type LeverOption = { value: string; name: string; effect: string; isRelaxation: boolean };
type Lever = {
  lever: string; name: string; description: string; enforcementPoint: string;
  selected: string; selectedName: string; selectedEffect: string;
  recommended: string; recommendedName: string; recommendationBasis: string; basisKind: string;
  deviationClass: string; releaseEffect: string; isRelaxation: boolean; options: LeverOption[];
};
type Deviation = {
  id: string; lever: string; leverName: string; scope: string; recommended: string; recommendationBasis: string;
  basisKind: string; selected: string; rationale: string; deviationClass: string; airworthinessDesignated: boolean;
  proposedBy: string; proposedAt: string; approvedBy: string; approvalAuthority: string;
  approvalAuthoritySource: string; authorityPolicyVersion: number; effectiveFrom: string;
  supersededAt: string | null; supersededBy: string; supersededReason: string; releaseEffect: string;
  recordHash: string; recordVerified: boolean;
};
type PolicyVersion = {
  version: number; declaredLevel: string; reason: string; createdBy: string; effectiveFrom: string;
  supersededAt: string | null; supersededBy: string; snapshotHash: string; selectionsSnapshot: string;
};
type AuthorityRule = {
  deviationClass: string; approvingRoles: string[]; minimumApprovals: number;
  delegationAllowed: boolean; selfApprovalAllowed: boolean;
};
type Policy = {
  projectId: string; version: number; declaredLevel: string; authorityPolicyVersion: number; canManage: boolean;
  mappingNotice: string; claimBoundary: string; levers: Lever[]; deviations: Deviation[];
  history: PolicyVersion[]; authorityRules: AuthorityRule[];
};

type DeviationDraft = { scope: string; rationale: string; airworthinessDesignated: boolean; approverUserName: string };

// The controlled value set a project may declare. Kept in step with the server enum rather than free text:
// a level nobody can spell is a level nobody can enforce, and this is metadata a reviewer reads.
const levels = ["NotDeclared", "LevelA", "LevelB", "LevelC", "LevelD", "LevelE"] as const;
const levelLabel = (value: string) => value === "NotDeclared" ? "Not declared" : value.replace("Level", "Level ");
const basisKindLabel = (value: string) => value === "AeroLinkRule" ? "AeroLink rule" : "Published guidance";
const when = (value: string | null) => value ? new Date(value).toLocaleString() : "—";

export default function AssurancePolicyPanel({ api, projectId }: { api: string; projectId: string }) {
  const [policy, setPolicy] = useState<Policy>();
  const [declaredLevel, setDeclaredLevel] = useState("NotDeclared");
  const [selections, setSelections] = useState<Record<string, string>>({});
  const [drafts, setDrafts] = useState<Record<string, DeviationDraft>>({});
  const [reason, setReason] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [saving, setSaving] = useState(false);

  const adopt = useCallback((value: Policy) => {
    setPolicy(value);
    setDeclaredLevel(value.declaredLevel);
    setSelections(Object.fromEntries(value.levers.map(lever => [lever.lever, lever.selected])));
    setDrafts({});
    setReason("");
  }, []);

  const load = useCallback(async () => {
    try { adopt(await apiRequest<Policy>(`${api}/api/projects/${projectId}/assurance-policy`)); setError(""); }
    catch (failure) { setError(operationError(failure, "The project assurance policy could not be loaded.")); }
  }, [adopt, api, projectId]);
  useEffect(() => { void load(); }, [load]);

  // Which levers the project is newly relaxing. A relaxation already carried by an effective deviation is
  // not asked for again, exactly as the server decides it, so the form never demands an approval the server
  // would then reject as "not a relaxation".
  const newRelaxations = useMemo(() => {
    if (!policy) return [] as Lever[];
    return policy.levers.filter(lever => {
      const selected = selections[lever.lever] ?? lever.selected;
      const option = lever.options.find(x => x.value === selected);
      if (!option?.isRelaxation) return false;
      const carried = policy.deviations.find(x => x.lever === lever.lever && !x.supersededAt);
      return !carried || carried.selected !== selected;
    });
  }, [policy, selections]);

  const dirty = useMemo(() => !!policy && (declaredLevel !== policy.declaredLevel
    || policy.levers.some(lever => (selections[lever.lever] ?? lever.selected) !== lever.selected)), [policy, declaredLevel, selections]);

  const draftFor = (lever: string) => drafts[lever] ?? { scope: "Project", rationale: "", airworthinessDesignated: false, approverUserName: "" };
  const patchDraft = (lever: string, patch: Partial<DeviationDraft>) =>
    setDrafts(current => ({ ...current, [lever]: { ...draftFor(lever), ...patch } }));

  const save = async () => {
    if (!policy) return;
    if (!reason.trim()) { setError("A meaningful reason is required for every assurance policy change."); return; }
    setSaving(true); setError(""); setNotice("");
    try {
      const value = await apiRequest<Policy>(`${api}/api/projects/${projectId}/assurance-policy`, {
        method: "PUT", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          expectedVersion: policy.version,
          declaredLevel,
          reason,
          selections: policy.levers.map(lever => ({ lever: lever.lever, value: selections[lever.lever] ?? lever.selected })),
          deviations: newRelaxations.map(lever => ({ lever: lever.lever, ...draftFor(lever.lever) })),
        }),
      });
      adopt(value);
      setNotice(`Assurance policy version ${value.version} recorded. It governs work started from now on; records already under way keep the snapshot they began with.`);
    } catch (failure) { setError(operationError(failure, "The assurance policy change was refused.")); }
    finally { setSaving(false); }
  };

  if (!policy) return <><h2>Assurance policy</h2><p>{error || "Loading the project's declared assurance policy…"}</p></>;

  return <>
    <div className="projectConfigurationPanelHeader">
      <div>
        <h2>Assurance policy</h2>
        <p>
          Version {policy.version === 0 ? "not yet recorded" : policy.version} · authority rules v{policy.authorityPolicyVersion}.
          This records <strong>the project's declared policy</strong>. {policy.claimBoundary}
        </p>
      </div>
      <span className="projectConfigurationPill">{dirty ? "Unsaved changes" : "Saved"}</span>
    </div>

    {error && <p className="projectConfigurationError" role="alert">{error}</p>}
    {notice && <p className="projectConfigurationNotice" role="status">{notice}</p>}

    <p className="assuranceMappingNotice">{policy.mappingNotice}</p>

    <div className="assuranceLevel">
      <label>
        Declared assurance level
        <select value={declaredLevel} disabled={!policy.canManage}
          onChange={event => setDeclaredLevel(event.target.value)}>
          {levels.map(level => <option key={level} value={level}>{levelLabel(level)}</option>)}
        </select>
      </label>
      <p>
        The level this project declares for itself, recorded as project metadata. It does not select or alter the
        recommendations below, because no certification-derived mapping has been approved for this installation.
        A project spanning components at different levels declares one level and applies it throughout.
      </p>
    </div>

    <h3>Policy levers</h3>
    <ul className="assuranceLevers">
      {policy.levers.map(lever => {
        const selected = selections[lever.lever] ?? lever.selected;
        const option = lever.options.find(x => x.value === selected);
        const relaxing = newRelaxations.some(x => x.lever === lever.lever);
        return <li key={lever.lever} className="assuranceLever">
          <div className="assuranceLeverHead">
            <div>
              <h4>{lever.name}</h4>
              <p>{lever.description}</p>
            </div>
            <label>
              This project
              <select value={selected} disabled={!policy.canManage}
                onChange={event => setSelections(current => ({ ...current, [lever.lever]: event.target.value }))}>
                {lever.options.map(x => <option key={x.value} value={x.value}>{x.name}</option>)}
              </select>
            </label>
          </div>
          <dl className="assuranceLeverFacts">
            <div><dt>AeroLink recommends</dt><dd>{lever.recommendedName}</dd></div>
            <div><dt>Basis kind</dt><dd><span className="assuranceBasisKind">{basisKindLabel(lever.basisKind)}</span></dd></div>
            <div><dt>Enforced at</dt><dd><code>{lever.enforcementPoint}</code></dd></div>
            <div><dt>Deviation authority</dt><dd>{(policy.authorityRules.find(x => x.deviationClass === lever.deviationClass)?.approvingRoles ?? []).join(" or ")}</dd></div>
          </dl>
          <p className="assuranceBasis"><strong>Why:</strong> {lever.recommendationBasis}</p>
          <p className="assuranceEffect"><strong>Effect of this project's setting:</strong> {option?.effect} {option?.isRelaxation ? lever.releaseEffect : ""}</p>
          {relaxing && <fieldset className="assuranceDeviationForm">
            <legend>This is looser than the AeroLink recommendation, so it needs an approved deviation</legend>
            <label>Scope<input value={draftFor(lever.lever).scope} onChange={event => patchDraft(lever.lever, { scope: event.target.value })} /></label>
            <label>Rationale<textarea rows={2} value={draftFor(lever.lever).rationale} placeholder="Why is this project departing from the recommendation?"
              onChange={event => patchDraft(lever.lever, { rationale: event.target.value })} /></label>
            <label>Approving authority (user name)<input value={draftFor(lever.lever).approverUserName}
              placeholder="The person who approves this relaxation"
              onChange={event => patchDraft(lever.lever, { approverUserName: event.target.value })} /></label>
            <label className="assuranceCheckbox"><input type="checkbox" checked={draftFor(lever.lever).airworthinessDesignated}
              onChange={event => patchDraft(lever.lever, { airworthinessDesignated: event.target.checked })} />
              Airworthiness-designated — Airworthiness approval is then required instead</label>
          </fieldset>}
        </li>;
      })}
    </ul>

    {policy.canManage
      ? <div className="ladderActions">
          <label>Reason<input value={reason} onChange={event => setReason(event.target.value)} placeholder="Why is this policy changing?" /></label>
          <button type="button" className="primaryProjectConfigurationAction"
            disabled={saving || !dirty} onClick={() => void save()}>Record policy</button>
        </div>
      : <p className="projectConfigurationNotice">You have read access to this project's declared policy. A Configuration Manager, Program Manager, or Administrator records changes, and a relaxation needs its own approving authority.</p>}

    <h3>Deviations</h3>
    {policy.deviations.length === 0
      ? <p>This project has recorded no deviation from an AeroLink recommendation.</p>
      : <table className="configurationHistory assuranceDeviations">
          <thead><tr><th>Lever</th><th>Scope</th><th>Recommended → selected</th><th>Class</th><th>Rationale</th><th>Approved by</th><th>Effective</th><th>Superseded</th></tr></thead>
          <tbody>{policy.deviations.map(item => <tr key={item.id} className={item.supersededAt ? "assuranceSuperseded" : ""}>
            <td>{item.leverName}</td>
            <td>{item.scope}</td>
            <td>{item.recommended} → <strong>{item.selected}</strong><br/><small>{basisKindLabel(item.basisKind)}</small></td>
            <td>{item.deviationClass}{item.airworthinessDesignated ? " (airworthiness-designated)" : ""}</td>
            <td>{item.rationale}<details><summary>Recommendation basis and release effect</summary><p>{item.recommendationBasis}</p><p>{item.releaseEffect}</p></details></td>
            <td>{item.approvedBy}<br/><small>{item.approvalAuthority} · {item.approvalAuthoritySource} · rules v{item.authorityPolicyVersion}</small><br/><small>proposed by {item.proposedBy}</small></td>
            <td>{when(item.effectiveFrom)}</td>
            <td>{item.supersededAt ? <>{when(item.supersededAt)}<br/><small>{item.supersededReason}</small></> : "In force"}</td>
          </tr>)}</tbody>
        </table>}

    <h3>Policy history</h3>
    {policy.history.length === 0
      ? <p>No assurance policy has been recorded for this project, so every lever sits at its AeroLink recommendation.</p>
      : <table className="configurationHistory assuranceHistory">
          <thead><tr><th>Version</th><th>Declared level</th><th>Reason</th><th>Recorded by</th><th>Effective from</th><th>Superseded</th><th>Snapshot</th></tr></thead>
          <tbody>{policy.history.map(item => <tr key={item.version}>
            <td>{item.version}</td><td>{levelLabel(item.declaredLevel)}</td><td>{item.reason}</td><td>{item.createdBy}</td>
            <td>{when(item.effectiveFrom)}</td><td>{item.supersededAt ? `${when(item.supersededAt)} by ${item.supersededBy}` : "In force"}</td>
            <td><details><summary><code>{item.snapshotHash.slice(0, 16)}…</code></summary><code>{item.selectionsSnapshot}</code></details></td>
          </tr>)}</tbody>
        </table>}

    <h3>Who may approve a deviation</h3>
    <table className="configurationHistory assuranceAuthority">
      <thead><tr><th>Deviation class</th><th>Approving authority</th><th>Approvals</th><th>Delegation</th><th>Self-approval</th></tr></thead>
      <tbody>{policy.authorityRules.map(rule => <tr key={rule.deviationClass}>
        <td>{rule.deviationClass}</td><td>{rule.approvingRoles.join(" or ")}</td><td>{rule.minimumApprovals}</td>
        <td>{rule.delegationAllowed ? "Explicit scoped and dated delegation accepted" : "Not accepted"}</td>
        <td>{rule.selfApprovalAllowed ? "Permitted" : "Prohibited"}</td>
      </tr>)}</tbody>
    </table>
    <p className="assuranceAuthorityNote">
      Administrator access carries no assurance authority on its own; an Administrator may approve only when they
      separately hold the qualifying project role. A Configuration Manager may prepare and record a deviation, but
      that role alone does not authorise the relaxation.
    </p>
  </>;
}
