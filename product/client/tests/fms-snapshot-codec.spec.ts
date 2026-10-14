import { expect, logicTest as test } from './isolated-client-test'
import { createHash } from 'node:crypto'
import { decodeState, encodeState, type ClassRegistration, type SnapshotUnit } from '../src/fmsCdu/snapshot/codec'
import { encodeHeaderBody, sealSnapshot, SNAPSHOT_HEADER_FIELD_ORDER, type SnapshotHeaderBody } from '../src/fmsCdu/snapshot/envelope'
import { sha256 } from '../src/fmsCdu/snapshot/integrity'

// #1516 I2a: the owner of the aerolink.fms.snapshot v1 state codec, its alias census, the envelope header and SHA-256.
// Every expected byte below is written out by hand from the format (tags, little-endian integers, IEEE 754 binary64,
// UTF-8), not produced by the encoder; SHA-256 is checked against the FIPS 180-4 example vectors.

/** Bytes from hex text; spaces are only for reading. */
const hex = (...parts: string[]) => Uint8Array.from(parts.join('').replace(/\s+/g, '').match(/../g) ?? [], byte => parseInt(byte, 16))
const show = (bytes: Uint8Array) => Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join(' ')
/** A u32 little-endian length and the ASCII bytes of `text`, as hex. */
const s = (text: string) => {
  const length = text.length.toString(16).padStart(8, '0')
  return length.slice(6, 8) + length.slice(4, 6) + length.slice(2, 4) + length.slice(0, 2) + Array.from(text, ch => ch.charCodeAt(0).toString(16).padStart(2, '0')).join('')
}

/** UTC0 = 0, one unit "U" with one authoritative field "s". */
const ONE_FIELD = '0000000000000000 01000000 0100000055 01000000 0100000073'
const encodeField = (value: unknown, utc0Ms = 0) => {
  const result = encodeState([{ unitId: 'U', root: { s: value }, fields: { s: 'authoritative' } }], { utc0Ms })
  if (!result.ok) throw new Error(`refused: ${result.refusal.code} ${result.refusal.detail}`)
  return result.value.bytes
}
const authoritative = (root: object): SnapshotUnit['fields'] => Object.fromEntries(Object.keys(root).map(key => [key, 'authoritative' as const]))
const refusalOf = (units: SnapshotUnit[], classes: ClassRegistration[] = []) => {
  const result = encodeState(units, { utc0Ms: 0, classes })
  return result.ok ? 'accepted' : result.refusal.code
}

test('object keys keep ECMAScript own-key order: integer-like keys ascending, then creation order', () => {
  const value = { b: 1, a: 2, 10: 'x', 2: true }
  expect(show(encodeField(value))).toBe(show(hex(ONE_FIELD,
    '07 04000000',
    '01000000 32  03',
    '02000000 3130  05 01000000 78',
    '01000000 62  04 000000000000F03F',
    '01000000 61  04 0000000000000040')))
})

test('signed zero keeps its sign', () => {
  expect(show(encodeField([-0, 0]))).toBe(show(hex(ONE_FIELD, '06 02000000', '04 0000000000000080', '04 0000000000000000')))
})

test('positive and negative infinity are kept as numbers', () => {
  expect(show(encodeField([Infinity, -Infinity]))).toBe(show(hex(ONE_FIELD, '06 02000000', '04 000000000000F07F', '04 000000000000F0FF')))
})

test('every scalar NaN encodes as the one canonical NaN, and decode refuses any other NaN', () => {
  const nan = (high: number, low: number) => {
    const view = new DataView(new ArrayBuffer(8))
    view.setUint32(0, low, true); view.setUint32(4, high, true)
    return view.getFloat64(0, true)
  }
  const payload = nan(0x7ff80000, 1), negative = nan(0xfff80000, 0)
  // Guard: these really are two different NaN bit patterns on this engine.
  const bits = (value: number) => { const view = new DataView(new ArrayBuffer(8)); view.setFloat64(0, value, true); return show(new Uint8Array(view.buffer)) }
  expect(bits(payload)).not.toBe(bits(negative))
  expect(show(encodeField([payload, negative, NaN]))).toBe(show(hex(ONE_FIELD, '06 03000000', '04 000000000000F87F', '04 000000000000F87F', '04 000000000000F87F')))
  const other = decodeState(hex(ONE_FIELD, '04 010000000000F87F'))
  expect(other.ok ? 'accepted' : other.refusal.code).toBe('NON_CANONICAL')
})

