import { useEffect, useMemo, useState } from "react";
import { stateLabel } from './presentation'
import { RichContentEditor, RichContentView } from "./RichContent";
import { emptyRichContent, fromPlainText } from "./richContentModel";
import "./ControlledRequirementEditor.css";

export type RequirementLevel = "System" | "HighLevel" | "LowLevel";
export type RequirementKind = "Introduce" | "Modify" | "Retire";

export type ControlledRequirementDraft = {
  baseNumber: string;
  revision: number;
  level: RequirementLevel;
  kind: RequirementKind;
  statement: string;
  rationale: string;
  verificationMethod: string;
  richText: string;
  attributesJson: string;
  impactDispositionJson: string;
  isDerived?: boolean;
  /**
   * Which section of the specification this requirement goes in. Empty means unchanged — leave a modified
   * requirement where it is, and let the existing placement rule decide for a new one.
   */
  targetSectionId?: string;
};

type TracedImpact = {
  baseNumber: string;
  /** False when the requirement does not exist yet, which is the normal case for an introduction. */
  known: boolean;
  displayNumber?: string;
  derivedRequirements: { id: string; displayNumber: string; level: string; statement: string; linkType: string }[];
  coveringProcedures: { id: string; revisionId: string; displayNumber: string; title: string; level: string; state: string; isSuspect: boolean; coverageState: "Confirmed" | "Suspect" }[];
};

type SpecificationSection = {
  id: string;
  parentId?: string;
  /** Read off the document's structure — "4", "4.1", "4.1.1" — never stored in the heading. */
  number: string;
  /** How many sections this one sits under, so the list can show the shape of the document. */
  depth: number;
  heading: string;
  position: number;
  specification: string;
};

type ExistingRequirement = {
  id: string;
  baseNumber: string;
  displayNumber: string;
  level: RequirementLevel;
  nextRevision: number;
  /** The section it is in today, so a modification can offer to keep it rather than silently move it. */
  currentSectionId?: string | null;
  statement: string;
  rationale: string;
  verificationMethod: string;
  state: string;
};

type Props = {
  api: string;
  projectId: string;
  scope: "System" | "Software";
  item: ControlledRequirementDraft;
  index: number;
  identityLocked: boolean;
  onChange: (
    key: keyof ControlledRequirementDraft,
    value: string | number | boolean,
  ) => void;
  /**
   * Changes what this proposal does to a requirement. Separate from `onChange` because the kind decides what
   * the identifier means, so it cannot be set as an ordinary field — the owner re-derives identity from it.
   * Omitted where a proposal's kind is fixed by the surface it appears on.
   */
  onKindChange?: (kind: RequirementKind) => void;
  onRemove: () => void;
};

const kindOptions: { value: RequirementKind; label: string }[] = [
  { value: "Introduce", label: "Introduce a new requirement" },
  { value: "Modify", label: "Modify an existing requirement" },
  { value: "Retire", label: "Retire an existing requirement" },
];

const parse = (value: string): Record<string, unknown> => {
  try {
    return JSON.parse(value) as Record<string, unknown>;
  } catch {
    return {};
  }
};

const levelLabel = (level: RequirementLevel) =>
  level === "System"
    ? "System"
    : level === "HighLevel"
      ? "Software HLR"
      : "Software LLR";

