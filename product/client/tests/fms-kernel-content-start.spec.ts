import { expect, logicTest as test } from './isolated-client-test'
import { ContentStore } from '../src/fmsCdu/kernel/contentStore'
import { createSession, type SessionSetup } from '../src/fmsCdu/kernel/session'
import { dualComposition } from '../src/fmsCdu/kernel/legacyPlantAdapter'
import { FmsKernel } from '../src/fmsCdu/kernel/kernel'
import { memoryUserDatabaseStore } from '../src/fmsCdu/userDatabase'
import { CanonicalWriter, frameBytes } from './fixtures/fms-kernel-digest'

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
test('WARM construction is unchanged and per-FMS COLD uses the actual five-second boot path with shared GPS preserved', () => {
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
