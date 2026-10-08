import { expect, logicTest as test } from './isolated-client-test'
import { createHash } from 'node:crypto'
import { CanonicalWriter, Sha256, writeValue, xxh64 } from './fixtures/fms-kernel-digest'
import { CENSUS_RUNS, runCensus } from './fixtures/fms-kernel-census'

// #1517 I1-0: the frozen state-digest encoder. Every later I1 change is judged by these bytes and hashes, so their
// expected values come from outside the encoder: the xxHash project's published sanity vectors, the FIPS 180-4
// example vectors, and bytes written out by hand from IEEE 754 and UTF-8.

/** The xxHash sanity buffer (xxHash 0.8.2, cli/xsum_sanity_check.c XSUM_fillTestBuffer). */
const sanityBuffer = (length: number) => {
  const buffer = new Uint8Array(length)
  let generator = 2654435761n
  for (let i = 0; i < length; i += 1) {
    buffer[i] = Number(generator >> 56n)
    generator = (generator * 11400714785074694797n) & ((1n << 64n) - 1n)
  }
  return buffer
}

// Owner of the hash: published XXH64 vectors (xxHash 0.8.2, tests/sanity_test_vectors.h, XSUM_XXH64_testdata) at
// seed 0 and seed PRIME32, over the lengths that reach every path: empty, the byte tail, the 4-byte and 8-byte lanes,
// one 32-byte stripe and many stripes with every tail.
test('xxHash64 matches the published sanity vectors at seed 0 and a non-zero seed', () => {
  const vectors: [number, bigint, bigint][] = [
    [0, 0n, 0xEF46DB3751D8E999n], [0, 0x9E3779B1n, 0xAC75FDA2929B17EFn],
    [1, 0n, 0xE934A84ADB052768n], [1, 0x9E3779B1n, 0x5014607643A9B4C3n],
    [4, 0n, 0x9136A0DCA57457EEn], [4, 0x9E3779B1n, 0xCAAB286BD8E9FDB5n],
    [8, 0n, 0xCDBCF538E71D1348n], [8, 0x9E3779B1n, 0xFE0C047A5353CDACn],
    [14, 0n, 0x8282DCC4994E35C8n], [14, 0x9E3779B1n, 0xC3BD6BF63DEB6DF0n],
    [32, 0n, 0x18B216492BB44B70n], [32, 0x9E3779B1n, 0xB3F33BDF93ADE409n],
    [33, 0n, 0x55C8DC3E578F5B59n], [33, 0x9E3779B1n, 0xE92C292F64BC3071n],
    [100, 0n, 0x4BFE019CD91D9EA4n], [100, 0x9E3779B1n, 0x4853706DC9625CAEn],
    [222, 0n, 0xB641AE8CB691C174n], [222, 0x9E3779B1n, 0x20CB8AB7AE10C14An],
    [4160, 0n, 0xEEE6A4E2AC952A5En], [4160, 0x9E3779B1n, 0xA4CB59E19D6A35F9n],
  ]
  for (const [length, seed, expected] of vectors) expect(xxh64(sanityBuffer(length), seed).toString(16), `length ${length} seed ${seed}`).toBe(expected.toString(16))
})

// Owner of the run hash: FIPS 180-4 examples, fed whole and in pieces that straddle the 64-byte block, and a large
// input checked against Node's own SHA-256.
test('the incremental SHA-256 matches the FIPS 180-4 vectors however its input is split', () => {
  const ascii = (text: string) => new TextEncoder().encode(text)
  const vectors: [Uint8Array, string][] = [
    [ascii(''), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'],
    [ascii('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'],
    [ascii('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'), '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1'],
    [ascii('a'.repeat(1_000_000)), 'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0'],
  ]
  for (const [input, expected] of vectors) {
    expect(new Sha256().update(input).hex()).toBe(expected)
    const pieces = new Sha256()
    for (let p = 0, size = 1; p < input.length; p += size, size = size === 1 ? 63 : size === 63 ? 65 : 1) pieces.update(input.subarray(p, p + size))
    expect(pieces.hex()).toBe(expected)
  }
  const large = sanityBuffer(70_001)
  expect(new Sha256().update(large).hex()).toBe(createHash('sha256').update(large).digest('hex'))
})

// Owner of the canonical bytes: tags, binary64 little-endian with -0 kept and NaN canonicalised, length-prefixed
// UTF-8, and records written in key order. Written out by hand.
test('the canonical encoding keeps -0, canonicalises every NaN and writes records in key order', () => {
  const bytes = (value: unknown) => { const w = new CanonicalWriter(); writeValue(w, value); return [...w.result()] }
  const signallingNaN = new Float64Array(new BigUint64Array([0x7FF0000000000001n]).buffer)[0]
  const negativeNaN = new Float64Array(new BigUint64Array([0xFFF8000000000000n]).buffer)[0]
  expect(bytes(0)).toEqual([4, 0, 0, 0, 0, 0, 0, 0, 0])
  expect(bytes(-0)).toEqual([4, 0, 0, 0, 0, 0, 0, 0, 0x80])
  expect(bytes(1)).toEqual([4, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F])
  expect(bytes(Infinity)).toEqual([4, 0, 0, 0, 0, 0, 0, 0xF0, 0x7F])
  expect(bytes(-Infinity)).toEqual([4, 0, 0, 0, 0, 0, 0, 0xF0, 0xFF])
  for (const nan of [NaN, signallingNaN, negativeNaN]) expect(bytes(nan)).toEqual([4, 0, 0, 0, 0, 0, 0, 0xF8, 0x7F])
  expect(bytes('')).toEqual([5, 0, 0, 0, 0])
  expect(bytes('é')).toEqual([5, 2, 0, 0, 0, 0xC3, 0xA9])
  expect(bytes(null)).toEqual([1])
  expect(bytes(undefined)).toEqual([0])
  expect(bytes([true, false])).toEqual([6, 2, 0, 0, 0, 3, 2])
  expect(bytes({ b: 1, a: true })).toEqual([7, 2, 0, 0, 0, 1, 0, 0, 0, 0x61, 3, 1, 0, 0, 0, 0x62, 4, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F])
  expect(bytes({ b: 1, a: true })).toEqual(bytes({ a: true, b: 1 }))
  expect(bytes(new Date(1))).toEqual([8, 0, 0, 0, 0, 0, 0, 0xF0, 0x3F])
  expect(() => bytes(() => 1)).toThrow(/not state/)
})

// Owner of the digest's independence from what it observes (#1518 made every read pure): a census run gives the same
// outcome, step results and end state with the digest taken at every frame as with no read before the end. The fast lane takes the KBTV
// advisory approach (single) and the scripted sessions; the full census, dual included, is the pull request's evidence.
test('taking the digest at every frame changes no census outcome or step result', () => {
  test.setTimeout(10 * 60_000)
  const fast = CENSUS_RUNS.filter(run => run.id === 'kbtv-rnav15-advisory/single' || run.kind === 'session')
  expect(fast.length).toBe(5)
  for (const run of fast) {
    const on = runCensus(run, { rate: 1, rendered: false, digest: true })
    const off = runCensus(run, { rate: 1, rendered: false, digest: false })
    expect(on.frames.length, run.id).toBeGreaterThan(40)
    expect(off.frames, run.id).toEqual([])
    expect({ outcome: off.outcome, results: off.results, endedAfter: off.endedAfter, end: off.end }, run.id)
      .toEqual({ outcome: on.outcome, results: on.results, endedAfter: on.endedAfter, end: on.end })
  }
})
