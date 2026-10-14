import { expect, logicTest as test } from './isolated-client-test'
import { createSession, type SessionSetup } from '../src/fmsCdu/kernel/session'
import { parseRecording, serializeRecording, SessionRecording, verifyRecording, type RecordingIdentity } from '../src/fmsCdu/kernel/recording'
import { CanonicalWriter, frameBytes, frameDigest, xxh64, writeValue } from './fixtures/fms-kernel-digest'
import * as product from '../src/fmsCdu/kernel/stateDigest'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { singleComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import { ContentStore } from '../src/fmsCdu/kernel/contentStore'

const setup: SessionSetup = { composition: 'dual', utc0Ms: Date.UTC(2026, 8, 29, 15), profileId: 'cma9000-s300-heli-civil',
  variantId: '002/003/005/102/103/302/303/502/503', initialFreeze: true, startModes: ['WARM', 'WARM'], onGround: false,
  startState: null, startSource: { kind: 'ios', id: 'navdata.startState', surface: 'main' }, scenario: null }
// Controlled pure-logic identity input, NOT a claim about this host. Actual manifest/engine binding belongs to UI.
const identity: RecordingIdentity = { build: { schema: 'aerolink.fms-build.v1', kind: 'built', commit: 'literal-fixture', dirty: false,
  bundleHash: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
  assets: [{ path: 'literal-abc.js', sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad', bytes: 3 }] },
  engine: { userAgent: 'literal-engine', platform: 'literal-platform', fullVersionList: [{ brand: 'literal', version: '1.2.3.4' }], architecture: null, platformVersion: null }, qualified: true, reason: null }

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
  const session = createSession(setup), recording = new SessionRecording(session, Promise.resolve(identity))
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
  const session = createSession(setup), recording = new SessionRecording(session, Promise.resolve(identity))
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

test('replay resolves both independently scoped initial databases without writing the original persistent store', async () => {
  const scope = { userId: 'crew', profileId: setup.profileId }, second = { ...scope, profileId: `${scope.profileId}:fms2` }
  // This exact saved string intentionally fails JSON.parse; stripping its BOM changes the initial parser branch.
  const one = `\uFEFF {"schema":"aerolink.fms.user-database","version":1,"userId":"crew","profileId":"${scope.profileId}","exported":"2026-09-29T15:00:00.000Z","waypoints":[{"ident":"ONE","position":{"lat":44,"lon":-73},"type":"FIXED"}],"routes":[]}\n`
  const two = `\n{"schema":"aerolink.fms.user-database","version":1,"userId":"crew","profileId":"${second.profileId}","exported":"2026-09-29T15:00:00.000Z","waypoints":[{"ident":"TWO","position":{"lat":45,"lon":-72},"type":"FIXED"}],"routes":[]}\r\n`
  const store = memoryUserDatabaseStore(); store.write(scope, one); store.write(second, two)
  let writes = 0
  const session = createSession(setup, { userDatabase: { scope, store: { read: store.read, write: () => { writes += 1 } } } })
  const recording = new SessionRecording(session, Promise.resolve(identity))
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
  const session = createSession(setup), recording = new SessionRecording(session, Promise.resolve(identity))
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
  const session = createSession(setup), recording = new SessionRecording(session, Promise.resolve(identity))
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

test('private verifyTo stops before the next poll, while fault and run-end exports never impersonate S_k', async () => {
  const scenario = { id: 'boundary-stop', title: 'Boundary', objective: '', maxSeconds: 2, steps: [
    { when: { kind: 'time' as const, seconds: 0.25 }, action: { kind: 'alert' as const, text: 'NEXT ACTION' } },
  ] }
  const privateSession = createSession({ ...setup, scenario }, { verification: true })
  privateSession.kernel.verifyTo(1)
  expect(privateSession.kernel.boundaryRecord().journal).toEqual([])
  expect(privateSession.kernel.frameDigests).toHaveLength(1)
  expect(() => privateSession.kernel.advance(1)).toThrow('cannot resume')
  const ended = createSession({ ...setup, scenario })
  ended.kernel.advance(4)
  expect(ended.kernel.runFinished).toBe(true)
  expect(() => ended.kernel.boundaryRecord()).toThrow('NOT_AT_BOUNDARY')
  const exported = await new SessionRecording(ended, Promise.resolve(identity)).requestExport()
  expect(exported.capture).toMatchObject({ kind: 'halted-open-action', exact: false })
  expect(exported.snapshots).toEqual([])
  const composition = singleComposition(setup.utc0Ms)
  const failed = new FmsKernel({ ...composition.plant, integrate: () => { composition.clock.advanceFrame(); throw new Error('actual composite failure') } })
  expect(() => failed.advance(1)).toThrow('actual composite failure')
  expect(failed.frame).toBe(1)
  expect(failed.lastSuccessfulBoundary).toBe(0)
  expect(() => failed.boundaryRecord()).toThrow('FAULTED')
})
