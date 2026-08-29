import { useCallback, useEffect, useState, type FormEvent } from "react";
import { PersonName } from "./People";
import { stateLabel } from "./presentation";
import {
  authorityLabel,
  authorityToken,
  baseRoleAuthorities,
  leadershipAuthorities,
  parseAuthorityToken,
} from "./workflowAuthorities";
import "./ReviewWorkflowCenter.css";

/**
 * Where a team records how it reviews.
 *
 * Until this existed, a project's review procedure lived in people's heads and was expressed only as the
 * author picking names by hand at submission — so nothing could tell whether a given review had followed it,
 * and an auditor asking "who was supposed to sign this?" had no answer to read.
 *
 * A procedure that has been used is never edited in place. Revising it produces the next version and retires
 * the prior one, which stays retained: a recorded approval has to remain explainable by the rules it was
 * actually judged against.
 *
 * Since the #816 Slice 4 cutover every stage records two independent facts: the required project authority
 * (a base project role, or a Project Leadership position) and what the signature means (Review or Approval).
 * A stage recorded before the cutover reads as legacy authority and, on revision, starts unselected — the
 * new version must be explicit, never a forwarded copy of the old demand.
 */

type Stage = {
  position: number;
  name: string;
  kind: "Review" | "Approval";
  requiredRole: string;
  authorityKind?: "BaseRole" | "LeadershipPosition" | null;
  isLegacy?: boolean;
  requiredAuthority?: { kind: "BaseRole" | "LeadershipPosition" | "LegacyRoleDemand"; role?: string | null; position?: string | null };
};
type Workflow = {
  id: string;
  logicalId: string;
  name: string;
  appliesTo: "System" | "Software";
  mode: "Sequential" | "Parallel";
  version: number;
  state: "Draft" | "Active" | "Retired";
  createdBy: string;
  createdAt: string;
  activatedAt?: string;
  retiredAt?: string;
  stages: Stage[];
};

/** The authority a composing stage demands, kept unselected until the author actually chooses. */
type ComposingStage = {
  name: string;
  kind: "Review" | "Approval";
  authorityKind: "" | "BaseRole" | "LeadershipPosition";
  authorityValue: string;
};

/** A saved stage loads unselected when its authority is legacy or absent: the new version must be explicit. */
const savedAuthority = (stage: Stage): { authorityKind: ComposingStage["authorityKind"]; authorityValue: string } =>
  stage.requiredAuthority?.kind === "BaseRole" && stage.requiredAuthority.role
    ? { authorityKind: "BaseRole", authorityValue: stage.requiredAuthority.role }
    : stage.requiredAuthority?.kind === "LeadershipPosition" && stage.requiredAuthority.position
      ? { authorityKind: "LeadershipPosition", authorityValue: stage.requiredAuthority.position }
      : { authorityKind: "", authorityValue: "" };

const composingComplete = (stage: ComposingStage) =>
  Boolean(stage.name.trim()) && parseAuthorityToken(`${stage.authorityKind}:${stage.authorityValue}`) !== null;

/** What a stage's requirement says. Legacy rows read as history, never as a modern choice. */
const stageAuthorityText = (stage: Stage) =>
  stage.requiredAuthority?.kind === "BaseRole" && stage.requiredAuthority.role
    ? authorityLabel(stage.requiredAuthority.role)
    : stage.requiredAuthority?.kind === "LeadershipPosition" && stage.requiredAuthority.position
      ? `${authorityLabel(stage.requiredAuthority.position)} — Project Leadership`
      : `Legacy authority · ${authorityLabel(stage.requiredRole)}`;

