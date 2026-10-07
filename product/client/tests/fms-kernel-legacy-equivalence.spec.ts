import { expect, logicTest as test } from './isolated-client-test'
import { CENSUS_RUNS, runCensus, type CensusMode } from './fixtures/fms-kernel-census'
import { GOLDEN_DIGESTS } from './fixtures/fms-kernel-golden-digests'

// #1517 I1a: the kernel and the legacy plant adapter leave every simulation trace bit-identical to the code before
// them. The golden digests were generated from the reference arm (the I1-0 commit, before any kernel code) by the
// frozen encoder; nothing here computes an expected value. The fast lane takes the KBTV advisory approach on runHeadless's
// composition at 1x and on the bench's composition at its 4x pacing with the bench's reads after each callback; the
// pull request runs the whole census, both arms, in Chromium.
const cases: [string, CensusMode][] = [
  ['kbtv-rnav15-advisory/single', { rate: 1, rendered: false, digest: true }],
  ['kbtv-rnav15-advisory/dual', { rate: 4, rendered: true, digest: true }],
]

for (const [id, mode] of cases) {
  test(`every frame of ${id} at ${mode.rate}x${mode.rendered ? ', rendered,' : ''} matches the reference arm's digests`, () => {
    test.setTimeout(5 * 60_000)
    const run = CENSUS_RUNS.find(entry => entry.id === id)!
    const golden = GOLDEN_DIGESTS[id]
    const result = runCensus(run, mode)
    expect(result.frames.length).toBe(golden.frames)
    expect(result.frames.filter((_, i) => i % 100 === 0)).toEqual(golden.every100th)
    expect(result.end).toBe(golden.end)
    expect({ outcome: result.outcome, endedAfter: result.endedAfter }).toEqual({ outcome: golden.outcome, endedAfter: golden.endedAfter })
    expect(result.sha256).toBe(golden.sha256)
  })
}
