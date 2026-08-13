import type { TestDiscipline } from './TestResultsWorkspace'

export type VerificationScope = TestDiscipline | 'Software'

/**
 * What a build's requirements are verified by, read once and shared.
 *
 * Two surfaces ask this question now. The Test Procedure Explorer answers "is this build covered", and the
 * test change request page still needs the coverage a requirement already has to show beside the decision
 * about it — a reader deciding whether a procedure must be written is answering a question about coverage.
 *
 * It lives here rather than in either of them because working out *which* configuration to ask about is the
 * subtle part, and two copies of that would drift. A release is a plan; what carries requirements is a
 * materialized baseline or the software build that froze one.
 */
export type CoverageItem = {
  revisionId: string
  displayNumber: string
  statement: string
  covered: boolean
  verified: boolean
  disposition: 'Covered' | 'Suspect' | 'Uncovered'
  coveredBy: {
    procedureId: string; revisionId: string; displayNumber: string; title: string; state: string
    coverageState: 'Confirmed' | 'Suspect'
  }[]
}

export type Coverage = {
  total: number; covered: number; suspect: number; verified: number; uncovered: number; items: CoverageItem[]
}

/** The requirement number a discipline's procedures verify. Coverage is computed for the whole
 *  configuration, and each verification surface speaks for one discipline. */
export const requirementPrefix = (discipline: TestDiscipline) =>
  discipline === 'System' ? 'SYSR-' : discipline === 'HighLevelSoftware' ? 'HLR-' : 'LLR-'

const requirementPrefixes = (scope: VerificationScope) =>
  scope === 'Software' ? ['HLR-', 'LLR-'] : [requirementPrefix(scope)]

/**
 * The baseline or build whose requirements this release actually carries.
 *
 * Passing the release id straight to the coverage endpoint answers 400, and a failed coverage read leaves the
 * surface empty rather than loud — so it read as "this build has no requirements at all" on a page whose whole
 * job is to say what is untested. A build in work has no baseline of its own yet and carries its
 * predecessor's, which is what build-context calls the effective baseline.
 */
export async function coverageConfiguration(api: string, projectId: string, releaseId: string) {
  const context = await fetch(`${api}/api/build-context?projectId=${projectId}&releaseId=${releaseId}`)
  const effectiveBaselineId = context.ok ? (await context.json())?.effectiveBaselineId as string | undefined : undefined
  const builds = await fetch(`${api}/api/builds?projectId=${projectId}`)
  const build = builds.ok
    ? (await builds.json()).find((x: { releaseId: string }) => x.releaseId === releaseId) as { id: string } | undefined
    : undefined
  const query = build ? `buildId=${build.id}` : effectiveBaselineId ? `baselineId=${effectiveBaselineId}` : ''
  return { effectiveBaselineId, query }
}

/** Narrows raw coverage to one discipline and recounts it, so the totals describe what is on screen. */
export function summarise(raw: Coverage, discipline: VerificationScope): Coverage {
  const prefixes = requirementPrefixes(discipline)
  const items = raw.items.filter(x => prefixes.some(prefix => x.displayNumber.startsWith(prefix)))
  return {
    items,
    total: items.length,
    covered: items.filter(x => x.disposition === 'Covered').length,
    suspect: items.filter(x => x.disposition === 'Suspect').length,
    verified: items.filter(x => x.verified).length,
    uncovered: items.filter(x => x.disposition === 'Uncovered').length,
  }
}

/**
 * Reads this build's coverage for one discipline.
 *
 * Returns undefined when the configuration carries nothing to compute coverage against, which is a real state
 * — a build whose requirements have not been materialized — and not an error.
 */
export async function loadCoverage(
  api: string, projectId: string, releaseId: string, discipline: VerificationScope,
): Promise<{ coverage?: Coverage; failed: boolean }> {
  const { query } = await coverageConfiguration(api, projectId, releaseId)
  if (!query) return { coverage: undefined, failed: false }
  const response = await fetch(`${api}/api/verification-coverage?projectId=${projectId}&${query}`)
  if (!response.ok) return { coverage: undefined, failed: true }
  return { coverage: summarise(await response.json() as Coverage, discipline), failed: false }
}
