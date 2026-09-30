import { removeBrowserStorage } from './browser-storage.mjs'
import { setTimeout as delay } from 'node:timers/promises'

export default class BrowserStorageReporter {
  constructor({ runId = process.env.AEROLINK_E2E_RUN_ID } = {}) { this.runId = runId }
  // CLI runs finish task/web-server teardown before onEnd. onExit errors are logged
  // but do not fail Playwright; onEnd can return the supported failed run status.
  async onEnd(result) {
    const deadline = performance.now() + 5_000
    let retried = false
    for (;;) {
      try {
        // Each attempt rechecks the complete ownership and link boundary.
        removeBrowserStorage(this.runId)
        return
      } catch (error) {
        if (!['EPERM', 'EBUSY', 'ENOTEMPTY'].includes(error.code) || performance.now() >= deadline) {
          console.error(`Browser storage cleanup failed (run ${this.runId}, tests ${result.status}):`, error)
          return { status: 'failed' }
        }
        if (!retried) console.warn(`Waiting for owned browser storage handle release (run ${this.runId}, ${error.code}).`)
        retried = true
        await delay(Math.min(100, Math.max(0, deadline - performance.now())))
      }
    }
  }
}
