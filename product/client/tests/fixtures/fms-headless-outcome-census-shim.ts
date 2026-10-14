// The #1517 I1d outcome census's stand-in for the scenario module, used only by the census driver's temporary copies of
// the specs that call runHeadless (fms-headless-outcome-census-run.ts). It re-exports the scenario module unchanged
// except runHeadless, which runs on the arm the driver names and records each call:
//
// - AEROLINK_I1D_ARM=single: today's runHeadless, plus a check that the census's single replica ends in the same state.
// - AEROLINK_I1D_ARM=dual: the dual composition (what the switch would return), plus the lockstep comparison.
//
// Each call appends one JSON line to AEROLINK_I1D_OUT: the calling spec line, the test, the outcome and END state.
import { appendFileSync } from 'node:fs'
import { test } from '@playwright/test'
import { runHeadless as singleRunHeadless, scenarioStart, type RunContext, type Scenario } from '../../src/fmsCdu/scenario'
import { DEFAULT_START, composeDual, composeSingle, flyToEnd, lockstep, record, unitDigest } from './fms-headless-outcome-census'

export * from '../../src/fmsCdu/scenario'

const arm = process.env.AEROLINK_I1D_ARM
const out = process.env.AEROLINK_I1D_OUT

/** The spec line that called runHeadless, from the stack: the first frame in a copied spec. */
function callSite() {
  const stack = new Error().stack ?? ''
  for (const line of stack.split('\n')) {
    const match = /[\\/]tests[\\/](i1d-[^\\/]*?\.spec\.ts):(\d+):(\d+)/.exec(line)
    if (match) return { file: match[1].replace(/^i1d-(single|dual)--/, ''), line: Number(match[2]), column: Number(match[3]) }
  }
  return { file: 'unknown', line: 0, column: 0 }
}

const ordinals = new Map<string, number>()

export function runHeadless(scenario: Scenario, start = scenarioStart(scenario) ?? DEFAULT_START, context?: RunContext) {
  if (arm !== 'single' && arm !== 'dual') throw new Error('AEROLINK_I1D_ARM must be single or dual')
  if (!out) throw new Error('AEROLINK_I1D_OUT is required')
  const site = callSite()
  let title: string[] = []
  try { title = test.info().titlePath.slice(1) } catch { /* outside a test body */ }
  const key = `${site.file}:${site.line}:${site.column}|${title.join(' > ')}`
  const ordinal = (ordinals.get(key) ?? 0) + 1
  ordinals.set(key, ordinal)
  const base = { schema: 'aerolink.fms-i1d-outcome-census-call.v1', arm, site, title, ordinal, scenarioId: scenario?.id ?? null, start }
  if (arm === 'single') {
    const real = singleRunHeadless(scenario, start, context)
    // The replica is runHeadless line for line; its end state must equal the real one's, or the lockstep proves nothing.
    const replica = flyToEnd(composeSingle(scenario, start, context), scenario)
    const realEnd = unitDigest({ fms: real.fms, sim: real.sim })
    appendFileSync(out, `${JSON.stringify({ ...base, outcome: real.runner.outcome, results: real.runner.results, endedAfter: real.runner.endedAfter,
      end: realEnd, replicaEnd: unitDigest(replica), replicaMatches: realEnd === unitDigest(replica) && real.runner.outcome === replica.runner.outcome })}\n`)
    return real
  }
  const dual = flyToEnd(composeDual(scenario, start, context), scenario)
  const compared = lockstep(scenario, start, context)
  const standalone = record(dual, -1)
  appendFileSync(out, `${JSON.stringify({ ...base, outcome: standalone.outcome, results: standalone.results, endedAfter: standalone.endedAfter, end: standalone.end,
    lockstepDualMatches: standalone.end === compared.dual.end && standalone.outcome === compared.dual.outcome, lockstep: compared })}\n`)
  return { runner: dual.runner, fms: dual.fms, sim: dual.sim }
}
