import { createHash } from 'node:crypto'
import { execFileSync, spawnSync } from 'node:child_process'
import { cpSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
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

export const W3_PAIR = { A: '7f2b53aec7f567089455c7c6b69f05f518a8e563', B: '5c7ec720845612271fb38a99880a341a6845f3ba' } as const
const W3_FILES = ['product/client/src/fmsCdu/benchPacing.ts', 'product/client/src/fmsCdu/FmsCduTestBench.tsx']
const w3Git = (root: string, ...args: string[]) => execFileSync('git', ['-C', root, ...args], { encoding:'utf8',maxBuffer:16*1024*1024 }).trim()
/** A separate paired plan. Calling browserCapturePlan() without W3 still returns the original128-slot W2 bytes. */
export function w3PairedPlan(manifest: any) {
  if (manifest?.schema !== 'aerolink.fms-w3-observation-overlay.v1' || !manifest.overlaySha256 || !manifest.harness?.commit ||
      !['A','B'].every(arm => manifest.arms?.[arm]?.original?.commit === W3_PAIR[arm as 'A'|'B'] && manifest.arms[arm].effectiveSourceSha256))
    throw new Error('W3 requires the exact paired original/effective observation manifest before output or children')
  const sessions=Array.from({length:16},(_,repeat)=>firstRow.map((offset,position)=>{
    const row=repeat%8,cycle=Math.floor(repeat/8),cell=(row+offset)%8,first=(row+position+cycle)%2?'B':'A'
    return {repeat,session:position,cell,configuration:configurations[Math.floor(cell/2)],rate:cell%2?64:1,orientation:first==='A'?'ABBA':'BAAB',
      slots:(first==='A'?['A','B','B','A']:['B','A','A','B']).map((arm,withinSession)=>({slot:repeat*32+position*4+withinSession,arm,position:withinSession}))}
  })).flat()
  const slots=sessions.flatMap(session=>session.slots.map(slot=>({...slot,repeat:session.repeat,session:session.session,cell:session.cell,configuration:session.configuration,rate:session.rate,slot:session.repeat*32+session.session*4+slot.position})))
  return {schema:'aerolink.fms-w3-paired-plan.v1',recipe:'aerolink.fms-perf-w3-prefix.v1',workload:'W3',inference:'DISABLED',qualification:'NOT_CALIBRATED',
    manifestSha256:digest(manifest),allocation:'16-row periodic Williams cell order; alternating first rate; ABBA/BAAB balanced across row/position and cycle. One complete four-run cell-session is ONE block/session unit, not four independent samples. Pilot must declare repetitions/N and serial/session treatment before qualification; this schedule is not inference.',sessions,slots}
}
function sourceFiles(root: string, relativePath = ''): {path:string;sha256:string}[] {
  const at = join(root,relativePath), out: {path:string;sha256:string}[] = []
  for(const name of readdirSync(at).sort()) {
    const next = relativePath ? `${relativePath}/${name}` : name, path=join(root,next), stat=lstatSync(path)
    if(stat.isSymbolicLink()) throw new Error('W3 source copies refuse links')
    if(stat.isDirectory()) {if(!['node_modules','dist','.git'].includes(name))out.push(...sourceFiles(root,next))}
    else if(stat.isFile())out.push({path:next,sha256:fileDigest(path)})
    else throw new Error('W3 source copy contains unsupported filesystem input')
  }
  return out
}
/** SOURCE-only preparation: archive EACH original arm, then apply the same pinned observation hunks. No build,
 * install, serving or current-model replacement. The head's distinct submission/journal source remains its own. */
export function materializeW3Arms(harness: string, destination: string) {
  if(existsSync(destination)) throw new Error('W3 needs a fresh owned source-copy destination')
  if(w3Git(harness,'status','--porcelain'))throw new Error('W3 overlay producer must be a clean committed harness')
  const patch=execFileSync('git',['-C',harness,'diff',W3_PAIR.A,'HEAD','--',W3_FILES[0]],{encoding:'utf8',maxBuffer:16*1024*1024})
  const ui=readFileSync(join(harness,W3_FILES[1]),'utf8')
  const observation=ui.match(/      observeFrame: ordinal => \(\{[\s\S]*?        \} : null \}\),\r?\n/)?.[0]
  const frozen=ui.match(/          try \{\r?\n            const done = presentation.frame\(\(\) => kernel.advance\(1, \{ flightFreeze: true \}\)\);[\s\S]*?\r?\n          \}/)?.[0]
  if(!patch.includes('PACING_FRAME') || !observation || !frozen)throw new Error('W3 observation delta is missing')
  const resolved=resolve(destination), fromHarness=relative(resolve(harness),resolved)
  if(!fromHarness || (!fromHarness.startsWith('..') && !/^[A-Za-z]:/.test(fromHarness)))throw new Error('W3 source copies must be outside the harness')
  for(let p=dirname(resolved);;p=dirname(p)){if(lstatSync(p).isSymbolicLink())throw new Error('W3 copy parent aliases another path');if(dirname(p)===p)break}
  // All static compatibility checks precede creation of any output.
  for(const commit of Object.values(W3_PAIR))if(w3Git(harness,'rev-parse',`${commit}:${W3_FILES[0]}`)!=='f56d3fd89a4e84868e3ea695a5f5de539a038789')throw new Error('W3 immediate-arm pacing source changed')
  mkdirSync(resolved);writeFileSync(join(resolved,'observation.patch'),patch,{flag:'wx'})
  const arms: Record<string,any>={}
  for(const [arm,commit] of Object.entries(W3_PAIR)) {
    const root=join(resolved,arm);mkdirSync(root)
    const archive=execFileSync('git',['-C',harness,'archive',commit,'product/client'],{maxBuffer:64*1024*1024})
    const extracted=spawnSync('tar',['-xf','-','-C',root],{input:archive,encoding:'utf8'})
    if(extracted.status!==0)throw new Error('W3 exact arm archive extraction failed')
    const before=sourceFiles(root), applied=spawnSync('git',['apply','--no-index','--whitespace=nowarn','-'],{cwd:root,input:patch,encoding:'utf8'})
    if(applied.status!==0)throw new Error(`W3 observation hunks do not apply to arm ${arm}; no fallback`)
    const caller=join(root,W3_FILES[1]),oldCaller=readFileSync(caller,'utf8'),expectedBlob=arm==='A'?'a54ffb5150025c16a0c0f97f1b28cdc75f62ac08':'f27efbd30f118e816b65be9d6eb80e52d3b0a96b'
    if(w3Git(harness,'rev-parse',`${commit}:${W3_FILES[1]}`)!==expectedBlob)throw new Error('W3 original caller source changed')
    const once=(text:string,from:string,to:string)=>{if(text.split(from).length!==2)throw new Error('W3 exact caller observation anchor changed');return text.replace(from,to)}
    let callerOverlay=once(oldCaller,'import { startKernelPacing } from "./benchPacing";','import { startKernelPacing, PACING_FROZEN } from "./benchPacing";')
    const originalAdvance=arm==='A'?'kernel.advance(1, { flightFreeze: true })':'kernel.advance(1)'
    callerOverlay=once(callerOverlay,`          try { presentation.frame(() => ${originalAdvance}); }`,frozen.replace('kernel.advance(1, { flightFreeze: true })',originalAdvance))
    callerOverlay=once(callerOverlay,'      frame: presentation.frame,\n','      frame: presentation.frame,\n'+observation)
    const originalPath=join(resolved,`${arm}.caller-original.tsx`);writeFileSync(originalPath,oldCaller,{flag:'wx'});writeFileSync(caller,callerOverlay)
    const diff=spawnSync('git',['diff','--no-index','--',originalPath,caller],{encoding:'utf8',maxBuffer:16*1024*1024})
    if(diff.status!==1)throw new Error('W3 exact caller observation patch missing')
    writeFileSync(join(resolved,`${arm}.caller.patch`),diff.stdout,{flag:'wx'})
    const after=sourceFiles(root), changed=after.filter((file,i)=>file.sha256!==before[i]?.sha256)
    if(changed.length!==2 || changed.some(file=>!W3_FILES.includes(file.path)))throw new Error('W3 overlay changes non-observation source paths')
    arms[arm]={root:realpathSync(root),original:{commit,tree:w3Git(harness,'rev-parse',`${commit}^{tree}`),srcTree:w3Git(harness,'rev-parse',`${commit}:product/client/src`),
      lockBlob:w3Git(harness,'rev-parse',`${commit}:product/client/package-lock.json`),files:W3_FILES.map(path=>({path,blob:w3Git(harness,'rev-parse',`${commit}:${path}`)}))},archiveSha256:createHash('sha256').update(archive).digest('hex'),callerPatchSha256:fileDigest(join(resolved,`${arm}.caller.patch`)),originalFiles:before,effectiveFiles:after,effectiveSourceSha256:digest(after),changed}
  }
  const manifest={schema:'aerolink.fms-w3-observation-overlay.v1',harness:{commit:w3Git(harness,'rev-parse','HEAD'),tree:w3Git(harness,'rev-parse','HEAD^{tree}')},
    overlaySha256:digest({bench:fileDigest(join(resolved,'observation.patch')),A:arms.A.callerPatchSha256,B:arms.B.callerPatchSha256}),patch:'observation.patch',arms,builds:null,qualification:'NOT_CALIBRATED',inference:'DISABLED'}
  w3PairedPlan(manifest);writeFileSync(join(resolved,'source-manifest.json'),JSON.stringify(manifest,null,2),{flag:'wx'});return manifest
}
/** Parses asserted build/closure provenance and checks actual bytes. This is not independent native proof. */
export function admitW3Manifest(manifest: any, requireBuild: boolean) {
  const plan=w3PairedPlan(manifest)
  const overlayRoot=dirname(manifest.arms.A.root)
  if(dirname(manifest.arms.B.root)!==overlayRoot || digest({bench:fileDigest(join(overlayRoot,'observation.patch')),A:fileDigest(join(overlayRoot,'A.caller.patch')),B:fileDigest(join(overlayRoot,'B.caller.patch'))})!==manifest.overlaySha256)throw new Error('W3 declared observation patches changed')
  for(const arm of ['A','B']) {
    const entry=manifest.arms[arm]
    if(lstatSync(entry.root).isSymbolicLink() || !sameFiles(sourceFiles(entry.root),entry.effectiveFiles) || digest(entry.effectiveFiles)!==entry.effectiveSourceSha256)
      throw new Error('W3 effective source bytes changed')
    if(requireBuild) {
      const build=manifest.builds?.[arm]
      if(!build || build.sourceSha256!==entry.effectiveSourceSha256 || build.harnessCommit!==manifest.harness.commit || !build.command ||
          !Array.isArray(build.dependencies) || !build.dependencies.length || digest(build.dependencies)!==build.dependenciesSha256 ||
          build.dependencies.some((file:any)=>fileDigest(join(entry.root,file.path))!==file.sha256) || !Array.isArray(build.dist) || !build.dist.length ||
          !sameFiles(sourceFiles(join(entry.root,'product/client/dist')),build.dist))throw new Error('W3 exact source/build/dependency provenance missing or changed')
      requireClosure(build.closure)
    }
  }
  return plan
}
/** Archives the already validated sealed W3 cold input under <out>/cold before any child, as the W2 capture does: payload,
 * seed and seed-writer closure, each re-observed against the sealed hashes. Returns the archived seed every slot reads. */
