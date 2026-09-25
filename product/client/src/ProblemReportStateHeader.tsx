import { useState, type ReactNode } from "react";
import { CANONICAL_STATES, isOnRail, stateIndex, stateLabel } from "./problemReportLifecycle";
import "./ProblemReportStateHeader.css";

export type TransitionOffer = { state: string; requiresRationale: boolean };

type Props = {
  /** The report's current controlled state, as the server reports it. */
  state: string;
  version: number;
  owner?: ReactNode;
  /** Exactly `capabilities.availableTransitions`. The server decides what may be performed. */
  transitions: TransitionOffer[];
  busy?: boolean;
  isReleaseBlocker?: boolean;
  waived?: boolean;
  canToggleBlocker?: boolean;
  /** Verifying only: closure needs a passing successor result before SQA can close. */
  showClosureResult?: boolean;
  /**
   * Waiting for SQA on a result a later change withdrew (DEC-133). Said here, beside where Close would be,
   * because the report did not move and a missing Close with no reason reads as a permissions fault.
   */
  closureBasisWithdrawn?: boolean;
  dispositionRationale?: string;
  /**
   * The working note: a draft of the rationale a backward move or a rejection will ask for.
   *
   * Composed by the parent, which owns the text and its per-report autosave, and placed here because
   * it belongs beside the actions it is written for. It is deliberately never submitted with an
   * action: the server keeps free text only where a transition requires a rationale and discards it
   * everywhere else (`acceptedRationale` in ProblemReportEndpoints), so a note that travelled with an
   * action would look like it was on the record while being dropped.
   */
  noteArea?: ReactNode;
  /**
   * The offer to restore an unsubmitted note, shown outside the disclosure.
   *
   * Deliberately not folded away with the note itself: work held in a browser that nobody is told
   * about is work nobody recovers, and an offer hidden behind a closed `<details>` is exactly that.
   */
  noteOffer?: ReactNode;
  onTransition: (target: string, requiresRationale: boolean) => void;
  /** Opens the disposition dialog. Rejection collects a disposition, so it is never a plain transition. */
  onReject: () => void;
  onToggleBlocker?: () => void;
  onClosureResult?: () => void;
  /** Verifying on a project without Verification (#1113): send to SQA on an attested statement instead. */
  onAttest?: (statement: string) => Promise<boolean>;
  /** The attested statement this report was sent to SQA on, when that was its basis. */
  attestation?: string | null;
};

/**
 * What state this Problem Report is in, and the one thing to do about it next.
 *
 * This sits directly under the record title and above the Code/Record/History tabs, so the state and
 * the next action stay visible whichever tab is open. It used to be the last section of the Record tab,
 * rendered as a flex row of every available transition at equal weight — on a report in Open that was
 * seven buttons of wrapped, truncated labels, and two of them duplicated others.
 *
 * Both duplicates are resolved here rather than restyled:
 *
 * - Backward moves were offered twice, once as the named target from `availableTransitions` and once as
 *   a `Move backward…` button that acted on whichever of Draft or Verifying appeared first in the same
 *   list. The menu below lists each allowed backward target by name, so the reader chooses the state
 *   instead of trusting a guess about list order.
 * - Rejecting was offered twice, once as a plain transition and once as the control that opens the
 *   disposition dialog. Only the second collects the disposition a rejection requires, so `Rejected`
 *   never renders as a plain transition button and the one real control routes through `onReject`.
 *
 * The rail is presentation. `CANONICAL_STATES` decides where a state sits; `transitions` — and nothing
 * else — decides what is offered. A state being adjacent on the rail never implies its edge is allowed.
 */
