import { expect, logicTest as test } from './isolated-client-test'
import { createSession, type SessionSetup } from '../src/fmsCdu/kernel/session'
import { parseRecording, serializeRecording, SessionRecording, verifyRecording, type RecordingIdentity } from '../src/fmsCdu/kernel/recording'
import { CanonicalWriter, frameBytes, frameDigest, xxh64, writeValue } from './fixtures/fms-kernel-digest'
import * as product from '../src/fmsCdu/kernel/stateDigest'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import { ContentStore } from '../src/fmsCdu/kernel/contentStore'
import type { ActionSource, KernelAction } from '../src/fmsCdu/kernel/actions'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { scenarioStart } from '../src/fmsCdu/scenario'

const setup: SessionSetup = { composition: 'dual', utc0Ms: Date.UTC(2026, 8, 29, 15), profileId: 'cma9000-s300-heli-civil',
  variantId: '002/003/005/102/103/302/303/502/503', initialFreeze: true, startModes: ['WARM', 'WARM'], onGround: false,
  startState: null, startSource: { kind: 'ios', id: 'navdata.startState', surface: 'main' }, scenario: null }
// Controlled pure-logic identity input, NOT a claim about this host. Actual manifest/engine binding belongs to UI.
const identity: RecordingIdentity = { build: { schema: 'aerolink.fms-build.v1', kind: 'built', commit: 'literal-fixture', dirty: false,
  bundleHash: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
  assets: [{ path: 'literal-abc.js', sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad', bytes: 3 }] },
  engine: { userAgent: 'literal-engine', platform: 'literal-platform', fullVersionList: [{ brand: 'literal', version: '1.2.3.4' }], architecture: null, platformVersion: null }, qualified: true, reason: null }
const S_K_LABEL = 'S_k; empty snapshot slot (I2 required for restore)'

// Multi-frame recordings: the live session is driven one frame at a time through the production session factory and
// submission port, with inputs at chosen R_j; the export is requested in the last frame's open ACTION.
type Live = ReturnType<typeof createSession>
type Step = (session: Live, recording: SessionRecording) => void
async function recordTo(sessionSetup: SessionSetup, at: number, inputs: Record<number, Step[]> = {}, options: Parameters<typeof createSession>[1] = {}) {
  const session = createSession(sessionSetup, options), recording = new SessionRecording(session, () => Promise.resolve(identity))
  let pending: ReturnType<SessionRecording['requestExport']> | null = null
  for (let frame = 0; frame < at; frame += 1) {
    for (const step of inputs[frame] ?? []) step(session, recording)
    if (frame === at - 1) pending = recording.requestExport()
    expect(session.kernel.advance(1), `frame ${frame} advances`).toBe(1)
  }
  return { session, exported: await pending! }
}
const cdu = (side: 1 | 2): ActionSource => ({ kind: 'cdu', id: `CDU ${side}`, side, surface: 'main' })
const ios: ActionSource = { kind: 'ios', id: 'replay-owner', surface: 'main' }
const submit = (action: KernelAction, source: ActionSource = ios): Step => session => { session.port.submit(action, source) }
const manualRnp = (): SessionSetup => {
  const scenario = SCENARIO_LIBRARY.find(item => item.id === 'manual-rnp')!
  return { ...setup, initialFreeze: false, scenario, utc0Ms: scenarioStart(scenario) ?? setup.utc0Ms }
}

// The replay driver's core: R_j inputs at frames after F_0 go through the per-frame advance branch, and EVERY D_j is
// compared, not only the last. A wind change at R_3 is observable in D_3 itself, so dropping it is caught at frame 3.
test('a ten-frame flying recording with inputs at several R_j verifies, and a changed middle frame is refused at that frame', async () => {
  const { exported } = await recordTo({ ...setup, initialFreeze: false }, 10, {
    0: [submit({ kind: 'cdu.key', side: 1, fn: 'RTE', held: false }, cdu(1))],
    3: [submit({ kind: 'cdu.key', side: 2, fn: 'LEGS', held: false }, cdu(2)), submit({ kind: 'ios.wind', direction: 270, speed: 20 })],
    6: [submit({ kind: 'cdu.key', side: 1, fn: 'NEXT', held: false }, cdu(1)), submit({ kind: 'ios.link', available: 'toggle' })],
    8: [submit({ kind: 'ios.flightFreeze', on: true })],
  })
  expect(exported.lastBoundary).toBe(10); expect(exported.digests).toHaveLength(10)
  expect(exported.journal.map(entry => entry.frame)).toEqual([0, 3, 3, 6, 6, 8])
  expect(exported.capture).toEqual({ kind: 'boundary', exact: true, label: S_K_LABEL })
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 10, exact: true })
  const corrupted = structuredClone(exported); (corrupted.digests as string[])[5] = '0000000000000000'
  await expect(verifyRecording(corrupted, identity)).rejects.toThrow('digest mismatch at frame 5')
  const rewound = structuredClone(exported)
  ;(rewound.journal.find(entry => entry.kind === 'ios.wind')!.payload as { direction: number }).direction = 90
  await expect(verifyRecording(rewound, identity)).rejects.toThrow('digest mismatch at frame 3')
  // The #1516 snapshot slot stays empty until I2; a populated slot is not accepted before restore exists.
  await expect(verifyRecording({ ...exported, snapshots: [{ atFrame: 5, ref: { sha256: '0'.repeat(64), bytes: 0 } }] }, identity)).rejects.toThrow('snapshots require I2')
})

// Control records replay at their recorded positions among actions and v1 translations: a halt, an input made while
// halted and the resume at one R_j, and a later rate record, in a library scenario exported mid-run.
test('a scenario run with v1 translations, a halt with input and resume, and a rate change verifies mid-run', async () => {
  const { session, exported } = await recordTo(manualRnp(), 81, {
    20: [submit({ kind: 'cdu.key', side: 1, fn: 'PROG', held: false }, cdu(1))],
    40: [s => { s.kernel.control({ kind: 'halt' }) }, submit({ kind: 'cdu.key', side: 1, fn: 'LEGS', held: false }, cdu(1)), s => { s.kernel.control({ kind: 'resume' }) }],
    60: [s => { s.kernel.control({ kind: 'rate', rate: 16 }) }],
  })
  expect(session.kernel.runFinished).toBe(false)
  expect(exported.journal.filter(entry => entry.origin === 'v1-translated').length).toBeGreaterThan(0)
  expect(exported.control.map(entry => entry.kind)).toEqual(['halt', 'resume', 'rate'])
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 81, exact: true })
  const unpaused = { ...exported, control: exported.control.filter(entry => entry.kind === 'rate') }
  // Control records share recordOrdinal with the journal, so a record without its halt/resume cannot replay as recorded.
  await expect(verifyRecording(unpaused, identity)).rejects.toThrow('journal or outcome replay differs')
})

