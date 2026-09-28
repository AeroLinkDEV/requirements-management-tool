import { removeBrowserStorage } from './browser-storage.mjs'

export default class BrowserStorageReporter {
  constructor({ runId = process.env.AEROLINK_E2E_RUN_ID } = {}) { this.runId = runId }
  // CLI runs finish task/web-server teardown before onEnd. onExit errors are logged
  // but do not fail Playwright; onEnd can return the supported failed run status.
  async onEnd(result) {
    try {
      removeBrowserStorage(this.runId)
    } catch (error) {
      console.error(`Browser storage cleanup failed (run ${this.runId}, tests ${result.status}):`, error)
      return { status: 'failed' }
    }
  }
}
