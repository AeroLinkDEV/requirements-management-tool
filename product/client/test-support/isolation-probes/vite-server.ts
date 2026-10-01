import { mkdtemp, writeFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { createServer } from 'vite'
import { renderedTest } from '../../tests/isolated-client-test'

type HmrServer = { origin: string; update: () => Promise<void> }

export const hmrTest = renderedTest.extend<{ hmrServer: HmrServer }>({
  hmrServer: async ({ browserName: _browserName }, provide) => {
    const root = await mkdtemp(join(tmpdir(), 'aerolink-isolation-hmr-'))
    // Validate the owned recursive-cleanup target before any setup or finalization.
    if (dirname(resolve(root)) !== resolve(tmpdir()) || !root.includes('aerolink-isolation-hmr-')) throw new Error('Unexpected disposable HMR directory.')
    const module = join(root, 'fixture.js')
    let server: Awaited<ReturnType<typeof createServer>> | undefined
    try {
      await writeFile(join(root, 'index.html'), '<!doctype html><title>Disposable Vite HMR proof</title><output>loading</output><script type="module" src="/fixture.js"></script>')
      await writeFile(module, 'export const value = "before"; document.querySelector("output").textContent = value; if (import.meta.hot) import.meta.hot.accept(next => { document.querySelector("output").textContent = next.value; });')
      server = await createServer({ root, configFile: false, server: { host: '127.0.0.1', port: 0 }, clearScreen: false, logLevel: 'error' })
      await server.listen()
      const origin = server.resolvedUrls!.local[0].replace(/\/$/, '')
      await provide({ origin, update: () => writeFile(module, 'export const value = "after"; document.querySelector("output").textContent = value; if (import.meta.hot) import.meta.hot.accept(next => { document.querySelector("output").textContent = next.value; });') })
    } finally {
      await server?.close()
      // This directory is created and owned by this fixture; never remove an arbitrary path.
      await rm(root, { recursive: true, force: true })
    }
  },
  baseURL: async ({ hmrServer }, provide) => { await provide(hmrServer.origin) },
})