export function archiveW3ColdInput(input: string, dir: string, entries: readonly PayloadEntry[], cold: { payloadSha256: string; seedSha256: string; closureSha256: string }) {
  const archive=join(dir,'cold','payload');mkdirSync(archive,{recursive:true});copyColdPayload(join(input,'payload'),archive,entries)
  const seed=join(dir,'cold','seed.json'),closure=join(dir,'cold','closure.json')
  cpSync(join(input,'seed.json'),seed,{errorOnExist:true,force:false});cpSync(join(input,'closure.json'),closure,{errorOnExist:true,force:false})
  if(fileDigest(seed)!==cold.seedSha256 || fileDigest(closure)!==cold.closureSha256)throw new Error('W3 archived seed or writer closure bytes disagree with the sealed cold input')
  return {archive,seed}
}
/** The runs.jsonl row recorded for one child run (run-perf.ts run()). */
export function runRow(entry: Record<string, any>, _resultFile: string, _dir: string) {
  return entry
}
/** The W3 session record (w3-session.json; w3-end.json repeats it). */
export function w3SessionRecord(fields: { id: string; plan: unknown; sessionIndex: number; slots: unknown[]; manifest: unknown; manifestInput: string; dir: string;
  cold: unknown; harness: unknown; startedAt: string; monotonicStart: number; deadline: number }) {
  const { id, plan, sessionIndex, slots, manifest, cold, harness, startedAt, monotonicStart, deadline } = fields
  return {schema:'aerolink.fms-w3-paired-session.v1',id,plan,sessionIndex,slots,manifest,cold,harness,startedAt,monotonicStart,deadline,qualification:'NOT_CALIBRATED',inference:'DISABLED',closure:'UNPROVEN'}
}
/** The W3 session end record the executor writes to w3-end.json. The end re-admission outcome is recorded beside the
 * original stop reason and never replaces it or prevents the record; the caller decides what to throw after writing. */
