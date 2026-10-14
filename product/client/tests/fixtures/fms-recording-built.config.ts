import { defineConfig, mergeConfig } from 'vite'
import { existsSync } from 'node:fs'
import { isAbsolute, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import client from '../../vite.config'

// A component fixture, with the actual production plugins/bench/worker. This is not the shipped distribution.
export default defineConfig(({ command }) => {
  const output = process.env.AEROLINK_FMS_RECORDING_DIST
  if (!output || !isAbsolute(output)) throw new Error('A fresh absolute component output directory is required')
  if (command === 'build' && existsSync(output)) throw new Error('Component output already exists; preserve it')
  const root = fileURLToPath(new URL('../../', import.meta.url))
  return mergeConfig(client, {
    root,
    build: { outDir: output, emptyOutDir: false, rollupOptions: { input: resolve(root, 'tests/fixtures/fms-cdu.html') } },
  })
})
