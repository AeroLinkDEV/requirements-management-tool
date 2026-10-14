import { createHash } from 'node:crypto'
import { cpSync, lstatSync, mkdirSync, readFileSync, readdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { browserWindowAccounting, captureEvidence } from './browser-accounting.ts'

export const CAPTURE_SCHEMA = 'aerolink.fms-browser-paired-capture.v1'
export const CAPTURE_GUARDIAN = '446d724e0c3d1b5e00af7e873b98f72a3025ac3d442bdf82cde3b0b48273017e'
const configurations = ['view-off-svs-off', 'view-on-svs-off', 'view-off-svs-on', 'view-on-svs-on']
const firstRow = [0, 1, 7, 2, 6, 3, 5, 4]
const firstCycle = ['A', 'B', 'B', 'A', 'A', 'B', 'B', 'A'] as const
const canonical = (value: any): any => Array.isArray(value) ? value.map(canonical) : value && typeof value === 'object'
  ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value
export const digest = (value: unknown) => createHash('sha256').update(JSON.stringify(canonical(value)) ?? 'undefined').digest('hex')
export const fileDigest = (path: string) => createHash('sha256').update(readFileSync(path)).digest('hex')

/** The production allocation, not a randomization or an inference procedure. */
export function browserCapturePlan() {
  const slots = [0, 1].flatMap(cycle => Array.from({ length: 8 }, (_, row) => firstRow.map((offset, position) => {
    const cell = (row + offset) % 8
    const arm = cycle ? firstCycle[row] === 'A' ? 'B' : 'A' : firstCycle[row]
    return { slot: cycle * 64 + row * 8 + position, cycle, row, position, cell, arm,
      configuration: configurations[Math.floor(cell / 2)], rate: cell % 2 ? 64 : 1 }
  })).flat())
  return { schema: CAPTURE_SCHEMA, scenario: '87n-offshore-sar', inference: 'NOT_CALIBRATED', slots }
}
export type CaptureSlot = ReturnType<typeof browserCapturePlan>['slots'][number]
export type PayloadEntry = { path: string; kind: 'directory' | 'file'; bytes: number; sha256: string | null }

/** Exact cold payload, including empty evidence directories and DB sidecars. Never follows links. */
export function coldPayload(root: string): PayloadEntry[] {
  for (let path = resolve(root);;) {
    if (lstatSync(path).isSymbolicLink()) throw new Error('cold input refuses link ancestors')
    const parent = dirname(path)
    if (parent === path) break
    path = parent
  }
  const entries: PayloadEntry[] = []
  const walk = (relative: string) => {
    const path = relative ? join(root, relative) : root
    const stat = lstatSync(path)
    if (stat.isSymbolicLink() || !(stat.isDirectory() || stat.isFile())) throw new Error('cold input refuses links and non-files')
    if (relative) entries.push({ path: relative, kind: stat.isDirectory() ? 'directory' : 'file', bytes: stat.isFile() ? stat.size : 0,
      sha256: stat.isFile() ? fileDigest(path) : null })
    if (stat.isDirectory()) for (const child of readdirSync(path).sort()) {
      if (!relative && child === '.owner') continue
      if (!relative && child !== 'evidence' && !/^aerolink\.db(?:-(?:wal|shm|journal))?$/.test(child)) throw new Error('unexpected cold input payload')
      walk(relative ? `${relative}/${child}` : child)
    }
  }
  walk('')
  if (!entries.some(entry => entry.path === 'aerolink.db' && entry.kind === 'file') || !entries.some(entry => entry.path === 'evidence' && entry.kind === 'directory'))
    throw new Error('cold input needs a database and evidence directory')
  return entries
}

/** Copies only sealed payload bytes, never the source's ownership marker. Caller owns destination lifecycle. */
export function copyColdPayload(source: string, destination: string, expected: readonly PayloadEntry[]) {
  if (digest(coldPayload(source)) !== digest(expected)) throw new Error('cold source bytes changed')
  for (const entry of expected) {
    const target = join(destination, entry.path)
    if (entry.kind === 'directory') mkdirSync(target, { recursive: true })
    else cpSync(join(source, entry.path), target, { errorOnExist: true, force: false })
  }
  if (digest(coldPayload(destination)) !== digest(expected)) throw new Error('cold run copy bytes disagree')
}

/** Evidence supplied by the independently held outer owner, after actual producer exit and writer closure.
 * Parsing a receipt binds its assertions; it does not independently reproduce native containment. */
export function requireClosure(receipt: any) {
  if (receipt?.guardianSha256 !== CAPTURE_GUARDIAN || receipt?.status !== 'EXITED' || receipt?.exitCode !== 0 ||
      receipt?.jobCount !== 0 || receipt?.handlesClosed !== true || receipt?.forced !== false ||
      !/^\d+$/.test(receipt?.producerBirth100ns ?? '') || typeof receipt?.producerImage !== 'string' || !receipt.producerImage ||
      typeof receipt?.producerArgv !== 'string' || !receipt.producerArgv || !Number.isFinite(Date.parse(receipt?.closedAt)))
    throw new Error('native writer closure is missing, incomplete or forced')
}

/** Closed observations, never calibration or a budget judge. Caller retains every failed or malformed row. */
export function captureDisposition(plan: ReturnType<typeof browserCapturePlan>, session: any, records: readonly any[], end: any) {
  const reasons: string[] = []
  const expected = browserCapturePlan()
  const equal = (a: unknown, b: unknown) => digest(a) === digest(b)
  if (!equal(plan, expected)) reasons.push('manifest differs from the declared 128-slot plan')
  if (!session?.id || !equal(session.plan, plan) || end?.id !== session.id || !equal(end?.plan, plan)) reasons.push('session or manifest lineage is missing or changed')
  if (end?.stopped !== null) reasons.push('capture did not finish its declared plan normally')
  const hash = (value: unknown, length: number) => typeof value === 'string' && new RegExp(`^[0-9a-f]{${length}}$`).test(value)
  const cohort = session?.cohort
  if (!Number.isFinite(Date.parse(session?.startedAt)) || !Number.isFinite(Date.parse(end?.endedAt)) || Date.parse(end.endedAt) < Date.parse(session.startedAt) ||
      !Number.isFinite(session?.monotonicStart) || session?.deadline - session?.monotonicStart !== 14_400_000 || !Number.isFinite(end?.monotonicEnd) || end.monotonicEnd > session.deadline)
    reasons.push('whole-session clock boundary is missing or exceeded')
  if (!equal(cohort, end?.cohort) || !cohort?.runtime?.node || !cohort.runtime.v8 || !cohort.runtime.playwright ||
      !cohort.runtime.os?.platform || !cohort.runtime.os?.release || !cohort.runtime.os?.arch || !cohort.runtime.cpu?.model || !(cohort.runtime.cpu?.logicalCores > 0) || !(cohort.runtime.memoryGiB > 0) ||
      !hash(cohort?.harness?.commit, 40) || !hash(cohort?.harness?.apiTree, 40) || !hash(cohort?.harness?.apiAssemblySha256, 64) || cohort?.harness?.dirtyEntries !== 0 ||
      cohort?.protocol?.browser?.capture?.schema !== CAPTURE_SCHEMA || cohort?.protocol?.browser?.capture?.inference !== 'DISABLED') reasons.push('session cohort is missing or changed')
  if (!equal(session?.arms?.A, session?.arms?.B) || !session?.arms?.A || !equal(session.arms, end?.arms)) reasons.push('arms are not one unchanged same-source build')
  if (!['commit', 'srcTree', 'lockBlob', 'viteBlob'].every(key => hash(session?.arms?.A?.[key], 40)) || !hash(session?.arms?.A?.distSha256, 64) || !(session?.arms?.A?.distFiles > 0)) reasons.push('arm build identity is incomplete')
  if (!['payloadSha256', 'seedSha256', 'closureSha256'].every(key => hash(session?.cold?.[key], 64)) || !equal(session.cold, end?.cold)) reasons.push('cold input provenance is missing or changed')
  try {
    requireClosure(end?.closure)
    if (end.closure.sessionId !== session?.id || end.closure.sourceCommit !== cohort?.harness?.commit || Date.parse(end.closure.closedAt) < Date.parse(end?.endedAt)) throw new Error('wrong closure')
  } catch { reasons.push('whole-session native closure is unproven') }
  if (records.length !== 128) reasons.push('capture does not contain exactly 128 records')
  for (let i = 0; i < records.length; i++) {
    const record = records[i], slot = plan.slots[i]
    if (!slot || !equal(record.lineage, { session: session?.id, manifest: digest(plan), ...slot })) reasons.push(`slot ${i} has missing, duplicate or wrong-order lineage`)
    if (record.status !== 'passed' || !record.host?.quietBefore?.quiet) reasons.push(`slot ${i} failed or was not quiet-admitted`)
    const quiet = record.host?.quietBefore
    if (!Array.isArray(quiet?.samples) || !quiet.samples.length || !quiet.samples.every((sample: any) => Number.isFinite(sample.busyPercent) && sample.busyPercent >= 0 && sample.busyPercent <= 100 && Number.isFinite(Date.parse(sample.utc)) && Number.isFinite(sample.monotonicMs)) ||
      quiet.samples.at(-1)?.busyPercent > cohort?.protocol?.quietHost?.maxBusyPercent || !(quiet.waitedSeconds >= cohort?.protocol?.quietHost?.sampleSeconds) ||
      !Number.isFinite(record.host?.busyPercentDuringRun)) reasons.push(`slot ${i} host admission/load evidence is incomplete`)
    if (!Number.isFinite(Date.parse(record.startedAt)) || Date.parse(record.startedAt) < Date.parse(session?.startedAt) || Date.parse(record.startedAt) > Date.parse(end?.endedAt)) reasons.push(`slot ${i} is outside the session span`)
    if (!equal(record.cold, session?.cold) || record.copySha256 !== session?.cold?.payloadSha256 || !Array.isArray(record.copiedInputs) || digest(record.copiedInputs) !== record.copySha256) reasons.push(`slot ${i} cold copy disagrees`)
    if (!equal(record.cohort, session?.cohort) || !equal(record.armIdentity, session?.arms?.[slot?.arm]) || !equal(record.servedAtEnd, record.armIdentity)) reasons.push(`slot ${i} build/cohort changed`)
    try {
      const raw = record.result?.rawObservation
      const accounting = browserWindowAccounting({ ...raw, rate: slot!.rate })
      const evidence = captureEvidence(raw, accounting)
      if (!equal(record.result.accounting, accounting) || evidence.boundaryStatus !== 'OBSERVED' || evidence.eventTimingStatus !== 'OBSERVED') throw new Error('incomplete')
      const target = slot?.rate === 1 ? 240 : 9600
      if (record.result.window?.targetFrames !== target || accounting.frames.completed < target || accounting.frames.discarded !== 0 || accounting.frames.unfinishedAttempts !== 0 ||
        record.result.window?.stalled !== false || record.result.scenarioFinishedInWindow !== false || record.result.refusal !== null ||
        !Number.isSafeInteger(record.result.window.steps) || record.result.window.steps < 1 || record.result.interaction?.presses !== record.result.window.steps * 2 ||
        raw.capture.inputs.length !== record.result.interaction.presses) throw new Error('fixed workload incomplete')
      if (record.result.scenario !== plan.scenario || record.result.rate !== slot?.rate || record.result.configuration !== slot?.configuration ||
        !record.result.browser?.version || !record.result.browser.renderer || !equal(record.result.browser.viewport, cohort?.protocol?.browser?.viewport) || typeof record.result.browser.headless !== 'boolean') throw new Error('runtime')
      if (i && !equal(records[0].result?.browser, record.result.browser)) throw new Error('runtime changed')
    } catch { reasons.push(`slot ${i} boundary, runtime or interaction evidence is incomplete`) }
  }
  return { schema: CAPTURE_SCHEMA, status: reasons.length ? 'CAPTURE_PARTIAL' : 'CAPTURE_COMPLETE',
    qualification: reasons.length ? 'NOT_QUALIFIED' : 'NOT_CALIBRATED', inference: 'DISABLED', reasons }
}
