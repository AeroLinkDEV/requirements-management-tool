import { useCallback, useEffect, useState } from "react";
import { ApiError, apiRequest, operationError } from "./apiClient";

type SourcePackage = {
  id: string;
  kind: string;
  fileName: string;
  format: string;
  sha256: string;
  sizeBytes: number;
  sourceTool: string;
  sourceBaselineId?: string | null;
  sourceProjectId?: string | null;
  sourceState?: string | null;
  selectedCategories: unknown;
  mapping: unknown;
  reconciliation?: unknown;
  manifestHash?: string | null;
  assertionHash?: string | null;
  capturedBy: string;
  capturedAt: string;
  materializedBaselineId?: string | null;
  updatedAt: string;
};

type SourceAcceptance = {
  userId: string;
  userName: string;
  displayName: string;
  signedAt: string;
  action: string;
  meaning: string;
  contentHash: string;
  authority: string;
  rationale: string;
};

type SourceRecord = {
  id: string;
  packageId: string;
  baselineId: string;
  targetKind: string;
  targetId: string;
  targetRevisionId?: string | null;
  sourceKey: string;
  sourceModule: string;
  sourceIdentifier: string;
  sourceRevision: string;
  sourceState: string;
  sourceSnapshot: unknown;
  createdAt: string;
};

