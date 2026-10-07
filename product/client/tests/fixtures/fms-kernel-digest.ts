// The frozen state-digest encoder of #1517 I1-0 (#1502 D7 9.3, D11). It observes the simulation from outside, as the
// census and the legacy-equivalence spec do: nothing in product code reads it before I1c, and later I1 pull requests
// must leave this file unchanged. Its frame digest D_j is taken when instant F_j closes, i.e. immediately before the
// INTEGRATE event at F_j (the plant step that leads into F_{j+1}).
//
// Encoding (schema v1):
// - Every value starts with a one-byte tag. Numbers are their IEEE-754 binary64 bit pattern, little-endian; -0 keeps
//   its sign bit (it is distinct from +0) and every NaN is written as the canonical quiet NaN 0x7FF8000000000000.
// - Strings are a u32 little-endian byte length followed by their UTF-8 bytes. Absent and null values carry tags.
// - The frame's top level is a fixed schema (frameBytes below). Records nested in it (bus outputs, air data, guidance)
//   are written with their own keys in code-point order, so the order fields were created in does not matter.
//   Arrays, Map and Set entries keep their order.
// - Per frame: xxHash64 (seed 0) of the frame's bytes. Per run: SHA-256 over every frame's bytes in order.
//
// The digest covers observable state only: the bus outputs and air data, the CDU screen, lamps and brightness, the
// aircraft, true position, physical altitude and wind of each computer, and each flight simulator's mode and guidance
// state. Complete state comes with I2's snapshots.

import { aircraftData, fmsOutputs } from '../../src/fmsCdu/efis'
import type { FlightSimulator } from '../../src/fmsCdu/flight'
import type { ScriptedFms } from '../../src/fmsCdu/scriptedFms'

export const DIGEST_SCHEMA = 'aerolink.fms-state-digest.v1'

// ------------------------------------------------------------------ xxHash64 (32-bit halves, no BigInt per lane)

const P1H = 0x9e3779b1, P1L = 0x85ebca87
const P2H = 0xc2b2ae3d, P2L = 0x27d4eb4f
const P3H = 0x165667b1, P3L = 0x9e3779f9
const P4H = 0x85ebca77, P4L = 0xc2b2ae63
const P5H = 0x27d4eb2f, P5L = 0x165667c5
const MASK64 = (1n << 64n) - 1n

/** The high half of the last 64-bit result; each helper returns the low half. */
let RH = 0

function mul(ah: number, al: number, bh: number, bl: number) {
  const a0 = al & 0xffff, a1 = al >>> 16, b0 = bl & 0xffff, b1 = bl >>> 16
  const p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1
  const mid = (p00 >>> 16) + (p01 & 0xffff) + (p10 & 0xffff)
  const lo = (((mid & 0xffff) << 16) | (p00 & 0xffff)) >>> 0
  const hi = p11 + (p01 >>> 16) + (p10 >>> 16) + (mid >>> 16)
  RH = (hi + Math.imul(ah, bl) + Math.imul(al, bh)) >>> 0
  return lo
}

function add(ah: number, al: number, bh: number, bl: number) {
  const sum = al + bl
  RH = (ah + bh + (sum > 0xffffffff ? 1 : 0)) >>> 0
  return sum >>> 0
}

/** Rotate left by 1..31. */
function rotl(h: number, l: number, r: number) {
  RH = ((h << r) | (l >>> (32 - r))) >>> 0
  return ((l << r) | (h >>> (32 - r))) >>> 0
}

/** round(acc, input) = rotl(acc + input * P2, 31) * P1. */
function round(acch: number, accl: number, inh: number, inl: number) {
  let l = mul(inh, inl, P2H, P2L), h = RH
  l = add(acch, accl, h, l); h = RH
  l = rotl(h, l, 31); h = RH
  return mul(h, l, P1H, P1L)
}

const split = (value: bigint) => [Number((value >> 32n) & 0xffffffffn), Number(value & 0xffffffffn)] as const

