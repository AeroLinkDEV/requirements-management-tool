import { useState } from "react";

/**
 * Unsealing a frozen build, and saying what that costs before anybody agrees to it.
 *
 * Reopening is deliberately a separate act rather than something that happens quietly when an author
 * withdraws their change request. A frozen baseline is the strongest statement this system makes about what a
 * build contains; if it stops being true, that should be somebody's decision with their name and their reason
 * on it.
 *
 * So the control is two steps. The first asks the server what reopening would do and shows the answer; the
 * second does it. The preview is not assembled here from several reads — it is the same computation the reopen
 * runs, returned early, which is what stops the confirmation and the act from describing different things.
 */

type StrandedChangeRequest = {
  changeRequestId: string;
  displayNumber: string;
  state: string;
  reviewWillBeCancelled: boolean;
  requirements: string[];
};

type DisturbedCoverage = {
  procedure: string;
  requirement: string;
  consequence: string;
};

export type ReopenConsequences = {
  revisionsTakenBack: string[];
  requirementsRemoved: string[];
  strandedChangeRequests: StrandedChangeRequest[];
  disturbedCoverage: DisturbedCoverage[];
  codeRecordsTakenBack: number;
};

type Preview = {
  available: boolean;
  error?: string | null;
  code?: string | null;
  consequences: ReopenConsequences;
};

export function ReopenBaselinePanel({
  api,
  baselineId,
  displayNumber,
  onReopened,
}: {
  api: string;
  baselineId: string;
  displayNumber: string;
  onReopened: () => Promise<void>;
}) {
  const [preview, setPreview] = useState<Preview | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const ask = async () => {
    setBusy(true);
    setError("");
    try {
      const response = await fetch(`${api}/api/baselines/${baselineId}/reopen-preview`);
      if (!response.ok) {
        setError("What reopening this build would do could not be read.");
        return;
      }
      setPreview((await response.json()) as Preview);
    } finally {
      setBusy(false);
    }
  };

  const reopen = async () => {
    if (!reason.trim()) {
      setError("Say why this build is being reopened.");
      return;
    }
    setBusy(true);
    setError("");
    try {
      const response = await fetch(`${api}/api/baselines/${baselineId}/reopen`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ reason }),
      });
      if (!response.ok) {
        setError(((await response.json()) as { error?: string }).error || "The build could not be reopened.");
        return;
      }
      setPreview(null);
      setReason("");
      await onReopened();
    } finally {
      setBusy(false);
    }
  };

  if (!preview) {
    return (
      <div className="reopenControl">
        <button type="button" className="reopenAction" disabled={busy} onClick={ask} data-testid="reopen-baseline">
          {busy ? "Checking…" : "Reopen build"}
        </button>
        {error && <p className="reopenError">{error}</p>}
      </div>
    );
  }

  const what = preview.consequences;
  // Nothing beyond the revisions themselves. Said plainly rather than by showing four empty lists, because a
  // confirmation whose ordinary case is a wall of headings teaches people to click through it.
  const undisturbed =
    what.strandedChangeRequests.length === 0 &&
    what.disturbedCoverage.length === 0 &&
    what.requirementsRemoved.length === 0 &&
    what.codeRecordsTakenBack === 0;

  return (
    <div className="reopenPanel" data-testid="reopen-preview">
      <header>
        <b>Reopening {displayNumber}</b>
        <button type="button" className="reopenDismiss" onClick={() => { setPreview(null); setError(""); }}>
          Cancel
        </button>
      </header>

      {!preview.available ? (
        <p className="reopenRefusal" data-testid="reopen-refusal">{preview.error}</p>
      ) : (
        <>
          <section>
            <b>{what.revisionsTakenBack.length} requirement revision{what.revisionsTakenBack.length === 1 ? "" : "s"} taken back</b>
            <ul>{what.revisionsTakenBack.map((x) => <li key={x}>{x}</li>)}</ul>
          </section>

          {what.requirementsRemoved.length > 0 && (
            <section>
              <b>Ceases to exist</b>
              <ul>
                {what.requirementsRemoved.map((x) => (
                  <li key={x}>{x} — introduced by this build, so nothing is left of it.</li>
                ))}
              </ul>
            </section>
          )}

          {what.strandedChangeRequests.length > 0 && (
            <section data-testid="reopen-stranded">
              <b>Left to be re-pointed</b>
              <ul>
                {what.strandedChangeRequests.map((x) => (
                  <li key={x.changeRequestId}>
                    {x.displayNumber} ({x.state}) on {x.requirements.join(", ")}
                    {x.reviewWillBeCancelled && " — its review is cancelled and it returns to Draft."}
                  </li>
                ))}
              </ul>
            </section>
          )}

          {what.disturbedCoverage.length > 0 && (
            <section data-testid="reopen-coverage">
              <b>Verification affected</b>
              <ul>
                {what.disturbedCoverage.map((x) => (
                  <li key={`${x.procedure}:${x.requirement}`}>{x.procedure} — {x.consequence}</li>
                ))}
              </ul>
            </section>
          )}

          {what.codeRecordsTakenBack > 0 && (
            <section>
              <b>{what.codeRecordsTakenBack} code traceability record{what.codeRecordsTakenBack === 1 ? "" : "s"} taken back</b>
              <p>Recorded against revisions that will not exist, so they cannot survive the reopen.</p>
            </section>
          )}

          {undisturbed && <p className="reopenQuiet">Nothing else was written against this build.</p>}

          <label>
            <span>Why is this build being reopened?</span>
            <textarea
              value={reason}
              rows={2}
              onChange={(event) => setReason(event.target.value)}
              placeholder="The reason is recorded against the build and stays with it."
            />
          </label>
          <button type="button" className="reopenConfirm" disabled={busy} onClick={reopen} data-testid="reopen-confirm">
            {busy ? "Reopening…" : `Reopen ${displayNumber}`}
          </button>
        </>
      )}
      {error && <p className="reopenError">{error}</p>}
    </div>
  );
}
