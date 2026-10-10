import { test } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { assertBrowserScoringAvailable } from './browser-accounting.ts'

// #1557: explicit unsupported-accounting refusal, including when this owner is selected by a different config.
// No authentication, timer instrumentation, scored window or metric artifact is admitted. The legacy interval
// producer remains in Git history under its actual protocol/source identity; it is not a paced-source measure.
const protocol = JSON.parse(readFileSync(new URL('./protocol.json', import.meta.url), 'utf8'))

test('browser frame cost', () => {
  assertBrowserScoringAvailable(protocol)
})
