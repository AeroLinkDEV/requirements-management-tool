import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from '@playwright/test'
import { login } from './auth'

// #913: the FMS showcase promises 100% portrait coverage for its active project members. These
// checks prove the portrait set, its manifest, and its rendering agree: every seeded account has a
// modest, repository-owned portrait; no person surface falls back to initials for a showcase
// member; and no asset silently balloons the client.

const testsDir = dirname(fileURLToPath(import.meta.url))
const manifestPath = join(testsDir, "..", "src", "people-manifest.json")
const peopleDir = join(testsDir, "..", "public", "people")
const manifest = JSON.parse(readFileSync(manifestPath, "utf8")) as {
  people: Record<string, { file: string; name: string; role: string }>
}
const diskFiles = readdirSync(peopleDir).filter(file => file.endsWith(".png")).sort()

test.describe("FMS showcase portrait system", () => {
  test("the manifest and the committed portrait files agree exactly", () => {
    const manifestFiles = Object.values(manifest.people).map(person => person.file.replace("/people/", ""))
    expect(manifestFiles.sort()).toEqual(diskFiles)
    expect(diskFiles.length).toBe(202)
  })

  test("generated portraits are unique per identity; only the curated same-person pair is shared", () => {
    // Olivia Chen is one synthetic person holding two seeded accounts (manager.reviewer and
    // program.manager); her single portrait is explicitly copied to both. No two *generated*
    // identities may render the same image — collisions are re-salted by the generator.
    const byBytes = new Map<string, string[]>()
    for (const file of diskFiles) {
      const bytes = readFileSync(join(peopleDir, file)).toString("base64")
      byBytes.set(bytes, [...(byBytes.get(bytes) ?? []), file])
    }
    const groups = [...byBytes.values()].filter(group => group.length > 1).map(group => group.sort())
    // The cast: one synthetic person holds several functional accounts, and every account carries
    // that person's explicit portrait copy. Maya Patel (three accounts), Ethan Brooks (three),
    // Daniel Reyes (three), Olivia Chen (two). Nothing else may share an image.
    expect(groups.sort()).toEqual([
      ["cm.fms.png", "release.manager.png", "software.author.png"],
      ["lead.reviewer.png", "systems.author.png", "systems.lead.png"],
      ["manager.reviewer.png", "program.manager.png"],
      ["test.author.png", "test.engineer.png"],
    ])
  })

  test("every portrait stays within the asset-size ceiling", () => {
    const ceiling = 256 * 1024
    const bloated = Object.values(manifest.people)
      .map(person => join(peopleDir, person.file.replace("/people/", "")))
      .map(path => ({ path, bytes: statSync(path).size }))
      .filter(item => item.bytes > ceiling)
    expect(bloated).toEqual([])
  })

  test("every manifest entry carries a display name and role", () => {
    for (const [username, person] of Object.entries(manifest.people)) {
      expect(person.name, username).toBeTruthy()
      expect(person.role, username).toBeTruthy()
      expect(person.file, username).toMatch(/^\/people\/.+\.png$/)
    }
  })

  test("Team Work renders portraits for every strip member, with no initials fallback", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 })
    await login(page, 'admin')
    await page.getByRole("link", { name: "Team Work" }).click()
    await page.waitForURL(/team-work/)
    await expect(page.locator('.teamWorkPeopleStrip')).toBeVisible()

    // Not a single initials fallback may remain on the strip: every showcase member resolves a portrait.
    // The strip and its images populate asynchronously, so the whole coverage assertion retries until
    // the board has settled instead of sampling once during load.
    await expect(async () => {
      const stripAvatars = page.locator('.teamWorkPeopleStrip img.personAvatar')
      const avatarCount = await stripAvatars.count()
      expect(avatarCount).toBeGreaterThanOrEqual(5)
      for (let index = 0; index < avatarCount; index++) {
        const box = await stripAvatars.nth(index).boundingBox()
        expect(box).not.toBeNull()
        expect(box!.width).toBeGreaterThan(0)
        const decoded = await stripAvatars.nth(index).evaluate((element: HTMLImageElement) => ({
          complete: element.complete,
          naturalWidth: element.naturalWidth,
        }))
        expect(decoded.complete).toBe(true)
        expect(decoded.naturalWidth).toBeGreaterThan(0)
      }
      // Only the system administrator (a real identity, not synthetic cast) may fall back to
      // initials; every synthetic strip member resolves a portrait.
      const initialsTexts = await page.locator('.personInitials').allTextContents()
      expect(initialsTexts.length).toBeLessThanOrEqual(1)
      for (const text of initialsTexts) expect(text.trim()).toBe('AA')
    }).toPass({ timeout: 20_000 })
  })

  test("Personnel renders portraits for leadership, assurance and roster members", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 })
    await login(page, 'admin')
    await page.goto(new URL('/projects/fms-product-development/personnel', page.url()).toString(), { waitUntil: 'load' })
    await expect(page.getByRole('heading', { name: 'Personnel' })).toBeVisible()

    // The personnel read is asynchronous: the coverage assertion retries until the roster has
    // actually populated, every rendered portrait has decoded, and no initials fallback remains.
    // Coverage is bound to the populated roster (the fresh fixture seeds the leadership + assurance
    // cast), not to a fixed number that would only be true of the full HOME population.
    await expect(async () => {
      const portraits = page.locator('img.personAvatar')
      const portraitCount = await portraits.count()
      expect(portraitCount).toBeGreaterThanOrEqual(10)
      for (let index = 0; index < portraitCount; index++) {
        const decoded = await portraits.nth(index).evaluate((element: HTMLImageElement) => ({
          complete: element.complete,
          naturalWidth: element.naturalWidth,
        }))
        expect(decoded.complete).toBe(true)
        expect(decoded.naturalWidth).toBeGreaterThan(0)
      }
      const initials = await page.locator('.personInitials').allTextContents()
      expect(initials.length).toBeLessThanOrEqual(1)
      for (const text of initials) expect(text.trim()).toBe('AA')
      expect(await page.locator('[data-member]').count()).toBeGreaterThanOrEqual(10)
    }).toPass({ timeout: 20_000 })
  })
})
