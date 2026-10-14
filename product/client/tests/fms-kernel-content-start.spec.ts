import { expect, logicTest as test } from './isolated-client-test'
import { ContentStore } from '../src/fmsCdu/kernel/contentStore'
import { createSession, type SessionSetup } from '../src/fmsCdu/kernel/session'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import { CanonicalWriter, frameBytes } from './fixtures/fms-kernel-digest'
import { SessionRecording, verifyRecording, type RecordingIdentity } from '../src/fmsCdu/kernel/recording'

const setup: SessionSetup = { composition: 'dual', utc0Ms: Date.UTC(2026, 8, 29, 15), profileId: 'cma9000-s300-heli-civil',
  variantId: '002/003/005/102/103/302/303/502/503', initialFreeze: true, startModes: ['WARM', 'WARM'], onGround: false,
  startState: null, startSource: { kind: 'ios', id: 'navdata.startState', surface: 'main' }, scenario: null }

// Content's primary owner: FIPS180-4 literals; neither the content encoder nor the decoder supplies expected hashes.
test('content addresses exact bytes, rejects absent/corrupt bytes and has no mutable retained alias', async () => {
  const store = new ContentStore(), abc = new Uint8Array([97, 98, 99])
  expect(await store.put(new Uint8Array())).toEqual({ sha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855', bytes: 0 })
  const ref = await store.put(abc)
  expect(ref).toEqual({ sha256: 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad', bytes: 3 })
  abc[0] = 0
  const out = await store.resolve(ref); out[0] = 0
  expect([...await store.resolve(ref)]).toEqual([97, 98, 99])
  await expect(new ContentStore().resolve(ref)).rejects.toThrow(`missing content ${ref.sha256}`)
  await expect(ContentStore.from([{ ...ref, data: [97, 98, 100] }])).rejects.toThrow(`corrupt content ${ref.sha256}`)
})

// Built-in datasets ship inside the emitted assets, so they resolve by bundle hash + asset path, never by a
// re-serialization. Controlled literal identity (FIPS 180-4 "abc" and empty digests), not a claim about this host.
const ABC = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
const EMPTY = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
const identity: RecordingIdentity = { build: { schema: 'aerolink.fms-build.v1', kind: 'built', commit: 'literal-fixture', dirty: false, bundleHash: ABC,
  assets: [{ path: 'assets/a.js', sha256: ABC, bytes: 3 }, { path: 'assets/b.js', sha256: EMPTY, bytes: 0 }] },
  engine: { userAgent: 'literal-engine', platform: 'literal-platform', fullVersionList: [{ brand: 'literal', version: '1.2.3.4' }], architecture: null, platformVersion: null }, qualified: true, reason: null }
const exportAtFirstBoundary = async () => {
  const session = createSession(setup), pending = new SessionRecording(session, () => Promise.resolve(identity)).requestExport()
  session.kernel.advance(1)
  return pending
}

test('built-in content is referenced by bundle hash and asset path, and replay refuses any other built-in set', async () => {
  const exported = await exportAtFirstBoundary()
  expect(exported.builtins).toEqual([{ bundleHash: ABC, path: 'assets/a.js', sha256: ABC, bytes: 3 }, { bundleHash: ABC, path: 'assets/b.js', sha256: EMPTY, bytes: 0 }])
  expect(await verifyRecording(exported, identity)).toEqual({ status: 'verified', frame: 1, exact: true })
  for (const builtins of [exported.builtins.slice(0, 1), [{ ...exported.builtins[0], path: 'assets/other.js' }, exported.builtins[1]],
    exported.builtins.map(item => ({ ...item, bundleHash: EMPTY }))]) {
    await expect(verifyRecording({ ...exported, builtins }, identity)).rejects.toThrow('built-in content identity differs')
  }
})

// D5 7.2 names the magnetic database among the initial databases. The built-in model is NOAA WMM2025 (epoch 2025).
test('initial state records each unit\'s magnetic database identity and replay refuses a different one', async () => {
  const exported = await exportAtFirstBoundary()
  const models = exported.initial.observations.magneticModels
  expect(models).toHaveLength(2)
  for (const model of models) expect(model).toMatchObject({ name: 'WMM2025', epoch: 2025, valid: true })
  const changed = { ...exported.initial, observations: { ...exported.initial.observations, magneticModels: [models[0], { ...models[1], crc: '00000000' }] } }
  await expect(verifyRecording({ ...exported, initial: changed }, identity)).rejects.toThrow('initial state differs')
})

// Initial bytes must be captured BEFORE model parsing/normalization and retain independent FMS scopes.
test('the shared session factory captures both exact initial user stores without writing them', () => {
  const memory = memoryUserDatabaseStore(), scope = { userId: 'crew', profileId: setup.profileId }
  const one = ' {"not":"a valid database"}\n', two = '\ufeff { "second": true }\r\n'
  memory.write(scope, one); memory.write({ ...scope, profileId: `${scope.profileId}:fms2` }, two)
  let writes = 0
  const session = createSession(setup, { userDatabase: { scope, store: { read: memory.read, write: () => { writes += 1 } } } })
  expect(session.users).toEqual([{ scope, text: one }, { scope: { ...scope, profileId: `${scope.profileId}:fms2` }, text: two }])
  expect(writes).toBe(0)
})

// Start orchestration, not another power-law matrix: WARM matches actual pre-factory construction; COLD calls the
// established native path for FMS1 only. Frozen independent bytes include observable GPS/clock/aircraft state.
test('WARM construction is unchanged and per-FMS COLD uses the actual five-second boot path and KALMAN re-initialisation', () => {
  for (const cold of [false, true]) {
    const session = createSession({ ...setup, startModes: [cold ? 'COLD' : 'WARM', 'WARM'] })
    const direct = dualComposition(setup.utc0Ms)
    if (cold) direct.system.computers[0].powerOn('COLD', false)
    const base = new FmsKernel(direct.plant, null, { flightFreeze: true })
    const observe = (units: typeof session.units) => Array.from(frameBytes(units, new CanonicalWriter()))
    expect(session.units.computers.map(fms => fms.powerState)).toEqual([cold ? 'TEST' : 'ON', 'ON'])
    expect(observe(session.units)).toEqual(observe(direct.plant.units))
    session.kernel.advance(19); base.advance(19)
    expect(session.units.computers[0].powerState).toBe(cold ? 'TEST' : 'ON')
    session.kernel.advance(1); base.advance(1)
    expect(session.units.computers.map(fms => fms.powerState)).toEqual(['ON', 'ON'])
    expect(observe(session.units)).toEqual(observe(direct.plant.units))
    if (cold) expect(session.units.computers[0].kalmanStatus.opMode).toBe('INI')
  }
})
