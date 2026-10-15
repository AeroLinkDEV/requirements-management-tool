import { expect, logicTest as test } from './isolated-client-test'
import { createSession, type SessionSetup } from '../src/fmsCdu/kernel/session'
import { pageRecordingIdentity, parseRecording, serializeRecording, SessionRecording, verifyRecording, type RecordingIdentity } from '../src/fmsCdu/kernel/recording'
import { CanonicalWriter, frameBytes, frameDigest, xxh64, writeValue } from './fixtures/fms-kernel-digest'
import * as product from '../src/fmsCdu/kernel/stateDigest'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import { ContentStore } from '../src/fmsCdu/kernel/contentStore'
import type { ActionSource, KernelAction } from '../src/fmsCdu/kernel/actions'
import { SCENARIO_LIBRARY } from '../src/fmsCdu/scenarioLibrary'
import { scenarioStart } from '../src/fmsCdu/scenario'
import { DualFmsSystem } from '../src/fmsCdu/dualFms'
import { FRAME_UNITS } from '../src/fmsCdu/kernel/time'

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
// only when an export is encoded, never when a session is constructed or flown. The page keeps the answer (below).
test('a recording asks for the build identity only when it encodes an export', async () => {
  let asked = 0
  const session = createSession(setup), recording = new SessionRecording(session, () => { asked += 1; return Promise.resolve(identity) })
  session.kernel.advance(5)
  expect(asked).toBe(0)
  const first = recording.requestExport(); session.kernel.advance(1); await first
  expect(asked).toBe(1)
})

// The page keeps a definitive identity for its lifetime, but a transient failure (here an HTTP 503 for the manifest)
// is returned unkept, so the next export asks again instead of staying unqualified until a reload.
test('the page identity retries after a failed manifest fetch and keeps the first definitive answer', async () => {
  const global = globalThis as { location?: unknown; fetch: typeof fetch }
  const hadLocation = Object.hasOwn(global, 'location'), previousLocation = global.location, previousFetch = global.fetch
  const requests: string[] = []
  global.location = { href: 'http://127.0.0.1:1/bench' }
  global.fetch = (async (input: string | URL) => {
    requests.push(String(input))
    return requests.length === 1 ? new Response('unavailable', { status: 503 })
      : new Response(JSON.stringify({ schema: 'aerolink.fms-build.v1', kind: 'development', commit: 'literal', dirty: false, assets: [], bundleHash: null }), { status: 200 })
  }) as typeof fetch
  try {
    const failed = await pageRecordingIdentity()
    expect(failed).toMatchObject({ qualified: false, reason: 'missing build identity', build: null })
    const definitive = await pageRecordingIdentity()
    expect(definitive).toMatchObject({ qualified: false, reason: 'development build: unqualified', build: { kind: 'development' } })
    expect(await pageRecordingIdentity()).toBe(definitive)
    expect(requests).toEqual(['http://127.0.0.1:1/fms-recording-build.json', 'http://127.0.0.1:1/fms-recording-build.json'])
  } finally { global.fetch = previousFetch; if (hadLocation) global.location = previousLocation; else delete global.location }
})