// An accepted mid-session import replays from its content reference, and its persistence write lands only in the
// verifier's own memory store. The counting browser Storage is what a verifier wired to browser persistence would
// write; the original persistent store must also be untouched by verification.
test('a mid-session user-database import replays from content and writes no browser or original store', async () => {
  const scope = { userId: 'crew', profileId: setup.profileId }
  const text = `{"schema":"aerolink.fms.user-database","version":1,"userId":"crew","profileId":"${setup.profileId}","exported":"2026-09-29T15:00:00.000Z","waypoints":[{"ident":"RPL","position":{"lat":44,"lon":-73},"type":"FIXED"}],"routes":[]}`
  const bytes = new TextEncoder().encode(text), original = memoryUserDatabaseStore()
  let originalWrites = 0
  const counted = { read: original.read, write: (s: typeof scope, value: string) => { originalWrites += 1; original.write(s, value) } }
  const { exported } = await recordTo({ ...setup, initialFreeze: false }, 6, { 3: [(s, r) => {
    const receipt = s.port.submit({ kind: 'config.userdb', unit: 1, text }, { kind: 'import', id: 'navdata.userDatabase', surface: 'main' })
    r.retainImport(receipt.submissionSeq, bytes)
  }] }, { userDatabase: { scope, store: counted } })
  const imported = exported.journal.find(entry => entry.kind === 'config.userdb')!
  expect(imported).toMatchObject({ frame: 3, outcome: { status: 'accepted' } })
  expect(imported.payload).not.toHaveProperty('text')
  expect(originalWrites, 'the live import persists, so its replay has a write to make').toBeGreaterThan(0)
  const liveWrites = originalWrites
  const writes: string[] = []
  const storage = { getItem: () => null, setItem: (key: string) => { writes.push(key) }, removeItem: () => {}, clear: () => {}, key: () => null, length: 0 }
  const global = globalThis as { localStorage?: unknown }, had = Object.hasOwn(global, 'localStorage'), previous = global.localStorage
  global.localStorage = storage
  try {
    expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 6, exact: true })
  } finally { if (had) global.localStorage = previous; else delete global.localStorage }
  expect(writes).toEqual([])
  expect(originalWrites).toBe(liveWrites)
  await expect(verifyRecording({ ...exported, contents: exported.contents.filter(item => item.bytes !== bytes.length) }, identity)).rejects.toThrow('missing content ')
})

