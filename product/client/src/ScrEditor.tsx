import { useState } from "react";
import type { FormEvent } from "react";
import "./ScrEditor.css";

type RequirementDraft = {
  baseNumber: string;
  revision: number;
  level: "System" | "HighLevel";
  kind: "Introduce" | "Modify" | "Retire";
  statement: string;
  rationale: string;
  verificationMethod: string;
};
type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  onCancel: () => void;
  onSaved: (scrId: string) => void;
};
const blank = (): RequirementDraft => ({
  baseNumber: "SWR-00000001",
  revision: 0,
  level: "HighLevel",
  kind: "Introduce",
  statement: "",
  rationale: "",
  verificationMethod: "Test",
});

export default function ScrEditor({
  api,
  projectId,
  releaseId,
  releaseVersion,
  onCancel,
  onSaved,
}: Props) {
  const [changes, setChanges] = useState<RequirementDraft[]>([blank()]),
    [saving, setSaving] = useState(false),
    [error, setError] = useState("");
  const update = (
    index: number,
    field: keyof RequirementDraft,
    value: string | number,
  ) =>
    setChanges((items) =>
      items.map((x, i) => (i === index ? { ...x, [field]: value } : x)),
    );
  const submit = async (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    setSaving(true);
    setError("");
    const data = new FormData(e.currentTarget);
    const response = await fetch(`${api}/api/scr-drafts`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        baseNumber: data.get("baseNumber"),
        projectId,
        targetReleaseId: releaseId,
        title: data.get("title"),
        problem: data.get("problem"),
        analysis: data.get("analysis"),
        solution: data.get("solution"),
        authorId: data.get("authorId"),
        requirementChanges: changes,
      }),
    });
    if (!response.ok) {
      setError(
        (await response.json()).error || "Unable to save the SCR draft.",
      );
      setSaving(false);
      return;
    }
    const created = await response.json();
    onSaved(created.id);
  };
  return (
    <main className="editorPage">
      <header className="editorHeader">
        <div>
          <button className="back" onClick={onCancel}>
            ← Command Center
          </button>
          <p className="eyebrow">CHANGE CONTROL / NEW SCR</p>
          <h1>Create System Change Request</h1>
          <p>
            Capture the case for change and its proposed requirement revisions.
            The complete SCR will later enter review.
          </p>
        </div>
        <span className="draftPill">DRAFT</span>
      </header>
      <form className="editorForm" onSubmit={submit}>
        <section className="editorCard">
          <div className="sectionTitle">
            <span>01</span>
            <div>
              <h2>Change request identity</h2>
              <p>Stable artifact number and release ownership</p>
            </div>
          </div>
          <div className="fields three">
            <label>
              SCR number
              <input
                name="baseNumber"
                defaultValue="SCR-00000001"
                pattern="[A-Z]+-[0-9]{8}"
                required
              />
              <small>The initial Draft is revision .00.</small>
            </label>
            <label>
              Target release
              <input value={releaseVersion} disabled />
            </label>
            <label>
              Author ID
              <input name="authorId" defaultValue="engineer.demo" required />
            </label>
            <label className="wide">
              Title
              <input
                name="title"
                placeholder="Concise description of the proposed change"
                required
              />
            </label>
          </div>
        </section>
        <section className="editorCard">
          <div className="sectionTitle">
            <span>02</span>
            <div>
              <h2>Problem, analysis, and solution</h2>
              <p>
                The author decides when this complete case is ready for review
              </p>
            </div>
          </div>
          <div className="pas">
            <label>
              <b>Problem</b>
              <textarea
                name="problem"
                placeholder="What operational, product, or assurance need exists?"
                required
              />
            </label>
            <label>
              <b>Analysis</b>
              <textarea
                name="analysis"
                placeholder="What is affected, what alternatives were considered, and what are the consequences?"
                required
              />
            </label>
            <label>
              <b>Solution</b>
              <textarea
                name="solution"
                placeholder="What controlled change is proposed?"
                required
              />
            </label>
          </div>
        </section>
        <section className="editorCard">
          <div className="sectionTitle">
            <span>03</span>
            <div>
              <h2>Proposed requirement changes</h2>
              <p>Requirements remain proposals inside this Draft SCR</p>
            </div>
            <button
              type="button"
              className="secondary"
              onClick={() => setChanges((x) => [...x, blank()])}
            >
              + Add requirement
            </button>
          </div>
          {changes.map((change, index) => (
            <div className="requirement" key={index}>
              <div className="requirementHead">
                <b>Requirement change {index + 1}</b>
                {changes.length > 1 && (
                  <button
                    type="button"
                    onClick={() =>
                      setChanges((x) => x.filter((_, i) => i !== index))
                    }
                  >
                    Remove
                  </button>
                )}
              </div>
              <div className="fields reqgrid">
                <label>
                  Requirement number
                  <input
                    value={change.baseNumber}
                    onChange={(e) =>
                      update(index, "baseNumber", e.target.value)
                    }
                    pattern="[A-Z]+-[0-9]{8}"
                    required
                  />
                </label>
                <label>
                  Level
                  <select
                    value={change.level}
                    onChange={(e) => update(index, "level", e.target.value)}
                  >
                    <option value="System">System</option>
                    <option value="HighLevel">Software HLR</option>
                  </select>
                </label>
                <label>
                  Change type
                  <select
                    value={change.kind}
                    onChange={(e) => update(index, "kind", e.target.value)}
                  >
                    <option value="Introduce">Introduce</option>
                    <option value="Modify">Modify</option>
                    <option value="Retire">Retire</option>
                  </select>
                </label>
                <label>
                  Revision
                  <input
                    type="number"
                    min="0"
                    value={change.revision}
                    onChange={(e) =>
                      update(index, "revision", Number(e.target.value))
                    }
                  />
                </label>
                <label className="wide">
                  Requirement statement
                  <textarea
                    value={change.statement}
                    onChange={(e) => update(index, "statement", e.target.value)}
                    required={change.kind !== "Retire"}
                  />
                </label>
                <label className="half">
                  Rationale
                  <textarea
                    value={change.rationale}
                    onChange={(e) => update(index, "rationale", e.target.value)}
                  />
                </label>
                <label className="half">
                  Verification method
                  <select
                    value={change.verificationMethod}
                    onChange={(e) =>
                      update(index, "verificationMethod", e.target.value)
                    }
                  >
                    <option>Test</option>
                    <option>Analysis</option>
                    <option>Inspection</option>
                    <option>Demonstration</option>
                  </select>
                </label>
              </div>
            </div>
          ))}
        </section>
        {error && <div className="formError">{error}</div>}
        <footer className="editorActions">
          <p>Saving creates an auditable Draft. It does not start approval.</p>
          <div>
            <button type="button" className="secondary" onClick={onCancel}>
              Cancel
            </button>
            <button disabled={saving}>
              {saving ? "Saving Draft…" : "Save SCR Draft"}
            </button>
          </div>
        </footer>
      </form>
    </main>
  );
}
