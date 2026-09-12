import { defineConfig } from 'C:/Sean Project/RMT-1022-astra-implementation/product/client/node_modules/@playwright/test/index.mjs'
import base from 'C:/Sean Project/RMT-1022-astra-implementation/product/client/playwright.config.ts'
export default defineConfig({ ...base,
  testDir: 'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912',
  testMatch: 'fullapp-visual.spec.ts',
  globalSetup: 'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests/global-setup.ts',
  reporter: [['list']],
  use: { ...base.use, video: 'on' },
  webServer: (base.webServer as object[]).map(server => ({ ...server, cwd: 'C:/Sean Project/RMT-1022-astra-implementation/product/client' })),
})

