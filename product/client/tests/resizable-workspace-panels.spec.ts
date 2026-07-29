import { expect, test } from '@playwright/test'
import { login } from './auth'

test('Command Center presents a responsive, aligned three-way work summary', async ({ page }) => {
  await login(page)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  const panels = page.locator('.dashboardTriptych > .dashboardAreaCard')
  await expect(panels).toHaveCount(3)
  await expect(page.locator('.dashboardTriptych').getByRole('separator')).toHaveCount(0)
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy()

  await page.setViewportSize({width: 680, height: 900})
  const boxes = await panels.evaluateAll(items=>items.map(item=>item.getBoundingClientRect()))
  expect(boxes[1].top).toBeGreaterThan(boxes[0].bottom)
  expect(boxes[2].top).toBeGreaterThan(boxes[1].bottom)
  const overflow = await page.evaluate(()=>[...document.querySelectorAll<HTMLElement>('body *')].filter(element=>{
    const box=element.getBoundingClientRect()
    return box.width>0&&box.right>innerWidth+1
  }).map(element=>({tag:element.tagName,className:element.className,right:element.getBoundingClientRect().right,width:element.getBoundingClientRect().width})).slice(0,8))
  expect(overflow).toEqual([])
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

test('Resizable layouts rebuild when a panel is added dynamically', async ({ page }) => {
  await page.goto('/')
  await page.evaluate(() => {
    document.body.innerHTML = `
      <div data-resizable-layout="horizontal" data-resizable-key="dynamic-test">
        <section>Left</section>
        <section>Center</section>
      </div>`
  })

  const layout = page.locator('[data-resizable-key="dynamic-test"]')
  await expect(layout.getByRole('separator')).toHaveCount(1)

  await page.evaluate(() => {
    const layout = document.querySelector('[data-resizable-key="dynamic-test"]')
    const panel = document.createElement('section')
    panel.textContent = 'Inspector'
    layout?.appendChild(panel)
  })

  await expect(layout.locator(':scope > .resizableWorkspacePanel')).toHaveCount(3)
  await expect(layout.getByRole('separator')).toHaveCount(2)
  await expect(layout).toHaveAttribute('data-resizable-panel-count', '3')
})