/** XXH64 of `bytes` (the reference algorithm, xxHash 0.8). */
export function xxh64(bytes: Uint8Array, seed: bigint = 0n): bigint {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  const length = bytes.length
  let p = 0, h: number, l: number
  if (length >= 32) {
    let [v1h, v1l] = split((seed + 0x9e3779b185ebca87n + 0xc2b2ae3d27d4eb4fn) & MASK64)
    let [v2h, v2l] = split((seed + 0xc2b2ae3d27d4eb4fn) & MASK64)
    let [v3h, v3l] = split(seed & MASK64)
    let [v4h, v4l] = split((seed - 0x9e3779b185ebca87n) & MASK64)
    while (p + 32 <= length) {
      v1l = round(v1h, v1l, view.getUint32(p + 4, true), view.getUint32(p, true)); v1h = RH
      v2l = round(v2h, v2l, view.getUint32(p + 12, true), view.getUint32(p + 8, true)); v2h = RH
      v3l = round(v3h, v3l, view.getUint32(p + 20, true), view.getUint32(p + 16, true)); v3h = RH
      v4l = round(v4h, v4l, view.getUint32(p + 28, true), view.getUint32(p + 24, true)); v4h = RH
      p += 32
    }
    l = rotl(v1h, v1l, 1); h = RH
    let tl = rotl(v2h, v2l, 7), th = RH
    l = add(h, l, th, tl); h = RH
    tl = rotl(v3h, v3l, 12); th = RH
    l = add(h, l, th, tl); h = RH
    tl = rotl(v4h, v4l, 18); th = RH
    l = add(h, l, th, tl); h = RH
    for (const [vh, vl] of [[v1h, v1l], [v2h, v2l], [v3h, v3l], [v4h, v4l]]) {
      // mergeRound: (acc ^ round(0, val)) * P1 + P4
      tl = round(0, 0, vh, vl); th = RH
      h = (h ^ th) >>> 0; l = (l ^ tl) >>> 0
      l = mul(h, l, P1H, P1L); h = RH
      l = add(h, l, P4H, P4L); h = RH
    }
  } else {
    const [sh, sl] = split(seed & MASK64)
    l = add(sh, sl, P5H, P5L); h = RH
  }
  l = add(h, l, Math.floor(length / 0x100000000) >>> 0, length >>> 0); h = RH
  while (p + 8 <= length) {
    const kl = round(0, 0, view.getUint32(p + 4, true), view.getUint32(p, true)), kh = RH
    h = (h ^ kh) >>> 0; l = (l ^ kl) >>> 0
    l = rotl(h, l, 27); h = RH
    l = mul(h, l, P1H, P1L); h = RH
    l = add(h, l, P4H, P4L); h = RH
    p += 8
  }
  if (p + 4 <= length) {
    const kl = mul(0, view.getUint32(p, true), P1H, P1L), kh = RH
    h = (h ^ kh) >>> 0; l = (l ^ kl) >>> 0
    l = rotl(h, l, 23); h = RH
    l = mul(h, l, P2H, P2L); h = RH
    l = add(h, l, P3H, P3L); h = RH
    p += 4
  }
  while (p < length) {
    const kl = mul(0, bytes[p], P5H, P5L), kh = RH
    h = (h ^ kh) >>> 0; l = (l ^ kl) >>> 0
    l = rotl(h, l, 11); h = RH
    l = mul(h, l, P1H, P1L); h = RH
    p += 1
  }
  // Avalanche.
  l = (l ^ (h >>> 1)) >>> 0
  l = mul(h, l, P2H, P2L); h = RH
  l = (l ^ ((l >>> 29) | (h << 3))) >>> 0; h = (h ^ (h >>> 29)) >>> 0
  l = mul(h, l, P3H, P3L); h = RH
  l = (l ^ h) >>> 0
  return (BigInt(h) << 32n) | BigInt(l)
}

export const hex64 = (value: bigint) => value.toString(16).padStart(16, '0')

// ------------------------------------------------------------------ SHA-256 (incremental, FIPS 180-4)

const K256 = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
])