export function w3EndRecord(session: Record<string, unknown>, stopped: string | null, manifest: any, observed: Record<string, unknown>) {
  let endAdmission: { status: 'ADMITTED' | 'REFUSED'; error: string | null }
  try { admitW3Manifest(manifest,true); endAdmission={status:'ADMITTED',error:null} }
  catch(error) { endAdmission={status:'REFUSED',error:error instanceof Error?error.message:String(error)} }
  return {...session,stopped,endAdmission,...observed,closure:'UNPROVEN'}
}
function sameFiles(a: unknown,b: unknown){return JSON.stringify(a)===JSON.stringify(b)}

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
    const closure = end.closure, owner = closure.wholeOwner
    const ownerStart = Date.parse(owner?.startedAt), exited = Date.parse(closure.exitedAt), closed = Date.parse(closure.closedAt)
    // Only compare this owner's monotonic readings with each other. The producer's performance.now() origin
    // is unrelated; its end stamp alone cannot account for the final file write and actual process exit.
    if (closure.sessionId !== session?.id || closure.sourceCommit !== cohort?.harness?.commit ||
        ![ownerStart, exited, closed, owner?.monotonicStartMs, owner?.measurementDeadlineMs, owner?.cleanupDeadlineMs, owner?.producerExitedMs, owner?.closedMs].every(Number.isFinite) ||
        owner.measurementDeadlineMs - owner.monotonicStartMs !== 14_400_000 || owner.cleanupDeadlineMs < owner.measurementDeadlineMs ||
        owner.cleanupDeadlineMs - owner.measurementDeadlineMs > 300_000 || owner.producerExitedMs < owner.monotonicStartMs ||
        owner.producerExitedMs > owner.measurementDeadlineMs || owner.closedMs < owner.producerExitedMs || owner.closedMs > owner.cleanupDeadlineMs ||
        ownerStart > Date.parse(session.startedAt) || exited < Date.parse(end.endedAt) || closed < exited ||
        exited - ownerStart > 14_400_000 || closed - ownerStart > owner.cleanupDeadlineMs - owner.monotonicStartMs) throw new Error('wrong or late closure')
  } catch { reasons.push('whole-session native closure is unproven') }
  if (records.length !== 128) reasons.push('capture does not contain exactly 128 records')
  let previousEnd = session?.monotonicStart
  for (let i = 0; i < records.length; i++) {
    const record = records[i], slot = plan.slots[i]
    if (!slot || !equal(record.lineage, { session: session?.id, manifest: digest(plan), ...slot })) reasons.push(`slot ${i} has missing, duplicate or wrong-order lineage`)
    if (record.status !== 'passed' || !record.host?.quietBefore?.quiet) reasons.push(`slot ${i} failed or was not quiet-admitted`)
    const quiet = record.host?.quietBefore
    if (!Array.isArray(quiet?.samples) || !quiet.samples.length || !quiet.samples.every((sample: any) => Number.isFinite(sample.busyPercent) && sample.busyPercent >= 0 && sample.busyPercent <= 100 && Number.isFinite(Date.parse(sample.utc)) && Number.isFinite(sample.monotonicMs)) ||
      quiet.samples.at(-1)?.busyPercent > cohort?.protocol?.quietHost?.maxBusyPercent || !(quiet.waitedSeconds >= cohort?.protocol?.quietHost?.sampleSeconds) ||
      !Number.isFinite(record.host?.busyPercentDuringRun)) reasons.push(`slot ${i} host admission/load evidence is incomplete`)
    if (!Number.isFinite(Date.parse(record.startedAt)) || Date.parse(record.startedAt) < Date.parse(session?.startedAt) || Date.parse(record.startedAt) > Date.parse(end?.endedAt)) reasons.push(`slot ${i} is outside the session span`)
    if (!Number.isFinite(record.monotonicStartedAt) || !Number.isFinite(record.monotonicEndedAt) || record.monotonicStartedAt < previousEnd ||
      record.monotonicEndedAt <= record.monotonicStartedAt || record.monotonicEndedAt > end?.monotonicEnd) reasons.push(`slot ${i} has missing or overlapping monotonic execution`)
    previousEnd = record.monotonicEndedAt
    if (!equal(record.cold, session?.cold) || record.copySha256 !== session?.cold?.payloadSha256 || !Array.isArray(record.copiedInputs) || digest(record.copiedInputs) !== record.copySha256) reasons.push(`slot ${i} cold copy disagrees`)
    if (!equal(record.cohort, session?.cohort) || !equal(record.armIdentity, session?.arms?.[slot?.arm]) || !equal(record.servedAtEnd, record.armIdentity)) reasons.push(`slot ${i} build/cohort changed`)
    try {
      const raw = record.result?.rawObservation
      const accounting = browserWindowAccounting({ ...raw, rate: slot!.rate })
      const evidence = captureEvidence(raw, accounting)
      if (!equal(record.result.accounting, accounting) || evidence.boundaryStatus !== 'OBSERVED' || evidence.eventTimingStatus !== 'OBSERVED') throw new Error('incomplete')
      if (record.monotonicEndedAt - record.monotonicStartedAt < (cohort.protocol.browser.warmupWallSeconds + accounting.wallSeconds) * 1000) throw new Error('insufficient fixed work interval')
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
