import { useCallback, useEffect, useState, type FormEvent } from "react";
import { stateLabel } from "./presentation";
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
 */

type Stage = { position: number; name: string; requiredRole: string };
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

const roles = [
  "Engineer",
  "Reviewer",
  "Approver",
  "ConfigurationManager",
  "TestEngineer",
  "TestLead",
  "ProgramManager",
] as const;

const roleLabel = (role: string) =>
  role === "ConfigurationManager"
    ? "Configuration Manager"
    : role === "TestEngineer"
      ? "Test Engineer"
      : role === "TestLead"
        ? "Test Lead"
        : role === "ProgramManager"
          ? "Program Manager"
          : role;

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
                      <small>Signed by a {roleLabel(stage.requiredRole)}</small>
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
                          {item.stages.length === 1 ? "" : "s"} · {item.createdBy} ·{" "}
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
  const [stages, setStages] = useState<{ name: string; requiredRole: string }[]>(
    base?.stages.map((x) => ({ name: x.name, requiredRole: x.requiredRole })) ?? [
      { name: "Peer engineering", requiredRole: "Reviewer" },
      { name: "Configuration management", requiredRole: "ConfigurationManager" },
    ],
  );
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    // Revising leaves the prior version exactly as it was; a completed review must stay explainable by the
    // procedure it was actually judged against.
    const url = base ? `${api}/api/review-workflows/${base.id}/revise` : `${api}/api/review-workflows`;
    const body = base ? { name, mode, stages } : { projectId, name, appliesTo, mode, stages };
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
                Stage {index + 1} signed by
                <select
                  value={stage.requiredRole}
                  onChange={(event) =>
                    setStages((items) =>
                      items.map((x, i) => (i === index ? { ...x, requiredRole: event.target.value } : x)),
                    )
                  }
                >
                  {roles.map((role) => (
                    <option value={role} key={role}>
                      {roleLabel(role)}
                    </option>
                  ))}
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
          onClick={() => setStages((items) => [...items, { name: "", requiredRole: "Approver" }])}
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
          <button disabled={busy}>{busy ? "Recording…" : "Save as draft"}</button>
        </div>
      </form>
    </div>
  );
}
