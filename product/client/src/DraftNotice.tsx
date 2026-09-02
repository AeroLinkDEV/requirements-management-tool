import type { AutosaveStatus } from "./autosave";
import { formatOrdinaryDateTime, formatOrdinaryTime } from "./presentation";
import "./DraftNotice.css";

/**
 * What the person is told about their draft.
 *
 * People trust what they can see. A tool that silently holds work gets no credit for it and, worse, gets no
 * warning across when it stops — so save state is shown rather than assumed, and a recovered draft is
 * offered rather than applied.
 */

const wording: Record<AutosaveStatus, string> = {
  Idle: "No changes",
  Saving: "Saving…",
  Saved: "Draft saved",
  Error: "Not saved",
  Conflict: "Changed elsewhere",
};

export function AutosaveState({ status, savedAt, where }: { status: AutosaveStatus; savedAt?: Date; where?: string }) {
  return (
    <div className={`draftState ${status.toLowerCase()}`} role="status" aria-live="polite">
      <i aria-hidden="true" />
      <span>
        {wording[status]}
        {status === "Saved" && savedAt ? ` · ${formatOrdinaryTime(savedAt)}` : ""}
      </span>
      {/* Where the draft is held is not a detail: one survives this machine dying and the other does not. */}
      {where && status !== "Idle" && <small>{where}</small>}
    </div>
  );
}

export function DraftRestore({
  savedAt,
  description,
  onRestore,
  onDiscard,
}: {
  savedAt: Date;
  description: string;
  onRestore: () => void;
  onDiscard: () => void;
}) {
  return (
    <div className="draftRestore" role="alert">
      <div>
        <b>
          Unsaved work from{" "}
          <time dateTime={savedAt.toISOString()}>{formatOrdinaryDateTime(savedAt)}</time>
        </b>
        <span>{description}</span>
      </div>
      <div className="draftRestoreActions">
        <button type="button" onClick={onDiscard}>
          Discard it
        </button>
        <button type="button" className="primary" onClick={onRestore}>
          Restore my draft
        </button>
      </div>
    </div>
  );
}
