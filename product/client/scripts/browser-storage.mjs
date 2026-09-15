import { createHash } from 'node:crypto'
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'

// Never accept an ambient Evidence__Root as disposable storage. A run's opaque identity
// determines a bounded temporary directory, including across API restarts.
export function browserStoragePath(runId) {
  if (typeof runId !== 'string' || !runId) throw new Error('Browser storage requires a run identity.')
  return join(tmpdir(), `aerolink-browser-${createHash('sha256').update(runId).digest('hex')}`)
}

function assertNoLinks(path) {
  if (!existsSync(path)) return
  if (lstatSync(path).isSymbolicLink()) throw new Error(`Browser storage refuses a link: ${path}`)
}

export function createBrowserStorage(runId) {
  const root = browserStoragePath(runId)
  assertNoLinks(root)
  const existed = existsSync(root)
  const marker = join(root, '.owner')
  if (existed && !existsSync(marker)) throw new Error('Existing browser storage has no owner marker.')
  mkdirSync(root, { recursive: true })
  assertNoLinks(marker)
  if (!existsSync(marker)) writeFileSync(marker, runId, { flag: 'wx' })
  if (readFileSync(marker, 'utf8') !== runId) throw new Error('Browser storage ownership mismatch.')
  const evidence = join(root, 'evidence')
  assertNoLinks(evidence)
  mkdirSync(evidence, { recursive: true })
  return { root, evidence, database: join(root, 'aerolink.db').replaceAll('\\', '/') }
}

export function removeBrowserStorage(runId) {
  const root = browserStoragePath(runId)
  if (!existsSync(root)) return
  if (dirname(resolve(root)) !== resolve(tmpdir())) throw new Error('Browser cleanup escaped temp.')
  assertNoLinks(root)
  const marker = join(root, '.owner')
  assertNoLinks(marker)
  if (!existsSync(marker) || readFileSync(marker, 'utf8') !== runId) throw new Error('Browser cleanup requires its owner marker.')
  function checkTree(path) {
    assertNoLinks(path)
    for (const entry of readdirSync(path, { withFileTypes: true })) {
      const child = join(path, entry.name)
      assertNoLinks(child)
      if (entry.isDirectory()) checkTree(child)
    }
  }
  checkTree(root)
  rmSync(root, { recursive: true, force: false, maxRetries: 3, retryDelay: 100 })
}