/** SHA-256 that takes its input in pieces, so a run's bytes never have to be held at once. */
export class Sha256 {
  private readonly state = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19])
  private readonly block = new Uint8Array(64)
  private readonly w = new Uint32Array(64)
  private filled = 0
  private total = 0
  private done = false

  update(bytes: Uint8Array) {
    if (this.done) throw new Error('Sha256: update after digest')
    let p = 0
    this.total += bytes.length
    while (p < bytes.length) {
      const take = Math.min(64 - this.filled, bytes.length - p)
      this.block.set(bytes.subarray(p, p + take), this.filled)
      this.filled += take; p += take
      if (this.filled === 64) { this.compress(); this.filled = 0 }
    }
    return this
  }

  hex() {
    if (!this.done) {
      const bits = this.total * 8
      this.block[this.filled++] = 0x80
      if (this.filled > 56) { this.block.fill(0, this.filled); this.compress(); this.filled = 0 }
      this.block.fill(0, this.filled, 56)
      const view = new DataView(this.block.buffer)
      view.setUint32(56, Math.floor(bits / 0x100000000)); view.setUint32(60, bits >>> 0)
      this.compress()
      this.done = true
    }
    return Array.from(this.state, word => word.toString(16).padStart(8, '0')).join('')
  }

  private compress() {
    const w = this.w, b = this.block, s = this.state
    for (let i = 0; i < 16; i += 1) w[i] = (b[4 * i] << 24) | (b[4 * i + 1] << 16) | (b[4 * i + 2] << 8) | b[4 * i + 3]
    for (let i = 16; i < 64; i += 1) {
      const x = w[i - 15], y = w[i - 2]
      const s0 = ((x >>> 7) | (x << 25)) ^ ((x >>> 18) | (x << 14)) ^ (x >>> 3)
      const s1 = ((y >>> 17) | (y << 15)) ^ ((y >>> 19) | (y << 13)) ^ (y >>> 10)
      w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0
    }
    let a = s[0], bb = s[1], c = s[2], d = s[3], e = s[4], f = s[5], g = s[6], h = s[7]
    for (let i = 0; i < 64; i += 1) {
      const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7))
      const t1 = (h + S1 + ((e & f) ^ (~e & g)) + K256[i] + w[i]) | 0
      const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10))
      const t2 = (S0 + ((a & bb) ^ (a & c) ^ (bb & c))) | 0
      h = g; g = f; f = e; e = (d + t1) | 0; d = c; c = bb; bb = a; a = (t1 + t2) | 0
    }
    s[0] += a; s[1] += bb; s[2] += c; s[3] += d; s[4] += e; s[5] += f; s[6] += g; s[7] += h
  }
}

// ------------------------------------------------------------------ canonical bytes

export const TAG = {
  undefined: 0, null: 1, false: 2, true: 3, number: 4, string: 5, array: 6, object: 7, date: 8, map: 9, set: 10, threw: 11,
} as const

const utf8 = new TextEncoder()

/** A growable little-endian byte buffer with the encoder's primitive writes. */
export class CanonicalWriter {
  private bytes = new Uint8Array(16384)
  private view = new DataView(this.bytes.buffer)
  length = 0

  private room(n: number) {
    if (this.length + n <= this.bytes.length) return
    let size = this.bytes.length * 2
    while (size < this.length + n) size *= 2
    const next = new Uint8Array(size)
    next.set(this.bytes.subarray(0, this.length))
    this.bytes = next; this.view = new DataView(next.buffer)
  }

  u8(value: number) { this.room(1); this.bytes[this.length++] = value }
  u32(value: number) {
    if (!Number.isInteger(value) || value < 0 || value > 0xffffffff) throw new Error(`CanonicalWriter.u32: ${value}`)
    this.room(4); this.view.setUint32(this.length, value, true); this.length += 4
  }
  /** binary64, -0 distinct, NaN canonicalised. */
  f64(value: number) {
    this.room(8)
    if (Number.isNaN(value)) { this.view.setUint32(this.length, 0, true); this.view.setUint32(this.length + 4, 0x7ff80000, true) }
    else this.view.setFloat64(this.length, value, true)
    this.length += 8
  }
  str(value: string) {
    const encoded = utf8.encode(value)
    this.u32(encoded.length); this.room(encoded.length); this.bytes.set(encoded, this.length); this.length += encoded.length
  }
  /** A copy of what has been written. */
  result() { return this.bytes.slice(0, this.length) }
}

