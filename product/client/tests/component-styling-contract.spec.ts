import { expect, test, type Locator, type Page } from "@playwright/test";
import { login, selectProgram } from "./auth";

/**
 * Shared components keep their own styling wherever a page puts them.
 *
 * Three times now a component has lost styling it depends on to a rule belonging to the page around
 * it, and each time it shipped:
 *
 * - `.richFileInput` lost its width to `.controlledEditor input { width: 100% }` and rendered 1160px
 *   wide, pushing the new-change-request page 106px past the viewport (recorded in vite.config.ts).
 * - `.richEditor` lost its font to an unscoped `.richEditor { font: … !important }` in another
 *   component's stylesheet, so every rich editor in the product rendered monospace (#1068).
 * - `.catTile` lost its layout to `.prIdentity span { display: block }`, so a two-digit category
 *   stretched across the identity card as a grey bar (#1082).
 *
 * A static rule cannot catch this. The dangerous shape — a page styling a bare descendant element —
 * appears 2,778 times in this client and is almost always correct; what makes an instance a defect is
 * whether it lands on an element a component owns, which is a fact about the rendered DOM. So the
 * contract is asserted where it is decided: in the browser, against the real page, on the property the
 * component depends on.
 *
 * It catches the failure however it is caused — specificity, `!important`, or stylesheet order — which
 * is the point. All three incidents had different causes and the same symptom.
 *
 * ## Adding a component
 *
 * An entry earns its place by having broken, not by being shared. Every component in this product is
 * rendered inside something else, so "it is shared" would grow this into a slow test that runs most of
 * the app to re-prove things nothing has ever got wrong. `.richFileInput` is not listed: its incident
 * predates this contract and vite.config.ts already records it. Add an entry when there is a new
 * incident.
 *
 * Add an entry to CONTRACTS with the incident in `because`, so a later reader can judge whether the
 * protection still applies. `styles` are the declarations the component sets on itself and cannot do
 * without — what would be a visible defect if lost, not everything it declares. `inherits` are the
 * properties it deliberately leaves to its surroundings; they must compute to the parent's value; a
 * difference means some rule outside the component reached in and set them.
 */

type Contract = {
  /** What it is, in the words a reviewer would use. */
  component: string;
  /** Why it is protected: the incident, so a later reader can judge whether it still applies. */
  because: string;
  selector: string;
  /** Computed values the component sets on itself. */
  styles?: Record<string, string>;
  /** Properties the component inherits on purpose, which must match its parent's computed value. */
  inherits?: string[];
};

const CONTRACTS: Contract[] = [
  {
    component: "Category tile",
    because: "#1082 — .prIdentity span { display: block } blockified it into a full-width bar",
    selector: ".prIdentity .catTile",
    styles: { display: "flex" },
  },
  {
    component: "Rich content editor",
    because: "#1068 — an unscoped .richEditor { font: … !important } made every editor monospace",
    selector: ".prModal .richEditor",
    styles: { display: "grid" },
    inherits: ["font-family", "font-size"],
  },
];

const computed = (element: Locator, property: string) =>
  element.evaluate((node, name) => {
    const own = getComputedStyle(node).getPropertyValue(name).trim();
    const parent = node.parentElement ? getComputedStyle(node.parentElement).getPropertyValue(name).trim() : "";
    return { own, parent };
  }, property);

const openProblemReportRecord = async (page: Page) => {
  await login(page, "admin", { openProject: false });
  await selectProgram(page, "Flight Management System Live Program");
  const root = new URL(page.url()).pathname.replace(/\/[^/]*$/, "");
  await page.goto(new URL(`${root}/problem-reports`, page.url()).toString(), { waitUntil: "load" });
  const firstRow = page.locator(".prList > button").first();
  await expect(firstRow).toBeVisible({ timeout: 30_000 });
  await firstRow.click();
  await expect(page.locator(".prDetail h2")).toBeVisible({ timeout: 30_000 });
};

test("shared components keep the styling they depend on wherever a page puts them", async ({ page }) => {
  test.setTimeout(240_000);
  await openProblemReportRecord(page);

  // The symptom of #1082, the code as a bar across the card, is problem-report-identity-card.spec.ts's.
  // This holds the mechanism, which is what any page can break.
  const tile = page.locator(".prIdentity .catTile").first();
  await expect(tile).toBeVisible({ timeout: 30_000 });

  // Positioning from outside is normal and allowed: .prIdentityCategory .catTile sets a margin. What is
  // not allowed is reaching past the component's root to its internals. Both hold on the same element,
  // so the contract below is not read as "pages may never mention a component".
  await expect(tile).toHaveCSS("margin-top", "5px");

  // The rich editor only exists while something is being authored, so open the create form for it.
  await page.getByRole("button", { name: "+ Record problem" }).click();
  const dialog = page.getByRole("dialog", { name: "Record a problem" });
  await expect(dialog.getByLabel("Title")).toBeVisible({ timeout: 30_000 });

  for (const contract of CONTRACTS) {
    const element = page.locator(contract.selector).first();
    await expect(element, `${contract.component} must be present to hold it to its contract`)
      .toBeVisible({ timeout: 30_000 });

    for (const [property, expected] of Object.entries(contract.styles ?? {})) {
      const { own } = await computed(element, property);
      expect(own, `${contract.component} lost ${property} to a rule outside it. ${contract.because}`).toBe(expected);
    }
    for (const property of contract.inherits ?? []) {
      const { own, parent } = await computed(element, property);
      expect(own, `${contract.component} had ${property} set from outside instead of inheriting it. ${contract.because}`)
        .toBe(parent);
    }
  }
});
