import { removeBrowserStorage } from './browser-storage.mjs'

export default class BrowserStorageReporter {
  constructor({ runId }) { this.runId = runId }
  // Normal Playwright runs finish web-server teardown before reporter exit.
  async onExit() { removeBrowserStorage(this.runId) }
}
