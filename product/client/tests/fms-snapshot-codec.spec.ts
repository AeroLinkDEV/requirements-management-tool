import { expect, logicTest as test } from './isolated-client-test'
import { createHash } from 'node:crypto'
import { decodeState, encodeState, type ClassRegistration, type ContentRegistry, type SnapshotUnit } from '../src/fmsCdu/snapshot/codec'
import { encodeHeaderBody, sealSnapshot, type SnapshotHeaderBody } from '../src/fmsCdu/snapshot/envelope'
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
const refusalOf = (units: SnapshotUnit[], classes: ClassRegistration[] = [], utc0Ms = 0) => {
  const result = encodeState(units, { utc0Ms, classes })
  return result.ok ? 'accepted' : result.refusal.code
}
const fieldRefusal = (value: unknown) => refusalOf([{ unitId: 'U', root: { s: value }, fields: { s: 'authoritative' } }])
const decodeCode = (...parts: string[]) => { const result = decodeState(hex(...parts)); return result.ok ? 'accepted' : result.refusal.code }

/** A header body whose bytes are written out by hand in BODY_FIELDS below. */
const BODY: SnapshotHeaderBody = {
  format: 'aerolink.fms.snapshot', formatVersion: 1, runId: 'r1', frame: 4, coverage: 'partial (kernel-only)',
  units: [{ unitId: 'K', schema: 'kernel', schemaVersion: 1 }],
  configuration: [{ unitId: 'FMS1', profileId: 'cma', profileVersion: '1', equipment: ['gps'], seeds: [7], frozenParametersHash: 'ff', rtcOffsetsMs: [0] }],
  contentRefs: [{ kind: 'userDatabase', contentHash: 'ab' }],
  environment: { bundleHash: 'h', runtimeKind: 'node', runtimeName: 'node', runtimeVersion: '24', engine: 'v8', os: 'w', platform: 'win32', arch: 'x64' },
}
const BODY_FIELDS = [
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
  s('runtimeVersion'), '05', s('24'), s('engine'), '05', s('v8'), s('os'), '05', s('w'), s('platform'), '05', s('win32'), s('arch'), '05', s('x64'),
].join('')

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
  expect(decodeCode(ONE_FIELD, '04 010000000000F87F')).toBe('NON_CANONICAL')
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
  // The earliest UTC0 and the latest Date but one are 17279999999999999 ms apart: odd and beyond 2^53, so binary64
  // arithmetic would round it to ...000 on either side.
  const earliest = -8_640_000_000_000_000
  const bytes = encodeField(new Date(8_639_999_999_999_999), earliest)
  expect(show(bytes)).toBe(show(hex('0000243DF74DE1FF 01000000 0100000055 01000000 0100000073', '09 FFFFB78511643D00')))
  const decoded = decodeState(bytes)
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  expect((decoded.value.units[0].fields.s as Date).getTime()).toBe(8_639_999_999_999_999)
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

test('another unit\'s object is shared only under a declared link to an earlier owner, on encode and on decode', () => {
  const frame = { gps: 1 }
  const owner: SnapshotUnit = { unitId: 'A', root: { x: frame }, fields: { x: 'authoritative' } }
  const holder = (links?: SnapshotUnit['links']): SnapshotUnit => ({ unitId: 'B', root: { y: { frame } }, fields: { y: 'authoritative' }, links })
  expect(refusalOf([owner, holder()])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, holder([{ path: ['y'], owner: 'C' }])])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, holder([{ path: ['z'], owner: 'A' }])])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([owner, { unitId: 'A', root: {}, fields: {} }])).toBe('OWNERSHIP_AMBIGUOUS')
  expect(refusalOf([holder([{ path: ['y'], owner: 'A' }]), owner])).toBe('OWNER_AFTER_HOLDER')
  const linked = encodeState([owner, holder([{ path: ['y'], owner: 'A' }])], { utc0Ms: 0 })
  if (!linked.ok) throw new Error(linked.refusal.detail)
  expect(show(linked.value.bytes)).toBe(show(hex('0000000000000000 02000000',
    '0100000041 01000000  01000000 78  07 01000000  03000000 677073  04 000000000000F03F',
    '0100000042 01000000  01000000 79  07 01000000  05000000 6672616D65  10 0100000041  01000000  00 01000000 78')))
  // Decode applies the same rule: without B's declared link the cross-unit reference refuses.
  const unlinked = decodeState(linked.value.bytes)
  expect(unlinked.ok ? 'accepted' : unlinked.refusal.code).toBe('OWNERSHIP_AMBIGUOUS')
  const decoded = decodeState(linked.value.bytes, { links: { B: [{ path: ['y'], owner: 'A' }] } })
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  expect((decoded.value.units[1].fields.y as { frame: unknown }).frame).toBe(decoded.value.units[0].fields.x)
})

