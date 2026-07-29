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

test('retired problem-report, product-version, and baseline pages reject direct navigation', () => {
  const root = '/programs/program-a/projects/project-a/releases/release-a'
  for (const path of ['problem-reports', 'release-planning', 'baselines'])
    expect(parseRoute(`${root}/${path}`)).toMatchObject({ view: 'notFound' })
  for (const kind of ['problem-report', 'baseline', 'build'])
    expect(parseRoute(`${root}/artifacts/${kind}/record-a`)).toMatchObject({ view: 'notFound' })
})

test('legacy context-free change-request routes remain loadable until detail canonicalizes them', () => {
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/change-requests/legacy-a'))
    .toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'legacy-a' })
})
