import { expect, test } from '@playwright/test'
import { artifactPath, parseRoute, routePath } from '../src/routing'

const context = {
  programId: 'program-a',
  projectId: 'project-a',
  releaseId: 'release-a',
}

test('the authenticated project selector has a context-free route', () => {
  expect(parseRoute('/')).toMatchObject({ view: 'projects', discipline: 'system' })
  expect(parseRoute('/projects')).toMatchObject({ view: 'projects', discipline: 'system' })
  expect(routePath(context, 'projects')).toBe('/projects')
  expect(parseRoute('/projects/fms-product-development/builds')).toMatchObject({ view: 'builds', discipline: 'system' })
  expect(routePath(context, 'builds')).toBe('/projects/fms-product-development/builds')
})

test('change-request route generation and parsing preserve both engineering disciplines', () => {
  const system = routePath(context, 'scr', 'system', 'scr-a')
  const software = routePath(context, 'scr', 'software', 'swcr-a')

  expect(system).toBe('/programs/program-a/projects/project-a/releases/release-a/systems/change-requests/scr-a')
  expect(software).toBe('/programs/program-a/projects/project-a/releases/release-a/software/change-requests/swcr-a')
  expect(parseRoute(system)).toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'scr-a' })
  expect(parseRoute(software)).toMatchObject({ view: 'scr', discipline: 'software', artifactId: 'swcr-a' })
  expect(artifactPath(context, 'change-request', 'swcr-a', 'software')).toBe(software)
})

test('software authoring routes preserve the selected HLR or LLR level', () => {
  const hlr = routePath(context, 'createSoftwareChange', 'software', undefined, 'HighLevel')
  const llr = routePath(context, 'createSoftwareChange', 'software', undefined, 'LowLevel')
  expect(hlr).toContain('/software/change-requests/new?level=HLR')
  expect(llr).toContain('/software/change-requests/new?level=LLR')
  expect(parseRoute(hlr)).toMatchObject({ view: 'createSoftwareChange', artifactKind: 'HighLevel' })
  expect(parseRoute(llr)).toMatchObject({ view: 'createSoftwareChange', artifactKind: 'LowLevel' })
})

test('software change history routes preserve the selected HLR or LLR level', () => {
  const hlr = routePath(context, 'history', 'software', undefined, 'HighLevel')
  const llr = routePath(context, 'history', 'software', undefined, 'LowLevel')
  expect(hlr).toContain('/software/change-requests?level=HLR')
  expect(llr).toContain('/software/change-requests?level=LLR')
  expect(parseRoute(hlr)).toMatchObject({ view: 'history', artifactKind: 'HighLevel' })
  expect(parseRoute(llr)).toMatchObject({ view: 'history', artifactKind: 'LowLevel' })
})

test('problem reports are active while retired product-version and baseline pages reject direct navigation', () => {
  const root = '/programs/program-a/projects/project-a/releases/release-a'
  expect(parseRoute(`${root}/problem-reports`)).toMatchObject({ view: 'problemReports', discipline: 'system' })
  expect(routePath(context, 'problemReports')).toBe(`${root}/problem-reports`)
  expect(parseRoute(`${root}/problem-reports/report-a`)).toMatchObject({ view: 'problemReports', artifactId: 'report-a' })
  expect(routePath(context, 'problemReports', 'system', 'report-a')).toBe(`${root}/problem-reports/report-a`)
  for (const path of ['release-planning', 'baselines'])
    expect(parseRoute(`${root}/${path}`)).toMatchObject({ view: 'notFound' })
  for (const kind of ['problem-report', 'baseline', 'build'])
    expect(parseRoute(`${root}/artifacts/${kind}/record-a`)).toMatchObject({ view: 'notFound' })
})

test('legacy context-free change-request routes remain loadable until detail canonicalizes them', () => {
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/change-requests/legacy-a'))
    .toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'legacy-a' })
})

/**
 * The six verification pages, and the corrective action that hangs off one of them.
 *
 * The software level rides on artifactKind rather than on a Discipline value, so a round trip through the
 * address is the only thing that proves HLR and LLR are actually distinct destinations rather than the same
 * page reached twice.
 */
test('each verification page round-trips, and a results route may carry a problem report', () => {
  const pages = [
    { view: 'testingCoverage', discipline: 'systemTest', kind: undefined, path: 'system-verification/coverage' },
    { view: 'testResults', discipline: 'systemTest', kind: undefined, path: 'system-verification/results' },
    { view: 'testingCoverage', discipline: 'softwareTest', kind: 'HighLevel', path: 'software-verification/hlr/coverage' },
    { view: 'testResults', discipline: 'softwareTest', kind: 'HighLevel', path: 'software-verification/hlr/results' },
    { view: 'testingCoverage', discipline: 'softwareTest', kind: 'LowLevel', path: 'software-verification/llr/coverage' },
    { view: 'testResults', discipline: 'softwareTest', kind: 'LowLevel', path: 'software-verification/llr/results' },
  ] as const

  for (const page of pages) {
    const address = routePath(context, page.view, page.discipline, undefined, page.kind)
    expect(address).toBe(`/programs/program-a/projects/project-a/releases/release-a/${page.path}`)
    expect(parseRoute(address)).toMatchObject(
      page.kind
        ? { view: page.view, discipline: page.discipline, artifactKind: page.kind }
        : { view: page.view, discipline: page.discipline },
    )
  }

  // The branch root is the chooser between the two, and carries nothing else.
  expect(routePath(context, 'verification', 'systemTest')).toBe('/programs/program-a/projects/project-a/releases/release-a/system-verification')
  expect(parseRoute(routePath(context, 'verification', 'softwareTest'))).toMatchObject({ view: 'verification', discipline: 'softwareTest' })

  // "results" must not be read as the name of a problem report, which is why the page routes are matched
  // before the rule that reads a trailing segment as one.
  const corrective = routePath(context, 'testResults', 'softwareTest', 'report-a', 'LowLevel')
  expect(corrective).toBe('/programs/program-a/projects/project-a/releases/release-a/software-verification/llr/results/report-a')
  expect(parseRoute(corrective)).toMatchObject({ view: 'testResults', discipline: 'softwareTest', artifactKind: 'LowLevel', artifactId: 'report-a' })
  expect(parseRoute(routePath(context, 'testResults', 'systemTest', 'report-b'))).toMatchObject({ view: 'testResults', discipline: 'systemTest', artifactId: 'report-b' })
})