test('values with no exact encoding are refused, never approximated', () => {
  class Unregistered { v = 1 }
  class Registered { v = 1 }
  type Buffers = { new (length: number, options: { maxByteLength: number }): ArrayBuffer }
  const resizable = new (ArrayBuffer as unknown as Buffers)(8, { maxByteLength: 16 })
  const detached = new ArrayBuffer(8)
  ;(detached as unknown as { transfer(): ArrayBuffer }).transfer()
  expect(fieldRefusal(() => 1)).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(Symbol('s'))).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(1n)).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(new Unregistered())).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal({ get v() { return 1 } })).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(Object.freeze({ v: 1 }))).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal({ [Symbol('k')]: 1 })).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(Object.assign([1], { extra: 2 }))).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(Object.assign(new Map(), { extra: 2 }))).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(Object.assign(new Date(0), { extra: 2 }))).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(resizable)).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal(detached)).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal('\uD800')).toBe('UNCLASSIFIED_VALUE')
  expect(fieldRefusal({ ['\uDC00']: 1 })).toBe('UNCLASSIFIED_VALUE')
  expect(refusalOf([{ unitId: 'U', root: { s: new Registered() }, fields: { s: 'authoritative' } }], [{ name: 'Registered', prototype: Registered.prototype }])).toBe('accepted')
})

test('UTC0 must be whole epoch milliseconds of a valid Date', () => {
  for (const utc0 of [1.5, Number.NaN, 8_640_000_000_000_001, -8_640_000_000_000_001]) {
    expect(refusalOf([{ unitId: 'U', root: { s: 1 }, fields: { s: 'authoritative' } }], [], utc0), String(utc0)).toBe('INVALID_UTC0')
  }
  expect(refusalOf([{ unitId: 'U', root: { s: 1 }, fields: { s: 'authoritative' } }], [], -8_640_000_000_000_000)).toBe('accepted')
})

test('a class registry may not name a class twice, register a prototype twice or register a built-in', () => {
  class First { v = 1 }
  class Second { v = 2 }
  const invalid = [
    [{ name: 'K', prototype: First.prototype }, { name: 'K', prototype: Second.prototype }],
    [{ name: 'K', prototype: First.prototype }, { name: 'L', prototype: First.prototype }],
    [{ name: 'M', prototype: Map.prototype }],
  ]
  for (const classes of invalid) {
    expect(refusalOf([{ unitId: 'U', root: { s: new First() }, fields: { s: 'authoritative' } }], classes)).toBe('CLASS_REGISTRY_INVALID')
    const decoded = decodeState(hex(ONE_FIELD, '01'), { classes })
    expect(decoded.ok ? 'accepted' : decoded.refusal.code).toBe('CLASS_REGISTRY_INVALID')
  }
})

