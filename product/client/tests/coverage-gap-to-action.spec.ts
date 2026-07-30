import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

/**
 * The workspace could filter on the verification *method* an author declared and nothing else, so "which of
 * my requirements need verification attention?" had no answer short of the release-readiness counts, which
 * arrive far too late to act on.
 *
 * This drives the whole path: filter to the gap, see it labelled, act on it, and confirm the server agrees.
 * The last step matters because the state is computed, not stored — a badge that reads Suspect while the API
 * would put the requirement somewhere else is the failure this test exists to catch.
 */
test("a verification gap can be filtered to, read, and acted on from the requirements workspace", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000);
  // Below 1360px the requirement inspector becomes a fixed overlay and covers the right-hand table columns,
  // including the one this test clicks. The workspace auto-selects a requirement on load, so that overlay is
  // open before the test touches anything.
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  await openNavigationGroup(page, "SYSTEMS ENGINEERING");
  await page.getByRole("link", { name: "System Requirements Explorer" }).click();
  await expect(
    page.getByRole("heading", { name: "System Requirements Explorer" }),
  ).toBeVisible();
  await expect(
    page.getByRole("status", { name: /Loading controlled requirements/ }),
  ).toBeHidden();

  // Every one of the 150 system requirements is covered except the two whose procedure is being reworked
  // for FMS 1.6, so the unfiltered count and the filtered count must differ.
  await expect(page.getByText("150 requirements")).toBeVisible();

  await page.getByLabel("Coverage state filter").selectOption("suspect");
  await expect(page.getByText("2 requirements")).toBeVisible();

  // The state is labelled on the row, not buried in a detail panel.
  const suspectBadges = page.locator("button.coverageState.suspect");
  await expect(suspectBadges).toHaveCount(2);
  await expect(suspectBadges.first()).toHaveText("Suspect");

  // Acting on it opens the trace panel, which is where the procedure that stopped counting actually is.
  await suspectBadges.first().click();
  await expect(page.locator(".traceInspector")).toBeVisible();

  // And that panel must agree with the badge that sent the reader here. It counted "confirmed tests" with
  // the raw suspect flag, so a link to a procedure being rewritten read as confirmed — the destination
  // contradicting the row that pointed at it.
  const confirmedTests = page.locator(".traceSummary article", { hasText: "confirmed tests" });
  await expect(confirmedTests.locator("b")).toHaveText("0");

  // The filter chip states the applied constraint and can be cleared back to the full population.
  await page.getByRole("button", { name: "Close requirement inspector" }).click();
  await expect(page.getByText("Coverage: Suspect")).toBeVisible();

  // The durable answer, not the rendered one. The workspace computes nothing locally, so the API must
  // return the same two requirements and must not report either of them as covered.
  const workspaces = await (await request.get(`${apiBase}/api/workspaces`)).json();
  const fms = workspaces.find(
    (x: { program: { name: string } }) => x.program.name === "Flight Management System Live Program",
  );
  const project = fms.projects[0].project;
  const suspect = await (
    await request.get(
      `${apiBase}/api/enterprise-requirements/workspace?projectId=${project.id}&level=System&coverageState=suspect&page=1&pageSize=50`,
    )
  ).json();
  expect(suspect.totalCount).toBe(2);
  expect(suspect.items.every((x: { coverageState: string }) => x.coverageState === "Suspect")).toBe(true);

  const covered = await (
    await request.get(
      `${apiBase}/api/enterprise-requirements/workspace?projectId=${project.id}&level=System&coverageState=covered&page=1&pageSize=250`,
    )
  ).json();
  expect(covered.totalCount).toBe(148);
  const suspectNumbers = suspect.items.map((x: { baseNumber: string }) => x.baseNumber);
  const coveredNumbers = covered.items.map((x: { baseNumber: string }) => x.baseNumber);
  expect(coveredNumbers.some((x: string) => suspectNumbers.includes(x))).toBe(false);
});