type InceptionSourceProjection = {
  projectId: string;
  package?: SourcePackage | null;
  acceptance?: SourceAcceptance | null;
  records: SourceRecord[];
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function text(value: unknown) {
  return typeof value === "string" ? value : "";
}

function formatDate(value?: string | null) {
  if (!value) return "Not recorded";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "Recorded time unavailable" : date.toLocaleString();
}

function formatSize(value: number) {
  return value > 0 ? `${new Intl.NumberFormat().format(value)} bytes` : "Size not reported";
}

function decodeProjection(value: unknown): InceptionSourceProjection | null {
  const row = asRecord(value);
  if (!row) return null;
  const projectId = text(row.projectId).trim();
  const records = Array.isArray(row.records)
    ? row.records.flatMap((entry) => {
        const item = asRecord(entry);
        if (!item) return [];
        const id = text(item.id).trim();
        if (!id) return [];
        return [{
          id,
          packageId: text(item.packageId).trim(),
          baselineId: text(item.baselineId).trim(),
          targetKind: text(item.targetKind).trim() || "Source record",
          targetId: text(item.targetId).trim(),
          targetRevisionId: text(item.targetRevisionId).trim() || null,
          sourceKey: text(item.sourceKey).trim(),
          sourceModule: text(item.sourceModule).trim(),
          sourceIdentifier: text(item.sourceIdentifier).trim(),
          sourceRevision: text(item.sourceRevision).trim(),
          sourceState: text(item.sourceState).trim(),
          sourceSnapshot: item.sourceSnapshot,
          createdAt: text(item.createdAt),
        }];
      })
    : [];
  if (!projectId || !records.length) return null;
  const packageRow = asRecord(row.package);
  const packageView = packageRow
    ? {
        id: text(packageRow.id).trim(),
        kind: text(packageRow.kind).trim() || "Source",
        fileName: text(packageRow.fileName).trim(),
        format: text(packageRow.format).trim(),
        sha256: text(packageRow.sha256).trim(),
        sizeBytes: typeof packageRow.sizeBytes === "number" ? packageRow.sizeBytes : 0,
        sourceTool: text(packageRow.sourceTool).trim(),
        sourceBaselineId: text(packageRow.sourceBaselineId).trim() || null,
        sourceProjectId: text(packageRow.sourceProjectId).trim() || null,
        sourceState: text(packageRow.sourceState).trim() || null,
        selectedCategories: packageRow.selectedCategories,
        mapping: packageRow.mapping,
        reconciliation: packageRow.reconciliation,
        manifestHash: text(packageRow.manifestHash).trim() || null,
        assertionHash: text(packageRow.assertionHash).trim() || null,
        capturedBy: text(packageRow.capturedBy).trim(),
        capturedAt: text(packageRow.capturedAt),
        materializedBaselineId: text(packageRow.materializedBaselineId).trim() || null,
        updatedAt: text(packageRow.updatedAt),
      }
    : null;
  const acceptanceRow = asRecord(row.acceptance);
  const acceptance = acceptanceRow
    ? {
        userId: text(acceptanceRow.userId),
        userName: text(acceptanceRow.userName),
        displayName: text(acceptanceRow.displayName),
        signedAt: text(acceptanceRow.signedAt),
        action: text(acceptanceRow.action),
        meaning: text(acceptanceRow.meaning),
        contentHash: text(acceptanceRow.contentHash),
        authority: text(acceptanceRow.authority),
        rationale: text(acceptanceRow.rationale),
      }
    : null;
  return { projectId, package: packageView, acceptance, records };
}

function snapshotEntries(value: unknown) {
  const row = asRecord(value);
  if (!row) return [];
  return Object.entries(row).filter(([key]) => key.toLowerCase() !== "storagekey");
}

function snapshotValue(value: unknown) {
  if (typeof value === "string") return value || "Empty source value";
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  if (Array.isArray(value)) return `${value.length} recorded value${value.length === 1 ? "" : "s"}`;
  if (value && typeof value === "object") return "Structured source fact";
  return "Unknown source value";
}

export default function InceptionSourceProvenancePanel({
  api,
  projectId,
  projectName,
}: {
  api: string;
  projectId: string;
  projectName: string;
}) {
  const [projection, setProjection] = useState<InceptionSourceProjection | null>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const response = await apiRequest<unknown>(`${api}/api/projects/${projectId}/inception-source`);
      const decoded = decodeProjection(response);
      if (!decoded || decoded.projectId !== projectId)
        throw new Error("The source provenance service returned an invalid project projection.");
      setProjection(decoded);
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 404) {
        setProjection(null);
      } else {
        setError(operationError(failure, "The project's source provenance could not be loaded."));
      }
    } finally {
      setLoading(false);
    }
  }, [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <section className="projectInceptionProvenance" aria-labelledby="project-inception-provenance-heading">
      <header className="projectConfigurationPanelHeader">
        <div>
          <h2 id="project-inception-provenance-heading">Source provenance</h2>
          <p>
            Exact source facts retained when {projectName} was created. Source approvals, signatures,
            and execution evidence remain facts about the source; they are not new-project approvals or
            new test executions.
          </p>
        </div>
        <button type="button" onClick={() => void load()} disabled={loading}>
          {loading ? "Loading…" : "Refresh provenance"}
        </button>
      </header>
      {error && (
        <p className="projectConfigurationError" role="alert">
          {error} <button type="button" onClick={() => void load()}>Retry</button>
        </p>
      )}
      {loading && <p>Loading the server-recorded source facts…</p>}
      {!loading && !error && !projection && (
        <p className="projectConfigurationNotice">
          No inherited source was recorded for this project. This is the truthful state for a fresh start.
        </p>
      )}
      {!loading && !error && projection && (
        <>
          {projection.package && (
            <section className="projectInceptionProvenanceCard" aria-label="Source package identity">
              <h3>Exact source snapshot</h3>
              <dl className="projectInceptionProvenanceGrid">
                <div><dt>Source kind</dt><dd>{projection.package.kind}</dd></div>
                <div><dt>Format</dt><dd>{projection.package.format || "Not reported"}</dd></div>
                <div><dt>File</dt><dd>{projection.package.fileName || "Not reported"}</dd></div>
                <div><dt>SHA-256</dt><dd><code>{projection.package.sha256 || "Not reported"}</code></dd></div>
                <div><dt>Source lifecycle</dt><dd>{projection.package.sourceState || "Not reported"}</dd></div>
                <div><dt>Captured by</dt><dd>{projection.package.capturedBy || "Not reported"}</dd></div>
                <div><dt>Captured at</dt><dd>{formatDate(projection.package.capturedAt)}</dd></div>
                <div><dt>Source size</dt><dd>{formatSize(projection.package.sizeBytes)}</dd></div>
                {projection.package.sourceBaselineId && <div><dt>Source baseline ID</dt><dd><code>{projection.package.sourceBaselineId}</code></dd></div>}
                {projection.package.sourceProjectId && <div><dt>Source project ID</dt><dd><code>{projection.package.sourceProjectId}</code></dd></div>}
                {projection.package.manifestHash && <div><dt>Reconciled manifest</dt><dd><code>{projection.package.manifestHash}</code></dd></div>}
              </dl>
            </section>
          )}
          {projection.acceptance && (
            <section className="projectInceptionProvenanceCard" aria-label="Source acceptance fact">
              <h3>Source acceptance fact</h3>
              <p className="projectConfigurationNotice">
                This records acceptance of the exact source assertion for inception. It does not approve
                the new project or claim that its engineering content was executed.
              </p>
              <dl className="projectInceptionProvenanceGrid">
                <div><dt>Accepted by</dt><dd>{projection.acceptance.displayName || projection.acceptance.userName || "Identity unavailable"}</dd></div>
                <div><dt>Authority</dt><dd>{projection.acceptance.authority || "Not reported"}</dd></div>
                <div><dt>Action</dt><dd>{projection.acceptance.action || "Not reported"}</dd></div>
                <div><dt>Accepted at</dt><dd>{formatDate(projection.acceptance.signedAt)}</dd></div>
                <div><dt>Assertion hash</dt><dd><code>{projection.acceptance.contentHash || "Not reported"}</code></dd></div>
              </dl>
              {projection.acceptance.meaning && <p><strong>Meaning:</strong> {projection.acceptance.meaning}</p>}
              {projection.acceptance.rationale && <p><strong>Rationale:</strong> {projection.acceptance.rationale}</p>}
            </section>
          )}
          <section className="projectInceptionProvenanceCard" aria-label="Inherited source records">
            <h3>Inherited source records ({projection.records.length})</h3>
            <p>Each row preserves the source identifier, source revision, lifecycle state, and target link recorded during materialization.</p>
            <div className="projectInceptionProvenanceTableWrap">
              <table className="configurationHistory">
                <thead><tr><th>Target</th><th>Source identity</th><th>Source revision</th><th>Source state</th><th>Recorded facts</th></tr></thead>
                <tbody>
                  {projection.records.map((record) => (
                    <tr key={record.id} data-source-record-key={record.sourceKey}>
                      <td><strong>{record.targetKind}</strong><small><code>{record.targetId}</code></small>{record.targetRevisionId && <small>Revision <code>{record.targetRevisionId}</code></small>}</td>
                      <td><strong>{record.sourceIdentifier || record.sourceKey}</strong><small>{record.sourceModule || "Source module unavailable"}</small><small><code>{record.sourceKey}</code></small></td>
                      <td>{record.sourceRevision || "Not reported"}</td>
                      <td>{record.sourceState || "Not reported"}</td>
                      <td>
                        {snapshotEntries(record.sourceSnapshot).length ? (
                          <details>
                            <summary>View source facts</summary>
                            <dl className="projectInceptionSourceFacts">
                              {snapshotEntries(record.sourceSnapshot).map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{snapshotValue(value)}</dd></div>)}
                            </dl>
                          </details>
                        ) : "No additional source fields recorded"}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}
    </section>
  );
}
