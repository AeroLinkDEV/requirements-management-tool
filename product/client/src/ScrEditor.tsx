import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import type { AuthUser } from "./IdentityCenter";
import ControlledRequirementEditor from "./ControlledRequirementEditor";
import RequirementsImportPanel from "./RequirementsImportPanel";
import type {
  ControlledRequirementDraft,
  RequirementKind,
  RequirementLevel,
} from "./ControlledRequirementEditor";
import { RichCaseField } from "./RichContent";
import { AutosaveState, DraftRestore } from "./DraftNotice";
import { useLocalDraft } from "./autosave";
import { fromPlainText, toPlainText } from "./richContentModel";
import "./ScrEditor.css";
import "./ScrEditorEnhancements.css";

type ChangeScope = "System" | "Software";
type AuthoringContext = {
  type: ChangeScope;
  changeRequestNumber: string;
  author: { userName: string; displayName: string };
  requirementNumbers: Partial<Record<"SYSR" | "HLR" | "LLR", string>>;
};
type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  scope: ChangeScope;
  user: AuthUser;
  sourceRequirementId?: string;
  onCancel: () => void;
  onSaved: (scrId: string, displayNumber: string) => void;
};
type SavedDraft = {
  title: string;
  problem: string;
  analysis: string;
  solution: string;
  changes: ControlledRequirementDraft[];
  // Autosaved drafts written before the change case could carry structure hold only the plain fields
  // above. They restore as paragraphs rather than being discarded.
  problemRich?: string;
  analysisRich?: string;
  solutionRich?: string;
};

const pendingImpact = JSON.stringify({
  trace: "Pending",
  verification: "Pending",
  documents: "Pending",
  baseline: "Pending",
  collaboration: "Pending",
});
const impactKeys = [
  "trace",
  "verification",
  "documents",
  "baseline",
  "collaboration",
] as const;
const prefixFor = (level: RequirementLevel) =>
  level === "System" ? "SYSR" : level === "HighLevel" ? "HLR" : "LLR";
const addToIdentifier = (identifier: string | undefined, offset: number) => {
  if (!identifier) return "";
  const match = identifier.match(/^([A-Z]+)-(\d+)$/);
  if (!match) return identifier;
  return `${match[1]}-${(Number(match[2]) + offset).toString().padStart(6, "0")}`;
};
const parseObject = (value: string | undefined): Record<string, unknown> => {
  try {
    return JSON.parse(value || "{}") as Record<string, unknown>;
  } catch {
    return {};
  }
};
const impactsComplete = (item: ControlledRequirementDraft) => {
  const values = parseObject(item.impactDispositionJson);
  return impactKeys.every((key) => values[key] && values[key] !== "Pending");
};
const createProposal = (
  level: RequirementLevel,
  kind: RequirementKind,
  baseNumber = "",
): ControlledRequirementDraft => ({
  baseNumber,
  revision: 0,
  level,
  kind,
  statement: "",
  rationale: "",
  verificationMethod: "Test",
  richText: "",
  attributesJson: JSON.stringify({ criticality: "Normal", owner: "" }),
  impactDispositionJson: pendingImpact,
  isDerived: false,
  // Empty means unchanged: leave a modified requirement where it is, and let the existing placement rule decide
  // where a newly introduced one goes.
  targetSectionId: "",
});
const normalizeProposal = (
  value: Partial<ControlledRequirementDraft>,
  fallbackLevel: RequirementLevel,
): ControlledRequirementDraft => ({
  ...createProposal(fallbackLevel, value.kind || "Introduce"),
  ...value,
  baseNumber: value.baseNumber === "Assigned when saved" ? "" : value.baseNumber || "",
  level: value.level || fallbackLevel,
  richText: value.richText || value.statement || "",
  attributesJson: value.attributesJson || JSON.stringify({ criticality: "Normal", owner: "" }),
  impactDispositionJson:
    value.impactDispositionJson && value.impactDispositionJson !== "{}"
      ? value.impactDispositionJson
      : pendingImpact,
});

