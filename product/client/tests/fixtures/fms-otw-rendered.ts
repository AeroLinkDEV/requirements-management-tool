import { expect, type Page } from '../isolated-client-test'

// Shared by the out-the-window view's rendered specs (fms-out-the-window-rendered, -imagery-rendered, -models-rendered):
// one copy of how they open the bench, start the view, choose its options, wait for it to settle and measure it.

/** The view draws with WebGL. Headless Chromium has no GPU here, and only uses its software renderer when told to. */
export const SOFTWARE_WEBGL = { launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] } }

/** Opens the bench: the fixture makes the terrain (fms-cdu.tsx), so no server is involved. */
export const open = async (page: Page, terrain: 'hill' | 'off') => {
  // Cleared on the first load only, so a reload shows what the bench remembered.
  await page.addInitScript(() => {
    try { if (!window.sessionStorage.getItem('cleared')) { window.localStorage.clear(); window.sessionStorage.setItem('cleared', 'yes') } } catch { /* private mode */ }
  })
  await page.goto(terrain === 'off' ? '/tests/fixtures/fms-cdu.html?terrain=off' : '/tests/fixtures/fms-cdu.html')
  await expect(page.locator('.fmsCdu')).toBeVisible()
}

/** Starts the view and waits for it to report itself ready. */
export const show = async (page: Page) => {
  await page.getByRole('button', { name: 'Show the view' }).click()
  const view = page.locator('.fmsOtw')
  await expect(view, 'the 3D view starts').toHaveAttribute('data-status', 'ready', { timeout: 60_000 })
  return view
}

export const choose = (page: Page, group: string, name: string) => page.getByRole('radiogroup', { name: group }).getByText(name, { exact: true }).click()

// How much of the view is the fixture's imagery (its teal and violet checkerboard) or the relative colouring's danger red,
// counted from a screenshot of the view decoded in the page.
export const shares = async (page: Page) => {
  const png = await page.locator('.fmsOtw').screenshot()
  return page.evaluate(async b64 => {
    const image = new Image()
    image.src = `data:image/png;base64,${b64}`
    await image.decode()
    const canvas = Object.assign(document.createElement('canvas'), { width: image.width, height: image.height }), pen = canvas.getContext('2d')!
    pen.drawImage(image, 0, 0)
    const data = pen.getImageData(0, 0, canvas.width, canvas.height).data
    let imagery = 0, red = 0, dark = 0, n = 0
    for (let i = 0; i < data.length; i += 4 * 5) {
      const r = data[i], g = data[i + 1], b = data[i + 2]
      n++
      if ((g > r + 60 && b > r + 40) || (b > g + 60 && r > g + 20)) imagery++
      if (r + g + b < 60) dark++
      // The danger red blended over the ground: strongly red, green and blue under half of it (the absolute top band's
      // firebrick, lighter over the ground, is not).
      if (r > 160 && g < 0.45 * r && b < 0.4 * r) red++
    }
    return { imagery: imagery / n, red: red / n, dark: dark / n }
  }, png.toString('base64'))
}

// The share of the view that looks different from an earlier screenshot of it (a colour change of more than 40 in sum).
// The scene as drawn: not the view's own controls, which change as they are chosen, nor the credits, whose text changes
// with the ground and wraps differently with the window size.
export const sceneShot = (page: Page) => page.locator('.fmsOtwScene').screenshot({ mask: [page.locator('.fmsOtwCredits')] })

/** The share of the scene's pixels that changed. */
export const changedSince = async (page: Page, before: Buffer) => {
  const after = await sceneShot(page)
  return page.evaluate(async ([a, b]) => {
    const pixels = async (b64: string) => {
      const image = new Image()
      image.src = `data:image/png;base64,${b64}`
      await image.decode()
      const canvas = Object.assign(document.createElement('canvas'), { width: image.width, height: image.height }), pen = canvas.getContext('2d')!
      pen.drawImage(image, 0, 0)
      return pen.getImageData(0, 0, canvas.width, canvas.height).data
    }
    const [p, q] = [await pixels(a), await pixels(b)]
    let changed = 0, n = 0
    for (let i = 0; i < Math.min(p.length, q.length); i += 4 * 5) { n++; if (Math.abs(p[i] - q[i]) + Math.abs(p[i + 1] - q[i + 1]) + Math.abs(p[i + 2] - q[i + 2]) > 40) changed++ }
    return changed / n
  }, [before.toString('base64'), after.toString('base64')])
}

// The scene has drawn what was asked: every tile the globe needs has loaded and its frame count has stopped (a lull
// between tiles, on a software renderer, is not enough).
export const drawn = async (page: Page) => {
  const scene = page.locator('.fmsOtwScene')
  let previous = -1
  await expect.poll(async () => {
    const now = Number(await scene.getAttribute('data-frames') ?? 0), loaded = await scene.getAttribute('data-tiles-loaded') === 'true'
    const same = loaded && now === previous
    previous = now
    return same
  }, { intervals: [3000], timeout: 150_000 }).toBe(true)
}
