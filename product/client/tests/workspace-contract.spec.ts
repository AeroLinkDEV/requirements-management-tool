import { expect, test } from "@playwright/test";
import { decodeWorkspaces } from "../src/workspaceContext";
import { exactTraceArtifactPath, parseRoute } from "../src/routing";
import { apiBase, apiLogin, showcaseSeed } from "./auth";

const context = { programId: "program", projectId: "project", releaseId: "build" };
const release = { id: "build", version: "1.6", isReleased: false, predecessorReleaseId: null };
const project = { id: "project", name: "Example", softwareProduct: "" };
const program = { id: "program", name: "Example", code: "" };
const workspace = { program, projects: [{ project, releases: [release] }] };

test("workspace projection accepts nullable predecessor and preserves exact IDs and release flags", () => {
  expect(decodeWorkspaces([workspace])).toEqual([workspace]);
  const successor = { ...release, id: "next", isReleased: true, predecessorReleaseId: "build" };
  expect(decodeWorkspaces([{ ...workspace, projects: [{ project, releases: [release, successor] }] }])[0].projects[0].releases)
    .toEqual([release, successor]);
  expect(decodeWorkspaces([])).toEqual([]);
  expect(decodeWorkspaces([{ ...workspace, projects: [] }])).toEqual([{ ...workspace, projects: [] }]);
});

test("malformed workspace payloads never become a partially accepted context", () => {
  const invalid: unknown[] = [null, {}, { error: "Access denied" }, [null],
    [{ ...workspace, program: { ...program, id: null } }],
    [{ ...workspace, program: { ...program, name: 12 } }],
    [{ ...workspace, projects: null }],
    [{ ...workspace, projects: [{ project: { ...project, id: " " }, releases: [] }] }],
    [{ ...workspace, projects: [{ project: { ...project, softwareProduct: null }, releases: [] }] }],
    ...[{ ...release, id: undefined }, { ...release, id: 12 }, { ...release, id: " " },
      { ...release, version: null }, { ...release, isReleased: "false" },
      { ...release, predecessorReleaseId: {} }].map(item => [{ ...workspace, projects: [{ project, releases: [item] }] }]),
  ];
  for (const payload of invalid) expect(() => decodeWorkspaces(payload)).toThrow("Workspace response is invalid.");
  expect(() => decodeWorkspaces([workspace, null])).toThrow();
});

test("controlled trace links require exact context and revision identities", () => {
  const node = { id: "case", kind: "TestCase", revisionId: "revision" };
  for (const field of ["programId", "projectId", "releaseId"])
    expect(exactTraceArtifactPath({ ...context, [field]: " " }, node)).toBeUndefined();
  for (const id of ["", " "])
    expect(exactTraceArtifactPath(context, { ...node, id })).toBeUndefined();
  for (const revisionId of [undefined, null, "", " "])
    expect(exactTraceArtifactPath(context, { ...node, revisionId })).toBeUndefined();
  expect(exactTraceArtifactPath(context, { ...node, buildId: " " })).toBeUndefined();
  expect(exactTraceArtifactPath(context, { id: "revision", kind: "RequirementRevision", artifactId: " " })).toBeUndefined();
  expect(exactTraceArtifactPath(context, JSON.parse('{"id":12,"kind":"Evidence"}'))).toBeUndefined();
  expect(exactTraceArtifactPath(context, JSON.parse('{"id":"case","kind":"TestCase","revisionId":12}'))).toBeUndefined();
  expect(exactTraceArtifactPath(context, JSON.parse('{"id":"case","kind":"TestCase","displayNumber":12}'))).toBeUndefined();
  const path = exactTraceArtifactPath(context, { ...node, buildId: "historical", level: null, displayNumber: null });
  expect(path).toContain("/releases/historical/");
  expect(parseRoute(path ?? "")).toMatchObject({ artifactId: "case", artifactRevisionId: "revision" });
});

test("supported trace kinds retain exact targets and unsupported kinds stay non-openable", () => {
  for (const kind of ["ChangeRequest", "TestChangeRequest", "RequirementRevision", "TestProcedure", "TestCase", "TestExecution", "Evidence"]) {
    const path = exactTraceArtifactPath(context, { id: "record", kind, artifactId: "requirement", revisionId: "revision", level: "HighLevel", displayNumber: "HLR-000001.00" });
    expect(path, kind).toContain("/programs/program/projects/project/releases/build/");
    expect(path, kind).not.toContain("undefined");
  }
  for (const kind of ["Build", "Code", "Unknown", "testcase", ""])
    expect(exactTraceArtifactPath(context, { id: "record", kind })).toBeUndefined();
});

test("real workspace projection matches the client contract and error payloads cannot be selected", async ({ request }) => {
  await apiLogin(request);
  const seed = await showcaseSeed(request);
  const response = await request.get(`${apiBase}/api/workspaces`);
  expect(response.ok(), await response.text()).toBeTruthy();
  const workspaces = decodeWorkspaces(await response.json());
  const selected = workspaces.find(item => item.program.id === seed.programId)?.projects.find(item => item.project.id === seed.projectId);
  expect(selected?.releases.some(item => item.id === seed.activeReleaseId && !item.isReleased)).toBe(true);
  expect(selected?.releases.some(item => item.predecessorReleaseId === null)).toBe(true);
  expect(selected?.releases.some(item => typeof item.predecessorReleaseId === "string")).toBe(true);
  const missing = await request.get(`${apiBase}/api/build-context?projectId=${seed.projectId}&releaseId=00000000-0000-0000-0000-000000000001`);
  expect(missing.status()).toBe(404);
  const error: unknown = await missing.json();
  expect(error).toEqual({ error: "The selected build does not exist in this project." });
  expect(() => decodeWorkspaces(error)).toThrow();
});
