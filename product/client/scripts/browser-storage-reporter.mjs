import { removeBrowserStorage } from './browser-storage.mjs'

export default class BrowserStorageReporter {
  constructor({ runId = process.env.AEROLINK_E2E_RUN_ID } = {}) { this.runId = runId }
  // Normal Playwright runs finish web-server teardown before reporter exit.
  async onExit() { removeBrowserStorage(this.runId) }
}