test('an invalid Date has its own explicit encoding and decodes to an invalid Date', () => {
  expect(show(encodeField(new Date(Number.NaN)))).toBe(show(hex(ONE_FIELD, '0A')))
  const decoded = decodeState(hex(ONE_FIELD, '0A'))
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  const date = decoded.value.units[0].fields.s
  expect(date instanceof Date && Number.isNaN(date.getTime())).toBe(true)
})

test('valid Dates are whole milliseconds relative to UTC0, exact across the whole Date range', () => {
  const utc0 = 1_700_000_000_000
  expect(show(encodeField([new Date(utc0 + 1234), new Date(utc0 - 1), new Date(0)], utc0))).toBe(show(hex(
    '0068E5CF8B010000 01000000 0100000055 01000000 0100000073',
    '06 03000000',
    '09 D204000000000000',
    '09 FFFFFFFFFFFFFFFF',
    '09 00981A3074FEFFFF')))
  // Offsets beyond 2^53 ms stay exact: the earliest UTC0 and the latest Date are 1.728e16 ms apart.
  const earliest = -8_640_000_000_000_000
  const bytes = encodeField(new Date(8_640_000_000_000_000), earliest)
  expect(show(bytes)).toBe(show(hex('0000243DF74DE1FF ','01000000 0100000055 01000000 0100000073', '09 0000B88511643D00')))
  const decoded = decodeState(bytes)
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  expect((decoded.value.units[0].fields.s as Date).getTime()).toBe(8_640_000_000_000_000)
})

test('Map and Set entries keep insertion order', () => {
  const map = new Map<string, number>([['z', 1], ['a', 2]])
  const set = new Set(['z', 'a'])
  expect(show(encodeField([map, set]))).toBe(show(hex(ONE_FIELD,
    '06 02000000',
    '0B 02000000  05 01000000 7A  04 000000000000F03F  05 01000000 61  04 0000000000000040',
    '0C 02000000  05 01000000 7A  05 01000000 61')))
})

test('undefined is distinct from absent, for properties and for array elements', () => {
  // eslint-disable-next-line no-sparse-arrays
  const holeThenUndefined = [, undefined]
  expect(show(encodeField([{ a: undefined }, {}, holeThenUndefined]))).toBe(show(hex(ONE_FIELD,
    '06 03000000',
    '07 01000000  01000000 61  00',
    '07 00000000',
    '06 02000000  0F  00')))
})

test('an alias is emitted once and referenced by its first path; equal but distinct objects stay distinct', () => {
  const shared = { v: 1 }
  const root = { p: shared, q: shared, r: { v: 1 } }
  const result = encodeState([{ unitId: 'U', root, fields: authoritative(root) }], { utc0Ms: 0 })
  if (!result.ok) throw new Error(result.refusal.detail)
  expect(show(result.value.bytes)).toBe(show(hex('0000000000000000 01000000 0100000055 03000000',
    '01000000 70  07 01000000  01000000 76  04 000000000000F03F',
    '01000000 71  10 0100000055  01000000  00 01000000 70',
    '01000000 72  07 01000000  01000000 76  04 000000000000F03F')))
  expect(result.value.aliases).toEqual([{ at: { unitId: 'U', path: [{ kind: 'key', key: 'q' }] }, target: { unitId: 'U', path: [{ kind: 'key', key: 'p' }] } }])
  const decoded = decodeState(result.value.bytes)
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  const fields = decoded.value.units[0].fields
  expect(fields.p).toBe(fields.q)
  expect(fields.r).not.toBe(fields.p)
  expect(fields.r).toEqual(fields.p)
})