test('unclassified root properties refuse, and serialized state may not reach anything restore rebuilds', () => {
  const shared = { id: 'p' }
  expect(refusalOf([{ unitId: 'U', root: { s: 1, extra: 2 }, fields: { s: 'authoritative' } }])).toBe('UNCLASSIFIED_PROPERTY')
  // Through config, observer and derived fields, directly or anywhere inside them.
  expect(refusalOf([{ unitId: 'U', root: { config: shared, s: { shared } }, fields: { config: 'config', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'U', root: { config: { inner: shared }, s: shared }, fields: { config: 'config', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'U', root: { seen: { list: [shared] }, s: { y: shared } }, fields: { seen: 'observer', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'U', root: { cache: new Map([['k', shared]]), s: shared }, fields: { cache: 'derived', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'P', root: { config: { deep: [shared] } }, fields: { config: 'config' } }, { unitId: 'U', root: { s: shared }, fields: { s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  // A unit root is never serialized state.
  const peer = { name: 'peer' }
  expect(refusalOf([{ unitId: 'P', root: peer, fields: { name: 'authoritative' } }, { unitId: 'U', root: { s: peer }, fields: { s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  // A link field holds a unit root, or an object a declared owner emits: never the holder's own state, never an
  // object nobody emits.
  expect(refusalOf([{ unitId: 'U', root: { peer: shared, s: shared }, fields: { peer: 'link', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([{ unitId: 'U', root: { peer: { w: shared }, s: shared }, fields: { peer: 'link', s: 'authoritative' } }])).toBe('UNCLASSIFIED_ALIAS')
  const frame = { gps: 1 }
  const owner: SnapshotUnit = { unitId: 'A', root: { x: frame }, fields: { x: 'authoritative' } }
  expect(refusalOf([owner, { unitId: 'B', root: { sensors: frame }, fields: { sensors: 'link' } }])).toBe('UNCLASSIFIED_ALIAS')
  expect(refusalOf([owner, { unitId: 'B', root: { sensors: frame }, fields: { sensors: 'link' }, links: [{ path: ['sensors'], owner: 'A' }] }])).toBe('accepted')
  expect(refusalOf([{ unitId: 'P', root: peer, fields: { name: 'authoritative' } }, { unitId: 'U', root: { peer, read: () => 1, s: 1 }, fields: { peer: 'link', read: 'link', s: 'authoritative' } }])).toBe('accepted')
  // Config, observer, derived and link fields are not encoded.
  const encoded = encodeState([{ unitId: 'U', root: { config: { big: 1 }, seen: [2], cache: new Set([3]), read: () => 4, s: 'p' }, fields: { config: 'config', seen: 'observer', cache: 'derived', read: 'link', s: 'authoritative' } }], { utc0Ms: 0 })
  if (!encoded.ok) throw new Error(encoded.refusal.detail)
  expect(show(encoded.value.bytes)).toBe(show(hex(ONE_FIELD, '05 01000000 70')))
})

test('registered content is always written as a content reference and resolved through the registry on decode', () => {
  const entry = { ident: 'KBTV', elevation: Object.freeze({ ft: 335 }) }
  const content: ContentRegistry = {
    referenceOf: value => value === entry ? { contentHash: 'c1', ident: 'KBTV', index: 2 } : undefined,
    resolve: reference => reference.contentHash === 'c1' && reference.ident === 'KBTV' && reference.index === 2 ? entry : undefined,
  }
  // Content held by configuration (the navigation database) is still referable from state.
  const root = { database: { entries: [entry] }, s: [entry, entry] }
  const encoded = encodeState([{ unitId: 'U', root, fields: { database: 'config', s: 'authoritative' } }], { utc0Ms: 0, content })
  if (!encoded.ok) throw new Error(encoded.refusal.detail)
  const contentRef = ['12', s('c1'), s('KBTV'), '02000000'].join('')
  expect(show(encoded.value.bytes)).toBe(show(hex(ONE_FIELD, '06 02000000', contentRef, contentRef)))
  const decoded = decodeState(encoded.value.bytes, { content })
  if (!decoded.ok) throw new Error(decoded.refusal.detail)
  expect(decoded.value.units[0].fields.s).toEqual([entry, entry])
  expect((decoded.value.units[0].fields.s as unknown[])[0]).toBe(entry)
  for (const registry of [undefined, { referenceOf: () => undefined, resolve: () => undefined }]) {
    const missing = decodeState(encoded.value.bytes, { content: registry })
    expect(missing.ok ? undefined : missing.refusal).toMatchObject({ code: 'CONTENT_MISSING', contentHash: 'c1' })
  }
})

test('nesting is limited to 256 path steps, at the same point on encode and decode, and never throws', () => {
  const nested = (depth: number) => { let value: unknown = 1; for (let i = 0; i < depth; i += 1) value = [value]; return value }
  const crafted = (depth: number) => hex(ONE_FIELD, '06 01000000'.repeat(depth), '04 000000000000F03F')
  // The number inside 255 arrays sits at step 256 (the field is step 1): the deepest value allowed.
  expect(show(encodeField(nested(255)))).toBe(show(crafted(255)))
  const atLimit = decodeState(crafted(255))
  expect(atLimit.ok).toBe(true)
  expect(fieldRefusal(nested(256))).toBe('NESTING_LIMIT')
  expect(decodeCode(show(crafted(256)))).toBe('NON_CANONICAL')
  // A crafted stream far deeper than any stack, and a deep linked list, refuse instead of throwing.
  expect(decodeCode(show(crafted(200_000)))).toBe('NON_CANONICAL')
  expect(decodeCode(ONE_FIELD, '07 01000000 01000000 6E'.repeat(200_000))).toBe('NON_CANONICAL')
  let list: unknown = null
  for (let i = 0; i < 5_000; i += 1) list = { next: list }
  expect(fieldRefusal(list)).toBe('NESTING_LIMIT')
})

test('the envelope header is written in its fixed version-1 field order, whatever order it is given in', () => {
  const expected = hex('07 09000000', BODY_FIELDS)
  const reversed = <T extends object>(record: T): T => Object.fromEntries(Object.entries(record).reverse()) as T
  const scrambled = reversed({ ...BODY, environment: reversed(BODY.environment), configuration: BODY.configuration.map(reversed), units: BODY.units.map(reversed) })
  for (const given of [BODY, scrambled]) {
    const bytes = encodeHeaderBody(given)
    if (!bytes.ok) throw new Error(bytes.refusal.detail)
    expect(show(bytes.value)).toBe(show(expected))
  }
  // The environment record carries no undeclared field (such as a host name) and no path.
  const withHost = encodeHeaderBody({ ...BODY, environment: { ...BODY.environment, hostname: 'bench-pc' } as SnapshotHeaderBody['environment'] })
  expect(withHost.ok ? 'accepted' : withHost.refusal.code).toBe('UNCLASSIFIED_PROPERTY')
  const withPath = encodeHeaderBody({ ...BODY, environment: { ...BODY.environment, os: 'C:\\Users\\someone' } })
  expect(withPath.ok ? 'accepted' : withPath.refusal.code).toBe('UNCLASSIFIED_VALUE')
  // A header field is read once, as data: an accessor refuses.
  const withAccessor = Object.defineProperty({ ...BODY }, 'frame', { get: () => 4, enumerable: true, configurable: true })
  const accessor = encodeHeaderBody(withAccessor)
  expect(accessor.ok ? 'accepted' : accessor.refusal.code).toBe('UNCLASSIFIED_PROPERTY')
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
  expect(decodeCode(ONE_FIELD, '07 02000000  01000000 62 01  01000000 31 01')).toBe('NON_CANONICAL') // "b" before "1"
  expect(decodeCode(ONE_FIELD, '07 02000000  01000000 61 01  01000000 61 01')).toBe('NON_CANONICAL') // duplicate key
  expect(decodeCode(ONE_FIELD, '01', '00')).toBe('NON_CANONICAL') // trailing byte
  expect(decodeCode(ONE_FIELD, '04 0000')).toBe('NON_CANONICAL') // truncated
  expect(decodeCode(ONE_FIELD, '0F')).toBe('NON_CANONICAL') // a hole outside an array
  expect(decodeCode(ONE_FIELD, '13')).toBe('NON_CANONICAL') // an unknown tag
  expect(decodeCode(ONE_FIELD, '05 01000000 FF')).toBe('NON_CANONICAL') // invalid UTF-8
  expect(decodeCode(ONE_FIELD, '0B 02000000  05 01000000 61 01  05 01000000 61 01')).toBe('NON_CANONICAL') // duplicate Map key
  expect(decodeCode(ONE_FIELD, '0B 01000000  04 0000000000000080 01')).toBe('NON_CANONICAL') // -0 Map key
  expect(decodeCode(ONE_FIELD, '0C 01000000  04 0000000000000080')).toBe('NON_CANONICAL') // -0 Set entry
  expect(decodeCode(ONE_FIELD, '09 0100DCC208B21E00')).toBe('NON_CANONICAL') // a Date at 8.64e15 + 1 ms
  expect(decodeCode('0100DCC208B21E00 00000000')).toBe('NON_CANONICAL') // UTC0 at 8.64e15 + 1 ms
  expect(decodeCode('0000000000000000 02000000  0100000055 00000000  0100000055 00000000')).toBe('OWNERSHIP_AMBIGUOUS') // duplicate unit id
  expect(decodeCode(ONE_FIELD, '10 0100000055 01000000 00 01000000 78')).toBe('REFERENCE_UNRESOLVED')
  expect(decodeCode(ONE_FIELD, '01')).toBe('accepted')
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

test('without a working WebCrypto, hashing and sealing refuse INTEGRITY_UNAVAILABLE instead of skipping the check', async () => {
  const original = Object.getOwnPropertyDescriptor(globalThis, 'crypto')
  expect(original?.configurable).toBe(true)
  const failing = { subtle: { digest: () => Promise.reject(new Error('digest failed')) } }
  for (const replacement of [undefined, {}, failing]) {
    Object.defineProperty(globalThis, 'crypto', { configurable: true, get: () => replacement })
    try {
      const hashed = await sha256(new Uint8Array([1]))
      expect(hashed.ok ? 'accepted' : hashed.refusal.code).toBe('INTEGRITY_UNAVAILABLE')
      const sealed = await sealSnapshot(BODY, new Uint8Array([1]))
      expect(sealed.ok ? 'accepted' : sealed.refusal.code).toBe('INTEGRITY_UNAVAILABLE')
    } finally {
      Object.defineProperty(globalThis, 'crypto', original!)
    }
  }
})

test('the seal covers header and state, copies the state before awaiting, and writes the exact sealed header', async () => {
  const state = hex(ONE_FIELD, '01')
  const original = state.slice()
  const pending = sealSnapshot(BODY, state)
  state.fill(0) // the caller reuses its buffer while the hash is awaited
  const sealed = await pending
  if (!sealed.ok) throw new Error(sealed.refusal.detail)
  const body = hex('07 09000000', BODY_FIELDS)
  const length = Buffer.alloc(4); length.writeUInt32LE(body.length)
  const expectedSha = createHash('sha256').update(length).update(body).update(original).digest('hex')
  expect(sealed.value.header.sha256).toBe(expectedSha)
  expect(show(sealed.value.state)).toBe(show(original))
  expect(show(sealed.value.headerBytes)).toBe(show(hex('07 0A000000', BODY_FIELDS, s('sha256'), '05', s(expectedSha))))
  const complete = await sealSnapshot({ ...BODY, coverage: 'complete' }, original)
  if (!complete.ok) throw new Error(complete.refusal.detail)
  expect(complete.value.header.sha256).not.toBe(expectedSha)
})
