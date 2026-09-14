import { useEffect, useRef, useState } from "react";
import { ApiError, apiRequest, operationError } from "./apiClient";
import {
  decodeNativeSourceOptions,
  decodeSourceView,
  defaultSourceObjectMapping,
  sourceConfigurationPayload,
  sourcePageUrl,
  sourceUploadAccept,
  type NativeSourceOption,
  type SourceCategory,
  type SourceDraftState,
  type SourceKind,
  type SourceMappingDestination,
  type SourceModule,
  type SourceObjectMapping,
  type SourceAttributeMapping,
  type SourceRelation,
  type SourceLadderSuggestion,
  type SourceView,
} from "./projectSetupSource";
import "./ProjectSetupSourcePanel.css";

type SetupLevelOption = { id: string; label: string };

export type ProjectSetupSourcePanelProps = {
  api: string;
  draftId: string;
  draftVersion: number;
  kind: SourceKind;
  canAcceptSource?: boolean;
  selectedCategories: string[];
  levelOptions: SetupLevelOption[];
  initialState: SourceDraftState;
  onSelectedCategoriesChange: (categories: string[]) => void;
  beforeSourceCall: () => Promise<number | null>;
  onSourceVersion: (version: number) => void;
  onSourceIdentity: (id: string) => void;
  onSourceStateChange: (state: SourceDraftState) => void;
  /** Changes to the accepted ladder invalidate a source reconciliation. */
  ladderRevision?: string;
  /** Lets the walkthrough review a server-derived, typed ladder suggestion. */
  onApplyLadderSuggestion?: (suggestion: SourceLadderSuggestion) => void;
  /** Registers the source-owned save used by global Save and exit/navigation actions. */
  onRegisterSourceSave?: (save: (() => Promise<number | null>) | null) => void;
};

type SourceEnvelope = { draftVersion?: number; source?: unknown };

// FMS-sized baselines can contain hundreds of source objects. Keep the complete mapping in
// memory and in the versioned payload, but bound the number of expensive object editors mounted
// at once so an existing authorized baseline remains usable in the browser.
const SOURCE_OBJECT_PAGE_SIZE = 20;
// Source relations carry the same exact decisions as object mappings. Keep the full collection
// in state and in the configuration payload, but mount a bounded page of relation controls so a
// large authorized baseline remains usable while every relation stays reachable.
const SOURCE_RELATION_PAGE_SIZE = 20;

const mappingDestinations: { value: SourceMappingDestination; label: string }[] = [
  { value: "SourceOnly", label: "Keep as source-only attribute" },
  { value: "Statement", label: "Requirement statement" },
  { value: "Rationale", label: "Requirement rationale" },
  { value: "VerificationMethod", label: "Verification method" },
  { value: "SourceIdentifier", label: "Source identifier" },
  { value: "Title", label: "Verification title" },
  { value: "Objective", label: "Verification objective" },
  { value: "Preconditions", label: "Verification preconditions" },
  { value: "Steps", label: "Verification steps" },
  { value: "ExpectedResult", label: "Verification expected result" },
  { value: "Exclude", label: "Exclude with a reason" },
];

function sourceFromEnvelope(value: unknown) {
  const row =
    value && typeof value === "object" && !Array.isArray(value) ? (value as SourceEnvelope) : {};
  return {
    draftVersion: typeof row.draftVersion === "number" ? row.draftVersion : undefined,
    source: decodeSourceView(row.source ?? value),
  };
}

function sourceCategoryLabel(category: SourceCategory) {
  return category.key === "Requirements"
    ? "Requirements"
    : category.key === "Traces"
      ? "Trace relationships"
      : category.key === "Cases"
        ? "Test cases"
        : category.key === "Procedures"
          ? "Test procedures"
          : "Evidence facts";
}

function sourceRelationLabel(value: string) {
  const labels: Record<string, string> = {
    RequirementTrace: "Requirement trace",
    AllocatedFrom: "Allocated from",
    DerivedFrom: "Derived from",
    CaseProcedure: "Test case to procedure",
    VerificationCoverage: "Verification coverage",
    EvidenceExecution: "Evidence to execution",
  };
  return labels[value] ?? value.replace(/([a-z])([A-Z])/g, "$1 $2");
}

type LadderSuggestionRelationshipSummary = {
  type: string;
  sourceLevel: string;
  targetLevel: string;
  count: number;
};

/**
 * A ladder suggestion describes source relationship patterns, while the source editor below
 * retains every exact relationship row. Group only identical typed level transitions here so a
 * large source cannot mount thousands of repeated suggestion rows or lose the fact that several
 * source relationships support the same transition.
 */
function summarizeLadderSuggestionRelationships(
  relationships: SourceLadderSuggestion["relationships"],
): LadderSuggestionRelationshipSummary[] {
  const summaries = new Map<string, LadderSuggestionRelationshipSummary>();
  for (const relationship of relationships) {
    // Deliberately preserve server spelling and first-seen order. These are controlled source
    // facts; case-folding or locale normalization could merge distinct maintained types/levels.
    const key = JSON.stringify([
      relationship.type,
      relationship.sourceLevel,
      relationship.targetLevel,
    ]);
    const existing = summaries.get(key);
    if (existing) {
      existing.count += 1;
    } else {
      summaries.set(key, {
        type: relationship.type,
        sourceLevel: relationship.sourceLevel,
        targetLevel: relationship.targetLevel,
        count: 1,
      });
    }
  }
  return Array.from(summaries.values());
}

function selectedCategoryKeys(source: SourceView, selected: string[]) {
  const known = new Set<string>(source.categories.map((category) => category.key));
  const sourceSelection = source.selectedCategories.filter((category) => known.has(category));
  return sourceSelection.length
    ? sourceSelection
    : selected.filter((category) => known.has(category));
}

function responseVersion(source: SourceEnvelope, fallback: number) {
  return typeof source.draftVersion === "number" ? source.draftVersion : fallback;
}

function hasRequiredCategories(category: SourceCategory, selected: string[]) {
  return category.requires.every((required) => selected.includes(required));
}

function formatCount(value: number) {
  return new Intl.NumberFormat().format(value);
}

