import DocumentActions from "./DocumentActions";
import type { Discipline } from "./routing";
import type { DocumentTarget } from "./presentation";

type Props = {
  api: string;
  projectId: string;
  release: { id: string; version: string; isReleased: boolean };
  discipline: Discipline;
  onBack: () => void;
};

const targets = (discipline: Discipline): DocumentTarget[] => {
  if (discipline === "system")
    return [{ type: "Sysrd", label: "System Requirements Document" }];
  if (discipline === "software")
    return [
      { type: "SwrdHighLevel", label: "Software Requirements Document — HLR" },
      { type: "SwrdLowLevel", label: "Software Requirements Document — LLR" },
    ];
  if (discipline === "systemTest")
    return [{ type: "SystemTestProcedures", label: "System Test Procedure Document" }];
  return [
    { type: "HighLevelTestProcedures", label: "HLR Test Procedure Document" },
    { type: "LowLevelTestProcedures", label: "LLR Test Procedure Document" },
  ];
};

export default function DocumentCenter({ api, projectId, release, discipline, onBack }: Props) {
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
        targets={targets(discipline)}
        heading={`${scope} ${assurance ? "assurance" : "engineering"} documents`}
      />
      {assurance && (
        <section className="documentOutputs" data-kind={release.isReleased ? "approved" : "draft"}>
          <h3>Traceability documentation</h3>
          <p>
            Traceability matrices remain generated from the selected software build’s exact requirement and
            procedure relationships. Open Digital Thread to choose the focused matrix and output format.
          </p>
        </section>
      )}
    </main>
  );
}
