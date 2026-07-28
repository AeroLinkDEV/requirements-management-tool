import { expect, test } from '@playwright/test'
import { artifactPath, parseRoute, routePath } from '../src/routing'

const context = {
  programId: 'program-a',
  projectId: 'project-a',
  releaseId: 'release-a',
}

test('change-request route generation and parsing preserve both engineering disciplines', () => {
  const system = routePath(context, 'scr', 'system', 'scr-a')
  const software = routePath(context, 'scr', 'software', 'swcr-a')

  expect(system).toBe('/programs/program-a/projects/project-a/releases/release-a/systems/change-requests/scr-a')
  expect(software).toBe('/programs/program-a/projects/project-a/releases/release-a/software/change-requests/swcr-a')
  expect(parseRoute(system)).toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'scr-a' })
  expect(parseRoute(software)).toMatchObject({ view: 'scr', discipline: 'software', artifactId: 'swcr-a' })
  expect(artifactPath(context, 'change-request', 'swcr-a', 'software')).toBe(software)
})

test('legacy context-free change-request routes remain loadable until detail canonicalizes them', () => {
  expect(parseRoute('/programs/program-a/projects/project-a/releases/release-a/change-requests/legacy-a'))
    .toMatchObject({ view: 'scr', discipline: 'system', artifactId: 'legacy-a' })
})
