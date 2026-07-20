import { expect, test } from '@playwright/test'
import { login } from './auth'

test('Command Center panels resize horizontally and persist', async ({ page }) => {
  await login(page)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()

  const layout = page.locator('.commandCenterPage > .grid')
  const panels = layout.locator(':scope > .resizableWorkspacePanel')
  const splitter = layout.getByRole('separator').first()

  await expect(splitter).toBeAttached()
  await expect(panels).toHaveCount(2)

  const before = await panels.first().boundingBox()
  const handle = await splitter.boundingBox()
  if (!before || !handle) throw new Error('Resizable Command Center layout was not measurable')

  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2)
  await page.mouse.down()
  await page.mouse.move(handle.x + handle.width / 2 + 120, handle.y + handle.height / 2)
  await page.mouse.up()

  const after = await panels.first().boundingBox()
  expect(after?.width ?? 0).toBeGreaterThan(before.width + 80)

  await page.reload()
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  const persisted = await page.locator('.commandCenterPage > .grid > .resizableWorkspacePanel').first().boundingBox()
  expect(persisted?.width ?? 0).toBeGreaterThan(before.width + 80)
})

test('Reusable layout supports up and down resizing with keyboard access', async ({ page }) => {
  await page.goto('/')
  await page.evaluate(() => {
    document.body.innerHTML = `
      <div data-resizable-layout="vertical" data-resizable-key="vertical-test" style="height:600px">
        <section>Top</section>
        <section>Bottom</section>
      </div>`
  })

  const layout = page.locator('[data-resizable-key="vertical-test"]')
  const splitter = layout.getByRole('separator')
  const top = layout.locator(':scope > .resizableWorkspacePanel').first()
  await expect(splitter).toHaveAttribute('aria-orientation', 'horizontal')

  const before = await top.boundingBox()
  await splitter.focus()
  await splitter.press('ArrowDown')
  const after = await top.boundingBox()

  expect(after?.height ?? 0).toBeGreaterThan(before?.height ?? 0)
})
