import { expect, test } from '@playwright/test'
import { artifactPath, coverageExplorerPath, exactTraceArtifactPath, parseRoute, routePath } from '../src/routing'

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
  const interfaceChange = routePath(context, 'scr', 'system', 'icd-a', 'Interface')

  expect(system).toBe('/programs/program-a/projects/project-a/releases/release-a/systems/change-requests/scr-a')
  expect(software).toBe('/programs/program-a/projects/project-a/releases/release-a/software/change-requests/swcr-a')
  expect(parseRoute(system)).toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'scr-a' })
  expect(parseRoute(`${system}?proposalId=proposal-a`)).toMatchObject({ view: 'scr', requirementProposalId: 'proposal-a' })
  expect(parseRoute(software)).toMatchObject({ view: 'scr', discipline: 'software', artifactId: 'swcr-a' })
  expect(artifactPath(context, 'change-request', 'swcr-a', 'software')).toBe(software)
  expect(interfaceChange).toBe('/programs/program-a/projects/project-a/releases/release-a/interfaces/change-requests/icd-a')
  expect(parseRoute(interfaceChange)).toMatchObject({ view: 'scr', discipline: 'system', artifactKind: 'Interface', artifactId: 'icd-a' })
})

test('Team Work route generation and parsing preserve project and shell build context', () => {
  const address = routePath(context, 'teamwork')
  expect(address).toBe('/programs/program-a/projects/project-a/releases/release-a/team-work')
  expect(parseRoute(address)).toMatchObject({
    view: 'teamwork',
    discipline: 'system',
    programId: 'program-a',
    projectId: 'project-a',
    releaseId: 'release-a',
  })
})

test('software authoring routes preserve the selected HLR or LLR level', () => {
  const hlr = routePath(context, 'createSoftwareChange', 'software', undefined, 'HighLevel')
  const llr = routePath(context, 'createSoftwareChange', 'software', undefined, 'LowLevel')
  expect(hlr).toContain('/software/change-requests/new?level=HLR')
  expect(llr).toContain('/software/change-requests/new?level=LLR')
  expect(parseRoute(hlr)).toMatchObject({ view: 'createSoftwareChange', artifactKind: 'HighLevel' })
  expect(parseRoute(llr)).toMatchObject({ view: 'createSoftwareChange', artifactKind: 'LowLevel' })
})

test('configured Interface ladders have a dedicated ICD change authoring route', () => {
  const address = routePath(context, 'createInterfaceChange', 'system')
  expect(address).toBe('/programs/program-a/projects/project-a/releases/release-a/interfaces/change-requests/new')
  expect(parseRoute(address)).toMatchObject({ view: 'createInterfaceChange', discipline: 'system', artifactKind: 'Interface' })
  const history = routePath(context, 'history', 'system', undefined, 'Interface', undefined, 'Interface')
  expect(history).toBe('/programs/program-a/projects/project-a/releases/release-a/interfaces/change-requests')
  expect(parseRoute(history)).toMatchObject({ view: 'history', discipline: 'system', historyTypeIntent: 'Interface' })
  const detail = routePath(context, 'scr', 'system', 'icdcr-a', 'Interface')
  expect(detail).toBe('/programs/program-a/projects/project-a/releases/release-a/interfaces/change-requests/icdcr-a')
  expect(parseRoute(detail)).toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'icdcr-a', artifactKind: 'Interface' })
  expect(artifactPath(context, 'change-request', 'icdcr-a', 'system', 'Interface')).toBe(detail)
})

test('software change history routes preserve the selected HLR or LLR level', () => {
  const hlr = routePath(context, 'history', 'software', undefined, 'HighLevel')
  const llr = routePath(context, 'history', 'software', undefined, 'LowLevel')
  expect(hlr).toContain('/software/change-requests?level=HLR')
  expect(llr).toContain('/software/change-requests?level=LLR')
  expect(parseRoute(hlr)).toMatchObject({ view: 'history', artifactKind: 'HighLevel' })
  expect(parseRoute(llr)).toMatchObject({ view: 'history', artifactKind: 'LowLevel' })
  const assessment=routePath(context,'history','software','assessment-a','HighLevel')
  expect(assessment).toContain('level=HLR')
  expect(assessment).toContain('assessment=assessment-a')
  expect(parseRoute(assessment)).toMatchObject({view:'history',artifactKind:'HighLevel',artifactId:'assessment-a'})
  const selected = `${assessment}&selection=cr-a`
  expect(parseRoute(selected)).toMatchObject({view:'history', historySelectionId:'cr-a'})
  expect(routePath(context, 'history', 'software', 'assessment-a', 'HighLevel', undefined, 'Software', 'cr-a'))
    .toContain('selection=cr-a')
})