// Boundary callbacks (a one-shot observer, or the caller's atNextBoundary) are outside the simulation. When one throws,
// F_{j+1}'s ACTION must still open BEFORE the error surfaces: the input pending from INTEGRATE executes, the scenario
// step due at F_{j+1} polls, and an input made right after the throw executes at R_{j+1}. The run then matches one
// that never had the callback.
test('a throwing boundary callback surfaces only after the next ACTION opened, and leaves the run unchanged', () => {
  const scenario = { id: 'callback', title: 'Callback', objective: '', maxSeconds: 10, steps: [
    { when: { kind: 'time' as const, seconds: 0.25 }, action: { kind: 'alert' as const, text: 'AT F1' } },
    { when: { kind: 'time' as const, seconds: 2 }, action: { kind: 'alert' as const, text: 'LATER' } },
  ] }
  const wind = (direction: number): KernelAction => ({ kind: 'ios.wind', direction, speed: direction / 10 })
  const drive = (callback: 'observer' | 'atNextBoundary' | null) => {
    const session = createSession({ ...setup, initialFreeze: false, scenario })
    session.port.submit(wind(10), ios)
    if (callback === 'observer') session.kernel.onNextBoundary(() => { throw new Error('callback failed') })
    let pending = false
    const advance = () => session.kernel.advance(callback ? 3 : 1, {
      onInstantClose: () => { if (!pending) { pending = true; session.port.submit(wind(20), ios) } },
      ...(callback === 'atNextBoundary' ? { atNextBoundary: () => { throw new Error('callback failed') } } : {}),
    })
    if (callback) expect(advance).toThrow('callback failed'); else expect(advance()).toBe(1)
    expect(session.kernel.frame).toBe(1)
    const atF1 = session.kernel.journal.filter(entry => entry.frame === 1).map(entry => `${entry.origin}:${entry.kind}`)
    expect(atF1).toContain('native:ios.wind')
    expect(atF1.some(entry => entry.startsWith('v1-translated'))).toBe(true)
    const r1 = session.port.submit(wind(30), ios)
    expect(session.kernel.journal.at(-1)).toMatchObject({ submissionSeq: r1.submissionSeq, frame: 1, outcome: { status: 'accepted' } })
    expect(session.kernel.advance(5)).toBe(5)
    return { digests: session.kernel.frameDigestsTo(Number.POSITIVE_INFINITY), journal: session.kernel.record().journal }
  }
  const control = drive(null)
  expect(drive('observer')).toEqual(control)
  expect(drive('atNextBoundary')).toEqual(control)
})

// At a private verifier's terminal S_k the kernel stays at the boundary, and a callback's failure is thrown rather
// than discarded by the return: a verifier never reports a boundary whose callback failed.
test('a callback failing at a verifier\'s terminal S_k is thrown, and the kernel stays at that boundary', () => {
  for (const kind of ['observer', 'atNextBoundary'] as const) {
    const verifier = createSession(setup, { verification: true })
    if (kind === 'observer') verifier.kernel.onNextBoundary(() => { throw new Error(`terminal ${kind}`) })
    expect(() => verifier.kernel.verifyTo(1, kind === 'atNextBoundary' ? { atNextBoundary: () => { throw new Error(`terminal ${kind}`) } } : {}))
      .toThrow(`terminal ${kind}`)
    expect(verifier.kernel.boundaryRecord().frame).toBe(1)
  }
})

// D_j lives in 4096-entry typed-array chunks. Every real recording crosses the first boundary (16 s at 64x), so the
// boundaries themselves are owned: exact 64-bit values (including 0 and 2^64-1 at a boundary) read back by index and
// by prefix across 4095/4096 and 8191/8192, and out-of-range reads refused. Expected hex is BigInt.toString(16), not
// the product's hex64.
test('the digest log keeps exact 64-bit values across its chunk boundaries and refuses out-of-range reads', () => {
  const log = new product.DigestLog(), values: bigint[] = []
  let x = 0x9e3779b97f4a7c15n
  for (let i = 0; i < 8200; i += 1) {
    x = (x * 6364136223846793005n + 1442695040888963407n) & ((1n << 64n) - 1n)
    values.push(i === 4095 || i === 8191 ? (1n << 64n) - 1n : i === 4096 || i === 8192 ? 0n : x)
    log.push(values[i])
  }
  const hex = (value: bigint) => value.toString(16).padStart(16, '0')
  expect(log.length).toBe(8200)
  for (const i of [0, 4094, 4095, 4096, 4097, 8190, 8191, 8192, 8193, 8199]) expect(log.hexAt(i), `hexAt ${i}`).toBe(hex(values[i]))
  expect([log.hexAt(4095), log.hexAt(4096), log.hexAt(8191), log.hexAt(8192)]).toEqual(['ffffffffffffffff', '0000000000000000', 'ffffffffffffffff', '0000000000000000'])
  expect(log.hex()).toEqual(values.map(hex))
  expect(log.hex(4097)).toEqual(values.slice(0, 4097).map(hex))
  expect(log.hex(8193)).toEqual(values.slice(0, 8193).map(hex))
  expect(log.hex(0)).toEqual([]); expect(log.hex(-5)).toEqual([]); expect(log.hex(99_999)).toHaveLength(8200)
  for (const bad of [-1, 8200, 1.5, NaN, Infinity]) expect(log.hexAt(bad), `hexAt ${bad}`).toBeUndefined()
})

