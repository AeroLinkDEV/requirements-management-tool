import { test, expect } from '@playwright/test'
import { readdirSync } from 'node:fs'
import { resolve } from 'node:path'
import { createBrowserStorage } from '../scripts/browser-storage.mjs'

test('showcase-generated evidence stays inside the browser run storage', () => {
  const runId = process.env.AEROLINK_E2E_RUN_ID
  expect(runId).toBeTruthy()
  const storage = createBrowserStorage(runId!)
  if (process.env.Evidence__Root) expect(resolve(storage.evidence)).not.toBe(resolve(process.env.Evidence__Root))
  const files = readdirSync(storage.evidence, { recursive: true, withFileTypes: true }).filter(entry => entry.isFile())
  expect(files.some(entry => entry.name.endsWith('.docx'))).toBe(true)
  expect(files.some(entry => entry.name.endsWith('.pdf'))).toBe(true)
})
