import { expect, test } from "@playwright/test";
import { apiLogin, login, openNavigationGroup, selectProgram } from "./auth";

test("Lifecycle Decision Room connects readiness, impact, evidence, people, and compact identifiers", async ({ page, request }) => {
  test.setTimeout(90_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  await selectProgram(page,"Flight Management System Live Program");

  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();
  await expect(page.getByRole("heading", { name: "Release Readiness" })).toBeVisible();
  await expect(page.getByRole("region", { name: "Release lifecycle" })).toBeVisible();
  // The attention panel must show what the server computed, not organisational facts invented in the
  // browser. It previously rendered an owner, a due date and a priority keyed by position in the list;
  // this assertion used to require that fabrication to be present.
  await expect(page.getByText("To clear this").first()).toBeVisible();
  await expect(page.getByText("Remaining").first()).toBeVisible();
  // Traceability, coverage, code traceability, verification and evidence all depend on the exact materialized
  // requirement population. The Code gate is deliberately counted here so removing it cannot leave a green UI.
  await expect(page.getByText("Waiting for a materialized baseline", { exact: false })).toHaveCount(5);
  await expect(page.getByText("Not evaluated", { exact: true })).toHaveCount(5);
  await expect(page.getByRole("button", { name: /Open prerequisite/ })).toHaveCount(5);
  await expect(page.getByRole("button", { name: /Explore changes vs 1\.5/ })).toBeVisible();

  await page.getByRole("button", { name: "Explore baseline changes →" }).click();
  await expect(page.getByRole("heading", { name: "Change Impact Review" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Lifecycle impact" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Requirement revision preview" })).toBeVisible();
  await expect(page.getByText(/SYSR-\d{6}\.\d{2}/).first()).toBeVisible();
  await expect(page.getByRole("img", { name: /Maya Patel, Systems Lead/ })).toBeVisible();

  await page.getByRole("button", { name: "← Back to readiness" }).click();
  await page.getByRole("button", { name: "Review release evidence" }).click();
  await expect(page.getByRole("heading", { name: "Release Evidence & Decision" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Evidence checklist" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Approval path" })).toBeVisible();
  const releaseNarrative = page.locator(".releaseNarrative");
  await expect(releaseNarrative.getByText("—", { exact: true })).toHaveCount(4);
  await expect(releaseNarrative.getByText("Counts become exact when the candidate baseline is materialized.")).toBeVisible();
  await expect(page.getByRole("img", { name: /Ethan Brooks, Verification Lead/ })).toBeVisible();
  await expect(page.getByRole("img", { name: /Olivia Chen, Program Manager/ })).toBeVisible();
  await expect(page.getByRole("img", { name: /Daniel Reyes, Release Manager/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /SYSRD-\d{6}\.\d{2}/ })).toBeVisible();
  await expect(page.getByRole("button", { name: /TRACE-\d{6}\.\d{2}/ })).toBeVisible();
});

/**
 * The decision room on a build that already shipped.
 *
 * Only the in-work build had a release campaign, so this page answered "Release readiness is not configured"
 * — which reads as a fault in the product rather than as what it was, a page with nothing to describe. A
 * released build is the one case where this page has the whole story: everything that was tracked, and every
 * approval that let it ship.
 */
test("a released build tells the story that let it ship, and offers no decision to take", async ({ page, request }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1600, height: 1000 });
  await apiLogin(request);
  await login(page, 'admin', { openProject: false });
  // Into the released build rather than the in-work one, the way the build lineage opens it.
  await page.getByRole("link", { name: "Open FMS Product Development" }).click();
  await page.getByRole("button", { name: "Open build 1.5" }).click();
  await expect(page.getByLabel("Active build 1.5")).toBeVisible({ timeout: 30_000 });
  await openNavigationGroup(page, "RELEASE & CONFIGURATION");
  await page.getByRole("link", { name: "Lifecycle Decision Room" }).click();

  // Not the empty state. This was the whole complaint.
  await expect(page.getByText("Release readiness is not configured")).toHaveCount(0);
  await expect(page.getByRole("heading", { name: "Release Readiness" })).toBeVisible({ timeout: 30_000 });

  await page.getByRole("button", { name: "Review release evidence" }).click();
  await expect(page.getByRole("heading", { name: "Release Evidence & Decision" })).toBeVisible({ timeout: 30_000 });

  // The story: the release was authorized, and real people signed for it.
  await expect(page.getByText("Release authorized")).toBeVisible();
  await expect(page.getByRole("heading", { name: "Decision recorded" })).toBeVisible();
  await expect(page.getByText(/approvers signed this release/)).toBeVisible();

  // And nothing left to do to it. The controls were only ever disabled by outstanding blockers, and a
  // shipped release has none — so this page used to offer to approve a release that had already happened.
  await expect(page.getByRole("button", { name: "Approve", exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Record decision" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Return for rework" })).toHaveCount(0);
});
