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
import { changeRequestAcronym } from "./presentation";
import { AutosaveState, DraftRestore } from "./DraftNotice";
import { useLocalDraft } from "./autosave";
import { fromPlainText, toPlainText } from "./richContentModel";
import { apiRequest, operationError, recordClientOperationFailure } from "./apiClient";
import { LadderCapability, ladderAllows } from "./projectLadder";
import type { ProjectLadderProjection } from "./projectLadder";
import ProblemReportPicker from "./ProblemReportPicker";
import "./ChangeRequestEditor.css";
import "./ChangeRequestEditorEnhancements.css";

type ChangeScope = "System" | "Software" | "Interface";
type AuthoringContext = {
  type: ChangeScope;
  changeRequestNumber: string;
  author: { userName: string; displayName: string };
  requirementNumbers: Partial<Record<"SYSR" | "HLR" | "LLR" | "ICDR", string>>;
};
type Props = {
  api: string;
  projectId: string;
  releaseId: string;
  releaseVersion: string;
  scope: ChangeScope;
  softwareLevel?: "HighLevel" | "LowLevel";
  ladder: ProjectLadderProjection | null;
  user: AuthUser;
  sourceRequirementId?: string;
  onCancel: () => void;
  onSaved: (changeRequestId: string, displayNumber: string) => void;
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
  problemReportIds?: string[];
};
type ValidationError = { kind: "title" | "proposal"; message: string };

const pendingImpact = JSON.stringify({
  trace: "Pending",
  verification: "Pending",
  documents: "Pending",
  baseline: "Pending",
  collaboration: "Pending",
});
const prefixFor = (level: RequirementLevel) =>
  level === "System" ? "SYSR" : level === "HighLevel" ? "HLR" : level === "LowLevel" ? "LLR" : "ICDR";
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
  verificationMethod: level === "Interface" ? "Not applicable" : "Test",
  richText: "",
  attributesJson: JSON.stringify({ criticality: "Normal", owner: "" }),
  impactDispositionJson: pendingImpact,
  isDerived: false,
  // Empty means unchanged: leave a modified requirement where it is, and let the existing placement rule decide
  // where a newly introduced one goes.
  targetSectionId: "",
  upstreamRevisionIds: [],
});
const normalizeProposal = (
  value: Partial<ControlledRequirementDraft>,
  fallbackLevel: RequirementLevel,
): ControlledRequirementDraft => ({
  ...createProposal(fallbackLevel, value.kind || "Introduce"),
  ...value,
  baseNumber: value.baseNumber === "Assigned when saved" ? "" : value.baseNumber || "",
  level: value.level || fallbackLevel,
  richText: value.richText || "",
  attributesJson: value.attributesJson || JSON.stringify({ criticality: "Normal", owner: "" }),
  impactDispositionJson:
    value.impactDispositionJson && value.impactDispositionJson !== "{}"
      ? value.impactDispositionJson
      : pendingImpact,
});

