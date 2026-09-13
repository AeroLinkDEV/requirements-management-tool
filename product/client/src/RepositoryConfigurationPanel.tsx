import { useCallback, useEffect, useState } from "react";
import { apiRequest, operationError } from "./apiClient";
import {
  decodeRepositoryResponse,
  decodeRepositoryObservation,
  repositoryStatusDescription,
  repositoryStatusLabel,
} from "./repositoryConfiguration";
import type {
  RepositoryMode,
  RepositoryObservation,
  RepositoryRecord,
} from "./repositoryConfiguration";
import "./RepositoryConfigurationPanel.css";

function displayDate(value?: string | null) {
  if (!value) return "Not recorded";
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? "Recorded time unavailable" : parsed.toLocaleString();
}

export default function RepositoryConfigurationPanel({
  api,
  projectId,
  projectName,
}: {
  api: string;
  projectId: string;
  projectName: string;
}) {
  const [record, setRecord] = useState<RepositoryRecord | null>();
  const [canManage, setCanManage] = useState(false);
  const [mode, setMode] = useState<RepositoryMode>("ConfigureLater");
  const [endpoint, setEndpoint] = useState("");
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [verifying, setVerifying] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [observation, setObservation] = useState<RepositoryObservation>();

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const decoded = decodeRepositoryResponse(
        await apiRequest<unknown>(`${api}/api/projects/${projectId}/repository`),
      );
      if (!decoded) throw new Error("The repository service returned an invalid project configuration.");
      if (decoded.repository && decoded.repository.projectId !== projectId)
        throw new Error("The repository service returned configuration for another project.");
      setRecord(decoded.repository);
      setCanManage(decoded.canManage);
      setMode(decoded.repository?.mode ?? "ConfigureLater");
      setEndpoint(decoded.repository?.endpoint ?? "");
      setObservation(undefined);
    } catch (failure) {
      setError(operationError(failure, "The project repository configuration could not be loaded."));
    } finally {
      setLoading(false);
    }
  }, [api, projectId]);

  useEffect(() => {
    void load();
  }, [load]);

  const save = async () => {
    if (!canManage) return;
    if (mode === "ConnectNow" && !endpoint.trim()) {
      setError("Enter the HTTPS GitLab project endpoint before saving Connect now.");
      return;
    }
    setSaving(true);
    setError("");
    setNotice("");
    setObservation(undefined);
    try {
      const saved = await apiRequest<RepositoryRecord>(
        `${api}/api/projects/${projectId}/repository`,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            expectedVersion: record?.version ?? 0,
            mode,
            provider: "GitLab",
            endpoint: mode === "ConnectNow" ? endpoint.trim() : null,
          }),
        },
      );
      const decoded = decodeRepositoryResponse({ repository: saved, canManage });
      if (!decoded?.repository) throw new Error("The repository service returned an invalid saved configuration.");
      if (decoded.repository.projectId !== projectId)
        throw new Error("The saved repository configuration belongs to another project.");
      setRecord(decoded.repository);
      setMode(decoded.repository.mode);
      setEndpoint(decoded.repository.endpoint ?? "");
      setNotice(
        decoded.repository.mode === "ConfigureLater"
          ? "Repository setup saved as Pending. Configure it here when the project is ready."
          : "Repository endpoint saved as Configured · unverified. Verify it through the server before relying on the connection.",
      );
    } catch (failure) {
      setError(operationError(failure, "The repository configuration could not be saved."));
    } finally {
      setSaving(false);
    }
  };

  const verify = async () => {
    if (!canManage || !record || mode !== "ConnectNow") return;
    setVerifying(true);
    setError("");
    setNotice("");
    setObservation(undefined);
    try {
      const result = await apiRequest<unknown>(
        `${api}/api/projects/${projectId}/repository/verify`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ expectedVersion: record.version }),
        },
      );
      const payload = result && typeof result === "object" && !Array.isArray(result)
        ? result as { repository?: unknown; observation?: unknown }
        : {};
      const decoded = decodeRepositoryResponse({ repository: payload.repository, canManage });
      const observed = decodeRepositoryObservation(payload.observation);
      if (!decoded?.repository) throw new Error("The repository service returned an invalid verification result.");
      if (!observed) throw new Error("The repository service returned an invalid verification observation.");
      if (decoded.repository.projectId !== projectId)
        throw new Error("The verification result belongs to another project.");
      setRecord(decoded.repository);
      setMode(decoded.repository.mode);
      setEndpoint(decoded.repository.endpoint ?? "");
      setObservation(observed);
      setNotice(
        observed.verified
          ? "The server verified the configured GitLab project. This records connection readiness only; it does not create code evidence or approve engineering work."
          : "The server could not verify the configured GitLab project. Review the observation and correct the endpoint before retrying.",
      );
    } catch (failure) {
      setError(operationError(failure, "Repository verification could not be completed."));
    } finally {
      setVerifying(false);
    }
  };

  const status = record?.status ?? "Pending";
  if (loading)
    return <section className="repositoryConfigurationPanel" aria-labelledby="repository-configuration-heading"><p>Loading repository configuration…</p></section>;

  return (
    <section className="repositoryConfigurationPanel" aria-labelledby="repository-configuration-heading">
      <header className="repositoryConfigurationHeader">
        <div>
          <h2 id="repository-configuration-heading">Repository setup</h2>
          <p>
            Configure the project-scoped GitLab connection after creation. The server owns credentials,
            provider identity, verification, and all connection evidence for {projectName}.
          </p>
        </div>
        <span className={`repositoryStatusPill ${status === "Verified" ? "verified" : status === "ConfiguredUnverified" ? "unverified" : "pending"}`}>
          {repositoryStatusLabel(status)}
        </span>
      </header>
      {error && (
        <p className="repositoryConfigurationError" role="alert">
          {error}
          <button type="button" className="repositoryConfigurationRecovery" onClick={() => void load()}>
            Reload current configuration
          </button>
        </p>
      )}
      {notice && <p className="repositoryConfigurationNotice" role="status">{notice}</p>}
      <p className={status === "ConfiguredUnverified" ? "repositoryConfigurationWarning" : "repositoryConfigurationNotice"}>
        <strong>{repositoryStatusLabel(status)}.</strong> {repositoryStatusDescription(status)}
      </p>
      <dl className="repositoryConfigurationSummary">
        <div><dt>Mode</dt><dd>{mode === "ConnectNow" ? "Connect now" : "Configure later"}</dd></div>
        <div><dt>Provider</dt><dd>{record?.provider || "Not selected"}</dd></div>
        <div><dt>Endpoint</dt><dd>{record?.endpoint ? <code>{record.endpoint}</code> : "No endpoint stored"}</dd></div>
        <div><dt>Configuration version</dt><dd>{record?.version ?? "Not configured"}</dd></div>
        {record?.remotePath && <div><dt>Server observed project</dt><dd><code>{record.remotePath}</code>{record.remoteProjectId ? ` · ${record.remoteProjectId}` : ""}</dd></div>}
      </dl>
      {canManage ? (
        <fieldset className="repositoryConfigurationForm" disabled={saving || verifying}>
          <legend>Project repository settings</legend>
          <div className="repositoryConfigurationChoices">
            <label><input type="radio" name="repository-mode" checked={mode === "ConnectNow"} onChange={() => setMode("ConnectNow")} /> Connect now</label>
            <label><input type="radio" name="repository-mode" checked={mode === "ConfigureLater"} onChange={() => { setMode("ConfigureLater"); setEndpoint(""); }} /> Configure later</label>
          </div>
          <p className="repositoryConfigurationProvider">Provider: <strong>GitLab</strong> (the supported provider for this project surface)</p>
          {mode === "ConnectNow" && <label>GitLab project endpoint (HTTPS)<input value={endpoint} onChange={event => setEndpoint(event.target.value)} placeholder="https://gitlab.example/group/project" autoComplete="off" /></label>}
          <div className="repositoryConfigurationActions">
            <button type="button" className="primary" onClick={() => void save()} disabled={saving || verifying}>{saving ? "Saving…" : "Save repository settings"}</button>
            <button type="button" onClick={() => void verify()} disabled={saving || verifying || mode !== "ConnectNow" || !record}>{verifying ? "Verifying…" : "Verify through server"}</button>
          </div>
          <p className="repositoryConfigurationAudit">Saving records configuration as Pending or Configured · unverified. Verification is a separate server observation and may fail without changing unrelated project evidence.</p>
        </fieldset>
      ) : (
        <p className="repositoryConfigurationWarning">You can view this project’s repository state. A Configuration Manager, Program Manager, or AeroLink administrator must change or verify it.</p>
      )}
      {observation && (
        <div className={observation.verified ? "repositoryConfigurationNotice" : "repositoryConfigurationWarning"} role="status">
          <strong>Server observation: {observation.verified ? "verified" : "not verified"}.</strong>{observation.detail ? ` ${observation.detail}` : ""}{observation.code ? ` (${observation.code})` : ""}
        </div>
      )}
      {record && <p className="repositoryConfigurationAudit">Configured by {record.configuredBy || "unavailable"} on {displayDate(record.configuredAt)} · Last verification {displayDate(record.lastVerifiedAt)}{record.lastVerificationFailureAt ? ` · Last failed observation ${displayDate(record.lastVerificationFailureAt)}` : ""}</p>}
    </section>
  );
}