// The build identity is the expensive part of a recording (a fetch and hash of every emitted asset): it is asked for
// only when an export is encoded, once per recording, never when a session is constructed or flown.
test('a recording asks for the build identity only when it encodes an export, and only once', async () => {
  let asked = 0
  const session = createSession(setup), recording = new SessionRecording(session, () => { asked += 1; return Promise.resolve(identity) })
  session.kernel.advance(5)
  expect(asked).toBe(0)
  const first = recording.requestExport(); session.kernel.advance(1); await first
  expect(asked).toBe(1)
  const second = recording.requestExport(); session.kernel.advance(1); await second
  expect(asked).toBe(1)
})

// A one-shot boundary observer is outside the simulation: if it throws, the kernel still leaves S_k, opens the next
// ACTION and keeps running, and the frames match a kernel that never had the observer.
test('a throwing boundary observer is reported but leaves the live kernel running and unchanged', () => {
  const observed = createSession(setup), control = createSession(setup)
  observed.kernel.onNextBoundary(() => { throw new Error('observer failed') })
  expect(() => observed.kernel.advance(3)).toThrow('observer failed')
  expect(observed.kernel.frame).toBe(1)
  expect(observed.kernel.advance(2)).toBe(2)
  observed.port.submit({ kind: 'ios.wind', direction: 180, speed: 5 }, ios)
  expect(observed.kernel.journal).toHaveLength(1)
  expect(observed.kernel.journal[0]).toMatchObject({ frame: 3, kind: 'ios.wind', outcome: { status: 'accepted' } })
  control.kernel.advance(3); control.port.submit({ kind: 'ios.wind', direction: 180, speed: 5 }, ios)
  expect(observed.kernel.frameDigests).toEqual(control.kernel.frameDigests)
})

// Codec parity is independent: published empty XXH64 and handwritten -0/NaN bytes, then unchanged frozen frame codec.
test('the production codec agrees with published literals and the frozen observable-frame encoder', () => {
  expect(product.xxh64(new Uint8Array())).toBe(0xef46db3751d8e999n)
  const encoded = new product.CanonicalWriter(); product.writeValue(encoded, -0)
  expect([...encoded.result()]).toEqual([4, 0, 0, 0, 0, 0, 0, 0, 128])
  const session = createSession(setup), frames: string[] = []
  session.kernel.advance(4, { onInstantClose: () => {
    const writer = new CanonicalWriter(), bytes = frameBytes(session.units, writer)
    frames.push(frameDigest(bytes))
    const actual = product.frameBytes(session.units, new product.CanonicalWriter())
    expect([...actual]).toEqual([...bytes]); expect(product.frameDigest(actual)).toBe(frameDigest(bytes))
  } })
  expect(session.kernel.frameDigests).toEqual(frames)
  // Keep the frozen scalar/hash owner authoritative; these imports do not manufacture a second expected codec.
  expect(xxh64(new Uint8Array())).toBe(0xef46db3751d8e999n)
  const frozen = new CanonicalWriter(); writeValue(frozen, NaN)
  const actual = new product.CanonicalWriter(); product.writeValue(actual, NaN)
  expect([...actual.result()]).toEqual([...frozen.result()])
})

test('recording takes the first lawful S_k without changing the callback rate and fresh replay stops before next ACTION', async () => {
  const session = createSession(setup), recording = new SessionRecording(session, () => Promise.resolve(identity))
  session.port.submit({ kind: 'cdu.key', side: 2, fn: 'RTE', held: false }, { kind: 'cdu', id: 'CDU 2', side: 2, surface: 'popout' })
  expect(() => session.kernel.boundaryRecord()).toThrow('NOT_AT_BOUNDARY')
  const pending = recording.requestExport()
  expect(session.kernel.advance(4)).toBe(4)
  const exported = await pending
  expect(exported.lastBoundary).toBe(1); expect(exported.frame).toBe(1)
  expect(exported.snapshots).toEqual([]); expect(exported.capture.kind).toBe('boundary')
  expect(exported.journal[0]).toMatchObject({ frame: 0, payload: { kind: 'cdu.key', side: 2, fn: 'RTE', held: false }, source: { surface: 'popout' } })
  expect(session.kernel.frame).toBe(4)
  // The same inputs with no export: the boundary capture must leave every live digest and journal entry unchanged.
  const control = createSession(setup)
  control.port.submit({ kind: 'cdu.key', side: 2, fn: 'RTE', held: false }, { kind: 'cdu', id: 'CDU 2', side: 2, surface: 'popout' })
  expect(control.kernel.advance(4)).toBe(4)
  expect(session.kernel.frameDigests).toHaveLength(4)
  expect(session.kernel.frameDigests).toEqual(control.kernel.frameDigests)
  expect(session.kernel.record()).toEqual(control.kernel.record())
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  expect(session.kernel.frame).toBe(4)
  const bad = structuredClone(exported); (bad.digests as string[])[0] = '0000000000000000'
  await expect(verifyRecording(bad, identity)).rejects.toThrow('digest mismatch at frame 0')
  await expect(verifyRecording(exported, { ...identity, qualified: false, reason: 'missing build identity' })).rejects.toThrow('unqualified environment')
  await expect(verifyRecording(exported, { ...identity, engine: { ...identity.engine, userAgent: 'different' } })).rejects.toThrow('build or engine identity differs')
})

