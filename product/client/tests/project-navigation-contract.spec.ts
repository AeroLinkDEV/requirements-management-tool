import { expect, test } from "@playwright/test";
import { officialBuildName, buildVersionOrder } from "../src/presentation";
import { parseRoute, projectAreaPath, projectSetupPath } from "../src/routing";
import {
  authorizedProjects,
  decodeWorkspaces,
  resolveWorkspaceContext,
} from "../src/workspaceContext";
import { decodeProjectSetupDraftSummaries } from "../src/projectSetupDrafts";

const wireWorkspaces = [
  {
    program: { id: "program-a", name: "Program A", code: "PA" },
    projects: [
      {
        project: { id: "project-a", name: "Navigation Software", softwareProduct: "Navigation" },
        releases: [
          { id: "release-10", version: "1.0", isReleased: true, predecessorReleaseId: null },
          {
            id: "release-11",
            version: "1.1",
            isReleased: false,
            predecessorReleaseId: "release-10",
          },
        ],
      },
    ],
  },
];

test("the project selector is derived only from the authorized workspace projection", () => {
  const workspaces = decodeWorkspaces(wireWorkspaces);
  expect(authorizedProjects(workspaces)).toEqual([
    {
      id: "project-a",
      name: "Navigation Software",
      softwareProduct: "Navigation",
      programId: "program-a",
      programName: "Program A",
      programCode: "PA",
      releases: workspaces[0].projects[0].releases,
    },
  ]);
});

test("stable project routes resolve without entering a build and preserve actual release edges", () => {
  const workspaces = decodeWorkspaces(wireWorkspaces);
  const path = projectAreaPath("project-a", "builds");
  expect(path).toBe("/projects/project-a/builds");
  expect(parseRoute(path)).toMatchObject({ view: "builds", projectId: "project-a" });

  const resolved = resolveWorkspaceContext(workspaces, parseRoute(path));
  expect(resolved.project?.project.id).toBe("project-a");
  expect(resolved.release).toBeUndefined();
  expect(resolved.unavailable).toBe(false);
  expect(resolved.project?.releases[1].predecessorReleaseId).toBe("release-10");
});

test("project setup routes retain the durable draft identity for resume", () => {
  expect(projectSetupPath()).toBe("/projects/new");
  expect(parseRoute(projectSetupPath())).toMatchObject({ view: "projectSetup" });
  const draftId = "00000000-0000-4000-8000-000000000001";
  expect(parseRoute(projectSetupPath(draftId))).toMatchObject({
    view: "projectSetup",
    projectSetupDraftId: draftId,
  });
});

test("setup draft discovery fails closed for malformed identities and retains valid resumable drafts", () => {
  const draftId = "00000000-0000-4000-8000-000000000001";
  expect(
    decodeProjectSetupDraftSummaries([
      { draftId: "not-an-id", state: "Draft", currentStep: "Details", version: 1, project: {} },
      {
        draftId,
        state: "Completed",
        currentStep: "Complete",
        version: 3,
        project: { name: "Done", softwareProduct: "Nav" },
      },
      {
        draftId,
        state: "Draft",
        currentStep: "Review",
        version: 2,
        project: { name: "Nav", softwareProduct: "Navigation" },
      },
    ]),
  ).toEqual([
    {
      draftId,
      state: "Draft",
      currentStep: "Review",
      version: 2,
      lastSavedAt: undefined,
      project: { name: "Nav", softwareProduct: "Navigation" },
    },
  ]);
});

test("legacy slug routes resolve only when one authorized project owns the slug", () => {
  const workspaces = decodeWorkspaces(wireWorkspaces);
  const legacy = resolveWorkspaceContext(
    workspaces,
    parseRoute("/projects/navigation-software/builds"),
  );
  expect(legacy.project?.project.id).toBe("project-a");

  const collision = decodeWorkspaces([
    ...wireWorkspaces,
    {
      ...wireWorkspaces[0],
      program: { id: "program-b", name: "Program B", code: "PB" },
      projects: [
        {
          ...wireWorkspaces[0].projects[0],
          project: { ...wireWorkspaces[0].projects[0].project, id: "project-b" },
        },
      ],
    },
  ]);
  const refused = resolveWorkspaceContext(
    collision,
    parseRoute("/projects/navigation-software/builds"),
  );
  expect(refused.unavailable).toBe(true);
  expect(refused.project).toBeUndefined();
});

test("build links reject an absent release instead of falling back to another build", () => {
  const workspaces = decodeWorkspaces(wireWorkspaces);
  const route = parseRoute(
    "/programs/program-a/projects/project-a/releases/removed/command-center",
  );
  const resolved = resolveWorkspaceContext(workspaces, route);
  expect(resolved).toEqual({
    active: undefined,
    project: undefined,
    release: undefined,
    unavailable: true,
  });
});

test("official build names and ordering use the maintained SW-NN.NN format", () => {
  expect(officialBuildName("0.01")).toBe("SW-00.01");
  expect(officialBuildName("1.02")).toBe("SW-01.02");
  expect(officialBuildName("1.3")).toBe("SW-01.30");
  expect(officialBuildName("1.30")).toBe("SW-01.30");
  expect(buildVersionOrder("1.3")).toBe(buildVersionOrder("1.30"));
  for (const value of ["", "NaN", "1", "1.", ".3", "1.2.3", "-1.2", "100.0", "1.100"])
    expect(officialBuildName(value)).toBeUndefined();
});
