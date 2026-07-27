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
};

type ExistingRequirement = {
  id: string;
  baseNumber: string;
  displayNumber: string;
  level: RequirementLevel;
  nextRevision: number;
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
  onRemove: () => void;
};

const impactAreas = [
  ["trace", "Trace relationships"],
  ["verification", "Verification coverage"],
  ["documents", "Controlled documents"],
  ["baseline", "Baselines and builds"],
  ["collaboration", "Open collaboration"],
] as const;

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
  onRemove,
}: Props) {
  const attributes = useMemo(() => parse(item.attributesJson), [item.attributesJson]);
  const impact = useMemo(
    () => parse(item.impactDispositionJson),
    [item.impactDispositionJson],
  );
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<ExistingRequirement[]>([]);
  const [lookupBusy, setLookupBusy] = useState(false);
  const [lookupError, setLookupError] = useState("");

  const setAttribute = (key: string, value: unknown) =>
    onChange("attributesJson", JSON.stringify({ ...attributes, [key]: value }));
  const setImpact = (key: string, value: string) =>
    onChange("impactDispositionJson", JSON.stringify({ ...impact, [key]: value }));

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

  const selectExisting = (selected: ExistingRequirement) => {
    onChange("baseNumber", selected.baseNumber);
    onChange("revision", selected.nextRevision);
    onChange("level", selected.level);
    onChange("statement", item.kind === "Retire" ? "" : selected.statement);
    onChange("rationale", selected.rationale);
    onChange("verificationMethod", selected.verificationMethod);
    onChange("richText", item.kind === "Retire" ? emptyRichContent : fromPlainText(selected.statement));
    setQuery(selected.displayNumber);
    setResults([]);
  };

  const pendingImpacts = impactAreas.filter(
    ([key]) => !impact[key] || impact[key] === "Pending",
  ).length;
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
              Search the current controlled repository. AeroLink will lock the exact identity and next revision.
            </span>
          </div>
          <label>
            Find controlled requirement
            <input
              aria-label={`Find controlled requirement ${index + 1}`}
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="Type any identifier fragment, for example 0150"
              autoComplete="off"
            />
          </label>
          {lookupBusy && <small className="lookupStatus">Searching…</small>}
          {lookupError && <small className="lookupStatus error">{lookupError}</small>}
          {!lookupBusy && query.trim().length >= 2 && !results.length && !lookupError && (
            <small className="lookupStatus">No permitted requirements match this identifier.</small>
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
        <label>
          Level
          <input value={levelLabel(item.level)} readOnly aria-readonly="true" />
        </label>
        <label>
          Change type
          <input value={item.kind} readOnly aria-readonly="true" />
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
          <label>
            Requirement statement
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
                <small>Formatted context, controlled references, criticality, and ownership</small>
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
              <div className="editorMetadata classificationMetadata">
                <label>
                  Criticality
                  <select
                    value={String(attributes.criticality || "Normal")}
                    onChange={(event) => setAttribute("criticality", event.target.value)}
                  >
                    <option>Normal</option>
                    <option>Safety Significant</option>
                    <option>Mission Critical</option>
                  </select>
                </label>
                <label>
                  Owner
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
              <b>Impact decisions</b>
              <span>Required before this package can enter review.</span>
            </div>
          </div>
          <p>
            Record an explicit engineering decision for every lifecycle area. “Affected” and “Follow-up Assigned” remain visible to reviewers.
          </p>
          {impactAreas.map(([key, label]) => {
            const value = String(impact[key] || "Pending");
            return (
              <label className={value.toLowerCase().replaceAll(" ", "-")} key={key}>
                <span>{label}</span>
                <select value={value} onChange={(event) => setImpact(key, event.target.value)}>
                  <option>Pending</option>
                  <option>Affected</option>
                  <option>Not Affected</option>
                  <option>Follow-up Assigned</option>
                </select>
              </label>
            );
          })}
          <div className={pendingImpacts ? "impactGate pending" : "impactGate complete"}>
            <b>{5 - pendingImpacts}/5 impact decisions complete</b>
            <span>
              {pendingImpacts
                ? `${pendingImpacts} decision${pendingImpacts === 1 ? "" : "s"} still block review readiness.`
                : "This proposal is ready to join the controlled review snapshot."}
            </span>
          </div>
        </aside>
      </div>
    </article>
  );
}
