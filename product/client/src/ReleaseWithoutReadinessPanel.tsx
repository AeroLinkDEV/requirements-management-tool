import { useState, type FormEvent } from "react";
import { apiRequest, operationError } from "./apiClient";
import type { WorkspaceRelease } from "./workspaceContext";

/**
 * Moving between builds in a project that has switched Release off (#1113, DEC-138). The in-work build is
 * released by a password-confirmed signed decision with a reason, recorded as released without readiness
 * evidence; the next build then follows it. A project with Release uses its release campaign instead.
 */
export default function ReleaseWithoutReadinessPanel({ api, projectId, releases, onChanged }: {
  api: string;
  projectId: string;
  /** Oldest first. */
  releases: WorkspaceRelease[];
  onChanged: () => void;
}) {
  const inWork = releases.find((release) => !release.isReleased);
  const latestReleased = [...releases].reverse().find((release) => release.isReleased);
  const [reason, setReason] = useState("");
  const [password, setPassword] = useState("");
  const [version, setVersion] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const run = async (event: FormEvent, request: () => Promise<unknown>) => {
    event.preventDefault();
    setBusy(true); setError("");
    try {
      await request();
      setReason(""); setPassword(""); setVersion("");
      onChanged();
    } catch (failure) {
      setError(operationError(failure, "The build change was refused."));
    } finally {
      setBusy(false);
    }
  };
  return (
    <section className="releaseWithoutReadiness" aria-labelledby="release-without-readiness-heading">
      <h2 id="release-without-readiness-heading">Move to the next build</h2>
      <p>
        This project does not use Release, so there is no readiness evidence to release a build on. A Configuration
        Manager or Program Manager releases it by a signed decision, which is recorded as released without readiness
        evidence.
      </p>
      {inWork ? (
        <form onSubmit={(event) => void run(event, () => apiRequest(`${api}/api/releases/${inWork.id}/release-without-readiness`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ reason, password }),
        }))}>
          <label>Why is Build {inWork.version} being released?
            <textarea rows={2} value={reason} onChange={(event) => setReason(event.target.value)} />
          </label>
          <label>Confirm with your password
            <input type="password" autoComplete="current-password" value={password} onChange={(event) => setPassword(event.target.value)} />
          </label>
          <button type="submit" disabled={busy || reason.trim().length < 10 || !password}>
            Release Build {inWork.version} without readiness evidence
          </button>
        </form>
      ) : (
        <form onSubmit={(event) => void run(event, () => apiRequest(`${api}/api/releases`, {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ projectId, version, predecessorReleaseId: latestReleased?.id ?? null }),
        }))}>
          <label>Next build version
            <input value={version} onChange={(event) => setVersion(event.target.value)} placeholder="e.g. 1.1" />
          </label>
          <button type="submit" disabled={busy || !version.trim()}>
            Start the next build{latestReleased ? ` after Build ${latestReleased.version}` : ""}
          </button>
        </form>
      )}
      {error && <p className="releaseWithoutReadinessError" role="alert">{error}</p>}
    </section>
  );
}
