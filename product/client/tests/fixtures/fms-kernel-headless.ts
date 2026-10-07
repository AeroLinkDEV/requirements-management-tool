// The headless kernel page (#1517 I1-0): the census in the browser engine the bench runs in, with no bench around it.
// It is the qualified environment for comparing arms (#1502 D7 9.5): both arms run here, in one Chromium build.
import { CENSUS_RUNS, runCensus, type CensusMode } from './fms-kernel-census'

declare global {
  interface Window {
    fmsKernelCensus: { ids: string[]; run(id: string, mode: CensusMode): ReturnType<typeof runCensus> & { ms: number } }
  }
}

window.fmsKernelCensus = {
  ids: CENSUS_RUNS.map(run => run.id),
  run(id, mode) {
    const run = CENSUS_RUNS.find(entry => entry.id === id)
    if (!run) throw new Error(`no census run ${id}`)
    const begin = performance.now()
    const result = runCensus(run, mode)
    return { ...result, ms: performance.now() - begin }
  },
}
document.getElementById('status')!.textContent = 'ready'