export default function ScrEditor({
  api,
  projectId,
  releaseId,
  releaseVersion,
  scope,
  user,
  sourceRequirementId,
  onCancel,
  onSaved,
}: Props) {
  const abbreviation = scope === "System" ? "SCR" : "SWCR";
  const defaultLevel: RequirementLevel = scope === "System" ? "System" : "HighLevel";
  const storageKey = `aerolink:new-${scope.toLowerCase()}-change:${projectId}:${releaseId}`;
  const seededSource = useRef("");
  const [context, setContext] = useState<AuthoringContext>();
  // Nothing is seeded from a stored draft. It is offered below, and applied only if the author says so.
  const [title, setTitle] = useState("");
  const [problemRich, setProblemRich] = useState(fromPlainText(""));
  const [analysisRich, setAnalysisRich] = useState(fromPlainText(""));
  const [solutionRich, setSolutionRich] = useState(fromPlainText(""));
  // The plain form is derived, never typed alongside. Keeping two independently editable copies of the
  // change case is how a document ends up saying something its record does not.
  const problem = toPlainText(problemRich);
  const analysis = toPlainText(analysisRich);
  const solution = toPlainText(solutionRich);
  // Nothing is assumed. A pre-seeded Introduce proposal decided what this change was before the author had
  // said, and because it arrived with an identifier already allocated it counted as identity-locked — so it
  // could not be turned into a Modify or a Retire either. The author chooses the first change.
  const [changes, setChanges] = useState<ControlledRequirementDraft[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");

  // Held in this browser, because the change request does not exist on the server yet and reserving one
  // would consume an identifier for something nobody submitted.
  const draft = useLocalDraft<SavedDraft>(
    storageKey,
    { title, problem, analysis, solution, changes, problemRich, analysisRich, solutionRich },
    { isEmpty: (value) => !value.title.trim() && !value.problem.trim() && !value.analysis.trim() && !value.solution.trim() },
  );

  const applyDraft = () => {
    const saved = draft.offered?.value;
    draft.restore();
    if (!saved) return;
    setTitle(saved.title || "");
    setProblemRich(saved.problemRich || fromPlainText(saved.problem || ""));
    setAnalysisRich(saved.analysisRich || fromPlainText(saved.analysis || ""));
    setSolutionRich(saved.solutionRich || fromPlainText(saved.solution || ""));
    if (saved.changes?.length) setChanges(saved.changes.map((item) => normalizeProposal(item, defaultLevel)));
  };

  useEffect(() => {
    let cancelled = false;
    fetch(`${api}/api/authoring/context?projectId=${projectId}&type=${scope}`)
      .then(async (response) => {
        if (!response.ok) {
          const body = (await response.json()) as { error?: string };
          throw new Error(body.error || "Authoring context unavailable.");
        }
        return response.json() as Promise<AuthoringContext>;
      })
      .then((value) => {
        if (!cancelled) setContext(value);
      })
      .catch((reason: unknown) => {
        if (!cancelled)
          setError(reason instanceof Error ? reason.message : "Authoring context unavailable.");
      });
    return () => {
      cancelled = true;
    };
  }, [api, projectId, scope]);

  useEffect(() => {
    if (!sourceRequirementId || seededSource.current === sourceRequirementId) return;
    seededSource.current = sourceRequirementId;
    fetch(`${api}/api/enterprise-requirements/${sourceRequirementId}`)
      .then(async (response) => {
        if (!response.ok) throw new Error("The selected requirement could not be loaded into this change.");
        return response.json() as Promise<{
          baseNumber: string;
          level: RequirementLevel;
          history: { revision: number; displayNumber: string; statement: string; rationale: string; verificationMethod: string; richText?: string; attributesJson?: string }[];
        }>;
      })
      .then((source) => {
        const latest = source.history[0];
        if (!latest) throw new Error("The selected requirement has no controlled revision.");
        setChanges((items) => items.some((item) => item.baseNumber === source.baseNumber)
          ? items
          : [...items, normalizeProposal({
              baseNumber: source.baseNumber,
              revision: latest.revision + 1,
              level: source.level,
              kind: "Modify",
              statement: latest.statement,
              rationale: latest.rationale,
              verificationMethod: latest.verificationMethod,
              richText: latest.richText || latest.statement,
              attributesJson: latest.attributesJson || "{}",
            }, defaultLevel)]);
        setTitle((value) => value || `Update ${latest.displayNumber} through controlled change`);
      })
      .catch((reason: unknown) => setError(reason instanceof Error ? reason.message : "The selected requirement could not be loaded."));
  }, [api, defaultLevel, sourceRequirementId]);

  useEffect(() => {
    if (!context) return;
    setChanges((items) => {
      const used: Record<string, number> = {};
      return items.map((item) => {
        if (item.kind !== "Introduce") return item;
        const prefix = prefixFor(item.level);
        const offset = used[prefix] || 0;
        used[prefix] = offset + 1;
        return item.baseNumber
          ? item
          : { ...item, baseNumber: addToIdentifier(context.requirementNumbers[prefix], offset) };
      });
    });
  }, [context]);

  const nextIdentifier = (level: RequirementLevel) => {
    const prefix = prefixFor(level);
    const count = changes.filter(
      (item) => item.kind === "Introduce" && prefixFor(item.level) === prefix,
    ).length;
    return addToIdentifier(context?.requirementNumbers[prefix], count);
  };
  const addProposal = (kind: RequirementKind, level: RequirementLevel) =>
    setChanges((items) => [
      ...items,
      createProposal(level, kind, kind === "Introduce" ? nextIdentifier(level) : ""),
    ]);

  /**
   * Changes what a proposal *does* to a requirement, after the card exists.
   *
   * The identifier means a different thing for each kind, which is why this is not a field update. An
   * introduced requirement is allocated the next free number here and now; a modified or retired one names a
   * requirement that already exists and has to be chosen from the repository. Carrying the identifier across a
   * kind change would either claim to modify a number nothing has been given yet, or silently keep an
   * allocation nobody asked for — so the identity is re-derived from the new kind, and clearing it is what
   * makes the requirement lookup appear.
   *
   * Retiring keeps no statement: the wording that stands is the one already in the baseline, and a proposal
   * that both retires a requirement and restates it is two different intentions in one row.
   */
  const changeKind = (index: number, kind: RequirementKind) =>
    setChanges((items) =>
      items.map((item, position) => {
        if (position !== index || item.kind === kind) return item;
        return {
          ...item,
          kind,
          baseNumber: kind === "Introduce" ? nextIdentifier(item.level) : "",
          revision: 0,
          statement: kind === "Retire" ? "" : item.statement,
          richText: kind === "Retire" ? "" : item.richText,
        };
      }),
    );
  const updateProposal = (
    index: number,
    key: keyof ControlledRequirementDraft,
    value: string | number | boolean,
  ) =>
    setChanges((items) =>
      items.map((item, position) => {
        if (position !== index) return item;
        const next = { ...item, [key]: value } as ControlledRequirementDraft;
        // While supporting content is nothing more than a restatement of the statement, it follows the
        // statement. The moment an author writes a table or a figure, it stops following and is theirs.
        if (key === "statement" && (!item.richText || toPlainText(item.richText) === item.statement)) {
          next.richText = fromPlainText(String(value));
        }
        return next;
      }),
    );

  const caseComplete = [title, problem, analysis, solution].every((value) => value.trim());
  const proposalsComplete =
    changes.length > 0 &&
    changes.every(
      (item) =>
        item.baseNumber &&
        (item.kind === "Retire" || item.statement.trim()) &&
        (!(item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
          item.rationale.trim()),
    );
  const impactCount = changes.filter(impactsComplete).length;
  const reviewReady = caseComplete && proposalsComplete && impactCount === changes.length;

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!title.trim()) {
      setError("Give this change request a title before saving it as a Draft.");
      return;
    }
    // An untouched proposal card is a blank form row, not a proposal, so it is not sent. A *partly* filled one
    // is somebody's work: the domain will not accept a requirement change with no statement, so rather than
    // drop what they typed, say which card needs a statement. Without this split, saving early failed with
    // "A requirement statement is required" from the pre-seeded empty card and named no card at all.
    const started = changes.filter((item) =>
      [item.baseNumber, item.statement, item.rationale, item.verificationMethod].some((value) => value.trim()),
    );
    const missingStatement = started.filter(
      (item) => item.kind !== "Retire" && !item.statement.trim(),
    );
    if (missingStatement.length) {
      setError(
        `Add a statement to ${missingStatement.map((item) => item.baseNumber || "the new requirement").join(", ")}, or clear the card, before saving this Draft.`,
      );
      return;
    }
    setSaving(true);
    setError("");
    try {
      const response = await fetch(`${api}/api/scr-drafts`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          projectId,
          targetReleaseId: releaseId,
          title,
          problem,
          analysis,
          solution,
          problemRich,
          analysisRich,
          solutionRich,
          type: scope,
          // An unset section is sent as null, not as "". A Guid? will not bind an empty string, and the failure
          // would be a 400 on the whole change request because one optional field was left alone.
          requirementChanges: started.map((item) => ({ ...item, targetSectionId: item.targetSectionId || null })),
        }),
      });
      if (!response.ok) {
        const body = (await response.json()) as { error?: string };
        throw new Error(body.error || `Unable to save the ${abbreviation} Draft.`);
      }
      const created = (await response.json()) as { id: string; displayNumber: string };
      // The record exists now, so the browser copy has nothing left to protect.
      draft.clear();
      onSaved(created.id, created.displayNumber);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : `Unable to save the ${abbreviation} Draft.`);
      setSaving(false);
    }
  };

  return (
    <main className="editorPage">
      <header className="editorHeader">
        <div>
          <button className="back" type="button" onClick={onCancel}>
            ← Command Center
          </button>
          <p className="eyebrow">{scope.toUpperCase()} CHANGE CONTROL / NEW {abbreviation}</p>
          <h1>Create {scope} Change Request</h1>
          <p>
            Build the case, define controlled requirement changes, and close impact decisions before review.
          </p>
          {sourceRequirementId && (
            <div className="sourceHandoff">
              <b>Started from Requirements Explorer</b>
              <span>The authoritative requirement remains read-only. Its proposed successor is being prepared in this Draft {abbreviation}.</span>
            </div>
          )}
        </div>
        <div className="editorStatus">
          <span className="draftPill">DRAFT</span>
          <AutosaveState status={draft.status} savedAt={draft.savedAt} where="this browser" />
        </div>
      </header>

      {draft.offered && (
        <DraftRestore
          savedAt={draft.offered.savedAt}
          description={`An unfinished ${abbreviation} was left in this browser. Nothing was submitted.`}
          onRestore={applyDraft}
          onDiscard={draft.discard}
        />
      )}

      <RequirementsImportPanel
        api={api}
        projectId={projectId}
        releaseId={releaseId}
        scope={scope}
        onCreated={(id) => {
          draft.clear();
          onSaved(id, "The change request");
        }}
      />

      <nav className="authoringStages" aria-label="Change authoring progress">
        <a href="#change-case" className={caseComplete ? "complete" : "active"}>
          <span>1</span><b>Change case</b><small>{caseComplete ? "Complete" : "In progress"}</small>
        </a>
        <a href="#requirement-changes" className={proposalsComplete ? "complete" : caseComplete ? "active" : ""}>
          <span>2</span><b>Requirement changes</b><small>{proposalsComplete ? "Complete" : `${changes.length} proposal${changes.length === 1 ? "" : "s"}`}</small>
        </a>
        <a href="#impact-readiness" className={reviewReady ? "complete" : proposalsComplete ? "active" : ""}>
          <span>3</span><b>Impact & readiness</b><small>{reviewReady ? "Review ready" : `${impactCount}/${changes.length} impacts closed`}</small>
        </a>
      </nav>

      <form className="editorForm" onSubmit={submit}>
        <section className="editorCard authoringStage" id="change-case">
          <div className="sectionTitle">
            <span>01</span>
            <div>
              <h2>Change case</h2>
              <p>Identity, ownership, and the complete engineering reason for change</p>
            </div>
            <i className={caseComplete ? "stageState complete" : "stageState"}>
              {caseComplete ? "Complete" : "Required"}
            </i>
          </div>
          <div className="fields three identityFields">
            <label>
              {abbreviation} number
              <input value={context?.changeRequestNumber || "Calculating next number…"} readOnly />
              <small>Previewed here; assigned atomically by the server on save.</small>
            </label>
            <label>
              Target release
              <input value={releaseVersion} readOnly />
            </label>
            <label>
              Author
              <input
                value={`${context?.author.displayName || user.displayName} (${context?.author.userName || user.userName})`}
                readOnly
              />
              <small>Derived from the authenticated session.</small>
            </label>
            <label className="wide">
              Title
              <input
                value={title}
                onChange={(event) => setTitle(event.target.value)}
                placeholder="A concise, decision-ready description"
                required
              />
            </label>
          </div>
          <div className="pas">
            <RichCaseField api={api} projectId={projectId} label="Problem" value={problemRich} onChange={setProblemRich}
              placeholder="What need, defect, or risk exists?" />
            <RichCaseField api={api} projectId={projectId} label="Analysis" value={analysisRich} onChange={setAnalysisRich}
              placeholder="What is affected and what alternatives were considered?" />
            <RichCaseField api={api} projectId={projectId} label="Solution" value={solutionRich} onChange={setSolutionRich}
              placeholder="What controlled outcome is proposed?" />
          </div>
        </section>

        <section className="editorCard authoringStage" id="requirement-changes">
          <div className="sectionTitle proposalSectionTitle">
            <span>02</span>
            <div>
              <h2>Requirement changes</h2>
              <p>Each proposal receives an authoritative identifier, level, revision, and change type</p>
            </div>
            <i className={proposalsComplete ? "stageState complete" : "stageState"}>
              {proposalsComplete ? "Complete" : "Needs content"}
            </i>
          </div>
          <div className="proposalActions" aria-label="Add requirement proposal">
            <span>Add a focused proposal:</span>
            {scope === "System" ? (
              <>
                <button type="button" onClick={() => addProposal("Introduce", "System")}>+ Introduce System requirement</button>
                <button type="button" onClick={() => addProposal("Modify", "System")}>Modify existing</button>
                <button type="button" onClick={() => addProposal("Retire", "System")}>Retire existing</button>
              </>
            ) : (
              <>
                <button type="button" onClick={() => addProposal("Introduce", "HighLevel")}>+ Introduce HLR</button>
                <button type="button" onClick={() => addProposal("Introduce", "LowLevel")}>+ Introduce LLR</button>
                <button type="button" onClick={() => addProposal("Modify", "HighLevel")}>Modify existing</button>
                <button type="button" onClick={() => addProposal("Retire", "HighLevel")}>Retire existing</button>
              </>
            )}
          </div>
          <div className="proposalStack">
            {changes.map((change, index) => (
              <ControlledRequirementEditor
                api={api}
                projectId={projectId}
                scope={scope}
                item={change}
                index={index}
                key={`${index}-${change.kind}`}
                identityLocked={Boolean(change.baseNumber)}
                onChange={(key, value) => updateProposal(index, key, value)}
                onKindChange={(kind) => changeKind(index, kind)}
                onRemove={() => setChanges((items) => items.filter((_, position) => position !== index))}
              />
            ))}
          </div>
          {!changes.length && (
            <div className="emptyProposals">
              <b>Choose the first requirement change</b>
              <p>
                Introduce a new requirement, modify one that exists, or retire one. Add the smallest controlled
                set needed to deliver this change — you can change what a proposal does after adding it.
              </p>
            </div>
          )}
        </section>

        <section className="editorCard readinessStage" id="impact-readiness">
          <div className="sectionTitle">
            <span>03</span>
            <div>
              <h2>Impact & review readiness</h2>
              <p>A Draft may be saved now; every impact decision must be explicit before review</p>
            </div>
            <i className={reviewReady ? "stageState complete" : "stageState"}>
              {reviewReady ? "Review ready" : "Draft only"}
            </i>
          </div>
          <div className="readinessGrid">
            <article className={caseComplete ? "complete" : ""}>
              <span>{caseComplete ? "✓" : "1"}</span><div><b>Change case</b><p>{caseComplete ? "Decision context is complete." : "Complete title, problem, analysis, and solution."}</p></div>
            </article>
            <article className={proposalsComplete ? "complete" : ""}>
              <span>{proposalsComplete ? "✓" : "2"}</span><div><b>Requirement proposals</b><p>{proposalsComplete ? `${changes.length} controlled proposal${changes.length === 1 ? " is" : "s are"} complete.` : "Select identities and complete required proposal content."}</p></div>
            </article>
            <article className={impactCount === changes.length && changes.length ? "complete" : ""}>
              <span>{impactCount === changes.length && changes.length ? "✓" : "3"}</span><div><b>Lifecycle impact</b><p>{impactCount}/{changes.length} proposals have all five decisions closed.</p></div>
            </article>
          </div>
          <div className={reviewReady ? "nextAction ready" : "nextAction"}>
            <div>
              <b>{reviewReady ? "Ready for controlled check-in" : "Next action"}</b>
              <p>{reviewReady ? "Save this Draft, confirm the checked-in snapshot, then assign review authority." : !caseComplete ? "Finish the change case before creating the Draft." : !proposalsComplete ? "Complete each requirement proposal." : "Close the remaining impact decisions in the proposal cards above."}</p>
            </div>
            <span>{reviewReady ? "REVIEW READY" : "DRAFT PATH"}</span>
          </div>
        </section>

        {error && <div className="formError" role="alert">{error}</div>}
        <footer className="editorActions">
          <p>
            Saving creates an attributable server-side Draft. Review begins only after check-in and reviewer selection.
          </p>
          <div>
            <button type="button" className="secondary" onClick={onCancel}>Cancel</button>
            {/* A title is all this needs, because a Draft is the thing you save when the work is *not*
                finished — that is what makes it a draft. Requiring a complete change case and every proposal
                closed meant the button stayed dead through the whole of authoring and only lit up at the point
                you no longer needed it, so there was nowhere to put work down. Completeness is still required,
                but by the review gate below, which is the decision it actually belongs to. */}
            <button disabled={saving || !context || !title.trim()}>
              {saving ? "Saving Draft…" : `Save ${abbreviation} Draft`}
            </button>
          </div>
        </footer>
      </form>
    </main>
  );
}