export default function ProblemReportStateHeader({
  state,
  version,
  owner,
  transitions,
  busy,
  isReleaseBlocker,
  waived,
  canToggleBlocker,
  showClosureResult,
  closureBasisWithdrawn,
  dispositionRationale,
  noteArea,
  noteOffer,
  onTransition,
  onReject,
  onToggleBlocker,
  onClosureResult,
  onAttest,
  attestation,
}: Props) {
  const [statement, setStatement] = useState("");
  const current = stateIndex(state);
  const offered = transitions.filter((transition) => transition.state !== "Rejected");
  const canReject = transitions.some((transition) => transition.state === "Rejected");

  // The next step forward is the lowest-positioned offer above the current state. A state this build
  // does not know — one added server-side — is still treated as forward so it stays reachable rather
  // than being silently dropped, but it sorts last: `stateIndex` gives it -1, and left to a plain
  // numeric sort that would put an unrecognised state ahead of the real next step and make it the
  // primary action.
  const rank = (state: string) => (isOnRail(state) ? stateIndex(state) : Number.MAX_SAFE_INTEGER);
  const forward = offered
    .filter((transition) => !isOnRail(transition.state) || stateIndex(transition.state) > current)
    .sort((a, b) => rank(a.state) - rank(b.state))[0];
  const backward = offered
    .filter((transition) => isOnRail(transition.state) && stateIndex(transition.state) < current)
    .sort((a, b) => stateIndex(b.state) - stateIndex(a.state));

  const blockerControl = canToggleBlocker && onToggleBlocker && (
    <button type="button" className="prStateQuiet" disabled={busy} onClick={onToggleBlocker}>
      {isReleaseBlocker ? "Clear release blocker" : "Raise release blocker"}
    </button>
  );
  const rejectControl = canReject && (
    <button type="button" className="prStateDanger" disabled={busy} onClick={onReject}>
      Reject…
    </button>
  );

  /* A report that left the lifecycle did not progress along it, so it gets the reason it left and the
     way back rather than a rail with a phantom position. */
  if (!isOnRail(state)) {
    return (
      <section className="prStateHeader prStateOffPath" aria-label="Problem Report lifecycle">
        <div className="prStateOffPathBody">
          <div className="prStateLine">
            <span className="prStateTerminal">{stateLabel(state)}</span>
            <span className="prStateMeta">terminal · controlled version {version}</span>
            {owner && <span className="prStateMeta">Owner {owner}</span>}
          </div>
          {dispositionRationale && <p className="prStateRationale">{dispositionRationale}</p>}
        </div>
        <div className="prStateActions">
          {offered.map((transition) => (
            <button
              key={transition.state}
              type="button"
              className="prStateQuiet"
              disabled={busy}
              onClick={() => onTransition(transition.state, transition.requiresRationale)}
            >
              {/* Keyed on the state, not on its rendered label: a label is display text and may be
                  retitled, and matching on it would quietly change which control this is. */}
              {transition.state === "Draft" ? "Return to Draft" : `Move to ${stateLabel(transition.state)}`}
              {transition.requiresRationale ? "…" : ""}
            </button>
          ))}
          {blockerControl}
        </div>
      </section>
    );
  }

  return (
    <section className="prStateHeader" aria-label="Problem Report lifecycle">
      <div className="prStateLine">
        <span className="prStateEyebrow">CURRENT STATE</span>
        <b className="prStateNow">{stateLabel(state)}</b>
        <span className="prStateMeta">
          step {current + 1} of {CANONICAL_STATES.length} · controlled version {version}
        </span>
        {owner && <span className="prStateOwner">Owner {owner}</span>}
      </div>

      <ol className="prRail">
        {CANONICAL_STATES.map((railState, index) => {
          const position = index < current ? "done" : index === current ? "current" : "todo";
          return (
            <li
              key={railState}
              className={`prRailStep ${position}`}
              aria-current={position === "current" ? "step" : undefined}
            >
              <span className="prRailTrack" aria-hidden="true">
                <i className="prRailBefore" />
                <span className="prRailMark">
                  {position === "done" && (
                    <svg width="11" height="11" viewBox="0 0 12 12" fill="none">
                      <path
                        d="M2.5 6.3 4.8 8.6 9.5 3.9"
                        stroke="currentColor"
                        strokeWidth="1.9"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                      />
                    </svg>
                  )}
                </span>
                <i className="prRailAfter" />
              </span>
              <span className="prRailLabel">
                {stateLabel(railState)}
                {/* The word, not just the ring, so the current step is not carried by colour or shape. */}
                {position === "current" && <em>Current</em>}
              </span>
            </li>
          );
        })}
      </ol>

      {showClosureResult && onClosureResult && (
        <div className="prStatePrereq">
          {/* #1088: there is no bare "Move to Waiting for SQA". The report goes to SQA only when a person
              sends it on a passing successor result they have chosen, which happens in Test Results. */}
          <span>This report goes to SQA only on a passing successor result, which you choose and confirm.</span>
          <button type="button" className="prStateLink" disabled={busy} onClick={onClosureResult}>
            Choose the closure-supporting result →
          </button>
        </div>
      )}

      {onAttest && (
        <form
          className="prStatePrereq prAttestation"
          onSubmit={(event) => {
            event.preventDefault();
            void onAttest(statement).then((sent) => { if (sent) setStatement(""); });
          }}
        >
          <label htmlFor="prAttestation">
            This project does not use Verification. Describe how the correction was verified; SQA closes the
            report independently on this statement.
          </label>
          <textarea
            id="prAttestation"
            rows={3}
            value={statement}
            onChange={(event) => setStatement(event.target.value)}
            placeholder="What was checked, on which build, and what was observed"
          />
          <button type="submit" className="prStateLink" disabled={busy || statement.trim().length < 20}>
            Send to SQA on this statement →
          </button>
        </form>
      )}

      {attestation && (
        <div className="prStatePrereq prAttestation" role="note">
          <span>
            <b>Sent to SQA on an attested statement:</b> {attestation}
          </span>
        </div>
      )}

      {closureBasisWithdrawn && (
        <div className="prStatePrereq" role="status">
          <span>
            A later change withdrew the result this report was sent to SQA on, so it cannot be closed. Return
            it to Verifying and send it again on a fresh passing result.
          </span>
        </div>
      )}

      <div className="prStateActions">
        {forward && (
          <button
            type="button"
            className="prStatePrimary"
            disabled={busy}
            onClick={() => onTransition(forward.state, forward.requiresRationale)}
          >
            Move to {stateLabel(forward.state)}
            {forward.requiresRationale ? "…" : " →"}
          </button>
        )}
        {backward.length > 0 && (
          /* A disclosure rather than a popup: it is keyboard reachable with no focus management of our
             own, and every target inside it is a real button with the state's real name. */
          <details className="prBackward">
            <summary>Move backward</summary>
            <div role="group" aria-label="Earlier states">
              {backward.map((transition) => (
                <button
                  key={transition.state}
                  type="button"
                  disabled={busy}
                  onClick={() => onTransition(transition.state, transition.requiresRationale)}
                >
                  {stateLabel(transition.state)}
                  {transition.requiresRationale ? "…" : ""}
                </button>
              ))}
            </div>
          </details>
        )}
        <span className="prStateSpacer" />
        {waived && <span className="prStateMeta">Controlled waiver active</span>}
        {blockerControl}
        {rejectControl}
      </div>

      {/* Folded away by default. It is a scratch pad for the rationale the next backward move or
          rejection will ask for, not something a reader of the record needs in front of them. */}
      {noteOffer}
      {noteArea && (
        <details className="prStateNote">
          <summary>Working note</summary>
          <div>{noteArea}</div>
        </details>
      )}
    </section>
  );
}
