import { expect, test } from "@playwright/test";
import { apiBase, apiLogin, login, selectProgram, showcaseSeed } from "./auth";

/**
 * Check-in wrote a serialized payload of session and evidence identifiers, adapter names, snapshot hashes and
 * aggregate versions into the audit event's narrative field, and the timeline faithfully rendered it. A reader
 * looking for who changed what got a wall of GUIDs as the audit story.
 *
 * A real checkout and check-in are performed here rather than reading a seeded record, because the seed
 * contains no controlled-editing events — a test that only read seeded history would have passed against the
 * defect it is meant to catch.
 */
test("a checked-in change reads as a narrative and keeps its technical evidence beside it", async ({ page, request }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await apiLogin(request);
  const showcase = await showcaseSeed(request);

  const created = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      baseNumber: "CLIENT-IGNORED", projectId: showcase.projectId, targetReleaseId: showcase.activeReleaseId,
      title: `Audit narrative probe ${Date.now()}`, problem: "Problem", analysis: "Analysis", solution: "Solution",
      requirementChanges: [{
        baseNumber: "SYSR-000001", revision: 0, level: "System", kind: "Introduce",
        statement: "The FMS shall record an auditable controlled edit.", rationale: "Audit narrative probe.",
        verificationMethod: "Test",
      }],
    },
  });
  expect(created.ok(), await created.text()).toBe(true);
  const scr = await created.json();

  const checkout = await request.post(`${apiBase}/api/controlled-editing/checkout`,
    { data: { artifactType: "ChangeRequest", artifactId: scr.id, leaseMinutes: 15 } });
  expect(checkout.ok(), await checkout.text()).toBe(true);
  const lock = await checkout.json();

  const checkIn = await request.post(`${apiBase}/api/controlled-editing/sessions/${lock.id}/check-in`,
    { data: { expectedVersion: lock.version } });
  expect(checkIn.ok(), await checkIn.text()).toBe(true);

  await login(page, 'admin', { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/systems/change-requests/${scr.id}`, page.url()).toString(), { waitUntil: "load" });
  await expect(page.getByText("Audit history")).toBeVisible({ timeout: 30_000 });

  // The check-in is present, and reads as a sentence rather than a payload.
  const checkedIn = page.locator(".auditRow", { hasText: "Artifact Checked In" });
  await expect(checkedIn).toBeVisible({ timeout: 30_000 });
  const narratives = await page.locator(".auditRow p").allInnerTexts();
  for (const line of narratives) {
    expect(line.trimStart().startsWith("{"), `audit narrative rendered as JSON: ${line.slice(0, 90)}`).toBe(false);
    expect(line).not.toContain("evidenceId");
    expect(line).not.toContain("SnapshotHash");
  }

  // Moving the evidence out of sight would have been a different defect: it is labelled and reachable.
  const evidence = checkedIn.locator(".auditEvidence");
  await expect(evidence).toBeVisible();
  await evidence.locator("summary").click();
  await expect(evidence.getByText("Evidence id")).toBeVisible();
  await expect(evidence.getByRole("button", { name: "Copy evidence" })).toBeVisible();
});
