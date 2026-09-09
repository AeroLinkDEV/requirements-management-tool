import { expect, renderedTest as test } from "../../tests/isolated-client-test"

test("ordinary rendered control does not use an API request context", async ({ page, context }) => {
  expect(page).toBeTruthy()
  expect(context).toBeTruthy()
})
