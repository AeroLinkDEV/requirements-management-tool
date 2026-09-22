import { expect, test, type Page } from "@playwright/test";
import { chooseCategory, login, selectProgram } from "./auth";

/**
 * The working note is a draft of the rationale a backward move or a rejection will ask for.
 *
 * It is deliberately not part of the controlled record. The server keeps free text only where a
 * transition requires a rationale and discards it everywhere else, so what these journeys hold is the
 * honest version of the promise the field makes: the note survives in this browser, it arrives in the
 * dialog, and what the record keeps is the rationale confirmed there — never the note itself.
 *
 * Before this, `note` existed with an autosave, a restore offer and a save indicator, and nothing on
 * the page could write to it (#1074).
 */

const header = (page: Page) => page.getByRole("region", { name: "Problem Report lifecycle" });
const noteBox = (page: Page) => header(page).getByLabel("Working note");

const openProblemReports = async (page: Page) => {
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: "load" });
};

const createIsolatedDraft = async (page: Page, title: string) => {
  await page.getByRole("button", { name: "+ Record problem" }).click();
  const dialog = page.getByRole("dialog", { name: "Record a problem" });
  await dialog.getByLabel("Title").fill(title);
  await dialog
    .getByRole("textbox", { name: "Problem Description paragraph 1" })
    .fill("A working note must reach the dialog that asks for a rationale.");
  await chooseCategory(dialog, "Code Issue — Functional Impact");
  await dialog.getByRole("button", { name: "Save Draft PR" }).click();
  await expect(page.getByRole("heading", { name: title })).toBeVisible();

  await page.getByLabel("Search").fill(title);
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(page.locator(".prList > button")).toHaveCount(1, { timeout: 30_000 });
  await page.locator(".prList > button").first().click();
  await expect(page.locator(".prDetail h2")).toHaveText(title);
};

/** Idempotent: clicking an open `<details>` closes it, which is not what any caller here wants. */
const openNote = async (page: Page) => {
  const disclosure = header(page).locator("details.prStateNote");
  if (!(await disclosure.evaluate((el: HTMLDetailsElement) => el.open))) {
    await disclosure.locator("summary").click();
  }
  await expect(noteBox(page)).toBeVisible();
};

test("a working note can be written, and says it is not the record", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `Working note field ${Date.now()}`);

  // The field #1074 was missing. Its absence was the whole defect: the value existed, the autosave
  // existed, and nothing could put text in either.
  await openNote(page);
  await noteBox(page).fill("The SCCB wants the containment section before this moves.");
  await expect(noteBox(page)).toHaveValue(/containment section/);

  // A field that autosaves and is never submitted has to admit that, or it reads as evidence.
  await expect(header(page)).toContainText("not part of the controlled record");
});

test("the note survives a reload of the same report, in this browser", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  const title = `Working note persistence ${Date.now()}`;
  await createIsolatedDraft(page, title);

  await openNote(page);
  const text = `Held for the next backward move ${Date.now()}`;
  await noteBox(page).fill(text);
  // The autosave is debounced; its own indicator is the signal that it has been written. The wording
  // is DraftNotice's, so it is matched as DraftNotice writes it.
  await expect(header(page).getByText(/Draft saved/)).toBeVisible({ timeout: 30_000 });
  // Where a draft is held is not a detail: one survives this machine dying and the other does not.
  await expect(header(page)).toContainText("this browser");

  await page.reload();
  await page.getByLabel("Search").fill(title);
  await page.getByRole("button", { name: "Apply filters" }).click();
  await expect(page.locator(".prList > button")).toHaveCount(1, { timeout: 30_000 });
  await page.locator(".prList > button").first().click();
  await expect(page.locator(".prDetail h2")).toHaveText(title);

  // Offered back rather than silently reinstated: it is the reader's text, and theirs to discard. The
  // offer sits outside the disclosure on purpose — held work nobody is told about is work nobody
  // recovers, and an offer behind a closed `<details>` is exactly that.
  const restore = header(page).getByRole("button", { name: /Restore/ });
  await expect(restore).toBeVisible({ timeout: 30_000 });
  await restore.click();
  await openNote(page);
  await expect(noteBox(page)).toHaveValue(text);
});

test("the note arrives in the rationale dialog, and the dialog is what is submitted", async ({
  page,
}) => {
  test.setTimeout(240_000);
  await openProblemReports(page);
  await createIsolatedDraft(page, `Working note to rationale ${Date.now()}`);

  await openNote(page);
  const drafted = "Drafted before the move was made.";
  await noteBox(page).fill(drafted);

  await header(page).getByRole("button", { name: /Move to Ready for SCCB/ }).click();
  const currentStep = header(page).getByRole("list").locator('[aria-current="step"]');
  await expect(currentStep).toContainText("Ready for SCCB");

  // A forward move does not spend the note. Only a transition that submits its text does, or drafting
  // a rationale would be destroyed by any unrelated action taken first.
  await openNote(page);
  await expect(noteBox(page)).toHaveValue(drafted);

  // Ready for SCCB -> Draft requires a rationale, and the draft is already in the field.
  const menu = header(page).locator("details.prBackward");
  await menu.locator("summary").click();
  await menu.getByRole("button", { name: /^Draft/ }).click();
  const dialog = page.getByRole("dialog", { name: "Backward Problem Report transition" });
  await expect(dialog.getByLabel("Rationale")).toHaveValue(drafted);

  // Editable there, and the dialog's value is what the record keeps — the note is never sent.
  const confirmed = `${drafted} Revised in the dialog.`;
  await dialog.getByLabel("Rationale").fill(confirmed);
  await dialog.getByRole("button", { name: /Move to Draft/ }).click();
  await expect(currentStep).toContainText("Draft");

  const history = page.getByRole("navigation", { name: "Problem Report sections" });
  await history.getByRole("button", { name: /^History/ }).click();
  await expect(page.locator(".prTimeline")).toContainText("Revised in the dialog.");
});
