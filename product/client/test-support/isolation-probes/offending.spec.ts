import { renderedTest as test } from "../../tests/isolated-client-test"

test("swallows denied API-context access", async ({ page }) => {
  try {
    void page.request
  } catch {
    // Deliberately swallowed: teardown must still fail the test.
  }

  try {
    await page.context().request.fetch("/api/probe")
  } catch {
    // Deliberately swallowed: teardown must still fail the test.
  }
})