test('problem reports and configuration baselines are active while retired product-version pages reject direct navigation', () => {
  const root = '/programs/program-a/projects/project-a/releases/release-a'
  expect(parseRoute(`${root}/problem-reports`)).toMatchObject({ view: 'problemReports', discipline: 'system' })
  expect(routePath(context, 'problemReports')).toBe(`${root}/problem-reports`)
  expect(parseRoute(`${root}/problem-reports/report-a`)).toMatchObject({ view: 'problemReports', artifactId: 'report-a' })
  expect(routePath(context, 'problemReports', 'system', 'report-a')).toBe(`${root}/problem-reports/report-a`)
  expect(parseRoute(`${root}/release-planning`)).toMatchObject({ view: 'notFound' })
  expect(parseRoute(`${root}/baselines`)).toMatchObject({ view: 'baselines' })
  for (const kind of ['problem-report', 'baseline', 'build'])
    expect(parseRoute(`${root}/artifacts/${kind}/record-a`)).toMatchObject({ view: 'notFound' })
})

test('exact verification artifact routes preserve immutable revision identity alongside release context', () => {
  const revisionId = 'revision-procedure-00'
  const path = routePath(context, 'artifact', 'system', 'procedure-a', 'test-procedure', undefined, undefined, undefined, undefined, revisionId)
  expect(path).toBe('/programs/program-a/projects/project-a/releases/release-a/artifacts/test-procedure/procedure-a?revisionId=revision-procedure-00')
  expect(parseRoute(path)).toMatchObject({
    view: 'artifact', artifactKind: 'test-procedure', artifactId: 'procedure-a', artifactRevisionId: revisionId,
  })
  expect(exactTraceArtifactPath(context, {
    id: 'procedure-a', kind: 'TestProcedure', revisionId, buildId: 'release-a', displayNumber: 'HLRTP-000001.00',
  })).toBe(path)
})

test('controlled document TCR links open the exact package in its build and discipline', () => {
  const root = '/programs/program-a/projects/project-a/releases/release-a'
  for (const [discipline, kind, branch] of [
    ['systemTest', undefined, 'system-verification'],
    ['softwareTest', 'HighLevel', 'software-verification/hlr'],
    ['softwareTest', 'LowLevel', 'software-verification/llr'],
  ] as const) {
    const path = routePath(context, 'testingCoverage', discipline, 'tcr-a', kind)
    expect(path).toBe(`${root}/${branch}/coverage/tcr-a`)
    expect(parseRoute(path)).toMatchObject({ view: 'testingCoverage', artifactId: 'tcr-a', ...(kind ? { artifactKind: kind } : {}) })
  }
})

test('HLR and LLR Procedure TCR routes round-trip through their level branch and retain the Procedure kind', () => {
  for (const [level, id, branch] of [
    ['HighLevelProcedure', 'procedure-hlr-a', 'hlr'],
    ['LowLevelProcedure', 'procedure-llr-a', 'llr'],
  ] as const) {
    const path = routePath(context, 'testChangeRequest', 'softwareTest', id, level)
    expect(path).toBe(`/programs/program-a/projects/project-a/releases/release-a/software-verification/${branch}/change-requests/${id}?kind=Procedure`)
    expect(parseRoute(path)).toMatchObject({
      view: 'testChangeRequest',
      discipline: 'softwareTest',
      artifactKind: level,
      artifactId: id,
    })
  }
})

test('TCR proposal focus is stable and shareable for System and software Procedure routes', () => {
  const system = routePath(context, 'testChangeRequest', 'systemTest', 'tcr-system', 'Procedure', undefined, undefined, undefined, 'proposal-system')
  expect(system).toBe('/programs/program-a/projects/project-a/releases/release-a/system-verification/change-requests/tcr-system?kind=Procedure&proposalId=proposal-system')
  expect(parseRoute(system)).toMatchObject({ view: 'testChangeRequest', discipline: 'systemTest', artifactId: 'tcr-system', artifactKind: 'Procedure', testChangeRequestProposalId: 'proposal-system' })

  const software = routePath(context, 'testChangeRequest', 'softwareTest', 'tcr-procedure', 'HighLevelProcedure', undefined, undefined, undefined, 'proposal-procedure')
  expect(software).toBe('/programs/program-a/projects/project-a/releases/release-a/software-verification/hlr/change-requests/tcr-procedure?kind=Procedure&proposalId=proposal-procedure')
  expect(parseRoute(software)).toMatchObject({ view: 'testChangeRequest', discipline: 'softwareTest', artifactId: 'tcr-procedure', artifactKind: 'HighLevelProcedure', testChangeRequestProposalId: 'proposal-procedure' })

  const back = routePath(context, 'testChangeRequests', 'softwareTest', undefined, 'HighLevelProcedure')
  expect(back).toBe('/programs/program-a/projects/project-a/releases/release-a/software-verification/hlr/change-requests?kind=Procedure')
  expect(parseRoute(back).testChangeRequestProposalId).toBeUndefined()
})

