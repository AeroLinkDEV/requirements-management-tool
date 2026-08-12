import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import { ProjectIcon } from "./ProjectsLanding";
import { officialBuildName } from "./presentation";
import "./SoftwareBuildsLanding.css";

export type SelectableRelease = {
  id: string;
  version: string;
  isReleased: boolean;
};

type BuildDefinition = {
  id: string;
  version: string;
  status: "released" | "in-work" | "planned";
  statusLabel: string;
  title: string;
  description: string;
  isAccessible: boolean;
  isReleased: boolean;
  isReadOnly: boolean;
  isCurrent: boolean;
  sortOrder: number;
  isPlan?: boolean;
};

const softwareBuilds: readonly BuildDefinition[] = [
  { id: "fms-0-5", version: "0.5", status: "released", statusLabel: "Released", title: "Baseline release", description: "Initial baseline for core FMS capabilities.", isAccessible: false, isReleased: true, isReadOnly: true, isCurrent: false, sortOrder: 1 },
  { id: "fms-1-0", version: "1.0", status: "released", statusLabel: "Released", title: "Feature release", description: "Adds advanced navigation and performance features.", isAccessible: false, isReleased: true, isReadOnly: true, isCurrent: false, sortOrder: 2 },
  { id: "fms-1-5", version: "1.5", status: "released", statusLabel: "Released", title: "Stability release", description: "Reliability improvements and defect remediation.", isAccessible: true, isReleased: true, isReadOnly: true, isCurrent: false, sortOrder: 3 },
  { id: "fms-1-6", version: "1.6", status: "in-work", statusLabel: "In Work", title: "Current in-work build", description: "", isAccessible: true, isReleased: false, isReadOnly: false, isCurrent: true, sortOrder: 4 },
  { id: "plan-next", version: "next", status: "planned", statusLabel: "Planned", title: "Plan next build", description: "Future-build placeholder. No build record has been created.", isAccessible: false, isReleased: false, isReadOnly: false, isCurrent: false, sortOrder: 5, isPlan: true },
];

function MetadataIcon({ kind }: { kind: "owner" | "created" | "phase" }) {
  const path = kind === "owner"
    ? <><circle cx="8" cy="6" r="3"/><circle cx="18" cy="8" r="3"/><path d="M2 18c0-4 3-7 6-7s6 3 6 7M13 18c0-3 2-6 5-6s5 3 5 6"/></>
    : kind === "created"
      ? <><rect x="3" y="5" width="19" height="17" rx="2"/><path d="M7 2v6m11-6v6M3 10h19"/></>
      : <><path d="m3 9 8-7 11 10-8 9L3 9z"/><circle cx="9" cy="8" r="1"/></>;
  return <svg viewBox="0 0 25 25" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">{path}</svg>;
}

export default function SoftwareBuildsLanding({
  user,
  releases,
  onOpenBuild,
  onProjectOverview,
  onImportedBaselines,
  onPersonnel,
  onSignOut,
}: {
  user: AuthUser;
  releases: SelectableRelease[];
  onOpenBuild: (release: SelectableRelease) => void;
  onProjectOverview: () => void;
  onImportedBaselines: () => void;
  onPersonnel: () => void;
  onSignOut: () => void;
}) {
  const releaseByVersion = new Map(releases.map((release) => [release.version, release]));

  return (
    <div className="buildsLandingPage">
      <PortalHeader user={user} onSignOut={onSignOut}/>
      <main className="buildsLandingMain">
        <nav className="buildBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onProjectOverview}>Projects</button>
          <span aria-hidden="true">/</span>
          <strong>FMS Product Development</strong>
        </nav>
        <header className="buildsLandingHeading">
          <div>
            <h1>Software Builds</h1>
            <p>Select a build to explore or work on.</p>
          </div>
          <div className="buildsLandingActions">
            {/* Alongside the import for the same reason: who is on the project, and what their position
                authorises, is the same across every build it has. There is no build to have entered when the
                question is who should be allowed in. */}
            <button type="button" className="personnelButton" onClick={onPersonnel}>
              Personnel
            </button>
            {/* Sits here rather than in a build's navigation because an import does not belong to a build —
                it creates one. Somebody porting a program in has no build to have entered yet. */}
            <button type="button" className="importedBaselinesButton" onClick={onImportedBaselines}>
              Imported baselines
            </button>
            <button type="button" className="projectOverviewButton" onClick={onProjectOverview}>
              <span aria-hidden="true">←</span> Project overview
            </button>
          </div>
        </header>

        <section className="buildProjectSummary" aria-labelledby="build-project-name">
          <span className="buildProjectIcon"><ProjectIcon name="fms"/></span>
          <div className="buildProjectContent">
            <h2 id="build-project-name">FMS Product Development</h2>
            <p>Requirements traceability, verification, and release planning.</p>
            <dl>
              <div><MetadataIcon kind="owner"/><span><dt>Project owner</dt><dd>Jane Doe</dd></span></div>
              <div><MetadataIcon kind="created"/><span><dt>Created</dt><dd>Feb 12, 2024</dd></span></div>
              <div><MetadataIcon kind="phase"/><span><dt>Lifecycle phase</dt><dd>Development</dd></span></div>
            </dl>
          </div>
        </section>

        <section className="buildLineage" aria-labelledby="build-lineage-heading">
          <header>
            <h2 id="build-lineage-heading">Build lineage</h2>
            <p>Builds are shown in evolutionary order from oldest to newest.</p>
          </header>
          <ol>
            {[...softwareBuilds].sort((a, b) => a.sortOrder - b.sortOrder).map((build, index) => {
              const release = releaseByVersion.get(build.version);
              const enabled = build.isAccessible && Boolean(release);
              return (
                <li key={build.id}>
                  <article
                    className={`softwareBuildCard${build.isCurrent ? " current" : ""}${enabled ? " accessible" : " unavailable"}`}
                    data-build-card
                    data-build-version={build.version}
                  >
                    <div className="buildCardTop">
                      <strong className="buildVersion">{build.isPlan ? "Next" : officialBuildName(build.version)}</strong>
                      <span className={`buildStatus ${build.status}`}>{build.statusLabel}</span>
                    </div>
                    <h3>{build.title}</h3>
                    {build.description && <p>{build.description}</p>}
                    {/*
                      The visible label is "Open build", so the accessible name has to contain that text
                      (WCAG 2.2 AA, Label in Name). "Open software build …" split those two words apart,
                      which broke the requirement and every locator that identified a build by its version.
                      Both identifiers are named because the card itself shows both.
                    */}
                    <button
                      type="button"
                      disabled={!enabled}
                      onClick={() => release && onOpenBuild(release)}
                      aria-label={build.isPlan ? "Plan next build placeholder" : `Open build ${build.version} (${officialBuildName(build.version)})`}
                      title={!enabled ? build.isPlan ? "No future build record has been created" : "Controlled workspace not available" : undefined}
                    >
                      <span aria-hidden="true">{build.isPlan ? "+" : "↗"}</span> {build.isPlan ? "Not created" : "Open build"}
                    </button>
                  </article>
                  {index < softwareBuilds.length - 1 && <span className="buildConnector" aria-hidden="true">→</span>}
                </li>
              );
            })}
          </ol>
        </section>

        <aside className="buildDetailsHelper">
          <span aria-hidden="true">◇</span>
          <div><h2>Build details</h2><p>Select a build above to view full details including changelog and other build information.</p></div>
          <button type="button" disabled title="Detailed build summaries are not available yet">Learn more <span aria-hidden="true">↗</span></button>
        </aside>
      </main>
    </div>
  );
}
