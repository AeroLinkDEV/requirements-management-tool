import { artifactAcronym } from "./presentation";
import "./ProblemReportImpactPanel.css";

/**
 * The impact assessment and the engineering evidence that has arrived under each answer.
 *
 * This replaces three separate places a reader used to have to look: an impact matrix here, "Approved
 * linked change requests" somewhere below it, and "Connected engineering artifacts" below that. None of
 * them sat next to the answer they were evidence for, so the question "we said system requirements are
 * impacted — what actually happened about it?" could not be answered by looking at one thing.
 *
 * Nothing on this panel is authored. Every row is derived by the server from links the change-request,
 * test-change-request and verification workflows already write, which is why a linked SRCR moving from
 * Draft to In review to Approved changes what this says without anybody touching the report.
 */

export type ImpactArtifact = {
  artifactType: string;
  artifactId: string;
  identifier: string;
  title: string;
  state: string;
  targetBuild: string;
  relationship: string;
  detail: string;
};

export type ImpactArea = {
  key: string;
  label: string;
  assessment: string;
  hasArtifactSlot: boolean;
  artifactTypes: string[];
  mismatch?: string | null;
  artifacts: ImpactArtifact[];
};

const destinationKind = (type: string) => ({
  ChangeRequest: "change-request",
  Requirement: "requirement",
  TestChangeRequest: "test-change-request",
  TestExecution: "test-execution",
  Document: "document",
} as Record<string, string>)[type];

const spaced = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");

const assessmentTone = (assessment: string) =>
  assessment === "Yes" ? "yes" : assessment === "No" ? "no" : "unknown";

const assessmentWords = (assessment: string) =>
  assessment === "Yes" ? "Impacted" : assessment === "No" ? "Not impacted" : "Unknown";

function ArtifactRow({ artifact, onOpen }: {
  artifact: ImpactArtifact;
  onOpen: (kind: string, id: string, identifier?: string) => void;
}) {
  const kind = destinationKind(artifact.artifactType);
  const body = (
    <>
      <i>{artifactAcronym(artifact.identifier, artifact.artifactType)}</i>
      <span>
        <b>{artifact.identifier}{artifact.title && ` — ${artifact.title}`}</b>
        <small>{[artifact.relationship && spaced(artifact.relationship), artifact.detail].filter(Boolean).join(" · ")}</small>
      </span>
      <span className="impactState">
        {artifact.state && <em className={assessmentTone(artifact.state)}>{spaced(artifact.state)}</em>}
        {artifact.targetBuild && <em className="build">{artifact.targetBuild}</em>}
      </span>
    </>
  );
  return kind
    ? <button type="button" className="impactArtifact" onClick={() => onOpen(kind, artifact.artifactId, artifact.identifier)}>{body}</button>
    // Read-only by nature: GitLab remains authoritative for its own records, and this is only the
    // controlled thread to them.
    : <article className="impactArtifact readOnly">{body}</article>;
}

export default function ProblemReportImpactPanel({ areas, narrative, onOpen }: {
  areas: ImpactArea[];
  narrative: string;
  onOpen: (kind: string, id: string, identifier?: string) => void;
}) {
  return (
    <section className="prImpactPanel" aria-label="Impact and linked evidence">
      <div className="prImpactPanelHead">
        <h3>Impact and linked evidence</h3>
        <span>Assembled from controlled links — nothing here is entered by hand</span>
      </div>
      <p className="prImpactNarrative">{narrative || "No combined System / aircraft impact narrative has been recorded."}</p>

      <div className="impactRows">
        {areas.map(area => (
          <div key={area.key} className={`impactRow ${assessmentTone(area.assessment)}`}>
            <div className="impactName">
              <b>{area.label}</b>
              <small>{area.hasArtifactSlot ? area.artifactTypes.join(" · ") : "Narrative only"}</small>
            </div>
            <div className="impactAnswer">
              <span className={`impactPill ${assessmentTone(area.assessment)}`}>{assessmentWords(area.assessment)}</span>
            </div>
            <div className="impactSlot">
              {area.mismatch && (
                // Both halves are shown and the disagreement is named. Hiding the link would make the
                // record assert something untrue; changing the answer would put words in an engineer's
                // mouth. Advisory only — it blocks nothing.
                <p className="impactMismatch" role="note">
                  <b>Answer and evidence disagree.</b> {area.mismatch}
                </p>
              )}
              {area.artifacts.map(artifact => (
                <ArtifactRow key={`${artifact.artifactType}-${artifact.artifactId}`} artifact={artifact} onOpen={onOpen} />
              ))}
              {area.artifacts.length === 0 && (
                <p className="impactEmpty">
                  {!area.hasArtifactSlot
                    ? "No controlled artifact type — the narrative above is the record."
                    : area.assessment === "Unknown"
                      ? "Not yet assessed."
                      : "Nothing has named this Problem Report yet."}
                </p>
              )}
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}
