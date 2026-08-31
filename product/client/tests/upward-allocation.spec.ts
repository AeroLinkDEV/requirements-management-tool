import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, authorNoUpstreamAnswer, firstSectionId, login, openNewSoftwareChangeRequest, showcaseSeed } from './auth'

const completeImpacts = JSON.stringify({ trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected', baseline: 'Not Affected', collaboration: 'Not Affected' })

test('software proposals govern exact build-scoped upward allocations and derived exceptions', async ({ request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcase = await showcaseSeed(request)
  const hlrSection = await firstSectionId(request, showcase.projectId, 'HighLevel')
  const query = (childLevel: 'HighLevel' | 'LowLevel', search: string) =>
    request.get(`${apiBase}/api/authoring/upstream-requirements?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&childLevel=${childLevel}&search=${encodeURIComponent(search)}&limit=12`)

  const systemResponse = await query('HighLevel', 'SYSR-')
  expect(systemResponse.ok(), await systemResponse.text()).toBeTruthy()
  const systems = await systemResponse.json()
  expect(systems.length).toBeGreaterThanOrEqual(2)
  expect(systems.every((item: any) => item.level === 'System' && /^SYSR-\d{6}\.\d{2}$/.test(item.displayNumber))).toBeTruthy()
  const hlrResponse = await query('LowLevel', 'HLR-')
  expect(hlrResponse.ok(), await hlrResponse.text()).toBeTruthy()
  const hlrs = await hlrResponse.json()
  expect(hlrs.length).toBeGreaterThan(0)
  expect(hlrs.every((item: any) => item.level === 'HighLevel')).toBeTruthy()

  const proposal = (overrides: Record<string, unknown> = {}) => ({
    level: 'HighLevel', kind: 'Introduce', targetSectionId: hlrSection,
    statement: 'The software shall retain an exact governed upward allocation.',
    rationale: 'The allocation is reviewed with the proposed software behavior.',
    verificationMethod: 'Test', impactDispositionJson: completeImpacts, ...overrides,
  })
  const draft = (title: string, requirementChanges: unknown[]) => request.post(`${apiBase}/api/change-request-drafts`, { data: {
    projectId: showcase.projectId, targetReleaseId: showcase.activeReleaseId, type: 'Software', title,
    problem: 'Prospective allocation must be controlled.', analysis: 'Client-only filtering is not sufficient.',
    solution: 'Store and review exact upstream revision identities.', requirementChanges,
  } })
  const submit = (id: string, version: number) => request.post(`${apiBase}/api/change-requests/${id}/submit`, { data: {
    expectedVersion: version, mode: 'Sequential', approvers: [{ userId: 'systems.reviewer', name: 'Systems Reviewer' }],
  } })

  const missingResponse = await draft(`Missing upward allocation ${Date.now()}`, [proposal()])
  expect(missingResponse.status(), await missingResponse.text()).toBe(201)
  const missing = await missingResponse.json()
  const missingReady = await authorNoUpstreamAnswer(request, missing.id, 'This software proposal has no direct upstream change request; its missing requirement allocation is intentional for this validation.')
  const missingSubmit = await submit(missing.id, missingReady.version)
  expect(missingSubmit.status()).toBe(400)
  expect(await missingSubmit.text()).toContain('at least one current upstream requirement')

  const wrongLevel = await draft(`Wrong-level upward allocation ${Date.now()}`, [proposal({ upstreamRevisionIds: [hlrs[0].revisionId] })])
  expect(wrongLevel.status()).toBe(400)
  expect(await wrongLevel.text()).toContain('current System revision')
  const unknownRevision = await draft(`Unknown-build upward allocation ${Date.now()}`, [proposal({ upstreamRevisionIds: ['00000000-0000-0000-0000-000000000001'] })])
  expect(unknownRevision.status()).toBe(400)

  const singleResponse = await draft(`One-parent upward allocation ${Date.now()}`, [proposal({ upstreamRevisionIds: [systems[0].revisionId] })])
  expect(singleResponse.status(), await singleResponse.text()).toBe(201)
  const single = await singleResponse.json()
  expect(single.requirementChanges[0].upstreamRevisionIds).toEqual([systems[0].revisionId])
  const singleReady = await authorNoUpstreamAnswer(request, single.id, 'This software proposal is allocated to a System requirement, not to an upstream change request.')
  expect((await submit(single.id, singleReady.version)).ok()).toBeTruthy()

  const manyResponse = await draft(`Many-parent upward allocation ${Date.now()}`, [proposal({ upstreamRevisionIds: [systems[0].revisionId, systems[1].revisionId] })])
  expect(manyResponse.status(), await manyResponse.text()).toBe(201)
  const many = await manyResponse.json()
  expect(many.requirementChanges[0].upstreamRevisionIds).toHaveLength(2)
  const manyReady = await authorNoUpstreamAnswer(request, many.id, 'This software proposal is allocated to System requirements, not to an upstream change request.')
  expect((await submit(many.id, manyReady.version)).ok()).toBeTruthy()

  const derivedResponse = await draft(`Derived exception ${Date.now()}`, [proposal({
    isDerived: true, upstreamRevisionIds: [],
    rationale: 'A timing monitor is derived from the approved software architecture safety analysis.',
  })])
  expect(derivedResponse.status(), await derivedResponse.text()).toBe(201)
  const derived = await derivedResponse.json()
  const derivedReady = await authorNoUpstreamAnswer(request, derived.id, 'This derived software exception has no direct upstream change request.')
  expect((await submit(derived.id, derivedReady.version)).ok()).toBeTruthy()
})

test('an engineer can search, select, and explicitly replace an upward allocation with a derived exception', async ({ page }) => {
  await login(page)
  await openNewSoftwareChangeRequest(page, 'HLR')
  await page.getByRole('button', { name: '+ Introduce HLR' }).click()
  const search = page.getByLabel('Find upstream requirement 1')
  await search.fill('SYSR-000001')
  const candidate = page.locator('.proposalLookupResults button').filter({ hasText: 'SYSR-000001' }).first()
  await expect(candidate).toBeVisible()
  await candidate.click()
  const selected = page.locator('.controlledEditor .roleCloud button').filter({ hasText: 'Remove' }).first()
  await expect(selected).toContainText('SYSR-000001.')
  await page.locator('.derivedControl button').click()
  await expect(search).toHaveCount(0)
  await expect(page.locator('.controlledEditor .roleCloud button').filter({ hasText: 'Remove' })).toHaveCount(0)
})

test('modifying an HLR hydrates its exact parent and preserves an engineer replacement as a navigable reference',async({page,request})=>{
  test.setTimeout(90_000)
  await apiLogin(request)
  const showcase=await showcaseSeed(request)
  const requirements=await (await request.get(`${apiBase}/api/authoring/requirements?projectId=${showcase.projectId}&scope=Software&search=HLR-&limit=50`)).json()
  const existing=requirements.find((item:{currentUpstreamRevisionIds?:string[]})=>item.currentUpstreamRevisionIds?.length)
  expect(existing,'A current HLR with an exact upward allocation').toBeTruthy()
  const parents=await (await request.get(`${apiBase}/api/authoring/upstream-requirements?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&childLevel=HighLevel&search=SYSR-&limit=50`)).json()
  const replacement=parents.find((item:{revisionId:string})=>!existing.currentUpstreamRevisionIds.includes(item.revisionId))
  expect(replacement,'A permitted replacement System requirement').toBeTruthy()
  await login(page)
  await openNewSoftwareChangeRequest(page,'HLR')
  await page.getByRole('button',{name:'Modify existing HLR'}).click()
  const requirementSearch=page.getByLabel('Find controlled requirement 1')
  await requirementSearch.fill(existing.baseNumber)
  await page.locator('.proposalLookupResults button').filter({hasText:existing.displayNumber}).first().click()

  // Every current exact System parent is cleared, not just the first one.
  //
  // An HLR may legitimately be allocated to more than one System requirement, and a preceding journey can
  // leave the one this journey selects carrying two. Removing `.first()` and then asserting none remain was
  // an assumption about how many parents the fixture happened to have, so the journey failed on the arithmetic
  // rather than on anything it is about. Counted down one removal at a time, waiting for the count to drop
  // after each, so a slow re-render cannot make the next click land on a chip that has already gone.
  const existingParents=page.locator('.controlledEditor .roleCloud button').filter({hasText:/SYSR-\d{6}\.\d{2}/})
  // Waited on before it is counted: `count()` answers immediately, so asking before the hydrated parents have
  // rendered reads zero and fails on the journey's own precondition rather than on anything it is testing.
  await expect(existingParents.first()).toBeVisible()
  let remainingParents=await existingParents.count()
  expect(remainingParents,'the HLR being modified should carry at least one current exact upward allocation').toBeGreaterThan(0)
  while(remainingParents>0){
    await existingParents.first().click()
    await expect(existingParents).toHaveCount(remainingParents-1)
    remainingParents-=1
  }
  await expect(existingParents).toHaveCount(0)

  const upstreamSearch=page.getByLabel('Find upstream requirement 1')
  await upstreamSearch.fill(replacement.displayNumber.replace(/\.\d{2}$/,''))
  await page.locator('.proposalLookupResults button').filter({hasText:replacement.displayNumber}).last().click()
  await expect(page.locator('.controlledEditor .roleCloud button').filter({hasText:replacement.displayNumber})).toBeVisible()
  await page.getByLabel('Title').fill('Replace HLR upward allocation')
  await page.getByRole('button',{name:'Save HLRCR Draft'}).click()

  const controlledParent=page.locator('.artifactReferenceCloud a.exactArtifactLink').filter({hasText:replacement.displayNumber})
  await expect(controlledParent).toBeVisible()
  await expect(controlledParent).toHaveAttribute('href', new RegExp(`/requirements/${replacement.artifactId}\\?discipline=system&requirementRevisionId=${replacement.revisionId}$`))
  await controlledParent.click()
  await expect(page).toHaveURL(new RegExp(`/requirements/${replacement.artifactId}\\?discipline=system&requirementRevisionId=${replacement.revisionId}$`, 'i'))
  await expect(page.getByRole('heading',{name:'System Requirements Explorer'})).toBeVisible()
})
