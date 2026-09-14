import { expect, test } from "@playwright/test";
import { officialBuildName, buildVersionOrder } from "../src/presentation";
import { parseRoute, projectAreaPath, projectSetupPath } from "../src/routing";
import {
  authorizedProjects,
  decodeWorkspaces,
  isInternalProjectWorkspace,
  resolveWorkspaceContext,
  workspaceDisplayName,
} from "../src/workspaceContext";
import { decodeProjectSetupDraftSummaries } from "../src/projectSetupDrafts";
import {
  decodeNativeSourceOptions,
  decodeSourceView,
  sourceConfigurationPayload,
  sourceFinalizationPayload,
  sourceUploadAccept,
} from "../src/projectSetupSource";

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

test("new project backing scopes stay hidden while authored legacy Programs retain their label", () => {
  const project = {
    project: { id: "project-new", name: "Navigation Inception", softwareProduct: "Navigation" },
    releases: [],
  };
  const internal = {
    program: {
      id: "program-new",
      name: "Navigation Inception backing scope 0123456789abcdef0123456789abcdef",
      code: "P-0123456789ABCDEF0123456789",
    },
    projects: [project],
  };
  const legacy = { ...internal, program: { id: "program-legacy", name: "Flight Program", code: "FMS" } };
  expect(isInternalProjectWorkspace(internal, project)).toBe(true);
  expect(workspaceDisplayName(internal, project)).toBe("Navigation Inception");
  expect(isInternalProjectWorkspace(legacy, project)).toBe(false);
  expect(workspaceDisplayName(legacy, project)).toBe("Flight Program");
});

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

test("source envelopes retain exact identity and configuration payload omits server observations", () => {
  const source = decodeSourceView({
    id: "source-1",
    kind: "ExternalBaseline",
    displayName: "Imported navigation",
    fileName: "navigation.reqif",
    format: "ReqIF",
    sha256: "abc123",
    metadata: { sourceSystem: "Other Tool" },
    categories: [
      { key: "Requirements", count: 2, requires: [], supported: true },
      { key: "Cases", count: 1, requires: ["Requirements"], supported: true },
    ],
    selectedCategories: ["Requirements", "Cases"],
    modules: [{
      key: "requirements",
      name: "Requirements",
      objectCount: 2,
      attributes: [{ key: "priority", name: "Priority" }],
      mappings: [{
        sourceAttribute: "priority",
        destination: "Statement",
        valueMappings: [{ sourceValue: "high", destinationValue: "High" }],
      }],
      include: true,
    }],
    relations: [{ sourceType: "satisfies", count: 1, include: true, sourceIsParent: true }],
    findings: [],
    findingResolutions: {},
    reconciliation: { ready: false, observedObjects: 2, includedObjects: 2, excludedObjects: 0, observedRelations: 1, includedRelations: 1, excludedRelations: 0, errors: [] },
    assertion: null,
  });
  expect(source?.id).toBe("source-1");
  expect(source?.sha256).toBe("abc123");
  expect(source?.modules[0].objectCount).toBe(2);
  const payload = sourceConfigurationPayload(source!, 7, ["Requirements"]);
  expect(payload).toMatchObject({ expectedVersion: 7, selectedCategories: ["Requirements"] });
  expect(payload).toEqual({
    expectedVersion: 7,
    selectedCategories: ["Requirements"],
    metadata: {},
    mapping: {
      sourceSha256: "abc123",
      objects: [],
      relations: [{ sourceKey: "satisfies", include: true, type: null, sourceIsParent: true }],
      findingResolutions: {},
    },
  });
  expect(JSON.stringify(payload)).not.toContain("objectCount");
  expect(JSON.stringify(payload)).not.toContain("password");
  expect(sourceUploadAccept).toContain(".reqif");
  expect(sourceUploadAccept).toContain(".csv");
  expect(sourceUploadAccept).toContain(".xlsx");
  const finalization = sourceFinalizationPayload(8, "attempt-103", {
    source: { ...source!, assertion: { text: "accepted source", hash: "assertion-hash" } },
    assertionAccepted: true,
    password: "memory-only-password",
  });
  expect(finalization).toEqual({
    expectedVersion: 8,
    idempotencyKey: "attempt-103",
    password: "memory-only-password",
    sourceAssertionHash: "assertion-hash",
    sourceAssertionAccepted: true,
  });
});

test("native source option pages are authoritative and keep server paging visible", () => {
  const page = decodeNativeSourceOptions({
    total: 101,
    offset: 50,
    limit: 50,
    items: [{
      baselineId: "baseline-2",
      projectId: "project-2",
      projectName: "Navigation",
      name: "Frozen baseline",
      displayNumber: "SW-01.02",
      state: "Frozen",
      requirementsCount: 3,
      casesCount: 1,
      proceduresCount: 1,
      evidenceCount: 1,
    }],
  });
  expect(page.offset).toBe(50);
  expect(page.total).toBe(101);
  expect(page.items[0]).toMatchObject({ baselineId: "baseline-2", state: "Frozen" });
});