function updateModule(source: SourceView, index: number, patch: Partial<SourceModule>): SourceView {
  return {
    ...source,
    modules: source.modules.map((module, currentIndex) => {
      if (currentIndex !== index) return module;
      const nextModule = { ...module, ...patch };
      // The module heading is a useful bulk-edit affordance. Keep each exact object decision in
      // sync with the bulk action so the visible change is what the versioned payload serializes.
      if (!module.objects?.length) return nextModule;
      const objectMappings = Object.fromEntries(module.objects.map((object) => {
        const current = module.objectMappings?.[object.key] ?? defaultSourceObjectMapping(module);
        const next = { ...current };
        if (Object.prototype.hasOwnProperty.call(patch, "include") && typeof patch.include === "boolean") {
          next.include = patch.include;
        }
        if (Object.prototype.hasOwnProperty.call(patch, "level")) {
          next.level = patch.level;
        }
        if (Object.prototype.hasOwnProperty.call(patch, "exclusionReason")) {
          next.exclusionReason = patch.exclusionReason;
        }
        if (patch.mappings) {
          next.mappings = patch.mappings.map((mapping) => ({
            ...mapping,
            ...(mapping.valueMappings
              ? { valueMappings: mapping.valueMappings.map((value) => ({ ...value })) }
              : {}),
          }));
        }
        return [object.key, next] as const;
      }));
      return { ...nextModule, objectMappings };
    }),
  };
}

function updateObjectMapping(
  source: SourceView,
  moduleIndex: number,
  objectKey: string,
  patch: Partial<SourceObjectMapping>,
): SourceView {
  return {
    ...source,
    modules: source.modules.map((module, currentIndex) => {
      if (currentIndex !== moduleIndex) return module;
      const objectMappings = module.objectMappings ?? Object.fromEntries(
        (module.objects ?? []).map((object) => [object.key, defaultSourceObjectMapping(module)]),
      );
      const current = objectMappings[objectKey] ?? defaultSourceObjectMapping(module);
      if (!current) return module;
      return {
        ...module,
        objectMappings: {
          ...objectMappings,
          [objectKey]: { ...current, ...patch },
        },
      };
    }),
  };
}

function updateRelation(
  source: SourceView,
  index: number,
  patch: Partial<SourceRelation>,
): SourceView {
  return {
    ...source,
    relations: source.relations.map((relation, currentIndex) =>
      currentIndex === index ? { ...relation, ...patch } : relation,
    ),
  };
}

