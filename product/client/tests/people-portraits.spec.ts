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
    expect(diskFiles.length).toBeGreaterThanOrEqual(203)
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
    const groups = [...byBytes.values()].filter(group => group.length > 1)
    expect(groups).toEqual([["manager.reviewer.png", "program.manager.png"]])
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
    const initials = await page.locator('.personInitials').count()
    expect(initials, "initials fallback must be gone from Team Work").toBe(0)

    // Every rendered avatar is a real image element inside the people strip.
    const avatarCount = await page.locator('.teamWorkPeopleStrip img.personAvatar').count()
    expect(avatarCount).toBeGreaterThanOrEqual(5)
    for (let index = 0; index < avatarCount; index++) {
      const box = await page.locator('.teamWorkPeopleStrip img.personAvatar').nth(index).boundingBox()
      expect(box).not.toBeNull()
      expect(box!.width).toBeGreaterThan(0)
    }
  })

  test("Personnel renders portraits for leadership, assurance and roster members", async ({ page }) => {
    await page.setViewportSize({ width: 1920, height: 1080 })
    await login(page, 'admin')
    await page.goto(new URL('/projects/fms-product-development/personnel', page.url()).toString(), { waitUntil: 'load' })
    await expect(page.getByRole('heading', { name: 'Personnel' })).toBeVisible()

    await expect(page.locator('.personInitials').first()).toHaveCount(0)
    const portraits = page.locator('img.personAvatar')
    const portraitCount = await portraits.count()
    expect(portraitCount).toBeGreaterThanOrEqual(10)
    for (let index = 0; index < portraitCount; index++) {
      const natural = await portraits.nth(index).evaluate((element: HTMLImageElement) => element.naturalWidth)
      expect(natural).toBeGreaterThan(0)
    }
  })
})
