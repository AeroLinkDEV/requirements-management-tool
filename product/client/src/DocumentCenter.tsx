import DocumentActions from "./DocumentActions";
import type { ProjectLadderProjection } from "./projectLadder";
import type { Discipline } from "./routing";
import { configuredProcedureTargetsFor, documentTypeLabel, type DocumentTarget } from "./presentation";

type Props = {
  api: string;
  projectId: string;
  release: { id: string; version: string; isReleased: boolean };
  discipline: Discipline;
  ladder: ProjectLadderProjection | null;
  onBack: () => void;
};

const targets = (discipline: Discipline, ladder: ProjectLadderProjection | null): DocumentTarget[] => {
  if (discipline === "system")
    return [{ type: "Sysrd", label: documentTypeLabel("Sysrd") }];
  if (discipline === "software")
    return [
      { type: "SwrdHighLevel", label: documentTypeLabel("SwrdHighLevel") },
      { type: "SwrdLowLevel", label: documentTypeLabel("SwrdLowLevel") },
    ];
  if (discipline === "systemTest")
    return configuredProcedureTargetsFor(ladder, "System");
  return [
    ...configuredProcedureTargetsFor(ladder, "Software"),
    ...configuredProcedureTargetsFor(ladder, "Software", undefined, "Procedure"),
  ];
};

export default function DocumentCenter({ api, projectId, release, discipline, ladder, onBack }: Props) {
  const assurance = discipline === "systemTest" || discipline === "softwareTest";
  const scope = discipline === "software" || discipline === "softwareTest" ? "Software" : "System";
  return (
    <main className="workspace documentCenter">
      <header className="workspaceHeader">
        <div>
          <button className="back" onClick={onBack}>← {assurance ? "Verification" : "Requirements Explorer"}</button>
          <p className="eyebrow">{assurance ? "ASSURANCE" : "ENGINEERING"} / {scope.toUpperCase()}</p>
          <h1>Documents</h1>
          <p>
            {release.isReleased
              ? `Approved documents belonging to software build SW-${release.version.padStart(4, "0").replace(".", ".")}0.`
              : `Living draft documents for the in-work software build, generated from approved content.`}
          </p>
        </div>
        <span className={release.isReleased ? "statusBadge released" : "statusBadge inWork"}>
          {release.isReleased ? "Released · read-only" : "In Work"}
        </span>
      </header>
      <DocumentActions
        api={api}
        projectId={projectId}
        release={release}
        targets={targets(discipline, ladder)}
        heading={`${scope} ${assurance ? "assurance" : "engineering"} documents`}
      />
      {assurance && (
        <section className="documentOutputs" data-kind={release.isReleased ? "approved" : "draft"}>
          <h3>Traceability documentation</h3>
          <p>
            Traceability matrices remain generated from the selected software build’s exact requirement and
            verification relationships. Open Digital Thread to choose the focused matrix and output format.
          </p>
        </section>
      )}
    </main>
  );
}