function AttributeMappingTable({
  title,
  mappings,
  onChange,
}: {
  title: string;
  mappings: SourceAttributeMapping[];
  onChange: (mappings: SourceAttributeMapping[]) => void;
}) {
  return (
    <div className="setupSourceAttributeTableWrap">
      <table className="setupSourceAttributeTable">
        <caption>{title}</caption>
        <thead>
          <tr>
            <th scope="col">Source attribute</th>
            <th scope="col">Destination</th>
            <th scope="col">Reason</th>
            <th scope="col">Values</th>
          </tr>
        </thead>
        <tbody>
          {mappings.map((mapping, mappingIndex) => (
            <tr key={`${title}-${mapping.sourceAttribute}`}>
              <td>{mapping.sourceAttribute}</td>
              <td>
                <select
                  aria-label={`Mapping for ${title} ${mapping.sourceAttribute}`}
                  value={mapping.destination}
                  onChange={(event) => {
                    onChange(mappings.map((current, currentIndex) =>
                      currentIndex === mappingIndex
                        ? { ...current, destination: event.target.value as SourceMappingDestination }
                        : current,
                    ));
                  }}
                >
                  {mappingDestinations.map((item) => (
                    <option value={item.value} key={item.value}>{item.label}</option>
                  ))}
                </select>
              </td>
              <td>
                <input
                  aria-label={`Reason for ${title} ${mapping.sourceAttribute}`}
                  value={mapping.reason ?? ""}
                  onChange={(event) => {
                    onChange(mappings.map((current, currentIndex) =>
                      currentIndex === mappingIndex ? { ...current, reason: event.target.value } : current,
                    ));
                  }}
                />
              </td>
              <td>
                {mapping.valueMappings?.length ? (
                  <div className="setupSourceValues">
                    {mapping.valueMappings.map((valueMapping, valueIndex) => (
                      <label key={`${mapping.sourceAttribute}-${valueIndex}`}>
                        {valueMapping.sourceValue}
                        <input
                          aria-label={`Destination value for ${title} ${mapping.sourceAttribute} ${valueMapping.sourceValue}`}
                          value={valueMapping.destinationValue}
                          onChange={(event) => {
                            onChange(mappings.map((current, currentIndex) =>
                              currentIndex === mappingIndex
                                ? {
                                    ...current,
                                    valueMappings: current.valueMappings?.map((row, rowIndex) =>
                                      rowIndex === valueIndex
                                        ? { ...row, destinationValue: event.target.value }
                                        : row,
                                    ),
                                  }
                                : current,
                            ));
                          }}
                        />
                      </label>
                    ))}
                  </div>
                ) : (
                  <span className="setupSourceMuted">No enumerated source values</span>
                )}
              </td>
            </tr>
          ))}
          {!mappings.length && (
            <tr><td colSpan={4}>No source attributes were observed.</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

export function SourceAcceptanceFields({
  source,
  accepted,
  password,
  onAcceptedChange,
  onPasswordChange,
  disabled = false,
}: {
  source: SourceView | null;
  accepted: boolean;
  password: string;
  onAcceptedChange: (accepted: boolean) => void;
  onPasswordChange: (password: string) => void;
  disabled?: boolean;
}) {
  if (!source?.assertion) {
    return (
      <p className="setupSourcePending" role="status">
        Source acceptance is not available yet. Save the source choices and reconcile them against
        the current ladder before finalization.
      </p>
    );
  }
  return (
    <div className="setupSourceAcceptance">
      <h3>Accept the source facts</h3>
      <p>
        This assertion covers the selected source revision, categories, mapping, exclusions, and
        reconciliation. It does not turn source approvals, executions, or evidence into new-project
        approvals or executions.
      </p>
      <blockquote>{source.assertion.text}</blockquote>
      <small>Assertion hash: {source.assertion.hash}</small>
      <label className="setupAccept">
        <input
          type="checkbox"
          checked={accepted}
          onChange={(event) => onAcceptedChange(event.target.checked)}
          disabled={disabled}
        />
        I accept this exact source assertion as the person authorizing this project start.
      </label>
      <label className="setupSourcePassword">
        Password to finalize source acceptance
        <input
          type="password"
          value={password}
          onChange={(event) => onPasswordChange(event.target.value)}
          autoComplete="current-password"
          disabled={disabled}
        />
        <small>
          Your password is used only for this finalization request and is never saved in the draft.
        </small>
      </label>
    </div>
  );
}

export default function ProjectSetupSourcePanel({
  api,
  draftId,
  draftVersion,
  kind,
  canAcceptSource = true,
  selectedCategories,
  levelOptions,
  initialState,
  onSelectedCategoriesChange,
  beforeSourceCall,
  onSourceVersion,
  onSourceIdentity,
  onSourceStateChange,
  ladderRevision,
  onApplyLadderSuggestion,
  onRegisterSourceSave,
}: ProjectSetupSourcePanelProps) {
  const [source, setSource] = useState<SourceView | null>(
    initialState.source?.kind === kind ? initialState.source : null,
  );
  const [accepted, setAccepted] = useState(initialState.assertionAccepted);
  const [password, setPassword] = useState(initialState.password);
  const [options, setOptions] = useState<NativeSourceOption[]>([]);
  const [sourceTotal, setSourceTotal] = useState(0);
  const [sourceOffset, setSourceOffset] = useState(0);
  const [search, setSearch] = useState("");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [selected, setSelected] = useState<string[]>(
    initialState.source?.kind === kind
      ? selectedCategoryKeys(initialState.source, selectedCategories)
      : [...selectedCategories],
  );
  const [busy, setBusy] = useState(false);
  const [optionsBusy, setOptionsBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [objectPages, setObjectPages] = useState<Record<string, number>>({});
  const [relationPage, setRelationPage] = useState(0);
  const requestNumber = useRef(0);
  const previousLadderRevision = useRef(ladderRevision);

  useEffect(() => {
    onSourceStateChange({ source, assertionAccepted: accepted, password });
  }, [accepted, onSourceStateChange, password, source]);

  useEffect(() => {
    setSelected((current) => {
      const sourceKeys = source?.categories.map((category) => category.key);
      if (!sourceKeys) return current;
      const allowed = new Set<string>(sourceKeys);
      return current.filter((category) => allowed.has(category));
    });
  }, [source]);

  useEffect(() => {
    // A newly selected source may reuse a module key. Start its object editor at the first page,
    // while ordinary mapping edits keep the current page so edits survive a local re-render.
    setObjectPages({});
    // Relation paging follows the source identity, not ordinary mapping or reconciliation edits;
    // a save must not move the creator away from the relation they are reviewing.
    setRelationPage(0);
  }, [source?.id, source?.sha256, kind]);

  useEffect(() => {
    // A refreshed source can legitimately contain fewer relation rows after server analysis. Clamp
    // the presentation page to the new collection without discarding any source decisions.
    const relationCount = source?.relations.length ?? 0;
    const lastPage = Math.max(0, Math.ceil(relationCount / SOURCE_RELATION_PAGE_SIZE) - 1);
    setRelationPage((current) => Math.min(current, lastPage));
  }, [source?.relations.length]);

  useEffect(() => {
    if (previousLadderRevision.current === ladderRevision) return;
    previousLadderRevision.current = ladderRevision;
    setSource((current) =>
      current
        ? {
            ...current,
            reconciliation: null,
            assertion: null,
          }
        : current,
    );
    setAccepted(false);
    setPassword("");
    setNotice("The ladder changed. Reconcile the exact source again before finalization.");
  }, [ladderRevision]);

  const applySource = (next: SourceView | null, version: number) => {
    if (!next) return;
    setSource(next);
    const nextSelected = selectedCategoryKeys(next, selectedCategories);
    setSelected(nextSelected);
    onSelectedCategoriesChange(nextSelected);
    onSourceIdentity(
      next.kind === "AeroLinkBaseline" ? (next.sourceBaselineId ?? next.id) : next.id,
    );
    onSourceVersion(version);
    setAccepted(false);
    setPassword("");
    setNotice(
      "The exact source snapshot is saved. Configure categories and mappings, then reconcile it before finalization.",
    );
  };

  const loadOptions = async (offset: number, expectedVersion: number, request: number) => {
    setOptionsBusy(true);
    try {
      const page = await apiRequest<unknown>(sourcePageUrl(api, draftId, offset));
      if (request !== requestNumber.current) return;
      const decoded = decodeNativeSourceOptions(page);
      setOptions(decoded.items);
      setSourceTotal(decoded.total);
      setSourceOffset(decoded.offset);
      onSourceVersion(expectedVersion);
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(operationError(failure, "Authorized native baselines could not be loaded."));
      }
    } finally {
      if (request === requestNumber.current) setOptionsBusy(false);
    }
  };

  const sourceAfterMutation = async (
    mutation: unknown,
    expectedVersion: number,
    request: number,
  ) => {
    const mutationResult = sourceFromEnvelope(mutation);
    if (mutationResult.source) {
      return {
        source: mutationResult.source,
        version: responseVersion(mutationResult, expectedVersion),
      };
    }
    // The source mutation routes return a compact durable receipt. Read the canonical SourceView
    // afterwards so parser facts, typed mappings, findings, and the server's ladder suggestion are
    // never reconstructed from a browser payload or a partial response.
    const current = await apiRequest<unknown>(`${api}/api/project-setups/${draftId}/source`);
    if (request !== requestNumber.current) return null;
    const currentResult = sourceFromEnvelope(current);
    if (!currentResult.source) {
      throw new Error("The source service did not return the saved source view.");
    }
    return {
      source: currentResult.source,
      version: responseVersion(currentResult, responseVersion(mutationResult, expectedVersion)),
    };
  };

  const loadSource = async () => {
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return;
      try {
        const envelope = await apiRequest<unknown>(`${api}/api/project-setups/${draftId}/source`);
        if (request !== requestNumber.current) return;
        const decoded = sourceFromEnvelope(envelope);
        if (decoded.draftVersion !== undefined) onSourceVersion(decoded.draftVersion);
        if (decoded.source?.kind === kind)
          applySource(decoded.source, responseVersion(decoded, expectedVersion));
        else if (kind === "ExternalBaseline") setSource(null);
      } catch (failure) {
        if (
          !(failure instanceof ApiError && failure.status === 404) &&
          request === requestNumber.current
        ) {
          setError(operationError(failure, "The saved source selection could not be loaded."));
        }
      }
      if (kind === "AeroLinkBaseline") await loadOptions(0, expectedVersion, request);
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "Save the current project answers before loading source choices.",
          ),
        );
      }
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

  useEffect(() => {
    void loadSource();
    return () => {
      requestNumber.current += 1;
    };
    // Source calls deliberately occur once when this start path is selected. Parent callbacks are
    // intentionally excluded so an answer update does not reload and replace the creator's edits.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, draftId, kind]);

  const selectNative = async (baselineId: string) => {
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return;
      const envelope = await apiRequest<unknown>(
        `${api}/api/project-setups/${draftId}/source/native`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ expectedVersion, baselineId }),
        },
      );
      if (request !== requestNumber.current) return;
      const committed = await sourceAfterMutation(envelope, expectedVersion, request);
      if (!committed || committed.source.kind !== "AeroLinkBaseline") {
        throw new Error("The source service did not return the selected native baseline.");
      }
      applySource(committed.source, committed.version);
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "The native baseline could not be selected. Earlier answers remain saved.",
          ),
        );
      }
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

  const uploadExternal = async () => {
    if (!selectedFile) {
      setError("Choose a ReqIF, CSV, or XLSX file before uploading.");
      return;
    }
    if (selectedFile.size > 50 * 1024 * 1024) {
      setError("This source file is larger than the 50 MB limit.");
      return;
    }
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return;
      const query = `?expectedVersion=${encodeURIComponent(expectedVersion)}&fileName=${encodeURIComponent(selectedFile.name)}`;
      const envelope = await apiRequest<unknown>(
        `${api}/api/project-setups/${draftId}/source/upload${query}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/octet-stream" },
          body: await selectedFile.arrayBuffer(),
        },
      );
      if (request !== requestNumber.current) return;
      const committed = await sourceAfterMutation(envelope, expectedVersion, request);
      if (!committed || committed.source.kind !== "ExternalBaseline") {
        throw new Error("The source service did not return the uploaded baseline.");
      }
      applySource(committed.source, committed.version);
      setSelectedFile(null);
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "The source file could not be staged. Earlier answers remain saved.",
          ),
        );
      }
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

  const updateSourceAndNotice = (next: SourceView) => {
    // A source assertion and reconciliation are proofs of the exact source, mapping, categories,
    // and ladder. Any local edit invalidates both until the server recomputes them.
    setSource({ ...next, reconciliation: null, assertion: null });
    setAccepted(false);
    setPassword("");
    setNotice("Unsaved source choices. Save and reconcile before finalization.");
  };

  const toggleCategory = (category: SourceCategory) => {
    if (!category.supported) return;
    if (selected.includes(category.key)) {
      const dependents =
        source?.categories
          .filter(
            (candidate) =>
              selected.includes(candidate.key) && candidate.requires.includes(category.key),
          )
          .map(sourceCategoryLabel) ?? [];
      if (dependents.length) {
        setError(
          `${sourceCategoryLabel(category)} is required by ${dependents.join(", ")}. Remove dependent categories first.`,
        );
        return;
      }
      const next = selected.filter((item) => item !== category.key);
      setSelected(next);
      onSelectedCategoriesChange(next);
      if (source) updateSourceAndNotice({ ...source, selectedCategories: next });
      return;
    }
    if (!hasRequiredCategories(category, selected)) {
      setError(
        `${sourceCategoryLabel(category)} requires ${category.requires.join(", ")}. Select those categories first.`,
      );
      return;
    }
    const next = [...selected, category.key];
    setSelected(next);
    onSelectedCategoriesChange(next);
    if (source) updateSourceAndNotice({ ...source, selectedCategories: next });
  };

  const saveConfiguration = async (): Promise<number | null> => {
    if (!source) return null;
    const isNativeRelation = (relation: SourceRelation) =>
      ["CaseProcedure", "VerificationCoverage", "EvidenceExecution"].includes(
        relation.type ?? "",
      );
    const isTraceRelation = (relation: SourceRelation) =>
      !isNativeRelation(relation) &&
      ["RequirementTrace", "AllocatedFrom", "DerivedFrom"].includes(
        relation.type ?? relation.sourceType,
      );
    const incompleteTrace = source.relations.find(
      (relation) => relation.include && isTraceRelation(relation) && !relation.mappingType,
    );
    if (incompleteTrace) {
      setError(
        `Choose AllocatedFrom or DerivedFrom for ${incompleteTrace.sourceType} before reconciling.`,
      );
      return null;
    }
    const incompleteRelation = source.relations.find(
      (relation) =>
        relation.include && !isNativeRelation(relation) && typeof relation.sourceIsParent !== "boolean",
    );
    if (incompleteRelation) {
      setError(
        `Choose the direction for ${incompleteRelation.sourceType}, or exclude it with a reason, before reconciling.`,
      );
      return null;
    }
    const unexplainedExclusion = source.relations.find(
      (relation) => !relation.include && !relation.exclusionReason?.trim(),
    );
    if (unexplainedExclusion) {
      setError(
        `Explain why ${unexplainedExclusion.sourceType} is excluded before reconciling.`,
      );
      return null;
    }
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return null;
      const envelope = await apiRequest<unknown>(
        `${api}/api/project-setups/${draftId}/source/configuration`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(sourceConfigurationPayload(source, expectedVersion, selected)),
        },
      );
      if (request !== requestNumber.current) return null;
      const committed = await sourceAfterMutation(envelope, expectedVersion, request);
      if (!committed) return null;
      applySource(committed.source, committed.version);
      setNotice(
        committed.source.reconciliation?.ready
          ? "Reconciliation passed for the exact source and current ladder. Review the source assertion before finalization."
          : "Source choices were saved. Resolve the reported findings before finalization.",
      );
      return committed.version;
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "Source choices could not be saved. Earlier project answers remain saved.",
          ),
        );
      }
      return null;
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

  const saveConfigurationRef = useRef<() => Promise<number | null>>(saveConfiguration);
  saveConfigurationRef.current = saveConfiguration;
  useEffect(() => {
    const registeredSave = () => saveConfigurationRef.current();
    onRegisterSourceSave?.(registeredSave);
    return () => onRegisterSourceSave?.(null);
  }, [onRegisterSourceSave]);

  const reconcile = async () => {
    if (!source) return;
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return;
      const envelope = await apiRequest<unknown>(
        `${api}/api/project-setups/${draftId}/source/reconcile`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ expectedVersion }),
        },
      );
      if (request !== requestNumber.current) return;
      const committed = await sourceAfterMutation(envelope, expectedVersion, request);
      if (!committed) return;
      applySource(committed.source, committed.version);
      setNotice(
        committed.source.reconciliation?.ready
          ? "The source was revalidated against the current ladder."
          : "The source was revalidated. Resolve the findings shown below before finalization.",
      );
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "The source could not be revalidated. Earlier answers remain saved.",
          ),
        );
      }
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

  const filteredOptions = options.filter((option) =>
    `${option.projectName} ${option.name} ${option.displayNumber} ${option.state}`
      .toLocaleLowerCase()
      .includes(search.trim().toLocaleLowerCase()),
  );
  const suggestionRelationships = source?.ladderSuggestion
    ? summarizeLadderSuggestionRelationships(source.ladderSuggestion.relationships)
    : [];

  return (
    <section className="setupSourcePanel" aria-label="Baseline source configuration">
      <header className="setupSourceHeader">
        <div>
          <h3>
            {kind === "AeroLinkBaseline"
              ? "Select an authorized AeroLink baseline"
              : "Stage a baseline from another tool"}
          </h3>
          <p>
            {kind === "AeroLinkBaseline"
              ? "Choose an exact Frozen or Released baseline that the server authorizes and can materialize. The source lifecycle and approvals remain source facts."
              : "Upload one supported ReqIF, CSV, or XLSX source. Source version or date stays unknown when the file does not provide it; uploader, time, and file hash remain attributable."}
          </p>
        </div>
        <button type="button" onClick={() => void loadSource()} disabled={busy || optionsBusy}>
          {busy || optionsBusy ? "Loading…" : "Refresh source"}
        </button>
      </header>

      {error && (
        <p className="setupSourceError" role="alert">
          {error}
        </p>
      )}
      {notice && (
        <p className="setupSourceNotice" role="status">
          {notice}
        </p>
      )}

      {kind === "AeroLinkBaseline" && (
        <div className="setupNativeSourcePicker">
          <label>
            Search loaded authorized baselines
            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Project, baseline, number, or lifecycle"
            />
            <small>Results are server-paged. This filter searches the loaded page only.</small>
          </label>
          <div className="setupSourceTableWrap">
            <table className="setupSourceTable">
              <caption>Authorized source baselines</caption>
              <thead>
                <tr>
                  <th scope="col">Baseline</th>
                  <th scope="col">Project</th>
                  <th scope="col">State</th>
                  <th scope="col">Materialized content</th>
                  <th scope="col">Action</th>
                </tr>
              </thead>
              <tbody>
                {filteredOptions.map((option) => (
                  <tr key={option.baselineId}>
                    <td>
                      <strong>{option.name}</strong>
                      <small>{option.displayNumber || "Number unavailable"}</small>
                    </td>
                    <td>{option.projectName}</td>
                    <td>{option.state}</td>
                    <td>
                      {formatCount(option.requirementsCount)} requirements ·{" "}
                      {formatCount(option.casesCount)} cases · {formatCount(option.proceduresCount)}{" "}
                      procedures · {formatCount(option.evidenceCount)} evidence facts
                    </td>
                    <td>
                      <button
                        type="button"
                        onClick={() => void selectNative(option.baselineId)}
                        disabled={busy}
                      >
                        Select exact baseline
                      </button>
                    </td>
                  </tr>
                ))}
                {!filteredOptions.length && (
                  <tr>
                    <td colSpan={5}>
                      {optionsBusy
                        ? "Loading authorized baselines…"
                        : "No authorized baselines match this page."}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <div className="setupSourcePager" aria-label="Baseline pages">
            <button
              type="button"
              onClick={() =>
                void loadOptions(
                  Math.max(0, sourceOffset - 50),
                  draftVersion,
                  requestNumber.current,
                )
              }
              disabled={sourceOffset === 0 || optionsBusy}
            >
              Previous page
            </button>
            <span>
              {sourceTotal
                ? `${sourceOffset + 1}–${Math.min(sourceOffset + 50, sourceTotal)} of ${sourceTotal}`
                : "No source count available"}
            </span>
            <button
              type="button"
              onClick={() =>
                void loadOptions(sourceOffset + 50, draftVersion, requestNumber.current)
              }
              disabled={sourceOffset + 50 >= sourceTotal || optionsBusy}
            >
              Next page
            </button>
          </div>
        </div>
      )}

      {kind === "ExternalBaseline" && (
        <div className="setupExternalUploader">
          <label>
            Baseline file (ReqIF, CSV, or XLSX)
            <input
              type="file"
              accept={sourceUploadAccept}
              onChange={(event) => setSelectedFile(event.target.files?.[0] ?? null)}
            />
            <small>
              Files are staged and parsed by the server. A ready Project is never created by upload.
            </small>
          </label>
          {selectedFile && (
            <p className="setupSourceFile">
              Selected: {selectedFile.name} · {formatCount(selectedFile.size)} bytes
            </p>
          )}
          <button
            type="button"
            onClick={() => void uploadExternal()}
            disabled={busy || !selectedFile}
          >
            Upload and analyze source
          </button>
        </div>
      )}

      {source && (
        <>
          <div className="setupSourceIdentity">
            <div>
              <strong>Exact source</strong>
              <span>{source.displayName}</span>
            </div>
            <div>
              <strong>Source kind</strong>
              <span>
                {source.kind === "AeroLinkBaseline" ? "AeroLink baseline" : "External baseline"}
              </span>
            </div>
            <div>
              <strong>Lifecycle</strong>
              <span>{source.sourceState || "Source lifecycle not provided"}</span>
            </div>
            <div>
              <strong>SHA-256</strong>
              <code>{source.sha256 || "Server hash pending"}</code>
            </div>
            {source.fileName && (
              <div>
                <strong>File</strong>
                <span>
                  {source.fileName}
                  {source.format ? ` · ${source.format}` : ""}
                </span>
              </div>
            )}
          </div>

          {source.ladderSuggestion && (
            <section className="setupSourceLadderSuggestion" aria-label="Source-informed ladder suggestion">
              <header>
                <div>
                  <h4>Source-informed ladder suggestion</h4>
                  <p>
                    The server derived these maintained levels and typed relationships from source
                    structure. Review them before applying; this does not invent project ancestry.
                  </p>
                </div>
                {onApplyLadderSuggestion && (
                  <button
                    type="button"
                    onClick={() => onApplyLadderSuggestion(source.ladderSuggestion!)}
                    disabled={busy}
                  >
                    Review and use compatible levels
                  </button>
                )}
              </header>
              <div className="setupSourceSuggestionColumns">
                <div>
                  <strong>Suggested maintained levels</strong>
                  {source.ladderSuggestion.levels.length ? (
                    <ul>
                      {source.ladderSuggestion.levels.map((level, levelIndex) => <li key={`${level}-${levelIndex}`}>{level}</li>)}
                    </ul>
                  ) : (
                    <p className="setupSourcePending">No supported levels were found.</p>
                  )}
                </div>
                <div>
                  <strong>Typed source relationships</strong>
                  {suggestionRelationships.length ? (
                    <ul>
                      {suggestionRelationships.map((relationship) => (
                        <li
                          key={JSON.stringify([
                            relationship.type,
                            relationship.sourceLevel,
                            relationship.targetLevel,
                          ])}
                        >
                          {sourceRelationLabel(relationship.type)}: {relationship.sourceLevel} → {relationship.targetLevel}
                          {` (${formatCount(relationship.count)} observed source ${relationship.count === 1 ? "relationship" : "relationships"})`}
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <p className="setupSourcePending">No cross-level source relationships were found.</p>
                  )}
                </div>
              </div>
              {source.ladderSuggestion.findings.length > 0 && (
                <div className="setupSourceSuggestionFindings">
                  <strong>Review findings before applying</strong>
                  <ul>
                    {source.ladderSuggestion.findings.map((finding, findingIndex) => <li key={`${finding}-${findingIndex}`}>{finding}</li>)}
                  </ul>
                </div>
              )}
            </section>
          )}

          {source.kind === "ExternalBaseline" && (
            <fieldset className="setupSourceMetadata">
              <legend>Source metadata</legend>
              <p className="setupSourceHint">
                Metadata is read from the uploaded source. Missing source facts remain unknown; the
                creator cannot replace them with browser-entered values.
              </p>
              <div className="setupSourceMetadataGrid">
                {(
                  [
                    "sourceSystem",
                    "sourceSystemVersion",
                    "sourceBaselineName",
                    "sourceBaselineDate",
                    "extractedBy",
                    "extractedAt",
                  ] as const
                ).map((field) => (
                  <label key={field}>
                    {field === "sourceSystem"
                      ? "Source system"
                      : field === "sourceSystemVersion"
                          ? "Source system version"
                          : field === "sourceBaselineName"
                            ? "Source baseline name"
                            : field === "sourceBaselineDate"
                              ? "Source baseline date"
                              : field === "extractedBy"
                                ? "Extracted by"
                                : "Extracted at"}
                    <output
                      aria-label={
                        field === "sourceSystem"
                          ? "Source system"
                          : field === "sourceSystemVersion"
                            ? "Source system version"
                            : field === "sourceBaselineName"
                              ? "Source baseline name"
                              : field === "sourceBaselineDate"
                                ? "Source baseline date"
                                : field === "extractedBy"
                                  ? "Extracted by"
                                  : "Extracted at"
                      }
                    >
                      {source.metadata[field]?.trim() || "Unknown (not reported by source)"}
                    </output>
                  </label>
                ))}
              </div>
            </fieldset>
          )}

          <fieldset className="setupSourceCategories">
            <legend>Inherited categories and dependencies</legend>
            <p className="setupSourceHint">
              Select only source facts this Project should inherit. Dependencies are checked here
              for clarity and rechecked by the server.
            </p>
            <div className="setupSourceCategoryGrid">
              {source.categories.map((category) => (
                <label key={category.key} className={!category.supported ? "unsupported" : ""}>
                  <input
                    type="checkbox"
                    checked={selected.includes(category.key)}
                    onChange={() => toggleCategory(category)}
                    disabled={!category.supported || busy}
                  />
                  <span>
                    <strong>{sourceCategoryLabel(category)}</strong>
                    <small>
                      {formatCount(category.count)} observed ·{" "}
                      {category.requires.length
                        ? `Requires ${category.requires.join(", ")}`
                        : "No category dependency"}
                    </small>
                    {!category.supported && (
                      <small>{category.reason || "Unsupported by this source path"}</small>
                    )}
                  </span>
                </label>
              ))}
            </div>
            {!source.categories.length && (
              <p className="setupSourcePending">
                The source has not reported supported categories yet.
              </p>
            )}
          </fieldset>

          <section className="setupSourceMappings">
            <header>
              <div>
                <h4>Module mappings</h4>
                <p>
                  Map every observed source attribute explicitly. Exclude unsupported values with a
                  reason.
                </p>
              </div>
            </header>
            {!source.modules.length && (
              <p className="setupSourcePending">No source modules are available for mapping yet.</p>
            )}
            {source.modules.map((module, moduleIndex) => (
              <article className="setupSourceModule" key={module.key}>
                <header>
                  <label>
                    <input
                      type="checkbox"
                      checked={module.include}
                      onChange={(event) =>
                        updateSourceAndNotice(
                          updateModule(source, moduleIndex, { include: event.target.checked }),
                        )
                      }
                    />{" "}
                    Include {module.name}
                  </label>
                  <span>{formatCount(module.objectCount)} observed objects</span>
                  <label>
                    Ladder level for {module.name}
                    <select
                      value={module.level ?? ""}
                      onChange={(event) =>
                        updateSourceAndNotice(
                          updateModule(source, moduleIndex, {
                            level: event.target.value || undefined,
                          }),
                        )
                      }
                    >
                      <option value="">Choose supported level</option>
                      {levelOptions.map((level) => (
                        <option value={level.id} key={level.id}>
                          {level.label}
                        </option>
                      ))}
                    </select>
                  </label>
                </header>
                <label className="setupSourceExclusion">
                  Exclusion reason for this module (required when excluded)
                  <input
                    value={module.exclusionReason ?? ""}
                    onChange={(event) =>
                      updateSourceAndNotice(
                        updateModule(source, moduleIndex, { exclusionReason: event.target.value }),
                      )
                    }
                    disabled={module.include}
                  />
                </label>
                {(module.objects ?? []).length > 0 ? (
                  <div className="setupSourceObjectMappings">
                    {(() => {
                      const objects = module.objects ?? [];
                      const pageCount = Math.ceil(objects.length / SOURCE_OBJECT_PAGE_SIZE);
                      const requestedPage = objectPages[module.key] ?? 0;
                      const page = Math.min(requestedPage, Math.max(0, pageCount - 1));
                      const pageStart = page * SOURCE_OBJECT_PAGE_SIZE;
                      const visibleObjects = objects.slice(
                        pageStart,
                        pageStart + SOURCE_OBJECT_PAGE_SIZE,
                      );
                      return (
                        <>
                          <p className="setupSourceHint">
                            {module.hasDivergentObjectMappings
                              ? "This module has decisions that differ between source objects. Review each exact object below. Module controls apply to every object on every page; individual controls override them."
                              : "Review each exact source object below. Module controls apply to every object on every page; individual controls can override the copied defaults."}
                          </p>
                          {visibleObjects.map((object) => {
                            const decision = module.objectMappings?.[object.key]
                              ?? defaultSourceObjectMapping(module);
                            return (
                              <section className="setupSourceObjectMapping" key={object.key}>
                                <header>
                                  <strong>{object.sourceIdentifier || object.key}</strong>
                                  <small>{object.key} · {object.kind || "Source object"}</small>
                                </header>
                                <div className="setupSourceObjectControls">
                                  <label>
                                    <input
                                      type="checkbox"
                                      checked={decision.include}
                                      onChange={(event) => updateSourceAndNotice(updateObjectMapping(
                                        source,
                                        moduleIndex,
                                        object.key,
                                        { include: event.target.checked },
                                      ))}
                                    /> Include this source object
                                  </label>
                                  <label>
                                    Ladder level
                                    <select
                                      value={decision.level ?? ""}
                                      onChange={(event) => updateSourceAndNotice(updateObjectMapping(
                                        source,
                                        moduleIndex,
                                        object.key,
                                        { level: event.target.value || undefined },
                                      ))}
                                    >
                                      <option value="">Choose supported level</option>
                                      {levelOptions.map((level) => (
                                        <option value={level.id} key={level.id}>{level.label}</option>
                                      ))}
                                    </select>
                                  </label>
                                  <label>
                                    Exclusion reason
                                    <input
                                      value={decision.exclusionReason ?? ""}
                                      onChange={(event) => updateSourceAndNotice(updateObjectMapping(
                                        source,
                                        moduleIndex,
                                        object.key,
                                        { exclusionReason: event.target.value },
                                      ))}
                                      disabled={decision.include}
                                    />
                                  </label>
                                </div>
                                <AttributeMappingTable
                                  title={`${module.name} ${object.sourceIdentifier || object.key}`}
                                  mappings={decision.mappings}
                                  onChange={(mappings) => updateSourceAndNotice(updateObjectMapping(
                                    source,
                                    moduleIndex,
                                    object.key,
                                    { mappings },
                                  ))}
                                />
                              </section>
                            );
                          })}
                          <div className="setupSourcePager setupSourceObjectPager" aria-label={`${module.name} object pages`}>
                            <button
                              type="button"
                              onClick={() => setObjectPages((current) => ({
                                ...current,
                                [module.key]: Math.max(0, page - 1),
                              }))}
                              disabled={page === 0 || busy}
                            >
                              Previous objects page
                            </button>
                            <span>
                              {pageStart + 1}–{Math.min(pageStart + SOURCE_OBJECT_PAGE_SIZE, objects.length)} of {objects.length} source objects
                            </span>
                            <button
                              type="button"
                              onClick={() => setObjectPages((current) => ({
                                ...current,
                                [module.key]: Math.min(pageCount - 1, page + 1),
                              }))}
                              disabled={page >= pageCount - 1 || busy}
                            >
                              Next objects page
                            </button>
                          </div>
                        </>
                      );
                    })()}
                  </div>
                ) : (
                  <AttributeMappingTable
                    title={`Attributes in ${module.name}`}
                    mappings={module.mappings}
                    onChange={(mappings) => updateSourceAndNotice(
                      updateModule(source, moduleIndex, { mappings }),
                    )}
                  />
                )}
              </article>
            ))}
          </section>

          <section className="setupSourceRelations">
            <header>
              <div>
                <h4>Relationships</h4>
                <p>
                  Keep the source relation direction explicit. Exclude unsupported relations with a
                  reason.
                </p>
              </div>
            </header>
            {(() => {
              const relationCount = source.relations.length;
              const pageCount = Math.ceil(relationCount / SOURCE_RELATION_PAGE_SIZE);
              const page = Math.min(relationPage, Math.max(0, pageCount - 1));
              const pageStart = page * SOURCE_RELATION_PAGE_SIZE;
              const visibleRelations = source.relations.slice(
                pageStart,
                pageStart + SOURCE_RELATION_PAGE_SIZE,
              );
              return (
                <>
                  {visibleRelations.map((relation, visibleIndex) => {
                    // The page is presentation only. Always update the relation's original index
                    // in the canonical complete SourceView so page navigation cannot retarget an
                    // edit to a different relation.
                    const relationIndex = pageStart + visibleIndex;
                    return (
                      <div
                        className="setupSourceRelation"
                        data-source-relation-key={relation.key}
                        key={`${relation.key ?? relation.sourceType}-${relation.sourceKey ?? ""}-${relation.targetKey ?? ""}-${relationIndex}`}
                      >
                        <label>
                          <input
                            type="checkbox"
                            checked={relation.include}
                            onChange={(event) =>
                              updateSourceAndNotice(
                                updateRelation(source, relationIndex, { include: event.target.checked }),
                              )
                            }
                          />{" "}
                          Include {sourceRelationLabel(relation.sourceType)} ({formatCount(relation.count)} observed)
                        </label>
                        <small className="setupSourceRelationType">
                          Source relationship type: {sourceRelationLabel(relation.type || relation.sourceType)}
                          {relation.sourceKey && relation.targetKey
                            ? ` · ${relation.sourceKey} → ${relation.targetKey}`
                            : ""}
                          {relation.attributes && Object.keys(relation.attributes).length > 0
                            ? ` · ${Object.keys(relation.attributes).length} source attributes require explicit handling`
                            : ""}
                        </small>
                        {relation.include && (
                          <>
                            {["RequirementTrace", "AllocatedFrom", "DerivedFrom"].includes(
                              relation.type ?? relation.sourceType,
                            ) && (
                              <label>
                                Trace type
                                <select
                                  aria-label={`Trace type for ${relation.sourceType}`}
                                  value={relation.mappingType ?? ""}
                                  onChange={(event) =>
                                    updateSourceAndNotice(
                                      updateRelation(source, relationIndex, {
                                        mappingType:
                                          event.target.value === ""
                                            ? undefined
                                            : (event.target.value as "AllocatedFrom" | "DerivedFrom"),
                                      }),
                                    )
                                  }
                                >
                                  <option value="">Choose supported trace type</option>
                                  <option value="AllocatedFrom">Allocated from</option>
                                  <option value="DerivedFrom">Derived from</option>
                                </select>
                              </label>
                            )}
                            {!['CaseProcedure', 'VerificationCoverage', 'EvidenceExecution'].includes(relation.type ?? "") && (
                              <label>
                                Direction
                                <select
                                  aria-label={`Relation direction for ${relation.sourceType}`}
                                  value={
                                    relation.sourceIsParent === undefined
                                      ? ""
                                      : relation.sourceIsParent
                                        ? "parent"
                                        : "child"
                                  }
                                  onChange={(event) =>
                                    updateSourceAndNotice(
                                      updateRelation(source, relationIndex, {
                                        sourceIsParent:
                                          event.target.value === ""
                                            ? undefined
                                            : event.target.value === "parent",
                                      }),
                                    )
                                  }
                                >
                                  <option value="">Choose direction</option>
                                  <option value="parent">Source is parent</option>
                                  <option value="child">Source is child</option>
                                </select>
                              </label>
                            )}
                          </>
                        )}
                        <label>
                          Exclusion reason
                          <input
                            value={relation.exclusionReason ?? ""}
                            onChange={(event) =>
                              updateSourceAndNotice(
                                updateRelation(source, relationIndex, {
                                  exclusionReason: event.target.value,
                                }),
                              )
                            }
                            disabled={relation.include}
                          />
                        </label>
                      </div>
                    );
                  })}
                  {relationCount > 0 && (
                    <div
                      className="setupSourcePager setupSourceRelationPager"
                      aria-label="Source relationship pages"
                    >
                      <button
                        type="button"
                        onClick={() => setRelationPage((current) => Math.max(0, Math.min(current, page) - 1))}
                        disabled={page === 0 || busy}
                      >
                        Previous relationships page
                      </button>
                      <span aria-live="polite">
                        {pageStart + 1}–{Math.min(pageStart + SOURCE_RELATION_PAGE_SIZE, relationCount)} of {formatCount(relationCount)} source relationships
                      </span>
                      <button
                        type="button"
                        onClick={() => setRelationPage((current) => Math.min(pageCount - 1, Math.max(current, page) + 1))}
                        disabled={page >= pageCount - 1 || busy}
                      >
                        Next relationships page
                      </button>
                    </div>
                  )}
                </>
              );
            })()}
            {!source.relations.length && (
              <p className="setupSourcePending">No source relationships were observed.</p>
            )}
          </section>

          <section className="setupSourceFindings">
            <header>
              <div>
                <h4>Source findings</h4>
                <p>
                  Resolve every server finding with an explicit explanation. The browser cannot mark
                  reconciliation ready.
                </p>
              </div>
            </header>
            {source.findings.map((finding) => (
              <label key={finding.key}>
                {finding.message}
                <textarea
                  value={source.findingResolutions[finding.key] ?? ""}
                  onChange={(event) =>
                    updateSourceAndNotice({
                      ...source,
                      findingResolutions: {
                        ...source.findingResolutions,
                        [finding.key]: event.target.value,
                      },
                    })
                  }
                  rows={2}
                  placeholder="Explain the accepted resolution or exclusion"
                />
              </label>
            ))}
            {!source.findings.length && (
              <p className="setupSourceReady">No unresolved source findings were reported.</p>
            )}
          </section>

          <div className="setupSourceActions">
            <button type="button" onClick={() => void saveConfiguration()} disabled={busy}>
              Save choices and reconcile
            </button>
            <button
              type="button"
              onClick={() => void reconcile()}
              disabled={busy || !source.reconciliation}
            >
              Revalidate against current ladder
            </button>
          </div>
          {source.reconciliation && (
            <div
              className={
                source.reconciliation.ready
                  ? "setupSourceReconciliation ready"
                  : "setupSourceReconciliation"
              }
              role="status"
            >
              <strong>
                {source.reconciliation.ready
                  ? "Reconciliation ready"
                  : "Reconciliation needs attention"}
              </strong>
              <span>
                {formatCount(source.reconciliation.includedObjects)} of{" "}
                {formatCount(source.reconciliation.observedObjects)} objects included ·{" "}
                {formatCount(source.reconciliation.includedRelations)} of{" "}
                {formatCount(source.reconciliation.observedRelations)} relations included
              </span>
              {source.reconciliation.errors.length > 0 && (
                <ul>
                  {source.reconciliation.errors.map((item, errorIndex) => (
                    <li key={`${item}-${errorIndex}`}>{item}</li>
                  ))}
                </ul>
              )}
              {source.reconciliation.manifestHash && (
                <small>
                  Materialized source manifest hash: {source.reconciliation.manifestHash}
                </small>
              )}
            </div>
          )}
          <SourceAcceptanceFields
            source={source}
            accepted={accepted}
            password={password}
            disabled={!canAcceptSource}
            onAcceptedChange={(next) => {
              setAccepted(next);
              setNotice("Unsaved source acceptance. Save the project review before finalization.");
            }}
            onPasswordChange={setPassword}
          />
          {!canAcceptSource && (
            <p className="setupSourcePending" role="status">
              Only an AeroLink administrator can accept source facts for finalization. You can
              continue reviewing and saving this draft.
            </p>
          )}
        </>
      )}

      {!source && (
        <p className="setupSourcePending">
          No source is selected yet. Earlier Project answers remain recoverable while this source
          path is unavailable or awaiting selection.
        </p>
      )}
      <p className="setupSourceMatrix">
        Supported external formats in this delivery: ReqIF, CSV, and XLSX. Unsupported categories
        remain visible as server findings and must be excluded explicitly.
      </p>
    </section>
  );
}

export type { SetupLevelOption };
