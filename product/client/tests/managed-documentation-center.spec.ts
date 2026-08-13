import { expect, test } from '@playwright/test'
import { readFile } from 'node:fs/promises'
import { apiBase, apiLogin, login, showcaseSeed } from './auth'

test('controlled relationship links use canonical targets and exact browser routes', async ({ page, request }) => {
  test.setTimeout(240_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  // Keep this relationship-only fixture outside build-targeted PR queues so it cannot perturb
  // their independently paged first-page assertions when the full browser suite shares a seed.
  const reportResponse=await request.post(`${apiBase}/api/problem-reports`,{data:{projectId:showcase.projectId,title:`Document relationship target ${Date.now()}`,problem:'Prove canonical Problem Report navigation.'}})
  expect(reportResponse.ok(),await reportResponse.text()).toBeTruthy()
  const report=await reportResponse.json()
  const documentsResponse=await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}`)
  expect(documentsResponse.ok(),await documentsResponse.text()).toBeTruthy()
  const document=(await documentsResponse.json()).items.find((item:{acronym:string})=>item.acronym==='SDP')
  expect(document).toBeTruthy()
  const created={id:document.id}
  const meanings:Record<string,string>={ChangeRequest:'MotivatedBy',ProblemReport:'AddressesProblem',TestChangeRequest:'VerificationImpact',Release:'RelatedBuild'}
  const linked:{type:string;id:string}[]=[]
  for(const artifactType of Object.keys(meanings)){
    const optionsResponse=await request.get(`${apiBase}/api/managed-documents/link-options?projectId=${showcase.projectId}&artifactType=${artifactType}`)
    expect(optionsResponse.ok(),await optionsResponse.text()).toBeTruthy()
    const options=(await optionsResponse.json() as {items:{id:string}[]}).items
    if(artifactType==='ProblemReport')options.unshift({id:report.id})
    expect(options.length,`Expected a showcase ${artifactType} target`).toBeGreaterThan(0)
    const detail=await (await request.get(`${apiBase}/api/managed-documents/${created.id}`)).json()
    const revision=detail.revisions.find((item:{state:string})=>['Draft','Returned'].includes(item.state))
    expect(revision).toBeTruthy()
    const response=await request.post(`${apiBase}/api/managed-documents/${created.id}/links`,{data:{revisionId:revision.id,artifactType,artifactId:options[0].id,displayNumber:'FORGED-CLIENT-LABEL',relationship:meanings[artifactType],expectedVersion:revision.version}})
    expect(response.ok(),await response.text()).toBeTruthy()
    linked.push({type:artifactType,id:options[0].id})
  }

  await login(page,'software.author',{openProject:false})
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  await page.getByRole('button',{name:'Links'}).click()
  for(const target of linked){
    const row=page.locator('.mdLinks > div').filter({has:page.locator('span').filter({hasText:new RegExp(`^${target.type} ·`)})})
    await expect(row).toBeVisible()
    const href=await row.getByRole('link').getAttribute('href')
    expect(href).not.toContain('FORGED-CLIENT-LABEL')
    if(target.type==='ChangeRequest')expect(href).toMatch(new RegExp(`/change-requests/${target.id}$`))
    if(target.type==='ProblemReport')expect(href).toMatch(new RegExp(`/problem-reports/${target.id}$`))
    if(target.type==='TestChangeRequest')expect(href).toMatch(new RegExp(`/coverage/${target.id}$`))
    if(target.type==='Release')expect(href).toMatch(new RegExp(`/releases/${target.id}/command-center$`))
  }
  const tcr=linked.find(item=>item.type==='TestChangeRequest')!
  await page.locator('.mdLinks > div').filter({has:page.locator('span').filter({hasText:/^TestChangeRequest ·/})}).getByRole('link').click()
  await expect(page).toHaveURL(new RegExp(`/coverage/${tcr.id}$`))
  await expect(page.getByRole('dialog')).toBeVisible()
})

test('managed Word documents remain one Project-wide register across build navigation', async ({ page }) => {
  test.setTimeout(240_000)
  await login(page, 'software.author')

  await page.getByRole('link', { name: 'Documentation Center' }).click()
  await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  const [trustDownload] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: 'Download connector trust' }).click(),
  ])
  expect(trustDownload.suggestedFilename()).toMatch(/^aerolink-.+-trust\.json$/)
  const trustPath = await trustDownload.path()
  expect(trustPath).toBeTruthy()
  const trust = JSON.parse(await readFile(trustPath!, 'utf8'))
  expect(trust.protocolVersion).toBe('aerolink-connector-launch-v1')
  expect(trust.profileVersion).toBe('aerolink-ooxml-safe-v1')
  expect(trust.publicKeyFingerprint).toMatch(/^[0-9a-f]{64}$/)
  await expect(page.getByText('7 of 7 matching records')).toBeVisible()
  await expect(page.locator('.mdMetrics').getByText('4', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: /SDP SDP-000001/ }).click()
  await expect(page).toHaveURL(/documentation-center\/[0-9a-f-]+$/)
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByRole('heading', { name: 'FMS Software Development Plan' })).toBeVisible()
  await expect(page.locator('.mdIdentity').getByText(/Draft SDP-000001\.01/)).toBeVisible()
  await expect(page.getByText('Document steward')).toBeVisible()
  await expect(page.getByText('Responsible owner')).toBeVisible()
  await expect(page.getByText('Revision initiated by')).toBeVisible()
  await expect(page.getByText('Contributors')).toBeVisible()

  await expect(page.getByText('Add GitLab merge-request traceability and desktop connector responsibilities.')).toBeVisible()
  await page.getByRole('button', { name: 'Edit formal scope' }).click()
  const summaryEditor = page.locator('.mdInlineForm')
  await summaryEditor.getByLabel('Formal revision scope').fill('Add GitLab traceability and preserve immutable check-in evidence.')
  await summaryEditor.getByLabel('Reason for correction').fill('Clarify the controlled formal scope before review.')
  await summaryEditor.getByRole('button', { name: 'Record formal scope correction' }).click()
  await expect(page.getByText(/formal revision scope for SDP-000001\.01 was revised/i)).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByText('Add GitLab traceability and preserve immutable check-in evidence.')).toBeVisible()
  await page.getByRole('button', { name: 'Versions' }).click()
  await expect(page.locator('.mdVersions').getByText('Most recent checked-in draft.', { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Review & release' }).click()
  await expect(page.getByRole('heading', { name: 'Electronic signatures for SDP-000001.01' })).toBeVisible()
  await expect(page.getByText('No signatures are recorded for this exact revision.')).toBeVisible()

  await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  await page.getByRole('button', { name: 'Open build 1.5' }).click()
  await page.goto(page.url().replace(/command-center$/, 'documentation-center'))
  await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  await expect(page.getByText('7 of 7 matching records')).toBeVisible()
  await expect(page.getByRole('button', { name: '+ New document' })).toBeVisible()
  await expect(page.locator('.mdList').getByText(/\.01 · (Draft|In Review|Returned)/)).toHaveCount(4)
})

test('the Project register loads bounded pages while direct document URLs remain reachable', async ({ page, request }) => {
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  const response = await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}&pageSize=100`)
  expect(response.ok(), await response.text()).toBeTruthy()
  const realItems = (await response.json()).items
  const direct = realItems.find((item:{acronym:string}) => item.acronym === 'SDP')
  const template = realItems.find((item:{id:string}) => item.id !== direct.id)
  expect(direct).toBeTruthy(); expect(template).toBeTruthy()

  await page.route('**/api/managed-documents?*', async route => {
    const url = new URL(route.request().url())
    if (url.searchParams.get('cursor') === 'mock-next') {
      await route.fulfill({ json: { totalCount: 51, pageSize: 50, hasMore: false, nextCursor: null, items: [{ ...template, id: crypto.randomUUID(), documentNumber: 'DOC-999999', title: 'Last paged document' }] } })
      return
    }
    const items = Array.from({ length: 50 }, (_, index) => ({ ...template, id: crypto.randomUUID(), documentNumber: `DOC-${String(index + 1).padStart(6, '0')}`, title: `Paged document ${index + 1}` }))
    await route.fulfill({ json: { totalCount: 51, pageSize: 50, hasMore: true, nextCursor: 'mock-next', items } })
  })
  await page.route('**/history/audit?*', async route => {
    const cursor = new URL(route.request().url()).searchParams.get('cursor')
    const items = cursor
      ? [{ id: crypto.randomUUID(), eventType: 'PagedAuditEvent51', actorId: 'software.author', detail: 'Final retained event', occurredAt: new Date().toISOString() }]
      : Array.from({ length: 50 }, (_, index) => ({ id: crypto.randomUUID(), eventType: `PagedAuditEvent${index + 1}`, actorId: 'software.author', detail: `Retained event ${index + 1}`, occurredAt: new Date().toISOString() }))
    await route.fulfill({ json: { pageSize: 50, hasMore: !cursor, nextCursor: cursor ? null : 'mock-audit-next', items } })
  })

  await login(page, 'software.author', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${direct.id}`)
  await expect(page.getByRole('heading', { name: direct.title })).toBeVisible()
  await expect(page.getByText('50 of 51 matching records')).toBeVisible()
  await page.getByRole('button', { name: 'Load more documents' }).click()
  await expect(page.getByText('51 of 51 matching records')).toBeVisible()
  await expect(page.getByRole('button', { name: /Last paged document/ })).toBeVisible()
  await page.getByRole('button', { name: 'Audit' }).click()
  await expect(page.getByRole('heading', { name: 'Complete retained evidence' })).toBeVisible()
  await page.getByRole('button', { name: 'Load more Audit' }).click()
  await expect(page.getByText('Paged Audit Event51')).toBeVisible()
})

test('review signature dialog exposes and submits the exact frozen intent', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  const suffix = Date.now().toString().slice(-6)
  const createdResponse = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: showcase.projectId,
    acronym: 'RIP',
    documentType: 'Review Integrity Plan',
    title: `Exact review intent ${suffix}`,
    ownerId: 'software.author',
    formalChangeSummary: 'Bind the browser decision to exact controlled evidence.',
    operationKey: crypto.randomUUID(),
  } })
  expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  const created = await createdResponse.json()
  const detailResponse = await request.get(`${apiBase}/api/managed-documents/${created.id}`)
  expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy()
  const detail = await detailResponse.json()
  const revision = detail.revisions.find((item:{id:string}) => item.id === created.revisionId)
  const working = revision.attachments.find((item:{id:string}) => item.id === revision.currentWorkingAttachmentId)
  const submitResponse = await request.post(`${apiBase}/api/managed-documents/revisions/${revision.id}/submit`, { data: {
    technicalReviewerId: 'software.lead',
    finalApproverId: 'quality.analyst',
    expectedVersion: revision.version,
    expectedWorkingAttachmentId: working.id,
    expectedWorkingSha256: working.sha256,
    expectedFormalSummaryVersion: revision.formalSummaryVersion,
    expectedFormalSummaryHash: revision.formalSummaryHash,
    expectedRelationshipManifestHash: revision.currentRelationshipManifestHash,
    operationKey: crypto.randomUUID(),
  } })
  expect(submitResponse.ok(), await submitResponse.text()).toBeTruthy()

  await login(page, 'software.lead', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  await page.getByRole('button', { name: 'Review & release' }).click()
  await page.getByRole('button', { name: 'Approve stage' }).click()
  const evidence = page.getByRole('status').filter({ hasText: 'Exact signature intent' })
  await expect(evidence).toContainText('cycle 1 · step 1 v1')
  await expect(evidence).toContainText('Reviewer via Direct Membership')
  await expect(evidence.getByText(/Snapshot [0-9a-f]{12}/)).toBeVisible()
  const dialog = page.getByRole('dialog', { name: 'Documentation Center action' })
  await dialog.getByLabel('Meaning').fill('I confirm this exact submitted snapshot is technically complete.')
  await dialog.getByLabel('Rationale').fill('The formal scope, working file, and relationship manifest are acceptable.')
  await dialog.getByLabel('Password').fill('AeroLink!2026')
  await dialog.getByRole('button', { name: 'Sign and approve' }).click()
  await expect(page.getByText(/was approved/)).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await page.getByRole('button', { name: 'Review & release' }).click()
  await expect(page.locator('.mdReview').getByText('I confirm this exact submitted snapshot is technically complete.')).toBeVisible()
  await expect(page.getByText('Rationale: The formal scope, working file, and relationship manifest are acceptable.')).toBeVisible()
})

test('an integrity-blocked revision is explicit and cannot launch or submit from the browser', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  const suffix = Date.now().toString().slice(-6)
  const createdResponse = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: showcase.projectId,
    acronym: 'IBP',
    documentType: 'Integrity Block Plan',
    title: `Integrity block ${suffix}`,
    ownerId: 'software.author',
    formalChangeSummary: 'Make a retained-file integrity incident visible and fail closed.',
    operationKey: crypto.randomUUID(),
  } })
  expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  const created = await createdResponse.json()
  await page.route(`**/api/managed-documents/${created.id}`, async route => {
    const response = await route.fetch()
    const detail = await response.json()
    const revision = detail.revisions.find((item:{id:string}) => item.id === created.revisionId)
    revision.integrityBlocked = true
    revision.integrityFailures = [{
      attachmentId: revision.currentWorkingAttachmentId,
      detail: 'Attachment failed hash_mismatch; immutable metadata was not changed.',
      openedAt: new Date().toISOString(),
    }]
    await route.fulfill({ response, json: detail })
  })

  await login(page, 'software.author', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  const block = page.getByRole('alert').filter({ hasText: 'Controlled file integrity block' })
  await expect(block).toContainText('failed hash_mismatch')
  await expect(page.getByRole('button', { name: 'Open in Word' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Submit for review' })).toBeDisabled()
})

test('a Draft revision can be withdrawn with retained history and survives refresh', async ({ page, request }) => {
  test.setTimeout(180_000)
  const showcase = await showcaseSeed(request)
  await apiLogin(request, 'software.author')
  const suffix = Date.now().toString().slice(-6)
  const createdResponse = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: showcase.projectId,
    acronym: 'WDP',
    documentType: 'Withdrawal Demonstration Plan',
    title: `Withdraw revision ${suffix}`,
    ownerId: 'software.author',
    formalChangeSummary: 'Prove controlled abandonment without deleting evidence.',
    operationKey: `browser-withdraw-${suffix}`,
  } })
  expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  const created = await createdResponse.json()

  await login(page, 'software.author', { openProject: false })
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  page.once('dialog', dialog => dialog.accept('The Project dispositioned this Draft before review.'))
  await page.getByRole('button', { name: 'Withdraw revision', exact: true }).click()
  await expect(page.getByText(/was withdrawn with its evidence retained/)).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByText('The released head has no active successor revision.')).toBeVisible()
  await page.getByRole('button', { name: /^audit$/i }).click()
  await expect(page.locator('.mdAudit').getByText(/Document Revision Withdrawn/i)).toBeVisible()
})

test('configuration authority can explicitly reassign document stewardship in the browser', async ({ page, request }) => {
  test.setTimeout(180_000)
  await apiLogin(request)
  const showcaseResponse = await request.post(`${apiBase}/api/showcase/seed`)
  expect(showcaseResponse.ok(), await showcaseResponse.text()).toBeTruthy()
  const showcase = await showcaseResponse.json()
  const suffix = Date.now().toString().slice(-6)
  const createdResponse = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: showcase.projectId,
    acronym: 'ARP',
    documentType: 'Assignment Recovery Plan',
    title: `Stewardship transfer ${suffix}`,
    ownerId: 'software.lead',
    formalChangeSummary: 'Prove the controlled browser reassignment path.',
    operationKey: crypto.randomUUID(),
  } })
  expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  const created = await createdResponse.json()

  await login(page, 'admin')
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  await expect(page.getByRole('heading', { name: `Stewardship transfer ${suffix}` })).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Reassign steward' }).click()
  const dialog = page.getByRole('dialog', { name: 'Controlled document reassignment' })
  const picker = dialog.getByLabel('Approver 5 search')
  await picker.fill('Software Requirements Author')
  const author = dialog.locator('.personSuggestions button[data-user-name="software.author"]')
  await expect(author).toBeVisible({ timeout: 30_000 })
  await author.click()
  await dialog.getByLabel('Reason').fill('Transfer long-term accountability to the active document author.')
  await dialog.getByRole('button', { name: 'Record reassignment' }).click()
  await expect(page.getByText('Document stewardship was reassigned with immutable evidence.')).toBeVisible()
  await page.reload({ waitUntil: 'load' })
  await expect(page.getByText('Daniel Reyes')).toBeVisible()
  await page.getByRole('button', { name: 'Audit' }).click()
  await expect(page.locator('.mdAudit').getByText('Transfer long-term accountability to the active document author.').first()).toBeVisible()
})

test('Documentation Center back navigation retains a non-showcase Project across refresh', async ({ page, request }) => {
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, { data: {
    programName: `Document navigation ${suffix}`,
    programCode: `DN${suffix}`,
    projectName: `Review Back Project ${suffix}`,
    softwareProduct: 'Document navigation product',
    initialRelease: '1.0',
    initialReleaseIsReleased: false,
  } })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const softwareAuthor = (await usersResponse.json()).find((person:{userName:string})=>person.userName==='software.author')
  const membership = await request.post(`${apiBase}/api/admin/users/${softwareAuthor.id}/memberships`, { data: { programId: workspace.program.id, role: 'Engineer' } })
  expect(membership.ok(), await membership.text()).toBeTruthy()
  const created = await request.post(`${apiBase}/api/managed-documents`, { data: {
    projectId: workspace.project.id,
    acronym: 'SQAP',
    documentType: 'Software Quality Assurance Plan',
    title: `Navigation SQAP ${suffix}`,
    ownerId: 'software.author',
    changeSummary: 'Prove Project-specific back navigation.',
    operationKey: crypto.randomUUID(),
  } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await login(page, 'admin', { openProject: false })
  await page.goto(`/programs/${workspace.program.id}/projects/${workspace.project.id}/documentation-center`)
  await page.getByRole('button', { name: new RegExp(`SQAP-${suffix}|Navigation SQAP ${suffix}`) }).click()
  await page.getByRole('button', { name: /Back to Software Builds/ }).click()

  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/builds$`))
  await page.reload({ waitUntil: 'load' })
  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/builds$`))
  await page.getByRole('button', { name: 'Imported baselines' }).click()
  await expect(page).toHaveURL(new RegExp(`/projects/review-back-project-${suffix}/imported-baselines$`))
})