export default function ChangeRequestEditor({
  api,
  projectId,
  releaseId,
  releaseVersion,
  scope,
  softwareLevel,
  ladder,
  user,
  sourceRequirementId,
  onCancel,
  onSaved,
}: Props) {
  const configuredSoftwareLevel: RequirementLevel = ladderAllows(ladder, "HighLevel", LadderCapability.ChangeControl) ? "HighLevel" : "LowLevel";
  const defaultLevel: RequirementLevel = scope === "System" ? "System" : scope === "Interface" ? "Interface" : (softwareLevel && ladderAllows(ladder, softwareLevel, LadderCapability.ChangeControl) ? softwareLevel : configuredSoftwareLevel);
  const abbreviation = changeRequestAcronym(defaultLevel);
  const softwareLevelLabel = defaultLevel === "LowLevel" ? "LLR" : "HLR";
  const storageKey = `aerolink:new-${scope.toLowerCase()}-${scope === "Software" ? softwareLevelLabel.toLowerCase() : scope === "Interface" ? "icd" : "system"}-change:${projectId}:${releaseId}`;
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
  const [problemReportIds, setProblemReportIds] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [validationError, setValidationError] = useState<ValidationError>();

  // Held in this browser, because the change request does not exist on the server yet and reserving one
  // would consume an identifier for something nobody submitted.
  const draft = useLocalDraft<SavedDraft>(
    storageKey,
    { title, problem, analysis, solution, changes, problemRich, analysisRich, solutionRich, problemReportIds },
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
    setProblemReportIds(saved.problemReportIds ?? []);
  };

  useEffect(() => {
    let cancelled = false;
    // The number preview depends on the level: a software change request is an HLRCR or an LLRCR, and the
    // two are numbered apart, so the server cannot answer without being told which workspace is asking.
    fetch(`${api}/api/authoring/context?projectId=${projectId}&type=${scope}${scope === "Software" ? `&softwareLevel=${defaultLevel}` : ""}`)
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
  }, [api, defaultLevel, projectId, scope]);

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
              richText: latest.richText || "",
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
  const addProposal = (kind: RequirementKind, level: RequirementLevel) => {
    if (validationError?.kind === "proposal") setValidationError(undefined);
    setChanges((items) => [
      ...items,
      createProposal(level, kind, kind === "Introduce" ? nextIdentifier(level) : ""),
    ]);
  };

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
  const changeKind = (index: number, kind: RequirementKind) => {
    if (validationError?.kind === "proposal") setValidationError(undefined);
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
  };
  const updateProposal = (
    index: number,
    key: keyof ControlledRequirementDraft,
    value: string | number | boolean | string[],
  ) => {
    if (validationError?.kind === "proposal") setValidationError(undefined);
    setChanges((items) =>
      items.map((item, position) => {
        if (position !== index) return item;
        return { ...item, [key]: value } as ControlledRequirementDraft;
      }),
    );
  };

  const caseComplete = [title, problem, analysis, solution].every((value) => value.trim());
  const proposalsComplete =
    changes.length > 0 &&
    changes.every(
      (item) =>
        item.baseNumber &&
        (item.kind === "Retire" || item.statement.trim()) &&
        (!(item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
          item.rationale.trim()) &&
        (item.level === "System" || item.level === "Interface" ||
          (item.isDerived ?? parseObject(item.attributesJson).derived === true) ||
          Boolean(item.upstreamRevisionIds?.length)),
    );
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (!title.trim()) {
      setValidationError({ kind: "title", message: "Title of change request must be filled out before save is available." });
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
      setValidationError({
        kind: "proposal",
        message: `Add a statement to ${missingStatement.map((item) => item.baseNumber || "the new requirement").join(", ")}, or clear the card, before saving this Draft.`,
      });
      return;
    }
    setValidationError(undefined);
    setSaving(true);
    setError("");
    try {
      const created = await apiRequest<{ id: string; displayNumber: string }>(`${api}/api/change-request-drafts`, {
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
          problemReportIds,
          type: scope,
          softwareLevel: scope === "Software" ? softwareLevel : null,
          // An unset section is sent as null, not as "". A Guid? will not bind an empty string, and the failure
          // would be a 400 on the whole change request because one optional field was left alone.
          requirementChanges: started.map((item) => ({ ...item, targetSectionId: item.targetSectionId || null })),
        }),
      });
      // The record exists now, so the browser copy has nothing left to protect.
      draft.clear();
      onSaved(created.id, created.displayNumber);
    } catch (reason) {
      recordClientOperationFailure('change-request.draft.create', reason);
      setError(operationError(reason, `Unable to save the ${abbreviation} Draft.`));
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
          <h1>Create {scope === "System" ? "System" : scope === "Interface" ? "Interface / ICD" : softwareLevelLabel} Change Request</h1>
          <p>
            Build the engineering case and define the requirement changes for Build {releaseVersion}.
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

      {scope !== "Interface" && <RequirementsImportPanel
          api={api}
          projectId={projectId}
          releaseId={releaseId}
          scope={scope}
          softwareLevel={softwareLevel}
          onCreated={(id) => {
            draft.clear();
            onSaved(id, "The change request");
          }}
        />}

      <nav className="authoringStages twoStages" aria-label="Change authoring progress">
        <a href="#change-case" className={caseComplete ? "complete" : "active"}>
          <span>1</span><b>Change case</b><small>{caseComplete ? "Complete" : "In progress"}</small>
        </a>
        <a href="#requirement-changes" className={proposalsComplete ? "complete" : caseComplete ? "active" : ""}>
          <span>2</span><b>Requirement changes</b><small>{proposalsComplete ? "Complete" : `${changes.length} proposal${changes.length === 1 ? "" : "s"}`}</small>
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
              <input aria-describedby="change-request-number-help" value={context?.changeRequestNumber || "Calculating next number…"} readOnly />
              <small id="change-request-number-help">Previewed here; assigned atomically by the server on save.</small>
            </label>
            <label>
              Target release
              <input value={releaseVersion} readOnly />
            </label>
            <label>
              Author
              <input
                aria-describedby="change-request-author-help"
                value={`${context?.author.displayName || user.displayName} (${context?.author.userName || user.userName})`}
                readOnly
              />
              <small id="change-request-author-help">Derived from the authenticated session.</small>
            </label>
            <label className="wide">
              Title
              <input
                value={title}
                onChange={(event) => {
                  setTitle(event.target.value);
                  if (validationError?.kind === "title") setValidationError(undefined);
                }}
                placeholder="A concise, decision-ready description"
              />
            </label>
          </div>
          <div className="pas">
            <RichCaseField api={api} projectId={projectId} label="Problem" value={problemRich} onChange={setProblemRich}
              placeholder="What need, defect, or risk exists?" required={false} />
            <RichCaseField api={api} projectId={projectId} label="Analysis" value={analysisRich} onChange={setAnalysisRich}
              placeholder="What is affected and what alternatives were considered?" required={false} />
            <RichCaseField api={api} projectId={projectId} label="Solution" value={solutionRich} onChange={setSolutionRich}
              placeholder="What controlled outcome is proposed?" required={false} />
          </div>
          <ProblemReportPicker api={api} projectId={projectId} scope="target-build" releaseId={releaseId}
            selected={problemReportIds} onChange={setProblemReportIds}
            legend={`PRs driving this ${abbreviation} (optional)`} />
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
            {scope === "System" && ladderAllows(ladder, "System", LadderCapability.ChangeControl) ? (
              <>
                <button type="button" onClick={() => addProposal("Introduce", "System")}>+ Introduce System requirement</button>
                <button type="button" onClick={() => addProposal("Modify", "System")}>Modify existing</button>
                <button type="button" onClick={() => addProposal("Retire", "System")}>Retire existing</button>
              </>
            ) : (scope === "Software" || scope === "Interface") && ladderAllows(ladder, defaultLevel, LadderCapability.ChangeControl) ? (
              <>
                <button type="button" onClick={() => addProposal("Introduce", defaultLevel)}>+ Introduce {scope === "Interface" ? "Interface / ICD" : softwareLevelLabel} requirement</button>
                <button type="button" onClick={() => addProposal("Modify", defaultLevel)}>Modify existing {scope === "Interface" ? "Interface / ICD" : softwareLevelLabel}</button>
                <button type="button" onClick={() => addProposal("Retire", defaultLevel)}>Retire existing {scope === "Interface" ? "Interface / ICD" : softwareLevelLabel}</button>
              </>
            ) : <span className="proposalUnavailable">No configured change-control level is available.</span>}
          </div>
          <div className="proposalStack">
            {changes.map((change, index) => (
              <ControlledRequirementEditor
                api={api}
                projectId={projectId}
                releaseId={releaseId}
                scope={scope}
                item={change}
                index={index}
                key={`${index}-${change.kind}`}
                identityLocked={Boolean(change.baseNumber)}
                onChange={(key, value) => updateProposal(index, key, value)}
                onKindChange={(kind) => changeKind(index, kind)}
                onRemove={() => {
                  if (validationError?.kind === "proposal") setValidationError(undefined);
                  setChanges((items) => items.filter((_, position) => position !== index));
                }}
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

        {error && <div className="formError" role="alert">{error}</div>}
        {validationError && <div className="formError" role="alert">{validationError.message}</div>}
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
            <button disabled={saving || !context}>
              {saving ? "Saving Draft…" : `Save ${abbreviation} Draft`}
            </button>
          </div>
        </footer>
      </form>
    </main>
  );
}