export default function ReviewWorkflowCenter({
  api,
  projectId,
  onBack,
}: {
  api: string;
  projectId: string;
  onBack: () => void;
}) {
  const [workflows, setWorkflows] = useState<Workflow[]>([]);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [composing, setComposing] = useState<{ appliesTo: "System" | "Software"; base?: Workflow }>();

  const load = useCallback(async () => {
    const response = await fetch(`${api}/api/review-workflows?projectId=${projectId}`);
    if (!response.ok) {
      setError("Review workflows could not be loaded.");
      return;
    }
    setWorkflows((await response.json()) as Workflow[]);
  }, [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = async (workflow: Workflow, action: "activate" | "retire", note: string) => {
    setBusy(true);
    setError("");
    setMessage("");
    const response = await fetch(`${api}/api/review-workflows/${workflow.id}/${action}`, { method: "POST" });
    setBusy(false);
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "That could not be done.");
      return;
    }
    setMessage(note);
    await load();
  };

  const active = (type: "System" | "Software") =>
    workflows.find((x) => x.appliesTo === type && x.state === "Active");

  return (
    <main className="workflowPage">
      <header>
        <div>
          <button className="back" onClick={onBack}>
            ← Command Center
          </button>
          <p className="eyebrow">ENTERPRISE CONTROL / REVIEW PROCEDURE</p>
          <h1>Review Workflows</h1>
          <p>
            Record who has to sign a change request, in what authority, and in what order. A change-request
            type with no active workflow keeps free approver selection.
          </p>
        </div>
      </header>

      {error && (
        <div className="workflowError" role="alert">
          {error}
        </div>
      )}
      {message && <div className="workflowMessage">{message}</div>}

      {(["System", "Software"] as const).map((type) => {
        const current = active(type);
        const history = workflows.filter((x) => x.appliesTo === type && x.id !== current?.id);
        return (
          <section className="workflowCard" key={type}>
            <div className="workflowTitle">
              <div>
                <h2>{type} change requests</h2>
                <p>
                  {current
                    ? `Every ${type.toLowerCase()} change request must follow ${current.name} v${current.version}.`
                    : `No procedure is recorded. Authors choose approvers freely.`}
                </p>
              </div>
              <button
                className="primaryAction"
                disabled={busy}
                onClick={() => setComposing({ appliesTo: type, base: current })}
              >
                {current ? "Revise procedure" : "Record a procedure"}
              </button>
            </div>

            {current && (
              <ol className="workflowStages">
                {current.stages.map((stage) => (
                  <li key={stage.position}>
                    <span>{stage.position + 1}</span>
                    <div>
                      <b>{stage.name}</b>
                      <small>
                        {stage.kind === "Approval" ? "Approval signature · " : "Review signature · "}
                        {stageAuthorityText(stage)}
                      </small>
                    </div>
                  </li>
                ))}
                <li className="workflowMode">
                  <span>◆</span>
                  <div>
                    <b>{current.mode === "Parallel" ? "Parallel" : "Sequential"}</b>
                    <small>
                      {current.mode === "Parallel"
                        ? "All stages are authorized together."
                        : "Each stage is authorized when the one before it signs."}
                    </small>
                  </div>
                </li>
              </ol>
            )}

            {current && (
              <div className="workflowActions">
                <button disabled={busy} onClick={() => void act(current, "retire", `${current.name} withdrawn from future use.`)}>
                  Withdraw from use
                </button>
              </div>
            )}

            {history.length > 0 && (
              <details className="workflowHistory">
                <summary>{history.length} other version{history.length === 1 ? "" : "s"} retained</summary>
                <ul>
                  {history.map((item) => (
                    <li key={item.id}>
                      <div>
                        <b>
                          {item.name} <i>v{item.version}</i>
                        </b>
                        <small>
                          {stateLabel(item.state)} · {item.stages.length} stage
                          {item.stages.length === 1 ? "" : "s"} · <PersonName userName={item.createdBy} /> ·{" "}
                          {new Date(item.createdAt).toLocaleDateString()}
                        </small>
                      </div>
                      {item.state === "Draft" && (
                        <button
                          disabled={busy}
                          onClick={() => void act(item, "activate", `${item.name} v${item.version} is now in force.`)}
                        >
                          Put in force
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              </details>
            )}
          </section>
        );
      })}

      {composing && (
        <WorkflowComposer
          api={api}
          projectId={projectId}
          appliesTo={composing.appliesTo}
          base={composing.base}
          onCancel={() => setComposing(undefined)}
          onSaved={async (note) => {
            setComposing(undefined);
            setMessage(note);
            await load();
          }}
        />
      )}
    </main>
  );
}

function WorkflowComposer({
  api,
  projectId,
  appliesTo,
  base,
  onCancel,
  onSaved,
}: {
  api: string;
  projectId: string;
  appliesTo: "System" | "Software";
  base?: Workflow;
  onCancel: () => void;
  onSaved: (note: string) => Promise<void>;
}) {
  const [name, setName] = useState(base?.name ?? `${appliesTo} change board`);
  const [mode, setMode] = useState<"Sequential" | "Parallel">(base?.mode ?? "Sequential");
  const [stages, setStages] = useState<ComposingStage[]>(
    base?.stages.map((x) => ({ name: x.name, kind: x.kind ?? "Review", ...savedAuthority(x) })) ?? [
      { name: "Peer engineering", kind: "Review", authorityKind: "", authorityValue: "" },
      { name: "Configuration approval", kind: "Approval", authorityKind: "", authorityValue: "" },
    ],
  );
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (stages.some((stage) => !composingComplete(stage))) return;
    setBusy(true);
    setError("");
    // Revising leaves the prior version exactly as it was; a completed review must stay explainable by the
    // procedure it was actually judged against.
    const url = base ? `${api}/api/review-workflows/${base.id}/revise` : `${api}/api/review-workflows`;
    const stageRequests = stages.map((stage) => {
      const authority = parseAuthorityToken(`${stage.authorityKind}:${stage.authorityValue}`);
      return {
        name: stage.name,
        kind: stage.kind,
        requiredAuthority:
          authority?.kind === "BaseRole"
            ? { kind: "BaseRole", role: authority.value }
            : { kind: "LeadershipPosition", position: authority?.value },
      };
    });
    const body = base ? { name, mode, stages: stageRequests } : { projectId, name, appliesTo, mode, stages: stageRequests };
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    setBusy(false);
    if (!response.ok) {
      const detail = (await response.json().catch(() => ({}))) as { error?: string };
      setError(detail.error || "The procedure could not be recorded.");
      return;
    }
    const saved = (await response.json()) as Workflow;
    await onSaved(
      `${saved.name} v${saved.version} recorded as a draft. Put it in force when the team has agreed it.`,
    );
  };

  return (
    <div className="workflowModal">
      <form onSubmit={submit}>
        <p className="eyebrow">{base ? `REVISING ${base.name.toUpperCase()} V${base.version}` : "NEW REVIEW PROCEDURE"}</p>
        <h2>{appliesTo} change requests</h2>
        <p className="workflowNote">
          A stage names the authority that has to sign it, not a person — so the procedure survives somebody
          changing jobs. It is saved as a draft; nothing changes for authors until it is put in force.
        </p>

        <label>
          Name
          <input value={name} onChange={(event) => setName(event.target.value)} required />
        </label>

        <fieldset className="workflowMode">
          <legend>Order</legend>
          <label>
            <input
              type="radio"
              name="mode"
              checked={mode === "Sequential"}
              onChange={() => setMode("Sequential")}
            />
            <span>
              <b>Sequential</b>
              <small>Each stage is authorized when the one before it signs.</small>
            </span>
          </label>
          <label>
            <input type="radio" name="mode" checked={mode === "Parallel"} onChange={() => setMode("Parallel")} />
            <span>
              <b>Parallel</b>
              <small>All stages are authorized together.</small>
            </span>
          </label>
        </fieldset>

        <ol className="workflowStageEditor">
          {stages.map((stage, index) => (
            <li key={index}>
              <span>{index + 1}</span>
              {/* Numbered rather than three identical "Stage" fields, so a screen reader announces which
                  stage a control belongs to instead of repeating the same name down the form. */}
              <label>
                Stage {index + 1} name
                <input
                  value={stage.name}
                  onChange={(event) =>
                    setStages((items) => items.map((x, i) => (i === index ? { ...x, name: event.target.value } : x)))
                  }
                  required
                />
              </label>
              <label>
                Stage {index + 1} signature
                <select
                  value={stage.kind}
                  aria-label={`Stage ${index + 1} signature meaning`}
                  onChange={(event) =>
                    setStages((items) =>
                      items.map((x, i) =>
                        i === index ? { ...x, kind: event.target.value as ComposingStage["kind"] } : x,
                      ),
                    )
                  }
                >
                  <option value="Review">Review</option>
                  <option value="Approval">Approval</option>
                </select>
              </label>
              <label>
                Stage {index + 1} required project authority
                <select
                  value={`${stage.authorityKind}:${stage.authorityValue}`}
                  aria-label={`Stage ${index + 1} required project authority`}
                  onChange={(event) => {
                    const parsed = parseAuthorityToken(event.target.value);
                    setStages((items) =>
                      items.map((x, i) =>
                        i === index
                          ? { ...x, authorityKind: parsed?.kind ?? "", authorityValue: parsed?.value ?? "" }
                          : x,
                      ),
                    );
                  }}
                >
                  <option value=":">Choose authority…</option>
                  <optgroup label="Base project roles">
                    {baseRoleAuthorities.map((role) => (
                      <option value={authorityToken("BaseRole", role)} key={`BaseRole:${role}`}>
                        {authorityLabel(role)}
                      </option>
                    ))}
                  </optgroup>
                  <optgroup label="Project Leadership">
                    {leadershipAuthorities.map((position) => (
                      <option value={authorityToken("LeadershipPosition", position)} key={`LeadershipPosition:${position}`}>
                        {`${authorityLabel(position)} — leadership position`}
                      </option>
                    ))}
                  </optgroup>
                </select>
              </label>
              <button
                type="button"
                disabled={stages.length === 1}
                aria-label={`Remove stage ${index + 1}`}
                onClick={() => setStages((items) => items.filter((_, i) => i !== index))}
              >
                Remove
              </button>
            </li>
          ))}
        </ol>

        <button
          type="button"
          className="workflowAddStage"
          onClick={() => setStages((items) => [...items, { name: "", kind: "Review", authorityKind: "", authorityValue: "" }])}
        >
          Add a stage
        </button>

        {error && (
          <div className="workflowError" role="alert">
            {error}
          </div>
        )}

        <div className="workflowModalActions">
          <button type="button" className="cancel" onClick={onCancel}>
            Cancel
          </button>
          <button
            disabled={busy || stages.some((stage) => !composingComplete(stage))}
          >
            {busy ? "Recording…" : "Save as draft"}
          </button>
        </div>
      </form>
    </div>
  );
}
