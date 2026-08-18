import { useEffect, useState } from "react";
import "./ReviewComments.css";

/**
 * Explains, once, that the review someone was summoned to has ended.
 *
 * A reviewer follows Monday's email on Wednesday. The record is still there and they can still read it, but
 * the decision they were asked for is gone. Without this they land on an ordinary page with no controls and
 * no explanation, and have to work out for themselves what happened.
 *
 * The server decides who sees this. It sets the flag only for somebody who actually held a step on a cycle
 * that has since closed, and only after the access check — so the notice can never be the thing that tells
 * a stranger the record exists.
 */
export type ReviewOutcome = {
  state: string;
  completedAt?: string | null;
  closureReason?: string | null;
  decidedBy?: string | null;
};

const outcomeLine = (outcome: ReviewOutcome | undefined) => {
  if (!outcome) return "The review cycle closed.";
  if (outcome.state === "ChangesRequested") return "Changes were requested and the package went back to its author.";
  if (outcome.state === "Approved") return "The review completed and the package was approved.";
  if (outcome.state === "Cancelled") return "The review was cancelled before it finished.";
  return "The review cycle closed.";
};

export function ReviewEndedNotice({ outcome, currentState }: { outcome?: ReviewOutcome; currentState: string }) {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    const url = new URL(window.location.href);
    if (url.searchParams.get("reviewEnded") !== "1") return;
    setOpen(true);
    // Taken out of the address straight away. Left there it would fire again on every reload and on any
    // link copied out of the address bar, which would say something untrue to whoever opened it.
    url.searchParams.delete("reviewEnded");
    window.history.replaceState({}, "", `${url.pathname}${url.search}${url.hash}`);
  }, []);

  if (!open) return null;

  return (
    <div className="reviewEndedBackdrop" role="presentation">
      <div className="reviewEndedDialog" role="alertdialog" aria-modal="true" aria-labelledby="reviewEndedTitle">
        <div className="reviewEndedRule" />
        <div className="reviewEndedBody">
          <h2 id="reviewEndedTitle">This is no longer out for review</h2>
          <p>
            The cycle closed before you opened this link, so there is no decision waiting on you. Nothing you
            do here affects the record.
          </p>
          <dl className="reviewEndedFacts">
            <div><dt>What happened</dt><dd>{outcomeLine(outcome)}</dd></div>
            {outcome?.completedAt && (
              <div><dt>When</dt><dd>{new Date(outcome.completedAt).toLocaleString()}</dd></div>
            )}
            <div><dt>Where it is now</dt><dd>{currentState}</dd></div>
          </dl>
          <p>You will be notified again if a later cycle needs you.</p>
          <div className="reviewEndedActions">
            <button type="button" autoFocus onClick={() => setOpen(false)}>OK</button>
          </div>
        </div>
      </div>
    </div>
  );
}