test('another unit\'s object is refused unless the holder declares a link to its owner', () => {
  const frame = { gps: 1 }
  const owner: SnapshotUnit = { unitId: 'A', root: { x: frame }, fields: { x: 'authoritative' } }
  const holder = (links?: SnapshotUnit['links']): SnapshotUnit => ({ unitId: 'B', root: { y: { frame } }, fields: { y: 'authoritative' }, links })
  expect(refusalOf([owner, holder()])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, holder([{ path: ['y'], owner: 'C' }])])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, holder([{ path: ['z'], owner: 'A' }])])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, { unitId: 'A', root: {}, fields: {} }])).toBe('OWNERSHIP_AMBIGUOUS')
  const linked = encodeState([owner, holder([{ path: ['y'], owner: 'A' }])], { utc0Ms: 0 })
  if (!linked.ok) throw new Error(linked.refusal.detail)
  expect(show(linked.value.bytes)).toBe(show(hex('0000000000000000 02000000',
    '0100000041 01000000  01000000 78  07 01000000  03000000 677073  04 000000000000F03F',
    '0100000042 01000000  01000000 79  07 01000000  05000000 6672616D65  10 0100000041  01000000  00 01000000 78')))
})

test('values with no exact encoding are refused, never approximated', () => {
  class Unregistered { v = 1 }
  class Registered { v = 1 }
  const refusal = (value: unknown, classes: ClassRegistration[] = []) => refusalOf([{ unitId: 'U', root: { s: value }, fields: { s: 'authoritative' } }], classes)
  expect(refusal(() => 1)).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(Symbol('s'))).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(1n)).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(new Unregistered())).toBe('UNCLASSIFIED_VALUE')
  expect(refusal({ get v() { return 1 } })).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(Object.freeze({ v: 1 }))).toBe('UNCLASSIFIED_VALUE')
  expect(refusal({ [Symbol('k')]: 1 })).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(Object.assign([1], { extra: 2 }))).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(Object.assign(new Map(), { extra: 2 }))).toBe('UNCLASSIFIED_VALUE')
  expect(refusal('\uD800')).toBe('UNCLASSIFIED_VALUE')
  expect(refusal(new Registered(), [{ name: 'Registered', prototype: Registered.prototype }])).toBe('accepted')
})

