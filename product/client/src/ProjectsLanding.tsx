import type { AuthUser } from "./IdentityCenter";
import PortalHeader from "./PortalHeader";
import "./ProjectsLanding.css";

type ProjectIconName =
  | "fms"
  | "satellite"
  | "navigation"
  | "certification"
  | "coverage"
  | "integrity"
  | "route"
  | "sensors"
  | "map"
  | "display"
  | "create";

type ProjectCardDefinition = {
  id: string;
  name: string;
  description: string;
  icon: ProjectIconName;
  status: "active" | "mock" | "disabled";
  statusLabel?: string;
  active: boolean;
  destination?: "current-workspace";
  footer?: string;
  cardType: "project" | "create-project";
};

const projectCards: readonly ProjectCardDefinition[] = [
  {
    id: "fms-product-development",
    name: "FMS Product Development",
    description: "Requirements, traceability, verification, and release planning.",
    icon: "fms",
    status: "active",
    statusLabel: "Active",
    active: true,
    destination: "current-workspace",
    footer: "Opens your current workspace.",
    cardType: "project",
  },
  {
    id: "gps-receiver-modernization",
    name: "GPS Receiver Modernization",
    description: "Upgrade planning and architecture study.",
    icon: "satellite",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "integrated-navigation-suite",
    name: "Integrated Navigation Suite",
    description: "Concept phase for integrated navigation subsystem.",
    icon: "navigation",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "fms-certification-block-2",
    name: "FMS Certification Block 2",
    description: "Certification planning and requirements package.",
    icon: "certification",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "waas-sbas-upgrade",
    name: "WAAS / SBAS Upgrade",
    description: "Upgrade planning for performance and coverage.",
    icon: "coverage",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "gnss-integrity-monitor",
    name: "GNSS Integrity Monitor",
    description: "Integrity monitoring and fault-detection concept.",
    icon: "integrity",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "flight-planning-core",
    name: "Flight Planning Core",
    description: "Core algorithms and route-optimization planning.",
    icon: "route",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "multi-sensor-position-engine",
    name: "Multi-Sensor Position Engine",
    description: "Fusion algorithms and sensor-integration concept.",
    icon: "sensors",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "avionics-map-database",
    name: "Avionics Map Database",
    description: "Database architecture and update strategy.",
    icon: "map",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "fms-hmi-refresh",
    name: "FMS HMI Refresh",
    description: "User-interface modernization and usability study.",
    icon: "display",
    status: "mock",
    statusLabel: "Mock",
    active: false,
    footer: "Mock project",
    cardType: "project",
  },
  {
    id: "create-project",
    name: "Create New Project",
    description: "Start a new requirements workspace for your team.",
    icon: "create",
    status: "disabled",
    active: false,
    cardType: "create-project",
  },
];

