import { expect, renderedTest as test } from "./isolated-client-test"

test("the rendered fixture refuses API request contexts even when the failure is swallowed", async ({ page, context }) => {
  expect(() => page.request).toThrow(/must not use an API request context/)

  await expect(async () => {
    await page.context().request.get("/api/probe")
  }).rejects.toThrow(/must not use an API request context/)

  await expect(async () => {
    await context.request.post("/api/probe")
  }).rejects.toThrow(/must not use an API request context/)

  let swallowed: unknown
  try {
    await page.context().request.fetch("/api/probe")
  } catch (error) {
    swallowed = error
  }
  expect(String(swallowed)).toContain("A rendered fixture must not use an API request context.")
})
