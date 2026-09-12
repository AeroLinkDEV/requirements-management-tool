import { expect, test } from 'C:/Sean Project/RMT-1022-astra-implementation/product/client/node_modules/@playwright/test/index.mjs'
import { apiBase, apiLogin, login, openNavigationGroup, selectProgram, showcaseSeed, surfacePainted } from 'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests/auth.ts'
import 'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests/digital-thread-page.spec.ts'
import 'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests/application-smoke.spec.ts'
import 'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests/showcase-usability.spec.ts'
test('full application quiet hover selection and manual exploration in all three views', async ({ page, request }, info) => {
  test.setTimeout(180000)
  await page.setViewportSize({ width: 1440, height: 1000 })
  await apiLogin(request); await showcaseSeed(request)
  await login(page, 'admin', { openProject: false })
  await selectProgram(page, 'Flight Management System Live Program')
  await openNavigationGroup(page, 'RELEASE')
  await page.getByRole('link', { name: 'Digital Thread', exact: true }).click()
  await surfacePainted(page)
  const root = new URL(page.url()).pathname.replace(/\/traceability.*$/, '')
  const match = /\/projects\/([^/]+)\/releases\/([^/]+)/.exec(root)!
  const [projectId, releaseId] = match.slice(1)
  const network = await (await request.get(`${apiBase}/api/change-requests/network?projectId=${projectId}&releaseId=${releaseId}`)).json()
  const change = network.nodes.find((n: { kind: string }) => n.kind !== 'ProblemReport')
  const context = await (await request.get(`${apiBase}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)).json()
  const list = await (await request.get(`${apiBase}/api/traceability?projectId=${projectId}&baselineId=${context.effectiveBaselineId}&page=1&pageSize=1`)).json()
  const artifact = (Array.isArray(list) ? list : list.items)[0]
  const records = []
  for (const [view, path] of [['network', `${root}/traceability`], ['inside', `${root}/traceability/change-requests/${change.id}?view=inside`], ['artifact', `${root}/traceability/${artifact.revisionId}`]]) {
    await page.goto(path); await expect(page.locator('.dtCanvas')).toBeVisible(); await surfacePainted(page)
    await page.locator('.dtCanvas').focus(); await page.keyboard.press('Escape')
    await expect(page.locator('.dtCanvasNode[aria-pressed=true]')).toHaveCount(0)
    await expect(page.locator('.dtnPanel, .dticPanel, .dtaPanel')).toHaveCount(0)
    await page.waitForTimeout(900)
    const shoot = async (state: string) => page.screenshot({ path: info.outputPath(`${view}-${state}.png`) })
    const camera = () => page.locator('.dtCanvasScene').evaluate(el => getComputedStyle(el).transform)
    await shoot('quiet')
    const target = await page.locator('.dtCanvasNode:not(.is-offscreen)').evaluateAll(elements => elements.find(el => {
      const r = el.getBoundingClientRect(); return document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2)?.closest('[data-node-id]') === el
    })?.getAttribute('data-node-id'))
    expect(target).toBeTruthy()
    const card = page.locator(`[data-node-id="${target}"]`)
    const before = await camera()
    await card.hover(); await page.waitForTimeout(400)
    expect(await camera()).toBe(before)
    await expect(page.locator('.dtCanvasNode[aria-pressed=true]')).toHaveCount(0)
    await expect(page.locator('.dtnPanel, .dticPanel, .dtaPanel')).toHaveCount(0)
    await expect(page.locator('.dtCanvasEdge.is-traced').first()).toBeAttached()
    await shoot('true-unselected-hover')
    await card.click(); await expect(card).toHaveAttribute('aria-pressed', 'true'); await page.waitForTimeout(900)
    await shoot('selected')
    for (const dock of ['Bottom', 'Right', 'Auto']) {
      await page.locator('[class$=PanelTools]').getByRole('button', { name: dock, exact: true }).click()
      await page.waitForTimeout(900); await shoot(`selected-${dock}`)
    }
    const canvas = (await page.locator('.dtCanvas').boundingBox())!
    const priorPan = await camera()
    await page.mouse.move(canvas.x + canvas.width - 8, canvas.y + 65); await page.mouse.down()
    await page.mouse.move(canvas.x + canvas.width - 188, canvas.y + 65, { steps: 6 }); await page.mouse.up()
    await page.waitForTimeout(400)
    expect(await camera()).not.toBe(priorPan)
    await expect(card).toHaveAttribute('aria-pressed', 'true')
    await shoot('manually-explored')
    records.push({ view, path, selectedId: target, before, after: await camera() })
  }
  await info.attach('full-application-provenance', { body: JSON.stringify({ kind: 'full application with explicit disposable SQLite showcase seed; not HOME or original dataset acceptance', records }, null, 2), contentType: 'application/json' })
})