test('the complete record resolves exact import bytes and refuses missing referenced content', async () => {
  const session = createSession(setup), recording = new SessionRecording(session, () => Promise.resolve(identity))
  const bytes = new TextEncoder().encode(' \n{"not":"a valid user database"}\r\n')
  const receipt = session.port.submit({ kind: 'config.userdb', unit: 2, text: new TextDecoder().decode(bytes) }, { kind: 'import', id: 'navdata.userDatabase', surface: 'main' })
  recording.retainImport(receipt.submissionSeq, bytes)
  const pending = recording.requestExport(); session.kernel.advance(1); const exported = await pending
  expect(exported.journal[0].outcome.status).toBe('refused')
  expect(exported.journal[0].payload).not.toHaveProperty('text')
  expect(exported.contents[0].data).toEqual([...bytes])
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  await expect(verifyRecording({ ...exported, contents: [] }, identity)).rejects.toThrow('missing content ')
})

test('replay resolves both independently scoped initial databases exactly, and live construction never writes them', async () => {
  const scope = { userId: 'crew', profileId: setup.profileId }, second = { ...scope, profileId: `${scope.profileId}:fms2` }
  // This exact saved string intentionally fails JSON.parse; stripping its BOM changes the initial parser branch.
  const one = `\uFEFF {"schema":"aerolink.fms.user-database","version":1,"userId":"crew","profileId":"${scope.profileId}","exported":"2026-09-29T15:00:00.000Z","waypoints":[{"ident":"ONE","position":{"lat":44,"lon":-73},"type":"FIXED"}],"routes":[]}\n`
  const two = `\n{"schema":"aerolink.fms.user-database","version":1,"userId":"crew","profileId":"${second.profileId}","exported":"2026-09-29T15:00:00.000Z","waypoints":[{"ident":"TWO","position":{"lat":45,"lon":-72},"type":"FIXED"}],"routes":[]}\r\n`
  const store = memoryUserDatabaseStore(); store.write(scope, one); store.write(second, two)
  let writes = 0
  const session = createSession(setup, { userDatabase: { scope, store: { read: store.read, write: () => { writes += 1 } } } })
  const recording = new SessionRecording(session, () => Promise.resolve(identity))
  const pending = recording.requestExport(); session.kernel.advance(1); const exported = await pending
  expect(exported.initial.users.map(user => user.scope)).toEqual([scope, second])
  expect(exported.contents.find(item => item.sha256 === exported.initial.users[0].content?.sha256)!.data.slice(0, 3)).toEqual([239, 187, 191])
  expect(exported.initial.users.map(user => new TextDecoder('utf-8', { ignoreBOM: true }).decode(new Uint8Array(exported.contents.find(item => item.sha256 === user.content?.sha256)!.data)))).toEqual([one, two])
  // The resolver is the production input to fresh-session construction, not the import File.text decoder.
  expect(await (await ContentStore.from(exported.contents)).text(exported.initial.users[0].content!)).toBe(one)
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  expect(writes).toBe(0)
  await expect(verifyRecording({ ...exported, initial: { ...exported.initial, users: exported.initial.users.slice(0, 1) } }, identity)).rejects.toThrow('initial user-store scopes or content differ')
})