/** Writes any plain value with tags: the rule for every record nested in the frame schema. */
export function writeValue(w: CanonicalWriter, value: unknown): void {
  if (value === undefined) { w.u8(TAG.undefined); return }
  if (value === null) { w.u8(TAG.null); return }
  switch (typeof value) {
    case 'boolean': w.u8(value ? TAG.true : TAG.false); return
    case 'number': w.u8(TAG.number); w.f64(value); return
    case 'string': w.u8(TAG.string); w.str(value); return
    case 'object': break
    default: throw new Error(`writeValue: a ${typeof value} is not state`)
  }
  if (Array.isArray(value)) { w.u8(TAG.array); w.u32(value.length); for (const item of value) writeValue(w, item); return }
  if (value instanceof Date) { w.u8(TAG.date); w.f64(value.getTime()); return }
  if (value instanceof Map) { w.u8(TAG.map); w.u32(value.size); for (const [key, item] of value) { writeValue(w, key); writeValue(w, item) } return }
  if (value instanceof Set) { w.u8(TAG.set); w.u32(value.size); for (const item of value) writeValue(w, item); return }
  const keys = Object.keys(value).sort()
  w.u8(TAG.object); w.u32(keys.length)
  for (const key of keys) { w.str(key); writeValue(w, (value as Record<string, unknown>)[key]) }
}

/** A read that may refuse in some states: the refusal is part of the state. */
function writeRead(w: CanonicalWriter, read: () => unknown) {
  let value: unknown
  try { value = read() } catch (error) { w.u8(TAG.threw); w.str(error instanceof Error ? error.message : String(error)); return }
  writeValue(w, value)
}

const COLORS = ['white', 'cyan', 'green', 'magenta', 'amber', 'red']
const SIZES = ['large', 'medium', 'small']

/** The flight simulator's public mode and guidance state, in schema order. */
const FLIGHT_FIELDS: readonly ((sim: FlightSimulator) => unknown)[] = [
  sim => sim.guidance, sim => sim.lateralMode, sim => sim.lnavIsArmed, sim => sim.verticalMode, sim => sim.approachMode,
  sim => sim.axisModes, sim => sim.axisArmed, sim => sim.selectedHeading, sim => sim.headingHeld, sim => sim.selectedAltitude,
  sim => sim.selectedSpeed, sim => sim.verticalSpeedTarget, sim => sim.altitudeHoldReference, sim => sim.gpsLateralActive,
  sim => sim.verticalFlag, sim => sim.holdProgress, sim => sim.hoverHeight, sim => sim.hoverCaptured, sim => sim.lowHeightCaption,
  sim => sim.inLowSpeedRegime, sim => sim.transitionInProgress, sim => sim.bankAngle, sim => sim.tas,
  sim => sim.modeEvents.length, sim => sim.modeEvents.at(-1),
]

/** What a frame is taken over: the computers and, beside each, the flight simulator it pairs with. */
export type DigestUnits = { readonly computers: readonly ScriptedFms[]; readonly flights: readonly FlightSimulator[] }

/** The frame's canonical bytes (schema v1). Reads only; #1518 made every read here side-effect free. */
export function frameBytes(units: DigestUnits, w = new CanonicalWriter()) {
  if (units.computers.length !== units.flights.length) throw new Error('frameBytes: one flight per computer')
  w.str(DIGEST_SCHEMA)
  w.u32(units.computers.length)
  units.computers.forEach((fms, i) => {
    const sim = units.flights[i]
    w.str(`FMS${i + 1}`)
    writeRead(w, () => fmsOutputs(fms, sim))
    writeRead(w, () => aircraftData(fms, sim))
    const screen = fms.screen()
    w.u32(screen.length)
    for (const row of screen) {
      w.u32(row.length)
      for (const cell of row) {
        const color = COLORS.indexOf(cell.color), size = SIZES.indexOf(cell.size)
        if (color < 0 || size < 0) throw new Error(`frameBytes: unknown cell style ${cell.color}/${cell.size}`)
        w.str(cell.ch); w.u8(color); w.u8(size); w.u8(cell.inverse ? 1 : 0)
      }
    }
    const lamps = [...fms.lamps()]
    w.u32(lamps.length); for (const lamp of lamps) w.str(lamp)
    w.f64(fms.brightness())
    // The modelled aircraft is a private record: read as data, never through an accessor.
    writeValue(w, (fms as unknown as { aircraft: unknown }).aircraft)
    writeValue(w, fms.truePosition)
    w.f64(fms.physicalAltitude)
    writeValue(w, fms.wind)
    w.u32(FLIGHT_FIELDS.length)
    for (const field of FLIGHT_FIELDS) writeRead(w, () => field(sim))
  })
  return w.result()
}

/** One frame's digest: xxHash64, seed 0, as 16 hex digits. */
export const frameDigest = (bytes: Uint8Array) => hex64(xxh64(bytes))
