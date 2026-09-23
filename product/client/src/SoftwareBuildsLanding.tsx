import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import { ProjectIcon } from "./ProjectsLanding";
import { buildVersionOrder, officialBuildName } from "./presentation";
import type { WorkspaceRelease } from "./workspaceContext";
import "./SoftwareBuildsLanding.css";

export type SelectableRelease = WorkspaceRelease;

function MetadataIcon({ kind }: { kind: "builds" | "released" | "work" }) {
  const path = kind === "builds"
    ? <><rect x="3" y="4" width="22" height="18" rx="2"/><path d="M3 10h22M8 2v5m12-5v5M7 16h4m3 0h4"/></>
    : kind === "released"
      ? <><path d="M12 3 23 8v7c0 6-5 10-11 13C6 25 1 21 1 15V8z"/><path d="m7 15 3 3 6-7"/></>
      : <><circle cx="12" cy="12" r="9"/><path d="M12 7v6l4 2"/></>;
  return <svg viewBox="0 0 26 26" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">{path}</svg>;
}

function statusFor(release: SelectableRelease, identity?: string) {
  if (!identity) return { key: "unavailable", label: "Unavailable" };
  return release.isReleased
    ? { key: "released", label: "Released" }
    : { key: "in-work", label: "In Work" };
}

function sortReleases(releases: SelectableRelease[]) {
  return [...releases].sort((left, right) => {
    const leftOrder = buildVersionOrder(left.version);
    const rightOrder = buildVersionOrder(right.version);
    if (leftOrder === undefined && rightOrder === undefined) return left.id.localeCompare(right.id);
    if (leftOrder === undefined) return 1;
    if (rightOrder === undefined) return -1;
    return leftOrder - rightOrder || left.id.localeCompare(right.id);
  });
}

export default function SoftwareBuildsLanding({
  user,
  releases,
  projectName,
  softwareProduct,
  onOpenBuild,
  onProjectOverview,
  onImportedBaselines,
  onPersonnel,
  onProjectConfiguration,
  onSignOut,
}: {
  user: AuthUser;
  releases: SelectableRelease[];
  projectName: string;
  softwareProduct: string;
  onOpenBuild: (release: SelectableRelease) => void;
  onProjectOverview: () => void;
  onImportedBaselines: () => void;
  onPersonnel: () => void;
  onProjectConfiguration: () => void;
  onSignOut: () => void;
}) {
  const ordered = sortReleases(releases);
  const releaseById = new Map(ordered.map(release => [release.id, release]));
  const releasedCount = ordered.filter(release => release.isReleased).length;
  const inWorkCount = ordered.length - releasedCount;

  return (
    <div className="buildsLandingPage">
      <PortalHeader user={user} onSignOut={onSignOut} />
      <main className="buildsLandingMain">
        <nav className="buildBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onProjectOverview}>Projects</button>
          <span aria-hidden="true">/</span>
          <strong>{projectName}</strong>
        </nav>
        <header className="buildsLandingHeading">
          <div>
            <h1>Software Builds</h1>
            <p>Select a build to explore or work on.</p>
          </div>
          <div className="buildsLandingActions">
            <button type="button" className="personnelButton" onClick={onPersonnel}>Personnel</button>
            <button type="button" className="approvalConfigurationButton" onClick={onProjectConfiguration}>Project configuration</button>
            <button type="button" className="importedBaselinesButton" onClick={onImportedBaselines}>Imported baselines</button>
            <button type="button" className="projectOverviewButton" onClick={onProjectOverview}><span aria-hidden="true">←</span> All projects</button>
          </div>
        </header>

        <section className="buildProjectSummary" aria-labelledby="build-project-name">
          <span className="buildProjectIcon"><ProjectIcon name="project" /></span>
          <div className="buildProjectContent">
            <h2 id="build-project-name">{projectName}</h2>
            <p>{softwareProduct}</p>
            <dl>
              <div><MetadataIcon kind="builds"/><span><dt>Builds</dt><dd>{ordered.length}</dd></span></div>
              <div><MetadataIcon kind="released"/><span><dt>Released</dt><dd>{releasedCount}</dd></span></div>
              <div><MetadataIcon kind="work"/><span><dt>In work</dt><dd>{inWorkCount}</dd></span></div>
            </dl>
          </div>
        </section>

        <section className="buildLineage" aria-labelledby="build-lineage-heading">
          <header>
            <h2 id="build-lineage-heading">Build lineage</h2>
            <p>Builds are ordered by version. Predecessors show which build each one follows.</p>
          </header>
          {ordered.length ? (
            <ol>
              {ordered.map(release => {
                const identity = officialBuildName(release.version);
                const status = statusFor(release, identity);
                const predecessor = release.predecessorReleaseId ? releaseById.get(release.predecessorReleaseId) : undefined;
                const predecessorIdentity = predecessor ? officialBuildName(predecessor.version) : undefined;
                const enabled = Boolean(identity);
                return (
                  <li key={release.id} data-predecessor-release-id={release.predecessorReleaseId ?? ""}>
                    <article
                      className={`softwareBuildCard ${status.key}${enabled ? " accessible" : " unavailable"}`}
                      data-build-card
                      data-build-id={release.id}
                      data-build-version={release.version}
                    >
                      <div className="buildCardTop">
                        <strong className="buildVersion">{identity ?? "Identity unavailable"}</strong>
                        <span className={`buildStatus ${status.key}`}>{status.label}</span>
                      </div>
                      <h3>{identity ? `Build ${release.version}` : "Unsupported build version"}</h3>
                      <p className="buildLifecycleDescription">
                        {release.isReleased ? "Released build · read-only" : "In-work build · authoring available"}
                      </p>
                      <p className="buildPredecessor" data-lineage-edge>
                        <strong>Predecessor</strong>{predecessorIdentity ? <span>↳ {predecessorIdentity}</span> : <span>{release.predecessorReleaseId ? "Unavailable source build" : "None recorded"}</span>}
                      </p>
                      <button
                        type="button"
                        disabled={!enabled}
                        onClick={() => enabled && onOpenBuild(release)}
                        aria-label={enabled ? `Open build ${release.version} (${identity})` : `Open build ${release.version}`}
                        title={!enabled ? "This build has no supported official identity" : undefined}
                      >
                        <span aria-hidden="true">↗</span> Open build
                      </button>
                    </article>
                  </li>
                );
              })}
            </ol>
          ) : (
            <p className="buildLineageEmpty">This project has no software builds yet. Use <b>Imported baselines</b> above to bring in an existing baseline as its first build.</p>
          )}
        </section>
      </main>
    </div>
  );
}