test('S_k input metadata retains an unresolved pending submission without executing it in private verification', async () => {
  const session = createSession(setup), recording = new SessionRecording(session, () => Promise.resolve(identity))
  const request = recording.requestExport()
  session.kernel.advance(1, { onInstantClose: () => {
    expect(session.port.submit({ kind: 'ios.link', available: 'toggle' }, { kind: 'ios', id: 'dual.link', surface: 'popout' })).toEqual({ submissionSeq: 1 })
  } })
  const exported = await request
  expect(exported.pendingInputs).toEqual([{ submissionSeq: 1, action: { kind: 'ios.link', available: 'toggle' }, source: { kind: 'ios', id: 'dual.link', surface: 'popout' } }])
  expect(exported.journal).toEqual([])
  expect(exported.counters).toEqual({ submissionSeq: 2, controlSeq: 1, recordOrdinal: 1 })
  expect(exported.liveQueue).toEqual({ v: 1, entries: [] }); expect(exported.snapshots).toEqual([])
  // The LIVE call continued through ACTION at F1 and resolved the toggle; that is deliberately beyond exported S1.
  expect(session.kernel.journal[0]).toMatchObject({ frame: 1, payload: { kind: 'ios.link', available: false }, outcome: { status: 'accepted' } })
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  await expect(verifyRecording({ ...exported, pendingInputs: [] }, identity)).rejects.toThrow('terminal ordering state differs')
  await expect(verifyRecording({ ...exported, pendingInputs: null }, identity)).rejects.toThrow('missing boundary pending-input context')
})

test('the public recording file retains literal negative-zero input bits rather than JSON-normalizing them', async () => {
  const session = createSession(setup), recording = new SessionRecording(session, () => Promise.resolve(identity))
  session.port.submit({ kind: 'ios.wind', direction: -0, speed: 0 }, { kind: 'ios', id: 'wind', surface: 'main' })
  const pending = recording.requestExport(); session.kernel.advance(1); const exported = await pending
  const file = serializeRecording(exported), literal = JSON.parse(file)
  expect(literal.numbers).toEqual([{ path: ['journal', '0', 'payload', 'direction'], value: '-0' }])
  expect(literal.recording.journal[0].payload.direction).toBeNull()
  const restored = parseRecording(file)
  expect(Object.is((restored.journal[0].payload as { direction: number }).direction, -0)).toBe(true)
  expect(await verifyRecording(restored, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  literal.numbers[0].path = ['journal', '0', 'payload', 'missing']
  expect(() => parseRecording(JSON.stringify(literal))).toThrow('invalid numeric input placeholder')
})

test('private verifyTo stops before the next poll; R_j and a fault are not S_k, while a finished run still passes S_k', async () => {
  const scenario = { id: 'boundary-stop', title: 'Boundary', objective: '', maxSeconds: 2, steps: [
    { when: { kind: 'time' as const, seconds: 0.25 }, action: { kind: 'alert' as const, text: 'NEXT ACTION' } },
  ] }
  const privateSession = createSession({ ...setup, scenario }, { verification: true })
  privateSession.kernel.verifyTo(1)
  expect(privateSession.kernel.boundaryRecord().journal).toEqual([])
  expect(privateSession.kernel.frameDigests).toHaveLength(1)
  expect(() => privateSession.kernel.advance(1)).toThrow('cannot resume')
  // After the run ends the bench keeps advancing (paused, without haltOnRunEnd), so the export waits for that S_k.
  const ended = createSession({ ...setup, scenario })
  ended.kernel.advance(4)
  expect(ended.kernel.runFinished).toBe(true)
  expect(() => ended.kernel.boundaryRecord()).toThrow('NOT_AT_BOUNDARY')
  const request = new SessionRecording(ended, () => Promise.resolve(identity)).requestExport()
  expect(ended.kernel.advance(1)).toBe(1)
  const exported = await request
  expect(exported.capture).toEqual({ kind: 'boundary', exact: true, label: S_K_LABEL })
  expect(exported.lastBoundary).toBe(ended.kernel.frame)
  expect(exported.snapshots).toEqual([])
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: exported.lastBoundary, exact: true })
  // An operator halt passes no S_k: the export is taken at once, labelled, and verifies only its closed prefix.
  const halted = createSession(manualRnp())
  halted.kernel.advance(3); halted.kernel.control({ kind: 'halt' })
  const open = await new SessionRecording(halted, () => Promise.resolve(identity)).requestExport()
  expect(open.capture).toEqual({ kind: 'halted-open-action', exact: false,
    label: 'Open ACTION export; only the prior closed prefix can be verified, not exact current state' })
  expect(await verifyRecording(open, identity)).toEqual({ status: 'verified', frame: 3, exact: false })
  const composition = singleComposition(setup.utc0Ms)
  const failed = new FmsKernel({ ...composition.plant, integrate: () => { composition.clock.advanceFrame(); throw new Error('actual composite failure') } })
  expect(() => failed.advance(1)).toThrow('actual composite failure')
  expect(failed.frame).toBe(1)
  expect(failed.lastSuccessfulBoundary).toBe(0)
  expect(() => failed.boundaryRecord()).toThrow('FAULTED')
})
