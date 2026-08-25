import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, showcaseSeed } from './auth'

type Artifact = {
  id: string
  revisionId: string
  displayNumber: string
  artifactKind: 'Case' | 'Procedure'
  level: 'System' | 'HighLevel' | 'LowLevel'
  state: string
  ownerId: string
  lastOutcome?: string
}

async function getArtifacts(request: Parameters<typeof apiLogin>[0], path: string) {
  const response = await request.get(`${apiBase}${path}`)
  expect(response.ok(), await response.text()).toBeTruthy()
  return await response.json() as { items: Artifact[]; totalCount: number; totalPages: number; page: number; pageSize: number }
}

test('profile-driven verification aliases and release-scoped paging stay deterministic', async ({ request }) => {
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const query = `projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&scope=Software`
  const combinedQuery = `projectId=${showcase.projectId}`

  const combinedAll = await getArtifacts(request, `/api/verification-artifacts?${combinedQuery}&page=1&pageSize=200`)
  expect(combinedAll.totalCount).toBeGreaterThan(0)
  expect(combinedAll.items.every(item => item.artifactKind === 'Case' || item.artifactKind === 'Procedure')).toBeTruthy()
  const combined = await getArtifacts(request, `/api/verification-artifacts?${combinedQuery}&sort=owner&page=1&pageSize=7`)
  expect(combined.totalPages).toBe(Math.ceil(combined.totalCount / combined.pageSize))
  expect(combined.items).toHaveLength(Math.min(7, combined.totalCount))
  for (let index = 1; index < combined.items.length; index++) {
    const previous = combined.items[index - 1]
    const current = combined.items[index]
    expect(`${previous.ownerId}\u0000${previous.displayNumber}` <= `${current.ownerId}\u0000${current.displayNumber}`).toBeTruthy()
  }
  if (combined.totalPages > 1) {
    const second = await getArtifacts(request, `/api/verification-artifacts?${combinedQuery}&sort=owner&page=2&pageSize=7`)
    expect(second.items).toHaveLength(Math.min(7, combined.totalCount - 7))
    expect(second.items.some(item => combined.items.some(first => first.id === item.id))).toBeFalsy()
    expect(`${combined.items.at(-1)!.ownerId}\u0000${combined.items.at(-1)!.displayNumber}` <= `${second.items[0].ownerId}\u0000${second.items[0].displayNumber}`).toBeTruthy()
  }

  // The historical aliases are deliberately asymmetric: software procedures without a kind were the Case
  // compatibility surface, while an explicit Procedure kind and the neutral endpoint expose Procedures.
  const legacyCases = await getArtifacts(request, `/api/test-procedures?${query}&page=1&pageSize=200`)
  expect(legacyCases.items.length).toBeGreaterThan(0)
  expect(legacyCases.items.every(item => item.artifactKind === 'Case')).toBeTruthy()
  const explicitProcedures = await getArtifacts(request, `/api/test-procedures?${query}&artifactKind=Procedure&page=1&pageSize=200`)
  // The showcase profile is Case-only; an explicit Procedure request must not leak historical Procedure rows.
  expect(explicitProcedures.items.length).toBe(0)
  expect(explicitProcedures.items.every(item => item.artifactKind === 'Procedure')).toBeTruthy()
  const legacySystem = await getArtifacts(request, `/api/test-procedures?projectId=${showcase.projectId}&scope=System&page=1&pageSize=200`)
  expect(legacySystem.items.every(item => item.artifactKind === 'Procedure' && item.level === 'System')).toBeTruthy()

  const sampleCase = legacyCases.items[0]
  const highLevelQuery = query.replace('scope=Software', 'scope=HighLevelSoftware')
  const caseLevel = await getArtifacts(request, `/api/verification-artifacts?${highLevelQuery}&artifactKind=Case&page=1&pageSize=200`)
  expect(caseLevel.items.every(item => item.artifactKind === 'Case' && item.level === 'HighLevel')).toBeTruthy()
  const search = await getArtifacts(request, `/api/verification-artifacts?${query}&search=${encodeURIComponent(sampleCase.displayNumber)}&page=1&pageSize=25`)
  expect(search.items.some(item => item.id === sampleCase.id)).toBeTruthy()
  const state = await getArtifacts(request, `/api/verification-artifacts?${query}&artifactKind=Case&state=${sampleCase.state}&page=1&pageSize=200`)
  expect(state.items.every(item => item.state === sampleCase.state)).toBeTruthy()
  if (sampleCase.lastOutcome) {
    const outcome = await getArtifacts(request, `/api/verification-artifacts?${query}&artifactKind=Case&outcome=${sampleCase.lastOutcome}&page=1&pageSize=200`)
    expect(outcome.items.every(item => item.lastOutcome === sampleCase.lastOutcome)).toBeTruthy()
  }

  const documentsResponse = await request.get(`${apiBase}/api/projects/${showcase.projectId}/test-artifacts?scope=Software`)
  expect(documentsResponse.ok(), await documentsResponse.text()).toBeTruthy()
  const documents = await documentsResponse.json() as { id: string; level: string; artifactKind: string; sections: { id: string }[] }[]
  const documentKeys = new Set(documents.map(document => `${document.level}:${document.artifactKind}`))
  expect(documentKeys.size).toBeGreaterThan(0)
  for (const key of documentKeys) expect(['HighLevel:Case', 'HighLevel:Procedure', 'LowLevel:Case', 'LowLevel:Procedure']).toContain(key)
  const document = documents.find(item => item.level === sampleCase.level && item.artifactKind === sampleCase.artifactKind)
  expect(document).toBeTruthy()
  const documentFiltered = await getArtifacts(request, `/api/verification-artifacts?${query}&documentId=${document!.id}&page=1&pageSize=200`)
  expect(documentFiltered.totalCount).toBeLessThanOrEqual(combinedAll.totalCount)
  expect(document!.sections.length).toBeGreaterThan(0)
  const sectionFiltered = await getArtifacts(request, `/api/verification-artifacts?${query}&sectionId=${document!.sections[0].id}&page=1&pageSize=200`)
  expect(sectionFiltered.totalCount).toBeLessThanOrEqual(documentFiltered.totalCount)

  // The generic showcase is intentionally Case-only; active Procedure discussion and the full four-key rail
  // are asserted on the activated Case + Procedure fixture in issue-726-case-procedure-execution-readiness.
  const commentResponse = await request.post(`${apiBase}/api/test-procedures/${sampleCase.id}/comments`, {
    data: { revisionId: sampleCase.revisionId, body: `#762 discussion ${Date.now()}`, mentions: [] },
  })
  expect(commentResponse.status(), await commentResponse.text()).toBe(201)
  const comment = await commentResponse.json() as { id: string }
  const resolved = await request.post(`${apiBase}/api/enterprise-requirements/comments/${comment.id}/resolve`, {
    data: { disposition: 'The exact Procedure discussion was reviewed.' },
  })
  expect(resolved.status(), await resolved.text()).toBe(204)
})
