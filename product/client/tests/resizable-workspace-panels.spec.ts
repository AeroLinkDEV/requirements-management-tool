import { expect, test } from '@playwright/test'
import { login } from './auth'

test('Command Center presents a responsive, aligned work summary with Problem Reports', async ({ page }) => {
  await login(page)
  await expect(page.getByRole('heading', { name: 'Command Center' })).toBeVisible()
  const panels = page.locator('.dashboardTriptych > .dashboardAreaCard')
  // System change, Software change, Verification, and the full-width Problem Reports row (#1113).
  await expect(panels).toHaveCount(4)
  await expect(page.locator('.dashboardTriptych').getByRole('separator')).toHaveCount(0)
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBeTruthy()

  await page.setViewportSize({width: 680, height: 900})
  const boxes = await panels.evaluateAll(items=>items.map(item=>item.getBoundingClientRect()))
  expect(boxes[1].top).toBeGreaterThan(boxes[0].bottom)
  expect(boxes[2].top).toBeGreaterThan(boxes[1].bottom)
  expect(boxes[3].top).toBeGreaterThan(boxes[2].bottom)
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
        <section><button onclick="this.textContent='Selected'">Bottom action</button></section>
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
  await expect.poll(() => layout.evaluate(element => {
    const panels = element.querySelectorAll(':scope > .resizableWorkspacePanel')
    const handle = element.querySelector('.workspaceSplitter')!.getBoundingClientRect()
    return handle.height >= 24 && handle.top >= panels[0].getBoundingClientRect().bottom - 0.5 &&
      handle.bottom <= panels[1].getBoundingClientRect().top + 0.5
  })).toBeTruthy()
  await layout.getByRole('button', { name: 'Bottom action' }).click()
  await expect(layout.getByRole('button', { name: 'Selected' })).toBeVisible()
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

test('Shared splitters stay between panels while inspector controls, dragging and saved sizes remain usable', async ({ page }) => {
  await page.setViewportSize({ width: 1400, height: 900 })
  await page.goto('/')
  const mount = () => page.evaluate(() => {
    document.body.innerHTML = `<div data-resizable-layout="horizontal" data-resizable-key="gutter-regression"
      class="reqLayout inspecting" style="width:90%;max-width:1000px;height:400px;min-height:0;margin:20px;padding:12px;border:3px solid">
      <section>Rail</section><section>Results</section>
      <aside><button role="tab">Discussion</button><p>Inspector content</p></aside></div>`
    const tab = document.querySelector<HTMLButtonElement>('[role="tab"]')!
    tab.addEventListener('click', () => { tab.setAttribute('aria-selected', 'true') })
  })
  await mount()
  const layout = page.locator('[data-resizable-key="gutter-regression"]')
  const panels = layout.locator(':scope > .resizableWorkspacePanel')
  const handles = layout.getByRole('separator')
  const assertGutters = async () => {
    await expect.poll(() => layout.evaluate(element => {
      const children = [...element.children].filter(child => !child.classList.contains('workspaceSplitter'))
      return [...element.querySelectorAll<HTMLElement>(':scope > .workspaceSplitter')].every(handle => {
        const index = Number(handle.dataset.boundary)
        const left = children[index].getBoundingClientRect()
        const right = children[index + 1].getBoundingClientRect()
        const target = handle.getBoundingClientRect()
        return target.width >= 24 && target.left >= left.right - 0.5 && target.right <= right.left + 0.5
      })
    })).toBeTruthy()
  }
  await expect(handles).toHaveCount(2)
  await assertGutters()
  await layout.getByRole('tab', { name: 'Discussion' }).click()
  await expect(layout.getByRole('tab')).toHaveAttribute('aria-selected', 'true')

  const middleBefore = (await panels.nth(1).boundingBox())!.width
  await handles.nth(1).focus()
  await handles.nth(1).press('ArrowRight')
  await expect.poll(async () => (await panels.nth(1).boundingBox())!.width).toBeGreaterThan(middleBefore)
  await assertGutters()

  // React can replace the frame's className without changing its number of panels.
  await layout.evaluate(element => {
    element.className = 'reqLayout inspecting'
  })
  await expect(layout).toHaveClass(/resizableWorkspace/)
  await assertGutters()
  // React can also replace every panel while keeping the existing separators.
  await panels.evaluateAll(elements => elements.forEach(element => element.replaceWith(element.cloneNode(true))))
  const replacedWidth = (await panels.nth(1).boundingBox())!.width
  await handles.nth(1).press('ArrowRight')
  await expect.poll(async () => (await panels.nth(1).boundingBox())!.width).toBeGreaterThan(replacedWidth)
  await assertGutters()
  const middleBeforeDrag = (await panels.nth(1).boundingBox())!.width
  const handle = (await handles.nth(1).boundingBox())!
  await page.mouse.move(handle.x + handle.width / 2, handle.y + handle.height / 2)
  await page.mouse.down()
  await page.mouse.move(handle.x + handle.width / 2 + 48, handle.y + handle.height / 2, { steps: 5 })
  await page.mouse.up()
  const savedWidth = (await panels.nth(1).boundingBox())!.width
  expect(savedWidth).toBeGreaterThan(middleBeforeDrag + 40)
  await assertGutters()

  await page.reload()
  await mount()
  await expect(handles).toHaveCount(2)
  await expect.poll(async () => Math.abs((await panels.nth(1).boundingBox())!.width - savedWidth)).toBeLessThan(1)
  await page.setViewportSize({ width: 1000, height: 900 })
  await assertGutters()
  await layout.getByRole('tab', { name: 'Discussion' }).click()
  await expect(layout.getByRole('tab')).toHaveAttribute('aria-selected', 'true')
  await page.setViewportSize({ width: 680, height: 900 })
  await expect(handles.first()).toBeHidden()
  await expect.poll(async () => {
    const first = (await panels.nth(0).boundingBox())!
    const second = (await panels.nth(1).boundingBox())!
    return second.y >= first.y + first.height - 0.5
  }).toBeTruthy()
})
