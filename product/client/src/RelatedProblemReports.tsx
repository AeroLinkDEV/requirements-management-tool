import { useState } from "react";
import ProblemReportPicker from "./ProblemReportPicker";
import { PersonName } from "./People";
import "./RelatedProblemReports.css";

/**
 * The Problem Reports that belong with this one.
 *
 * Symmetric and unlabelled beyond "related", on purpose. A directed relationship asserts which report is
 * the parent, and that is a judgement nobody has been asked to make — naming a kind now would mean
 * inventing an answer and storing it. The link is written on both records, so whoever opens either one
 * finds the other.
 */

export type RelatedReport = {
  id: string;
  displayNumber: string;
  title: string;
  state: string;
  severity: string;
  reportedBy: string;
  targetBuild: string;
};

const spaced = (value: string) =>
  value === "WaitingForSqaToClose" ? "Waiting for SQA to Close"
    : value === "ReadyForSccb" ? "Ready for SCCB"
      : value.replace(/([a-z])([A-Z])/g, "$1 $2");

export default function RelatedProblemReports({ api, projectId, reportId, related, canEdit, busy, onLink, onUnlink, onOpen }: {
  api: string;
  projectId: string;
  reportId: string;
  related: RelatedReport[];
  canEdit: boolean;
  busy: boolean;
  onLink: (relatedId: string) => Promise<void>;
  onUnlink: (relatedId: string) => Promise<void>;
  onOpen: (id: string) => void;
}) {
  const [picking, setPicking] = useState(false);

  return (
    <section className="prRelated" aria-label="Related Problem Reports">
      <div className="prRelatedHead">
        <div>
          <h3>Related Problem Reports</h3>
          <span>Linked on both records — whoever opens either one finds the other</span>
        </div>
        {canEdit && (
          <button type="button" className="quiet" disabled={busy} onClick={() => setPicking(current => !current)}>
            {picking ? "Done" : "Link a Problem Report"}
          </button>
        )}
      </div>

      {picking && (
        <div className="prRelatedPicker">
          <ProblemReportPicker
            api={api}
            projectId={projectId}
            scope="project"
            selected={related.map(item => item.id)}
            // Already-related reports and this report itself are shown as taken rather than hidden: a
            // picker that silently omits them looks like the record is missing.
            locked={[reportId, ...related.map(item => item.id)]}
            legend="Choose a Problem Report to relate"
            onChange={async ids => {
              const added = ids.find(id => id !== reportId && !related.some(item => item.id === id));
              if (!added) return;
              // Closed the moment a choice is made. The picker is a controlled multi-select whose ticks
              // come from the saved relationships, so leaving it open while the link is in flight shows
              // the tick springing back until the record reloads — and reads as the click not working.
              setPicking(false);
              await onLink(added);
            }}
          />
        </div>
      )}

      {related.length === 0 && !picking && (
        <p className="prRelatedEmpty">No other Problem Report has been related to this one.</p>
      )}

      {related.length > 0 && (
        <div className="prRelatedList">
          {related.map(item => (
            <div key={item.id} className="prRelatedCard">
              <button type="button" className="prRelatedOpen" onClick={() => onOpen(item.id)}>
                <i>PR</i>
                <span>
                  <b>{item.displayNumber} — {item.title}</b>
                  <small>Raised by <PersonName userName={item.reportedBy} /></small>
                </span>
                <span className="prRelatedState">
                  <em>{spaced(item.state)}</em>
                  {item.targetBuild && <em className="build">{item.targetBuild}</em>}
                </span>
              </button>
              {canEdit && (
                <button
                  type="button"
                  className="prRelatedUnlink"
                  disabled={busy}
                  aria-label={`Unlink ${item.displayNumber}`}
                  onClick={() => void onUnlink(item.id)}
                >
                  Unlink
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