// The same boundary through the verifier: a 4200-frame recording (frames past the first chunk) whose R_4150 input is
// changed must be refused at frame 4150, not verified because later chunks went unread.
test('a recording changed after frame 4095 is refused at that frame', async () => {
  test.setTimeout(5 * 60_000)
  const single: SessionSetup = { ...setup, composition: 'single', startModes: ['WARM'], initialFreeze: false }
  const { exported } = await recordTo(single, 4200, { 4150: [submit({ kind: 'ios.wind', direction: 270, speed: 20 })] })
  expect(exported.digests).toHaveLength(4200)
  const changed = structuredClone(exported)
  ;(changed.journal.find(entry => entry.kind === 'ios.wind')!.payload as { direction: number }).direction = 90
  await expect(verifyRecording(changed, identity)).rejects.toThrow('digest mismatch at frame 4150')
})

// T17: a composite INTEGRATE that throws is recorded as an execution-error control record, and replay accepts the
// fault only where the fresh composition faults at the same instant with the same record. The fault is made the way
// the I1a phases owner makes one (the composite's step replaced in the test), here on the prototype so the verifier's
// own fresh composition faults too; it is restored before anything else runs.
test('a recorded composite fault replays through the verifier, and a different or missing fault is refused', async () => {
  const original = DualFmsSystem.prototype.step
  const steps = new WeakMap<object, number>()
  DualFmsSystem.prototype.step = function (this: DualFmsSystem, dt: number) {
    const n = (steps.get(this) ?? 0) + 1; steps.set(this, n)
    if (n === 3) throw new Error('guidance source switch')
    return original.call(this, dt)
  }
  let exported: Awaited<ReturnType<SessionRecording['requestExport']>>
  try {
    const session = createSession({ ...setup, initialFreeze: false }), recording = new SessionRecording(session, () => Promise.resolve(identity))
    let faults = 0
    while (session.kernel.frame < 8) { try { session.kernel.advance(1) } catch { faults += 1 } }
    const pending = recording.requestExport(); expect(session.kernel.advance(1)).toBe(1); exported = await pending
    expect(faults).toBe(1)
    expect(exported.control).toMatchObject([{ kind: 'execution-error', message: 'guidance source switch', instant: 2 * FRAME_UNITS }])
    expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 9, exact: true })
    const otherMessage = structuredClone(exported); (otherMessage.control[0] as { message: string }).message = 'a different failure'
    await expect(verifyRecording(otherMessage, identity)).rejects.toThrow('guidance source switch')
  } finally { DualFmsSystem.prototype.step = original }
  // Without the fault the fresh composition completes the step the record says failed, so frame 3 differs.
  await expect(verifyRecording(exported!, identity)).rejects.toThrow('digest mismatch at frame 3')
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
  expect(session.kernel.frameDigestsTo(Number.POSITIVE_INFINITY)).toEqual(frames)
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
  expect(session.kernel.frameDigestsTo(Number.POSITIVE_INFINITY)).toHaveLength(4)
  expect(session.kernel.frameDigestsTo(Number.POSITIVE_INFINITY)).toEqual(control.kernel.frameDigestsTo(Number.POSITIVE_INFINITY))
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
  expect(privateSession.kernel.frameDigestsTo(Number.POSITIVE_INFINITY)).toHaveLength(1)
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