export default function ControlledRequirementEditor({
  api,
  projectId,
  scope,
  item,
  index,
  identityLocked,
  onChange,
  onKindChange,
  onRemove,
}: Props) {
  const attributes = useMemo(() => parse(item.attributesJson), [item.attributesJson]);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<ExistingRequirement[]>([]);
  const [lookupBusy, setLookupBusy] = useState(false);
  const [lookupError, setLookupError] = useState("");

  const setAttribute = (key: string, value: unknown) =>
    onChange("attributesJson", JSON.stringify({ ...attributes, [key]: value }));

  useEffect(() => {
    if (identityLocked || item.kind === "Introduce") {
      setResults([]);
      return;
    }
    const term = query.trim();
    if (term.length < 2) {
      setResults([]);
      setLookupError("");
      return;
    }
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      setLookupBusy(true);
      setLookupError("");
      try {
        const response = await fetch(
          `${api}/api/authoring/requirements?projectId=${projectId}&scope=${scope}&search=${encodeURIComponent(term)}&limit=8`,
        );
        if (!response.ok) throw new Error("Requirement lookup is unavailable.");
        const rows = (await response.json()) as ExistingRequirement[];
        if (!cancelled) setResults(rows);
      } catch (reason) {
        if (!cancelled)
          setLookupError(
            reason instanceof Error ? reason.message : "Requirement lookup failed.",
          );
      } finally {
        if (!cancelled) setLookupBusy(false);
      }
    }, 180);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [api, identityLocked, item.kind, projectId, query, scope]);

  /**
   * The approved wording this proposal is changing, held separately from the proposal itself.
   *
   * Selecting a requirement copies its statement into the editable field, so the author starts from the
   * current text — which is right, and also means the original is gone the moment they type. A reviewer
   * cannot see what changed without leaving the page, and neither can the author.
   *
   * Read from the server rather than kept in the draft on purpose: a draft reopened tomorrow, or recovered
   * from an autosave snapshot, still knows what it is changing. Storing it in the draft would put a second
   * copy of controlled text inside an uncontrolled record, and the two would drift.
   */
  const [approvedWording, setApprovedWording] = useState<string>();
  useEffect(() => {
    if (item.kind !== "Modify" || !item.baseNumber) {
      setApprovedWording(undefined);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const response = await fetch(
          `${api}/api/authoring/requirements?projectId=${projectId}&scope=${scope}&search=${encodeURIComponent(item.baseNumber)}&limit=5`,
        );
        if (!response.ok) return;
        const rows = (await response.json()) as ExistingRequirement[];
        const match = rows.find((row) => row.baseNumber === item.baseNumber);
        if (!cancelled) setApprovedWording(match?.statement);
      } catch {
        // A missing original is shown as unavailable rather than as an error: it must never block authoring.
        if (!cancelled) setApprovedWording(undefined);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [api, item.baseNumber, item.kind, projectId, scope]);

  const selectExisting = (selected: ExistingRequirement) => {
    onChange("baseNumber", selected.baseNumber);
    onChange("revision", selected.nextRevision);
    onChange("level", selected.level);
    onChange("statement", item.kind === "Retire" ? "" : selected.statement);
    onChange("rationale", selected.rationale);
    onChange("verificationMethod", selected.verificationMethod);
    onChange("richText", item.kind === "Retire" ? emptyRichContent : fromPlainText(selected.statement));
    // Its existing section comes with it, so choosing a requirement to modify does not quietly relocate it.
    onChange("targetSectionId", selected.currentSectionId ?? "");
    setQuery(selected.displayNumber);
    setResults([]);
  };

  // Which sections this requirement could go in. Keyed by level, because the level fixes which specification the
  // requirement belongs to — a low-level requirement cannot be filed in the system document.
  const [sections, setSections] = useState<SpecificationSection[]>([]);
  const staleSection = Boolean(item.targetSectionId) &&
    !sections.some((section) => section.id === item.targetSectionId);
  useEffect(() => {
    let cancelled = false;
    fetch(`${api}/api/authoring/sections?projectId=${projectId}&level=${item.level}`)
      .then((response) => (response.ok ? (response.json() as Promise<SpecificationSection[]>) : []))
      .then((rows) => { if (!cancelled) setSections(Array.isArray(rows) ? rows : []); })
      .catch(() => { if (!cancelled) setSections([]); });
    return () => { cancelled = true; };
  }, [api, projectId, item.level]);

  /**
   * What the traceability graph says this change touches.
   *
   * The five dispositions below ask an author to decide whether trace relationships and verification coverage
   * are affected, and until now asked it from memory — while the links that answer it were recorded and shown
   * only on the requirements explorer, a page away from the person deciding.
   *
   * This informs the decision and never makes it. Nothing here writes a disposition: "the tool found no links"
   * and "an engineer confirmed there is no impact" are different claims, and only the second means anything in
   * a review. An introduced requirement has nothing downstream, so nothing is fetched for one.
   */
  const [traced, setTraced] = useState<TracedImpact>();
  const [tracedBusy, setTracedBusy] = useState(false);
  useEffect(() => {
    if (item.kind === "Introduce" || !item.baseNumber) {
      setTraced(undefined);
      return;
    }
    let cancelled = false;
    setTracedBusy(true);
    fetch(`${api}/api/authoring/impact?projectId=${projectId}&baseNumber=${encodeURIComponent(item.baseNumber)}`)
      .then((response) => (response.ok ? (response.json() as Promise<TracedImpact>) : undefined))
      .then((value) => { if (!cancelled) setTraced(value); })
      // Never blocks authoring. A proposal must remain writable when this cannot be read.
      .catch(() => { if (!cancelled) setTraced(undefined); })
      .finally(() => { if (!cancelled) setTracedBusy(false); });
    return () => { cancelled = true; };
  }, [api, projectId, item.kind, item.baseNumber]);

  const derived = item.isDerived ?? attributes.derived === true;
  const displayNumber = item.baseNumber
    ? `${item.baseNumber}.${String(item.revision).padStart(2, "0")}`
    : "Select an existing controlled requirement";

  return (
    <article className={`controlledEditor ${identityLocked ? "identityLocked" : "identityPending"}`}>
      <header>
        <div>
          <span>PROPOSAL {index + 1}</span>
          <h3>{displayNumber}</h3>
        </div>
        <div>
          <i>{levelLabel(item.level)}</i>
          <i>{item.kind}</i>
          <button type="button" onClick={onRemove}>
            Remove
          </button>
        </div>
      </header>

      {!identityLocked && item.kind !== "Introduce" && (
        <section className="proposalLookup" aria-label={`Select requirement for proposal ${index + 1}`}>
          <div>
            <b>
              {item.kind === "Modify" ? "Select the requirement to modify" : "Select the requirement to retire"}
            </b>
            <span>
              Search by requirement identifier or words in the requirement. AeroLink will lock the exact identity
              and next revision.
            </span>
          </div>
          <label>
            Find controlled requirement
            <input
              aria-label={`Find controlled requirement ${index + 1}`}
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Search identifier or requirement wording"
              autoComplete="off"
            />
          </label>
          {lookupBusy && <small className="lookupStatus">Searching…</small>}
          {lookupError && <small className="lookupStatus error">{lookupError}</small>}
          {!lookupBusy && query.trim().length >= 2 && !results.length && !lookupError && (
            <small className="lookupStatus">No permitted requirements match that identifier or wording.</small>
          )}
          {!!results.length && (
            <div className="proposalLookupResults">
              {results.map((result) => (
                <button type="button" key={result.id} onClick={() => selectExisting(result)}>
                  <span>
                    <b>{result.displayNumber}</b>
                    <small>{levelLabel(result.level)} · {stateLabel(result.state)}</small>
                  </span>
                  <p>{result.statement}</p>
                  <em>Use revision {String(result.nextRevision).padStart(2, "0")} →</em>
                </button>
              ))}
            </div>
          )}
        </section>
      )}

      <div className="editorIdentity">
        <label>
          Identifier
          <input
            value={item.baseNumber || "Awaiting controlled selection"}
            readOnly
            aria-readonly="true"
          />
          <small>Server-issued and immutable in this proposal.</small>
        </label>
        <label>
          Revision
          <input
            value={item.baseNumber ? String(item.revision).padStart(2, "0") : "Pending"}
            readOnly
            aria-readonly="true"
          />
        </label>
        {/* Where this requirement goes in the document, chosen by the author.
            A requirement's place in a specification is part of what a change request proposes, and nothing used
            to carry it: an introduced requirement landed wherever a backfill put it, and a modification could
            not move one at all. Hidden when the change is a retirement, which removes a requirement from future
            baselines and so has no section to be in. */}
        {item.kind !== "Retire" && (sections.length > 0 || staleSection) && (
          <label>
            Section
            <select
              aria-label={`Section for proposal ${index + 1}`}
              value={item.targetSectionId ?? ""}
              onChange={(event) => onChange("targetSectionId", event.target.value)}
            >
              {staleSection && (
                <option value={item.targetSectionId}>
                  Previously selected section is unavailable — choose another
                </option>
              )}
              {/* A new requirement has to be given a section, so its placeholder cannot be chosen. This used
                  to offer "Decide when the baseline is assembled", which deferred the decision from the one
                  person who knew the answer — the author writing it — to whoever later assembled the
                  baseline, by which time the requirement had already landed wherever a backfill put it.
                  A modification may still be left alone: it already has a section. */}
              <option value="" disabled={item.kind !== "Modify"}>
                {item.kind === "Modify" ? "Leave where it is" : "Choose a section…"}
              </option>
              {sections.map((section) => (
                // Indented by depth and numbered from the structure, so 4.1.1 reads as being inside 4.1.
                // `&nbsp;` rather than padding, because an <option> takes no styling in most browsers.
                <option value={section.id} key={section.id}>
                  {`${"  ".repeat(section.depth)}${section.number} ${section.heading}`}
                </option>
              ))}
            </select>
            <small>
              {item.kind === "Modify"
                ? "Changing this moves the requirement when the baseline is materialized."
                : "Applied when the baseline is materialized and the requirement first exists."}
            </small>
          </label>
        )}
        <label>
          Level
          <input value={levelLabel(item.level)} readOnly aria-readonly="true" />
        </label>
        {/* The one thing in this row the author decides, and it was readOnly.
            A proposal added as one kind could only be turned into another by removing it and starting again —
            and the editor pre-seeded an Introduce proposal whose identifier was already allocated, which counted
            as identity-locked, so the first proposal on every new change request could not be changed at all.
            It sits here rather than beside the badges above because it governs the Identifier next to it:
            changing it re-derives that identity, since an introduced requirement is allocated a number now
            while a modified or retired one names one that already exists. */}
        <label>
          Change type
          {onKindChange ? (
            <>
              {/* Named explicitly. The hint below is inside the label, as the readonly fields' hints are, so
                  without this the computed name would swallow it — and it mentions the identifier, which made
                  the select answer to a search for the Identifier field as well as this one. */}
              <select
                aria-label="Change type"
                value={item.kind}
                onChange={(event) => onKindChange(event.target.value as RequirementKind)}
              >
                {kindOptions.map((option) => (
                  <option value={option.value} key={option.value}>{option.label}</option>
                ))}
              </select>
              <small>Changing this re-issues the identifier above.</small>
            </>
          ) : (
            <input value={item.kind} readOnly aria-readonly="true" />
          )}
        </label>
      </div>

      <div className="editorColumns">
        <section>
          <div className="sectionTitle">
            <div>
              <b>Controlled requirement content</b>
              <span>The statement and rationale become part of one exact review snapshot.</span>
            </div>
            <em>{item.kind === "Retire" ? "History retained" : "Draft content"}</em>
          </div>
          {item.kind === "Modify" && (
            <label className="approvedWording">
              Existing requirement wording
              <textarea
                className="statementEditor"
                value={approvedWording ?? "Loading the approved wording…"}
                readOnly
                aria-readonly="true"
                tabIndex={-1}
              />
              <small>
                {item.baseNumber} as approved today. Read-only — the change goes in the field below.
              </small>
            </label>
          )}
          <label>
            {item.kind === "Modify" ? "Modified requirement wording" : "Requirement statement"}
            <textarea
              className="statementEditor"
              value={item.statement}
              onChange={(event) => onChange("statement", event.target.value)}
              readOnly={item.kind === "Retire" || !identityLocked}
              placeholder={
                !identityLocked
                  ? "Select the controlled requirement before editing its proposed revision."
                  : item.kind === "Retire"
                    ? "Retirement preserves the prior statement in immutable history."
                    : "State one clear, verifiable requirement."
              }
              required={item.kind !== "Retire"}
            />
          </label>
          <div className="editorMetadata primaryMetadata">
            <label>
              Rationale
              <textarea
                value={item.rationale}
                onChange={(event) => onChange("rationale", event.target.value)}
                placeholder="Why is this change necessary?"
              />
            </label>
            <label>
              Verification method
              <select
                value={item.verificationMethod}
                onChange={(event) => onChange("verificationMethod", event.target.value)}
                disabled={!identityLocked}
              >
                <option>Test</option>
                <option>Analysis</option>
                <option>Inspection</option>
                <option>Demonstration</option>
              </select>
            </label>
          </div>

          <details className="supportingDetails" open>
            <summary>
              <span>
                <b>Supporting content and classification</b>
                <small>Formatted context, controlled references, and the responsible author</small>
              </span>
              <em>Show / hide</em>
            </summary>
            <div className="supportingBody">
              <RichContentEditor
                api={api}
                projectId={projectId}
                label="Formatted supporting content"
                placeholder="Add the tables, figures, and context an approver needs alongside the statement."
                value={item.richText}
                onChange={(value) => onChange("richText", value)}
              />
              <div className="controlledPreview">
                <small>CONTROLLED PREVIEW</small>
                {/* What the approver will read, and what the generated Word and PDF documents will carry.
                    The old preview showed the authored source, which is the one thing nobody signs. */}
                <RichContentView
                  api={api}
                  value={item.richText}
                  empty={item.statement || "No supporting content recorded."}
                />
              </div>
              {/* Criticality was asked of the author on every proposal and used by nothing. It remains a
                  Program-configurable schema field for a programme that wants it, but it is no longer a
                  question this form puts to somebody writing a change.

                  Author, not Owner: a requirement has an author, and the change request already records who
                  wrote it — two words for one idea invited the reader to look for a distinction that does not
                  exist. The stored attribute key stays `owner`, because the workspace's owner filter reads it
                  and renaming the key would silently break every saved view that uses it. */}
              <div className="editorMetadata classificationMetadata">
                <label>
                  Author
                  <input
                    value={String(attributes.owner || "")}
                    onChange={(event) => setAttribute("owner", event.target.value)}
                    placeholder="responsible.username"
                  />
                </label>
                {scope === "Software" && (
                  <label className="derivedControl">
                    Classification
                    <button
                      type="button"
                      aria-pressed={derived}
                      className={derived ? "active" : ""}
                      onClick={() => {
                        onChange("isDerived", !derived);
                        setAttribute("derived", !derived);
                      }}
                    >
                      <i>{derived ? "✓" : "○"}</i>
                      <span>
                        <b>Derived requirement</b>
                        <small>Not directly allocated from a higher-level requirement</small>
                      </span>
                    </button>
                  </label>
                )}
              </div>
            </div>
          </details>
        </section>

        <aside>
          <div className="sectionTitle">
            <div>
              <b>Known downstream context</b>
              <span>Read-only information for the change author.</span>
            </div>
          </div>
          <p>
            Verification and consuming engineering teams assess downstream impact after this change request is reviewed.
          </p>
          {item.kind !== "Introduce" && item.baseNumber && (
            <section className="tracedImpact" aria-label={`Recorded links for proposal ${index + 1}`}>
              <header>
                <b>Live trace for {item.baseNumber}</b>
              </header>
              {tracedBusy && !traced && <p className="tracedEmpty">Reading recorded links…</p>}
              {traced && (
                <dl>
                  <div>
                    <dt>Requirements derived from this one</dt>
                    <dd>
                      {traced.derivedRequirements.length
                        ? traced.derivedRequirements.map((row) => (
                            // The level chip is dropped on purpose: HLR-000149 is a high-level requirement by
                            // its identifier, so the chip restated the prefix the reader had just read.
                            <span className="tracedItem" key={row.id} title={row.statement}>
                              {row.displayNumber}
                            </span>
                          ))
                        : <em>No derived requirements are recorded.</em>}
                    </dd>
                  </div>
                  <div>
                    <dt>Procedures that verify it</dt>
                    <dd>
                      {traced.coveringProcedures.length
                        ? traced.coveringProcedures.map((row) => (
                            // Only suspect applicability earns a chip. "Approved" was the answer for almost
                            // every procedure listed, so it distinguished nothing and buried the one value
                            // that needed to stand out.
                            <span className={`tracedItem${row.isSuspect ? " suspect" : ""}`} key={row.revisionId} title={row.title}>
                              {row.displayNumber} {row.isSuspect && <i>Suspect applicability</i>}
                            </span>
                          ))
                        : <em>No covering procedures are recorded.</em>}
                    </dd>
                  </div>
                </dl>
              )}
              {traced?.coveringProcedures.some((row) => row.isSuspect) && <p className="tracedWarning">Changed wording made this applicability suspect. Resolve it through Verification → Change impact; the procedure remains approved, but it does not count as confirmed coverage.</p>}
            </section>
          )}
          {item.kind === "Introduce" && <div className="tracedImpact tracedEmpty"><b>New requirement</b><p>No earlier lifecycle trace exists. Downstream teams will establish the necessary traceability and verification after approval.</p></div>}
        </aside>
      </div>
    </article>
  );
}