test('unclassified root properties and aliases into unserialized fields are refused', () => {
  const profile = { id: 'p' }
  const peer = { name: 'peer' }
  expect(refusalOf([{ unitId: 'U', root: { s: 1, extra: 2 }, fields: { s: 'authoritative' } }])).toBe('UNCLASSIFIED_PROPERTY')
  expect(refusalOf([{ unitId: 'U', root: { config: profile, s: { profile } }, fields: { config: 'config', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'U', root: { seen: profile, s: [profile] }, fields: { seen: 'observer', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'P', root: peer, fields: { name: 'authoritative' } }, { unitId: 'U', root: { s: peer }, fields: { s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  // A link field is re-bound by link() on restore, so sharing with it is allowed; config and link fields are not encoded.
  const linked = encodeState([{ unitId: 'U', root: { peer: profile, s: profile, config: { big: 1 } }, fields: { peer: 'link', s: 'authoritative', config: 'config' } }], { utc0Ms: 0 })
  if (!linked.ok) throw new Error(linked.refusal.detail)
  expect(show(linked.value.bytes)).toBe(show(hex(ONE_FIELD, '07 01000000  02000000 6964  05 01000000 70')))
})

test('the envelope header is written in its fixed version-1 field order, whatever order it is given in', () => {
  const body: SnapshotHeaderBody = {
    format: 'aerolink.fms.snapshot', formatVersion: 1, runId: 'r1', frame: 4, coverage: 'partial (kernel-only)',
    units: [{ unitId: 'K', schema: 'kernel', schemaVersion: 1 }],
    configuration: [{ unitId: 'FMS1', profileId: 'cma', profileVersion: '1', equipment: ['gps'], seeds: [7], frozenParametersHash: 'ff', rtcOffsetsMs: [0] }],
    contentRefs: [{ kind: 'userDatabase', contentHash: 'ab' }],
    environment: { bundleHash: 'h', runtimeKind: 'node', runtimeName: 'node', runtimeVersion: '24', engine: 'v8', os: 'w', platform: 'win32', arch: 'x64' },
  }
  const expected = hex('07 09000000',
    s('format'), '05', s('aerolink.fms.snapshot'),
    s('formatVersion'), '04 000000000000F03F',
    s('runId'), '05', s('r1'),
    s('frame'), '04 0000000000001040',
    s('coverage'), '05', s('partial (kernel-only)'),
    s('units'), '06 01000000 07 03000000', s('unitId'), '05', s('K'), s('schema'), '05', s('kernel'), s('schemaVersion'), '04 000000000000F03F',
    s('configuration'), '06 01000000 07 07000000', s('unitId'), '05', s('FMS1'), s('profileId'), '05', s('cma'), s('profileVersion'), '05', s('1'),
    s('equipment'), '06 01000000 05', s('gps'), s('seeds'), '06 01000000 04 0000000000001C40', s('frozenParametersHash'), '05', s('ff'),
    s('rtcOffsetsMs'), '06 01000000 04 0000000000000000',
    s('contentRefs'), '06 01000000 07 02000000', s('kind'), '05', s('userDatabase'), s('contentHash'), '05', s('ab'),
    s('environment'), '07 08000000', s('bundleHash'), '05', s('h'), s('runtimeKind'), '05', s('node'), s('runtimeName'), '05', s('node'),
    s('runtimeVersion'), '05', s('24'), s('engine'), '05', s('v8'), s('os'), '05', s('w'), s('platform'), '05', s('win32'), s('arch'), '05', s('x64'))
  const reversed = <T extends object>(record: T): T => Object.fromEntries(Object.entries(record).reverse()) as T
  const scrambled = reversed({ ...body, environment: reversed(body.environment), configuration: body.configuration.map(reversed), units: body.units.map(reversed) })
  for (const given of [body, scrambled]) {
    const bytes = encodeHeaderBody(given)
    if (!bytes.ok) throw new Error(bytes.refusal.detail)
    expect(show(bytes.value)).toBe(show(expected))
  }
  expect(SNAPSHOT_HEADER_FIELD_ORDER).toEqual(['format', 'formatVersion', 'runId', 'frame', 'coverage', 'units', 'configuration', 'contentRefs', 'environment', 'sha256'])
  // The environment record carries no host name and no path.
  const withHost = encodeHeaderBody({ ...body, environment: { ...body.environment, hostname: 'bench-pc' } as SnapshotHeaderBody['environment'] })
  expect(withHost.ok ? 'accepted' : withHost.refusal.code).toBe('UNCLASSIFIED_PROPERTY')
  const withPath = encodeHeaderBody({ ...body, environment: { ...body.environment, os: 'C:\\Users\\someone' } })
  expect(withPath.ok ? 'accepted' : withPath.refusal.code).toBe('UNCLASSIFIED_VALUE')
})

test('decode reproduces the state exactly: identity, prototypes, order, buffer bytes and every typed value', () => {
  class Point { lat = 0; lon = 0 }
  const classes = [{ name: 'Point', prototype: Point.prototype }]
  const utc0 = 1_700_000_000_000
  const shared = { v: 1 }
  const key = { id: 1 }
  const buffer = new ArrayBuffer(16)
  new Uint8Array(buffer).set([1, 0, 0, 0, 0, 0, 0xf8, 0x7f, 0, 0, 0, 0, 0, 0, 0xf8, 0xff]) // two distinct NaN payloads
  const nullProto = Object.create(null) as Record<string, unknown>
  nullProto.k = 'v'
  const protoKey = {}
  Object.defineProperty(protoKey, '__proto__', { value: 7, writable: true, enumerable: true, configurable: true })
  const cyclic: Record<string, unknown> = { name: 'c' }
  cyclic.self = cyclic
  // eslint-disable-next-line no-sparse-arrays
  const sparse = [shared, , undefined, -0, NaN, Infinity, '\uFEFFlead', 'é😀']
  const root = {
    a: shared, b: sparse, c: new Map<unknown, unknown>([[key, shared], ['k', key]]), d: new Set<unknown>([key, 2]),
    e: { 10: 'ten', 2: 'two', z: 'z', a: 'a' }, f: new Float64Array(buffer, 0, 2), g: new Uint8Array(buffer, 8, 8), h: new DataView(buffer, 4, 4),
    i: nullProto, j: protoKey, k: cyclic, l: [new Date(utc0 + 5), new Date(Number.NaN), new Date(-8_640_000_000_000_000)],
    m: new Point(), n: Object.assign(new Point(), { lat: 1.5 }), o: new Date(utc0),
  }
  const first = encodeState([{ unitId: 'U', root, fields: authoritative(root) }], { utc0Ms: utc0, classes })
  if (!first.ok) throw new Error(first.refusal.detail)
  const decoded = decodeState(first.value.bytes, { classes })
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  expect(decoded.value.utc0Ms).toBe(utc0)
  const d = decoded.value.units[0].fields as typeof root
  const again = encodeState([{ unitId: 'U', root: d, fields: authoritative(d) }], { utc0Ms: decoded.value.utc0Ms, classes })
  if (!again.ok) throw new Error(again.refusal.detail)
  expect(show(again.value.bytes)).toBe(show(first.value.bytes))

  expect(Object.keys(d)).toEqual(Object.keys(root))
  expect(d.b[0]).toBe(d.a)
  expect(d.c.get([...d.c.keys()][0])).toBe(d.a)
  expect(d.c.get('k')).toBe([...d.d][0])
  expect(1 in d.b).toBe(false)
  expect(2 in d.b).toBe(true)
  expect(Object.is(d.b[3], -0)).toBe(true)
  expect(d.b[6]).toBe('\uFEFFlead')
  expect(d.b[7]).toBe('é😀')
  expect(Object.keys(d.e)).toEqual(['2', '10', 'z', 'a'])
  expect(d.f.buffer).toBe(d.g.buffer)
  expect(d.h.buffer).toBe(d.f.buffer)
  expect(show(new Uint8Array(d.f.buffer))).toBe(show(new Uint8Array(buffer)))
  expect([d.f.byteOffset, d.f.length, d.g.byteOffset, d.g.length, d.h.byteOffset, d.h.byteLength]).toEqual([0, 2, 8, 8, 4, 4])
  expect(Object.getPrototypeOf(d.i)).toBeNull()
  expect(Object.hasOwn(d.j, '__proto__')).toBe(true)
  expect(Object.getPrototypeOf(d.j)).toBe(Object.prototype)
  expect(d.k.self).toBe(d.k)
  expect(d.l.map(date => date.getTime())).toEqual([utc0 + 5, Number.NaN, -8_640_000_000_000_000])
  expect(d.m).toBeInstanceOf(Point)
  expect(d.n.lat).toBe(1.5)
})

test('decode refuses bytes that are not the canonical encoding', () => {
  const code = (...parts: string[]) => { const result = decodeState(hex(...parts)); return result.ok ? 'accepted' : result.refusal.code }
  expect(code(ONE_FIELD, '07 02000000  01000000 62 01  01000000 31 01')).toBe('NON_CANONICAL') // "b" before "1"
  expect(code(ONE_FIELD, '07 02000000  01000000 61 01  01000000 61 01')).toBe('NON_CANONICAL') // duplicate key
  expect(code(ONE_FIELD, '01', '00')).toBe('NON_CANONICAL') // trailing byte
  expect(code(ONE_FIELD, '04 0000')).toBe('NON_CANONICAL') // truncated
  expect(code(ONE_FIELD, '0F')).toBe('NON_CANONICAL') // a hole outside an array
  expect(code(ONE_FIELD, '05 01000000 FF')).toBe('NON_CANONICAL') // invalid UTF-8
  expect(code(ONE_FIELD, '0B 02000000  05 01000000 61 01  05 01000000 61 01')).toBe('NON_CANONICAL') // duplicate Map key
  expect(code(ONE_FIELD, '10 0100000055 01000000 00 01000000 78')).toBe('REFERENCE_UNRESOLVED')
  expect(code(ONE_FIELD, '01')).toBe('accepted')
})

test('SHA-256 matches the FIPS 180-4 examples, including input viewed inside a larger buffer', async () => {
  const ascii = (text: string) => new TextEncoder().encode(text)
  const vectors: [Uint8Array, string][] = [
    [ascii(''), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    [ascii('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    [ascii('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'), '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1'],
    [ascii('xxabcxx').subarray(2, 5), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
  ]
  for (const [input, expected] of vectors) expect(await sha256(input)).toEqual({ ok: true, value: expected })
})

test('without WebCrypto, hashing and sealing refuse INTEGRITY_UNAVAILABLE instead of skipping the check', async () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'crypto')
  expect(original?.configurable).toBe(true)
  const body: SnapshotHeaderBody = {
    format: 'aerolink.fms.snapshot', formatVersion: 1, runId: 'r', frame: 0, coverage: 'partial (kernel-only)', units: [], configuration: [], contentRefs: [],
    environment: { bundleHash: 'h', runtimeKind: 'node', runtimeName: 'node', runtimeVersion: '24', engine: 'v8', os: 'w', platform: 'win32', arch: 'x64' },
  }
  for (const replacement of [undefined, {}]) {
    Object.defineProperty(globalThis, 'crypto', { configurable: true, get: () => replacement })
    try {
      const hashed = await sha256(new Uint8Array([1]))
      expect(hashed.ok ? 'accepted' : hashed.refusal.code).toBe('INTEGRITY_UNAVAILABLE')
      const sealed = await sealSnapshot(body, new Uint8Array([1]))
      expect(sealed.ok ? 'accepted' : sealed.refusal.code).toBe('INTEGRITY_UNAVAILABLE')
    } finally {
      Object.defineProperty(globalThis, 'crypto', original!)
    }
  }
})

test('the seal covers the header, so the coverage label cannot change without changing sha256', async () => {
  const body: SnapshotHeaderBody = {
    format: 'aerolink.fms.snapshot', formatVersion: 1, runId: 'r', frame: 3, coverage: 'partial (kernel-only)', units: [], configuration: [], contentRefs: [],
    environment: { bundleHash: 'h', runtimeKind: 'node', runtimeName: 'node', runtimeVersion: '24', engine: 'v8', os: 'w', platform: 'win32', arch: 'x64' },
  }
  const state = hex(ONE_FIELD, '01')
  const partial = await sealSnapshot(body, state)
  const complete = await sealSnapshot({ ...body, coverage: 'complete' }, state)
  if (!partial.ok || !complete.ok) throw new Error('seal refused')
  const bodyBytes = encodeHeaderBody(body)
  if (!bodyBytes.ok) throw new Error(bodyBytes.refusal.detail)
  const length = Buffer.alloc(4); length.writeUInt32LE(bodyBytes.value.length)
  expect(partial.value.header.sha256).toBe(createHash('sha256').update(length).update(bodyBytes.value).update(state).digest('hex'))
  expect(complete.value.header.sha256).not.toBe(partial.value.header.sha256)
  expect(Object.keys(partial.value.header)).toEqual(SNAPSHOT_HEADER_FIELD_ORDER)
  expect(show(partial.value.state)).toBe(show(state))
})