export function ProjectIcon({ name }: { name: ProjectIconName }) {
  const shared = {
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.7,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
  };
  const paths: Record<ProjectIconName, React.ReactNode> = {
    fms: <><rect x="5" y="4" width="22" height="26" rx="3"/><rect x="9" y="8" width="14" height="10" rx="1"/><path d="M9 23h2m3 0h2m3 0h2M9 27h2m3 0h2m3 0h2"/></>,
    satellite: <><path d="m13 13 6 6m-8-4 6-6 6 6-6 6zM8 7l5 5-4 4-5-5zm16 16 5 5-5 2-4-4z"/><path d="M20 11c4-3 8-2 10 0M22 8c5-4 9-3 11-1"/></>,
    navigation: <><circle cx="17" cy="17" r="13"/><path d="m21 10-3 9-9 3 3-9zM17 1v4m0 24v4M1 17h4m24 0h4"/></>,
    certification: <><path d="M7 3h15l6 6v20H7zM22 3v7h6M11 15h12m-12 5h8"/><circle cx="23" cy="24" r="5"/><path d="m20 29-1 4 4-2 3 2 1-5"/></>,
    coverage: <><path d="M17 25V14m-4 11h8M9 31h16"/><circle cx="17" cy="10" r="2"/><path d="M10 17a10 10 0 0 1 14 0M6 13a15 15 0 0 1 22 0M3 9a20 20 0 0 1 28 0"/></>,
    integrity: <><path d="M17 3 29 8v8c0 8-5 13-12 16C10 29 5 24 5 16V8z"/><path d="m11 17 4 4 8-9"/></>,
    route: <><circle cx="6" cy="27" r="3"/><circle cx="14" cy="12" r="3"/><path d="M8 25c4-2 2-7 5-10m4-2c5 1 6 7 10 6"/><path d="m24 7 7 3-6 3 1-3z"/></>,
    sensors: <><circle cx="17" cy="17" r="4"/><circle cx="17" cy="17" r="9"/><path d="M17 3v3m0 22v3M3 17h3m22 0h3M7 7l3 3m14 14 3 3m0-20-3 3M10 24l-3 3"/><path d="m25 25 5 5m0-5-5 5"/></>,
    map: <><path d="m4 7 8-3 10 3 8-3v24l-8 3-10-3-8 3zM12 4v24M22 7v24"/><path d="M17 14c0-3 5-3 5 0 0 2-2.5 5-2.5 5S17 16 17 14z"/></>,
    display: <><rect x="3" y="5" width="28" height="23" rx="3"/><path d="M8 22V11h18M10 19l4-4 4 2 5-6M12 32h10m-5-4v4"/></>,
    create: <><circle cx="17" cy="17" r="14"/><path d="M17 10v14m-7-7h14"/></>,
  };
  return <svg viewBox="0 0 34 34" aria-hidden="true" focusable="false" {...shared}>{paths[name]}</svg>;
}

function ProjectCard({
  project,
  workspaceHref,
  onOpenWorkspace,
}: {
  project: ProjectCardDefinition;
  workspaceHref?: string;
  onOpenWorkspace: () => void;
}) {
  if (project.cardType === "create-project") {
    return (
      <article className="projectCard createProjectCard" data-project-card aria-disabled="true">
        <span className="createProjectIcon"><ProjectIcon name={project.icon}/></span>
        <h2>{project.name}</h2>
        <p>{project.description}</p>
        <small>Project creation is not available yet.</small>
      </article>
    );
  }

  const content = (
    <>
      <div className="projectCardTop">
        <span className="projectIcon"><ProjectIcon name={project.icon}/></span>
        <span className={`projectBadge ${project.status}`}>{project.statusLabel}</span>
      </div>
      <h2>{project.name}</h2>
      <p>{project.description}</p>
      <footer>
        {project.active && <strong>Open project <span aria-hidden="true">→</span></strong>}
        <small><span aria-hidden="true">{project.active ? "◇" : "□"}</span>{project.footer}</small>
      </footer>
    </>
  );

  if (project.active && workspaceHref) {
    return (
      <a
        className="projectCard activeProjectCard"
        data-project-card
        href={workspaceHref}
        onClick={(event) => {
          event.preventDefault();
          onOpenWorkspace();
        }}
        aria-label={`Open ${project.name}`}
      >
        {content}
      </a>
    );
  }

  return (
    <article className="projectCard mockProjectCard" data-project-card aria-disabled="true">
      {content}
    </article>
  );
}

export default function ProjectsLanding({
  api,
  user,
  workspaceHref,
  onOpenWorkspace,
  onSignOut,
}: {
  api: string;
  user: AuthUser;
  workspaceHref?: string;
  onOpenWorkspace: () => void;
  onSignOut: () => void;
}) {
  return (
    <div className="projectsPage">
      <PortalHeader api={api} user={user} onSignOut={onSignOut}/>
      <main className="projectsMain">
        <header>
          <div>
            <p className="eyebrow">AUTHORIZED WORKSPACES</p>
            <h1>Projects</h1>
            <p>Select a project to continue.</p>
          </div>
        </header>
        <section className="projectsGrid" aria-label="Available projects">
          {projectCards.map((project) => (
            <ProjectCard
              key={project.id}
              project={project}
              workspaceHref={project.destination ? workspaceHref : undefined}
              onOpenWorkspace={onOpenWorkspace}
            />
          ))}
        </section>
      </main>
    </div>
  );
}
