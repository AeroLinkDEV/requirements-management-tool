import { useEffect, useRef, useState } from "react";
import { ApiError, apiRequest, operationError } from "./apiClient";
import {
  decodeNativeSourceOptions,
  decodeSourceView,
  sourceConfigurationPayload,
  sourcePageUrl,
  sourceUploadAccept,
  type NativeSourceOption,
  type SourceCategory,
  type SourceDraftState,
  type SourceKind,
  type SourceMappingDestination,
  type SourceModule,
  type SourceRelation,
  type SourceView,
} from "./projectSetupSource";
import "./ProjectSetupSourcePanel.css";

type SetupLevelOption = { id: string; label: string };

export type ProjectSetupSourcePanelProps = {
  api: string;
  draftId: string;
  draftVersion: number;
  kind: SourceKind;
  selectedCategories: string[];
  levelOptions: SetupLevelOption[];
  initialState: SourceDraftState;
  onSelectedCategoriesChange: (categories: string[]) => void;
  beforeSourceCall: () => Promise<number | null>;
  onSourceVersion: (version: number) => void;
  onSourceIdentity: (id: string) => void;
  onSourceStateChange: (state: SourceDraftState) => void;
};

type SourceEnvelope = { draftVersion?: number; source?: unknown };

const mappingDestinations: { value: SourceMappingDestination; label: string }[] = [
  { value: "SourceOnly", label: "Keep as source-only attribute" },
  { value: "Statement", label: "Requirement statement" },
  { value: "Rationale", label: "Requirement rationale" },
  { value: "VerificationMethod", label: "Verification method" },
  { value: "SourceIdentifier", label: "Source identifier" },
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
    modules: source.modules.map((module, currentIndex) =>
      currentIndex === index ? { ...module, ...patch } : module,
    ),
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

export function SourceAcceptanceFields({
  source,
  accepted,
  password,
  onAcceptedChange,
  onPasswordChange,
}: {
  source: SourceView | null;
  accepted: boolean;
  password: string;
  onAcceptedChange: (accepted: boolean) => void;
  onPasswordChange: (password: string) => void;
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
  selectedCategories,
  levelOptions,
  initialState,
  onSelectedCategoriesChange,
  beforeSourceCall,
  onSourceVersion,
  onSourceIdentity,
  onSourceStateChange,
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
  const [metadata, setMetadata] = useState<SourceView["metadata"]>(
    initialState.source?.kind === kind ? initialState.source.metadata : {},
  );
  const [selected, setSelected] = useState<string[]>(
    initialState.source?.kind === kind
      ? selectedCategoryKeys(initialState.source, selectedCategories)
      : [...selectedCategories],
  );
  const [busy, setBusy] = useState(false);
  const [optionsBusy, setOptionsBusy] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const requestNumber = useRef(0);

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

  const applySource = (next: SourceView | null, version: number) => {
    if (!next) return;
    setSource(next);
    setMetadata(next.metadata);
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
      const decoded = sourceFromEnvelope(envelope);
      if (!decoded.source || decoded.source.kind !== "AeroLinkBaseline") {
        throw new Error("The source service did not return the selected native baseline.");
      }
      applySource(decoded.source, responseVersion(decoded, expectedVersion));
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
      const decoded = sourceFromEnvelope(envelope);
      if (!decoded.source || decoded.source.kind !== "ExternalBaseline") {
        throw new Error("The source service did not return the uploaded baseline.");
      }
      applySource(decoded.source, responseVersion(decoded, expectedVersion));
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
    setSource({ ...next });
    setAccepted(false);
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

  const saveConfiguration = async () => {
    if (!source) return;
    const request = ++requestNumber.current;
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const expectedVersion = await beforeSourceCall();
      if (expectedVersion === null || request !== requestNumber.current) return;
      const envelope = await apiRequest<unknown>(
        `${api}/api/project-setups/${draftId}/source/configuration`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(sourceConfigurationPayload(source, expectedVersion, selected)),
        },
      );
      if (request !== requestNumber.current) return;
      const decoded = sourceFromEnvelope(envelope);
      if (!decoded.source)
        throw new Error("The source service did not return reconciliation results.");
      applySource(decoded.source, responseVersion(decoded, expectedVersion));
      setNotice(
        decoded.source.reconciliation?.ready
          ? "Reconciliation passed for the exact source and current ladder. Review the source assertion before finalization."
          : "Source choices were saved. Resolve the reported findings before finalization.",
      );
    } catch (failure) {
      if (request === requestNumber.current) {
        setError(
          operationError(
            failure,
            "Source choices could not be saved. Earlier project answers remain saved.",
          ),
        );
      }
    } finally {
      if (request === requestNumber.current) setBusy(false);
    }
  };

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
      const decoded = sourceFromEnvelope(envelope);
      if (!decoded.source)
        throw new Error("The source service did not return reconciliation results.");
      applySource(decoded.source, responseVersion(decoded, expectedVersion));
      setNotice(
        decoded.source.reconciliation?.ready
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

  const updateMetadata = (key: keyof SourceView["metadata"], value: string) => {
    if (!source) return;
    const next = { ...source, metadata: { ...metadata, [key]: value } };
    setMetadata(next.metadata);
    updateSourceAndNotice(next);
  };

  const filteredOptions = options.filter((option) =>
    `${option.projectName} ${option.name} ${option.displayNumber} ${option.state}`
      .toLocaleLowerCase()
      .includes(search.trim().toLocaleLowerCase()),
  );

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

          {source.kind === "ExternalBaseline" && (
            <fieldset className="setupSourceMetadata">
              <legend>Source metadata</legend>
              <p className="setupSourceHint">
                Leave unavailable source facts blank. The server retains unknown version/date rather
                than guessing them.
              </p>
              <div className="setupSourceMetadataGrid">
                {(
                  [
                    "sourceSystem",
                    "sourceSystemVersion",
                    "sourceBaselineName",
                    "sourceBaselineDate",
                  ] as const
                ).map((field) => (
                  <label key={field}>
                    {field === "sourceSystem"
                      ? "Source system"
                      : field === "sourceSystemVersion"
                        ? "Source system version"
                        : field === "sourceBaselineName"
                          ? "Source baseline name"
                          : "Source baseline date"}
                    <input
                      value={metadata[field] ?? ""}
                      onChange={(event) => updateMetadata(field, event.target.value)}
                      placeholder={
                        field === "sourceBaselineDate" ? "Unknown if absent" : "Unknown if absent"
                      }
                    />
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
                <div className="setupSourceAttributeTableWrap">
                  <table className="setupSourceAttributeTable">
                    <caption>Attributes in {module.name}</caption>
                    <thead>
                      <tr>
                        <th scope="col">Source attribute</th>
                        <th scope="col">Destination</th>
                        <th scope="col">Reason</th>
                        <th scope="col">Values</th>
                      </tr>
                    </thead>
                    <tbody>
                      {module.mappings.map((mapping, mappingIndex) => (
                        <tr key={`${module.key}-${mapping.sourceAttribute}`}>
                          <td>{mapping.sourceAttribute}</td>
                          <td>
                            <select
                              aria-label={`Mapping for ${module.name} ${mapping.sourceAttribute}`}
                              value={mapping.destination}
                              onChange={(event) => {
                                const mappings = module.mappings.map((current, currentIndex) =>
                                  currentIndex === mappingIndex
                                    ? {
                                        ...current,
                                        destination: event.target.value as SourceMappingDestination,
                                      }
                                    : current,
                                );
                                updateSourceAndNotice(
                                  updateModule(source, moduleIndex, { mappings }),
                                );
                              }}
                            >
                              {mappingDestinations.map((item) => (
                                <option value={item.value} key={item.value}>
                                  {item.label}
                                </option>
                              ))}
                            </select>
                          </td>
                          <td>
                            <input
                              aria-label={`Reason for ${module.name} ${mapping.sourceAttribute}`}
                              value={mapping.reason ?? ""}
                              onChange={(event) => {
                                const mappings = module.mappings.map((current, currentIndex) =>
                                  currentIndex === mappingIndex
                                    ? { ...current, reason: event.target.value }
                                    : current,
                                );
                                updateSourceAndNotice(
                                  updateModule(source, moduleIndex, { mappings }),
                                );
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
                                      aria-label={`Destination value for ${module.name} ${mapping.sourceAttribute} ${valueMapping.sourceValue}`}
                                      value={valueMapping.destinationValue}
                                      onChange={(event) => {
                                        const mappings = module.mappings.map(
                                          (current, currentIndex) =>
                                            currentIndex === mappingIndex
                                              ? {
                                                  ...current,
                                                  valueMappings: current.valueMappings?.map(
                                                    (row, rowIndex) =>
                                                      rowIndex === valueIndex
                                                        ? {
                                                            ...row,
                                                            destinationValue: event.target.value,
                                                          }
                                                        : row,
                                                  ),
                                                }
                                              : current,
                                        );
                                        updateSourceAndNotice(
                                          updateModule(source, moduleIndex, { mappings }),
                                        );
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
                      {!module.mappings.length && (
                        <tr>
                          <td colSpan={4}>No source attributes were observed.</td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
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
            {source.relations.map((relation, relationIndex) => (
              <div className="setupSourceRelation" key={relation.sourceType}>
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
                  Include {relation.sourceType} ({formatCount(relation.count)} observed)
                </label>
                {relation.include && (
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
                            sourceIsParent: event.target.value === "parent",
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
            ))}
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
                  {source.reconciliation.errors.map((item) => (
                    <li key={item}>{item}</li>
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
            onAcceptedChange={(next) => {
              setAccepted(next);
              setNotice("Unsaved source acceptance. Save the project review before finalization.");
            }}
            onPasswordChange={setPassword}
          />
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