test('TCR register selection is stable, typed, and omitted when context changes', () => {
  const selected = routePath(context, 'testChangeRequests', 'softwareTest', undefined, 'HighLevelProcedure', undefined, undefined, 'tcr-a')
  expect(selected).toContain('/software-verification/hlr/change-requests?kind=Procedure&selection=tcr-a')
  expect(parseRoute(selected)).toMatchObject({
    view: 'testChangeRequests', discipline: 'softwareTest', artifactKind: 'HighLevelProcedure',
    testChangeRequestSelectionId: 'tcr-a',
  })
  const changedLevel = routePath(context, 'testChangeRequests', 'softwareTest', undefined, 'LowLevel')
  expect(changedLevel).not.toContain('selection=')
  expect(parseRoute(changedLevel).testChangeRequestSelectionId).toBeUndefined()
  const requirements = routePath(context, 'history', 'software', undefined, 'LowLevel')
  expect(requirements).not.toContain('selection=')
  expect(parseRoute(`${requirements}&selection=cr-a`).historySelectionId).toBe('cr-a')
})

test('legacy context-free change-request routes remain loadable until detail canonicalizes them', () => {
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/change-requests/legacy-a'))
    .toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'legacy-a' })
})

test('Change Request Digital Thread routes use an explicit stable-ID kind', () => {
  const address = routePath(context, 'lifecycle', 'system', 'cr-a', 'change-request')
  expect(address).toBe('/programs/program-a/projects/project-a/releases/release-a/traceability/change-requests/cr-a')
  expect(parseRoute(address)).toMatchObject({
    view: 'lifecycle', discipline: 'system', artifactId: 'cr-a', artifactKind: 'change-request',
  })
  expect(routePath(context, 'lifecycle', 'system', 'requirement-a')).toBe(
    '/programs/program-a/projects/project-a/releases/release-a/traceability/requirement-a',
  )
})

test('Documentation Center has a canonical Project route while legacy build routes remain readable', () => {
  const canonical = '/programs/program-a/projects/project-a/documentation-center'
  expect(routePath(context, 'managedDocuments')).toBe(canonical)
  expect(routePath(context, 'managedDocuments', 'system', 'document-a')).toBe(`${canonical}/document-a`)
  expect(parseRoute(canonical)).toMatchObject({ view: 'managedDocuments', programId: 'program-a', projectId: 'project-a' })
  expect(parseRoute(canonical).releaseId).toBeUndefined()
  expect(parseRoute(`${canonical}/document-a`)).toMatchObject({ view: 'managedDocuments', artifactId: 'document-a' })
  expect(parseRoute(`${canonical}/document-a`).releaseId).toBeUndefined()
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/documentation-center/document-a'))
    .toMatchObject({ view: 'managedDocuments', artifactId: 'document-a', releaseId: 'release-a' })
})

/**
 * The verification pages, and the corrective action that hangs off one of them.
 *
 * Change control and results keep distinct HLR and LLR routes. The shared Case/Procedure Explorer is the
 * combined exception, matching the Software Requirements Explorer rather than duplicating it by level.
 */
test('each verification page round-trips, and a results route may carry a problem report', () => {
  const pages = [
    { view: 'testingCoverage', discipline: 'systemTest', kind: undefined, path: 'system-verification/coverage' },
    { view: 'procedureExplorer', discipline: 'systemTest', kind: undefined, path: 'system-verification/procedures' },
    { view: 'testResults', discipline: 'systemTest', kind: undefined, path: 'system-verification/results' },
    { view: 'procedureExplorer', discipline: 'softwareTest', kind: undefined, path: 'software-verification/test-artifacts' },
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

  // Existing level-specific Procedure links remain parse-compatible, while Case is the current vocabulary.
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/hlr/cases'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'HighLevel' })
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/llr/cases'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'LowLevel' })
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/hlr/procedures'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'HighLevelProcedure' })
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/llr/procedures'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'LowLevelProcedure' })
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/cases'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'Case' })
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/software-verification/procedures'))
    .toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: 'Procedure' })

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

test('Coverage routes open the existing Explorer report without changing Downstream Assessment compatibility routes', () => {
  const system = coverageExplorerPath(context, 'systemTest')
  expect(system).toBe('/programs/program-a/projects/project-a/releases/release-a/system-verification/procedures?coverage=report')
  expect(parseRoute(system)).toMatchObject({ view: 'procedureExplorer', discipline: 'systemTest', coverageReport: true })

  for (const level of ['HighLevel', 'LowLevel'] as const) {
    const path = coverageExplorerPath(context, 'softwareTest', level)
    expect(path).toBe(`/programs/program-a/projects/project-a/releases/release-a/software-verification/test-artifacts?coverage=report&artifactLevel=${level}&artifactKind=Case`)
    expect(parseRoute(path)).toMatchObject({ view: 'procedureExplorer', discipline: 'softwareTest', artifactKind: level, coverageReport: true })
  }

  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/system-verification/coverage'))
    .toMatchObject({ view: 'testingCoverage', discipline: 'systemTest' })
})
