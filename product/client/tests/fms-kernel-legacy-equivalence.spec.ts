import { expect, logicTest as test } from './isolated-client-test'
import { CENSUS_RUNS, runCensus, type CensusMode } from './fixtures/fms-kernel-census'
import { GOLDEN_DIGESTS } from './fixtures/fms-kernel-golden-digests'

// #1517 I1a: the kernel and the legacy plant adapter leave every simulation trace bit-identical to the code before
// them. The golden digests come from a reference arm with no kernel change, through the frozen encoder; nothing here
// computes an expected value. These cases are the enforced golden entries (the fixture's header says which entries are
// authoritative): the KBTV advisory approach on runHeadless's composition at 1x and on the bench's at its 4x pacing
// with the bench's reads, and every scripted dual session, so the freeze INTEGRATE, the controls at the resting points
// and an FMS failure frozen and flying are all guarded. Each pull request also compares the whole census, both arms.
const cases: [string, CensusMode][] = [
  ['kbtv-rnav15-advisory/single', { rate: 1, rendered: false, digest: true }],
  ['kbtv-rnav15-advisory/dual', { rate: 4, rendered: true, digest: true }],
  ['session d5-rows/dual', { rate: 4, rendered: true, digest: true }],
  ['session fms-fail-frozen-and-flying/dual', { rate: 1, rendered: false, digest: true }],
  ['session moving-waypoint-exec/dual', { rate: 1, rendered: false, digest: true }],
  ['session legs-shrink-next-lsk/dual', { rate: 4, rendered: true, digest: true }],
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
