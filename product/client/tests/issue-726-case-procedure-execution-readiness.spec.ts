import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, login } from './auth'

/**
 * #726 UI acceptance: a new project defaults to the Case + Procedure tier, an exact allocated software
 * Procedure executes the Case, the exact Procedure revision is the row in the matching BuildTestSet, the
 * latest build-scoped execution drives release readiness, and checksummed evidence remains a separate gate.
 *
 * The setup is driven through the real API (the only way to bring a controlled Case→Procedure package into
 * existence, exactly as the product does); the browser then proves the two surfaces that consume the chain:
 * the HLR Test Results workspace shows the Procedure as the executable row, and Release Operations shows the
 * exact Case-to-Procedure obligations blocking readiness until a Pass with evidence is recorded.
 */
test('Case to allocated Procedure execution chain drives release readiness', async ({ page, request, playwright }) => {
  test.setTimeout(420_000)
  await apiLogin(request)
  const suffix = Date.now().toString().slice(-7)
  const label = `726-${suffix}`

  // 1. A new project: the real creation seam must default to System [Procedure] and software
  //    [Case, Procedure] (authored Draft), then activate through the sole public activation gate.
  const workspaceResponse = await request.post(`${apiBase}/api/workspaces`, {
    data: {
      programName: `${label} Program`,
      programCode: `7${suffix.slice(-5)}`,
      projectName: `${label} Software`,
      softwareProduct: `${label} Product`,
      initialRelease: '1.0',
      initialReleaseIsReleased: false,
    },
  })
  expect(workspaceResponse.ok(), await workspaceResponse.text()).toBeTruthy()
  const workspace = await workspaceResponse.json()
  const activateResponse = await request.post(
    `${apiBase}/api/projects/${workspace.project.id}/configuration/activate`, {
      data: { expectedVersion: 1, reason: 'Activate the #726 default Case + Procedure verification tier.' },
    })
  expect(activateResponse.ok(), await activateResponse.text()).toBeTruthy()

  // 2. An approved HLR requirement change, a frozen materialized baseline, and an exact build identity.
  const impacts = JSON.stringify({
    trace: 'Not Affected', verification: 'Not Affected', documents: 'Not Affected',
    baseline: 'Not Affected', collaboration: 'Not Affected',
  })
  const sectionId = await firstSectionId(request, workspace.project.id, 'HighLevel')
  const draftResponse = await request.post(`${apiBase}/api/change-request-drafts`, {
    data: {
      projectId: workspace.project.id,
      targetReleaseId: workspace.release.id,
      type: 'Software',
      softwareLevel: 'HighLevel',
      title: `${label} sequencing requirement`,
      problem: 'The build requires controlled sequencing behaviour.',
      analysis: 'An exact HLR revision is required for verification.',
      solution: 'Introduce one exact HLR requirement revision.',
      requirementChanges: [{
        level: 'HighLevel',
        kind: 'Introduce',
        targetSectionId: sectionId,
        statement: 'The software shall sequence eligible waypoints deterministically.',
        rationale: 'Capability qualification for the #726 execution journey.',
        verificationMethod: 'Test',
        isDerived: true,
        impactDispositionJson: impacts,
      }],
    },
  })
  expect(draftResponse.ok(), await draftResponse.text()).toBeTruthy()
  const scr = await draftResponse.json()
  const scrSubmit = await request.post(`${apiBase}/api/change-requests/${scr.id}/submit`, {
    data: { approvers: [{ userId: 'admin', name: 'Ignored' }] },
  })
  expect(scrSubmit.ok(), await scrSubmit.text()).toBeTruthy()
  const scrApprove = await request.post(`${apiBase}/api/change-requests/${scr.id}/approve`, {
    data: { password: 'AeroLink!2026', meaning: 'Approved for the #726 execution journey.' },
  })
  expect(scrApprove.ok(), await scrApprove.text()).toBeTruthy()

  const baselineResponse = await request.post(`${apiBase}/api/baselines`, {
    data: {
      baseNumber: `SW-99.${suffix.slice(-2)}`,
      revision: 0,
      projectId: workspace.project.id,
      releaseId: workspace.release.id,
      name: `${label} materialized build`,
    },
  })
  expect(baselineResponse.ok(), await baselineResponse.text()).toBeTruthy()
  const baseline = await baselineResponse.json()
  for (const [path, data] of [
    ['selections', { changeRequestId: scr.id }],
    ['freeze', {}],
    ['materialize-requirements', {}],
  ] as const) {
    const response = await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`, { data })
    expect(response.ok(), `${path}: ${await response.text()}`).toBeTruthy()
  }
  const requirementsResponse = await request.get(
    `${apiBase}/api/requirements?projectId=${workspace.project.id}&baselineId=${baseline.id}` +
    '&scope=HighLevelSoftware&includeRetired=false&page=1&pageSize=10')
  expect(requirementsResponse.ok(), await requirementsResponse.text()).toBeTruthy()
  const requirementRevisionId = (await requirementsResponse.json()).items[0].revisionId as string
  const buildResponse = await request.post(`${apiBase}/api/builds`, {
    data: {
      projectId: workspace.project.id,
      releaseId: workspace.release.id,
      baselineId: baseline.id,
      buildNumber: `B-${suffix}`,
      description: `${label} verification build`,
    },
  })
  expect(buildResponse.ok(), await buildResponse.text()).toBeTruthy()
  const build = await buildResponse.json()

  // 3. The Case package: a controlled HLR Case change introduced by the approved requirement change,
  //    approved through the package's review, and materialized into the exact baseline.
  const reviewerRequest = await playwright.request.newContext()
  const reviewerLogin = await reviewerRequest.post(`${apiBase}/api/auth/login`, {
    data: { userName: 'systems.reviewer', password: 'AeroLink!2026' },
  })
  expect(reviewerLogin.ok(), await reviewerLogin.text()).toBeTruthy()
  const usersResponse = await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(), await usersResponse.text()).toBeTruthy()
  const reviewer = (await usersResponse.json()).find(
    (user: { userName: string }) => user.userName === 'systems.reviewer')
  expect(reviewer, 'the seeded reviewer account must exist').toBeTruthy()
  const grant = await request.post(`${apiBase}/api/admin/users/${reviewer.id}/memberships`, {
    data: { programId: workspace.program.id, role: 'Approver' },
  })
  expect(grant.ok(), await grant.text()).toBeTruthy()

  const caseReviewResponse = await request.post(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-requests`, {
      data: {
        discipline: 'HighLevelSoftware',
        artifactKind: 'Case',
        title: `${label} sequencing case package`,
        problem: 'The introduced HLR requirement has no controlled verification case.',
        analysis: 'A software Case must cover the exact introduced HLR revision.',
        solution: 'Approve and materialize the proposed Case.',
        changeRequestIds: [scr.id],
      },
    })
  expect(caseReviewResponse.ok(), await caseReviewResponse.text()).toBeTruthy()
  const caseReview = await caseReviewResponse.json()
  const caseChangeResponse = await request.post(
    `${apiBase}/api/test-change-reviews/${caseReview.id}/case-changes`, {
      data: {
        kind: 'Introduce',
        revision: 0,
        title: `${label} sequencing case`,
        objective: 'Verify the exact introduced HLR sequencing requirement.',
        preconditions: 'Controlled configuration available.',
        steps: 'Exercise the approved sequencing behaviour.',
        expectedResult: 'The expected sequencing behaviour is observed.',
        rationale: 'Nothing in this build covers the new requirement.',
        drivingRequirementRevisionIds: [requirementRevisionId],
      },
    })
  expect(caseChangeResponse.ok(), await caseChangeResponse.text()).toBeTruthy()
  const caseChange = await caseChangeResponse.json()
  const impactResponse = await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/verification-impact`)
  expect(impactResponse.ok(), await impactResponse.text()).toBeTruthy()
  for (const item of (await impactResponse.json()).filter(
    (candidate: { testChangeReviewId: string }) => candidate.testChangeReviewId === caseReview.id)) {
    const resolved = await request.post(`${apiBase}/api/verification-impact/${item.id}/resolve`, {
      data: {
        outcome: 'NewProcedureRequired',
        rationale: 'The Case proposed on this package will cover it.',
      },
    })
    expect(resolved.ok(), await resolved.text()).toBeTruthy()
  }
  const caseSubmit = await request.post(`${apiBase}/api/test-change-reviews/${caseReview.id}/submit`, {
    data: { approverId: 'systems.reviewer' },
  })
  expect(caseSubmit.ok(), await caseSubmit.text()).toBeTruthy()
  const caseApprove = await reviewerRequest.post(
    `${apiBase}/api/test-change-reviews/${caseReview.id}/approve`, {
      data: {
        rationale: 'The proposed Case is complete and technically sound.',
        password: 'AeroLink!2026',
        meaning: 'I approve this exact Case test change request package.',
      },
    })
  expect(caseApprove.ok(), await caseApprove.text()).toBeTruthy()
  const procedureSourcesResponse = await request.get(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-request-sources` +
    '?discipline=HighLevelSoftware&artifactKind=Procedure')
  expect(procedureSourcesResponse.ok(), await procedureSourcesResponse.text()).toBeTruthy()
  const procedureSources = await procedureSourcesResponse.json() as Array<{
    sourceKind: string; sourceId: string; displayNumber: string; selectable: boolean
  }>
  expect(procedureSources.some(source => source.sourceKind === 'CaseChange'
    && source.sourceId === caseChange.id && source.selectable)).toBeTruthy()
  const caseSelected = await request.post(`${apiBase}/api/baselines/${baseline.id}/test-change-requests`, {
    data: { testChangeRequestId: caseReview.id },
  })
  expect(caseSelected.ok(), await caseSelected.text()).toBeTruthy()
  const caseMaterialized = await request.post(
    `${apiBase}/api/baselines/${baseline.id}/materialize-test-procedures`, { data: {} })
  expect(caseMaterialized.ok(), await caseMaterialized.text()).toBeTruthy()
  const caseSearchResponse = await request.get(
    `${apiBase}/api/test-cases?projectId=${workspace.project.id}&scope=HighLevelSoftware` +
    `&search=${encodeURIComponent(caseChange.baseNumber)}&page=1&pageSize=1`)
  expect(caseSearchResponse.ok(), await caseSearchResponse.text()).toBeTruthy()
  const exactCase = (await caseSearchResponse.json()).items[0]
  expect(exactCase, 'materialisation produced no exact Case revision').toBeTruthy()
  expect(exactCase.state).toBe('Approved')
  const caseRevisionId = exactCase.revisionId as string

  // 4. The Procedure package: an exact allocated software Procedure whose parent is the materialized Case
  //    revision. The package names the exact Case change as its origin, so the link is not fabricated.
  const procedureReviewResponse = await request.post(
    `${apiBase}/api/releases/${workspace.release.id}/test-change-requests`, {
      data: {
        discipline: 'HighLevelSoftware',
        artifactKind: 'Procedure',
        title: `${label} sequencing procedure package`,
        caseChangeIds: [caseChange.id],
      },
    })
  expect(procedureReviewResponse.ok(), await procedureReviewResponse.text()).toBeTruthy()
  const procedureReview = await procedureReviewResponse.json()
  const procedureChangeResponse = await request.post(
    `${apiBase}/api/test-change-reviews/${procedureReview.id}/procedure-changes`, {
      data: {
        kind: 'Introduce',
        revision: 0,
        title: `${label} sequencing procedure`,
        objective: 'Execute the exact allocated Case.',
        preconditions: 'Controlled configuration available.',
        steps: 'Exercise the approved sequencing behaviour and record each observation.',
        expectedResult: 'Every observation meets the Case acceptance criteria.',
        rationale: 'The exact Case must be executed by its allocated Procedure.',
        parentKind: 'Allocated',
        parentRevisionIds: [caseRevisionId],
        environmentSetup: 'Controlled configuration loaded.',
        testData: 'Approved sequencing scenario data.',
        orderedSteps: 'Initialize, stimulate, observe, and record.',
        expectedObservations: 'Every observation meets the Case acceptance criteria.',
        cleanup: 'Restore the controlled fixture.',
        toolingAutomation: 'Qualified verification runner.',
      },
    })
  expect(procedureChangeResponse.ok(), await procedureChangeResponse.text()).toBeTruthy()
  const procedureCase = await request.post(
    `${apiBase}/api/test-change-reviews/${procedureReview.id}/case`, {
      data: {
        title: `${label} sequencing procedure package`,
        problem: 'The exact allocated Case has no executing Procedure.',
        analysis: 'The approved Case must be executed by an allocated software Procedure.',
        solution: 'Approve and materialize the allocated Procedure.',
      },
    })
  expect(procedureCase.ok(), await procedureCase.text()).toBeTruthy()
  const procedureSubmit = await request.post(
    `${apiBase}/api/test-change-reviews/${procedureReview.id}/submit`, {
      data: { approverId: 'systems.reviewer' },
    })
  expect(procedureSubmit.ok(), await procedureSubmit.text()).toBeTruthy()
  const procedureApprove = await reviewerRequest.post(
    `${apiBase}/api/test-change-reviews/${procedureReview.id}/approve`, {
      data: {
        rationale: 'The allocated Procedure decision is complete and technically sound.',
        password: 'AeroLink!2026',
        meaning: 'I approve this exact Procedure test change request package.',
      },
    })
  expect(procedureApprove.ok(), await procedureApprove.text()).toBeTruthy()
  const procedureSelected = await request.post(`${apiBase}/api/baselines/${baseline.id}/test-change-requests`, {
    data: { testChangeRequestId: procedureReview.id },
  })
  expect(procedureSelected.ok(), await procedureSelected.text()).toBeTruthy()
  const procedureMaterialized = await request.post(
    `${apiBase}/api/baselines/${baseline.id}/materialize-test-procedures`, { data: {} })
  expect(procedureMaterialized.ok(), await procedureMaterialized.text()).toBeTruthy()
  const procedureSearchResponse = await request.get(
    `${apiBase}/api/test-procedures?projectId=${workspace.project.id}` +
    `&search=${encodeURIComponent(`${label} sequencing procedure`)}&artifactKind=Procedure&page=1&pageSize=1`)
  expect(procedureSearchResponse.ok(), await procedureSearchResponse.text()).toBeTruthy()
  const procedure = (await procedureSearchResponse.json()).items[0]
  expect(procedure, 'materialisation produced no software Procedure revision').toBeTruthy()
  expect(procedure.state).toBe('Approved')
  expect(procedure.displayNumber).toMatch(/^HLRTP-\d{6}\.\d{2}$/)
  const procedureRevisionId = procedure.revisionId as string

  // #762 API boundary: this activated Case + Procedure profile exposes one globally sorted mixed inventory,
  // while the historical procedure alias remains a Case compatibility surface until Procedure is explicit.
  const mixedResponse = await request.get(
    `${apiBase}/api/verification-artifacts?projectId=${workspace.project.id}&releaseId=${workspace.release.id}` +
    '&scope=Software&sort=owner&page=1&pageSize=200')
  expect(mixedResponse.ok(), await mixedResponse.text()).toBeTruthy()
  const mixed = await mixedResponse.json()
  expect(new Set(mixed.items.map((item: { artifactKind: string }) => item.artifactKind))).toEqual(new Set(['Case', 'Procedure']))
  expect(mixed.totalPages).toBe(Math.ceil(mixed.totalCount / mixed.pageSize))
  const mixedPageOneResponse = await request.get(
    `${apiBase}/api/verification-artifacts?projectId=${workspace.project.id}&releaseId=${workspace.release.id}` +
    '&scope=Software&sort=owner&page=1&pageSize=1')
  const mixedPageTwoResponse = await request.get(
    `${apiBase}/api/verification-artifacts?projectId=${workspace.project.id}&releaseId=${workspace.release.id}` +
    '&scope=Software&sort=owner&page=2&pageSize=1')
  expect(mixedPageOneResponse.ok(), await mixedPageOneResponse.text()).toBeTruthy()
  expect(mixedPageTwoResponse.ok(), await mixedPageTwoResponse.text()).toBeTruthy()
  const mixedPageOne = await mixedPageOneResponse.json()
  const mixedPageTwo = await mixedPageTwoResponse.json()
  expect(mixedPageOne.totalPages).toBe(Math.ceil(mixedPageOne.totalCount / mixedPageOne.pageSize))
  expect(mixedPageOne.items[0].id).not.toBe(mixedPageTwo.items[0].id)
  expect(`${mixedPageOne.items[0].ownerId}\u0000${mixedPageOne.items[0].displayNumber}` <= `${mixedPageTwo.items[0].ownerId}\u0000${mixedPageTwo.items[0].displayNumber}`).toBeTruthy()
  const compatibility = await request.get(
    `${apiBase}/api/test-procedures?projectId=${workspace.project.id}&releaseId=${workspace.release.id}&scope=Software&page=1&pageSize=200`)
  expect(compatibility.ok(), await compatibility.text()).toBeTruthy()
  expect((await compatibility.json()).items.every((item: { artifactKind: string }) => item.artifactKind === 'Case')).toBeTruthy()
  const procedureOnly = await request.get(
    `${apiBase}/api/test-procedures?projectId=${workspace.project.id}&releaseId=${workspace.release.id}&scope=Software&artifactKind=Procedure&page=1&pageSize=200`)
  expect(procedureOnly.ok(), await procedureOnly.text()).toBeTruthy()
  expect((await procedureOnly.json()).items.every((item: { artifactKind: string }) => item.artifactKind === 'Procedure')).toBeTruthy()
  const procedureTrace = await request.get(
    `${apiBase}/api/test-procedures/${procedure.id}/trace?releaseId=${workspace.release.id}&revisionId=${procedureRevisionId}`)
  expect(procedureTrace.ok(), await procedureTrace.text()).toBeTruthy()
  const trace = await procedureTrace.json()
  expect(trace.artifactKind).toBe('Procedure')
  expect(trace.requirements).toHaveLength(0)
  expect(trace.caseParents.some((parent: { caseRevisionId: string }) => parent.caseRevisionId === caseRevisionId)).toBeTruthy()
  const configuredDocuments = await request.get(
    `${apiBase}/api/projects/${workspace.project.id}/test-artifacts?scope=Software`)
  expect(configuredDocuments.ok(), await configuredDocuments.text()).toBeTruthy()
  const documents = await configuredDocuments.json()
  expect(new Set(documents.map((document: { level: string; artifactKind: string }) => `${document.level}:${document.artifactKind}`))).toEqual(
    new Set(['HighLevel:Case', 'HighLevel:Procedure', 'LowLevel:Case', 'LowLevel:Procedure']))
  const procedureCommentResponse = await request.post(`${apiBase}/api/test-procedures/${procedure.id}/comments`, {
    data: { releaseId: workspace.release.id, revisionId: procedureRevisionId, body: `#762 Procedure discussion ${Date.now()}`, mentions: [] },
  })
  expect(procedureCommentResponse.status(), await procedureCommentResponse.text()).toBe(201)
  const procedureComment = await procedureCommentResponse.json() as { id: string }
  const procedureCommentResolved = await request.post(
    `${apiBase}/api/enterprise-requirements/comments/${procedureComment.id}/resolve`, {
      data: { releaseId: workspace.release.id, disposition: 'The exact Case-to-Procedure discussion was reviewed.' },
    })
  expect(procedureCommentResolved.status(), await procedureCommentResolved.text()).toBe(204)

  // 5. Campaign + build scope + BuildTestSet selection.
  const campaignResponse = await request.post(`${apiBase}/api/release-campaigns`, {
    data: {
      projectId: workspace.project.id,
      releaseId: workspace.release.id,
      baselineId: baseline.id,
      name: `${label} Release Campaign`,
    },
  })
  expect(campaignResponse.ok(), await campaignResponse.text()).toBeTruthy()
  const campaign = await campaignResponse.json()
  const startVerification = await request.post(
    `${apiBase}/api/release-campaigns/${campaign.id}/start-verification`, { data: {} })
  expect(startVerification.ok(), await startVerification.text()).toBeTruthy()
  const selectBuild = await request.post(
    `${apiBase}/api/release-campaigns/${campaign.id}/verification-build`, {
      data: { softwareBuildId: build.id },
    })
  expect(selectBuild.ok(), await selectBuild.text()).toBeTruthy()
  // 6. The user visibly searches for and adds the exact Procedure through the real Test Results UI. The
  //    contract reports the effective executable kind, so software under the full profile searches and
  //    selects Procedures — this would fail if the UI still hard-coded the Case segment.
  await login(page, 'admin', { openProject: false })
  const root = `/programs/${workspace.program.id}/projects/${workspace.project.id}/releases/${workspace.release.id}`
  // #762 full-profile navigation contract: the activated ladder exposes four exact ordered package keys.
  // Each tab keeps its raw kind separate from the route key, so Case retains the legacy level URL while
  // Procedure carries the explicit kind query. The CTA and destination must name the selected artifact.
  await page.goto(`${root}/software-verification/hlr/change-requests`)
  const packageTabs = page.getByRole('tab')
  await expect(packageTabs).toHaveCount(4, { timeout: 30_000 })
  expect((await packageTabs.allTextContents()).map(text => text.replace(/\s+/g, ' ').trim())).toEqual([
    'HLRTest cases', 'HLRTest procedures', 'LLRTest cases', 'LLRTest procedures',
  ])
  for (const [name, path, cta, heading] of [
    ['HLR Test cases', '/software-verification/hlr/change-requests', '+ New HLR Test Case Change Request', 'Create HLR Test Case Change Request'],
    ['HLR Test procedures', '/software-verification/hlr/change-requests?kind=Procedure', '+ New HLR Test Procedure Change Request', 'Create HLR Test Procedure Change Request'],
    ['LLR Test cases', '/software-verification/llr/change-requests', '+ New LLR Test Case Change Request', 'Create LLR Test Case Change Request'],
    ['LLR Test procedures', '/software-verification/llr/change-requests?kind=Procedure', '+ New LLR Test Procedure Change Request', 'Create LLR Test Procedure Change Request'],
  ] as const) {
    await page.getByRole('tab', { name }).click()
    await expect(page).toHaveURL(new RegExp(`${path.replace(/[?]/g, '\\?')}$`))
    await expect(page.getByRole('button', { name: cta })).toBeVisible()
    await page.reload()
    await expect(page.getByRole('tab', { name })).toHaveAttribute('aria-selected', 'true')
    await expect(page.getByRole('button', { name: cta })).toBeVisible()
    await page.getByRole('button', { name: cta }).click()
    await expect(page).toHaveURL(new RegExp(`${path.split('/change-requests')[0]}/change-requests/new${name.includes('procedures') ? '\\?kind=Procedure' : ''}$`))
    await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible()
    await page.goBack()
    await expect(page.getByRole('tab', { name })).toHaveAttribute('aria-selected', 'true')
  }
  await page.goForward()
  await expect(page.getByRole('heading', { name: /Create (HLR|LLR) Test (Case|Procedure) Change Request/, level: 1 })).toBeVisible()
  await page.goto(`${root}/software-verification/hlr/change-requests`)
  // Exercise all four shared-editor contracts against the activated fixture. The package mutation itself is
  // already proven above for the real HLR Case and HLR Procedure packages; these isolated UI submissions
  // ensure the LLR paths also send their exact kind/origin fields without creating duplicate governed rows.
  const editorSubmissions: Record<string, unknown>[] = []
  await page.route('**/api/releases/*/test-change-request-sources*', async route => {
    const url = new URL(route.request().url())
    const llr = url.searchParams.get('discipline') === 'LowLevelSoftware'
    const procedureKind = url.searchParams.get('artifactKind') === 'Procedure'
    const sourceId = `${llr ? 'LLR' : 'HLR'}-source-${procedureKind ? 'procedure' : 'case'}`
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(procedureKind
      ? [{ sourceKind: 'CaseChange', sourceId, displayNumber: `${llr ? 'LLRTC' : 'HLRTC'}-000762.00`,
          title: 'Exact Case change origin', state: 'Approved', selectable: true }]
      : [{ changeRequestId: sourceId, displayNumber: `${llr ? 'LLRCR' : 'HLRCR'}-000762.00`,
          title: 'Approved Case change source', state: 'Approved', selectable: true }]) })
  })
  await page.route('**/api/releases/*/test-change-requests', async route => {
    editorSubmissions.push(route.request().postDataJSON() as Record<string, unknown>)
    await route.fulfill({ status: 201, contentType: 'application/json', body: JSON.stringify({ id: `editor-${editorSubmissions.length}`, displayNumber: 'EDITOR-000762.00' }) })
  })
  for (const [level, kind] of [['hlr', 'Case'], ['hlr', 'Procedure'], ['llr', 'Case'], ['llr', 'Procedure']] as const) {
    const procedureKind = kind === 'Procedure'
    await page.goto(`${root}/software-verification/${level}/change-requests/new${procedureKind ? '?kind=Procedure' : ''}`)
    await expect(page.getByRole('heading', { name: `Create ${level === 'hlr' ? 'HLR' : 'LLR'} Test ${kind} Change Request`, level: 1 })).toBeVisible()
    const source = page.locator('label').filter({ hasText: procedureKind ? 'Exact Case change origin' : 'Approved Case change source' }).first()
    await expect(source).toBeVisible()
    await source.locator('input').check()
    const editor = page.locator('[data-tcr-editor]')
    await editor.getByLabel('Title').fill(`${level.toUpperCase()} ${kind} editor contract`)
    for (const field of ['Problem', 'Analysis', 'Solution'])
      await editor.getByLabel(field).fill(`${field} for ${level} ${kind}.`)
    const raise = page.getByRole('button', { name: `Raise ${procedureKind ? level === 'hlr' ? 'HLRTPCR' : 'LLRTPCR' : level === 'hlr' ? 'HLRTCCR' : 'LLRTCCR'}` })
    await expect(raise).toBeEnabled()
    await raise.click()
    await expect.poll(() => editorSubmissions.length).toBe(editorSubmissions.length + 1)
  }
  expect(editorSubmissions).toHaveLength(4)
  expect(editorSubmissions.filter(body => body.artifactKind === 'Procedure')).toHaveLength(2)
  expect(editorSubmissions.filter(body => body.artifactKind !== 'Procedure' && body.changeRequestIds)).toHaveLength(2)
  for (const body of editorSubmissions.filter(item => item.artifactKind === 'Procedure')) {
    expect(body.caseChangeIds).toHaveLength(1)
    expect(body).not.toHaveProperty('changeRequestIds')
    expect(body).not.toHaveProperty('problemReportIds')
  }
  // The same activated fixture proves the combined Explorer: mixed rows and badges, all four configured
  // document rails, exact kind/level filters, search, and the Procedure inspector's Case-parent trace.
  await page.goto(`${root}/software-verification/test-artifacts`)
  await expect(page.getByRole('heading', { name: 'Software Test Case/Procedure Explorer', level: 1 })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByLabel('Artifact filter')).toHaveValue('all')
  const caseExplorerRow = page.locator('.procedureRow').filter({ hasText: /Case/ })
  const procedureExplorerRow = page.locator('.procedureRow').filter({ hasText: /Procedure/ })
  await expect(caseExplorerRow).toHaveCount(1, { timeout: 30_000 })
  await expect(procedureExplorerRow).toHaveCount(1, { timeout: 30_000 })
  await expect(caseExplorerRow.locator('span').nth(2)).toHaveText('1')
  await expect(procedureExplorerRow.locator('span').nth(2)).toHaveText('1')
  for (const documentNumber of ['HLRTD', 'HLRTPD', 'LLRTD', 'LLRTPD'])
    await expect(page.locator(`[data-document^="${documentNumber}-"]`)).toBeVisible()
  await page.getByLabel('Artifact filter').selectOption('Procedure')
  await expect(page.locator('.procedureRow')).toHaveCount(1)
  await page.getByLabel('Level filter').selectOption('HighLevel')
  await expect(page.locator('.procedureRow')).toHaveCount(1)
  await page.getByLabel('Find a procedure').fill(procedure.displayNumber)
  await expect(page.locator('[data-procedure]').filter({ hasText: procedure.displayNumber })).toBeVisible()
  await page.locator('[data-procedure]').filter({ hasText: procedure.displayNumber }).getByRole('button').click()
  await page.getByRole('button', { name: 'Trace & impact' }).click()
  await expect(page.getByRole('list', { name: 'Exact Case parents' })).toContainText(exactCase.displayNumber)
  await page.goto(`${root}/software-verification/hlr/results`)
  await expect(page.getByRole('heading', { name: 'Test Results' })).toBeVisible({ timeout: 30_000 })
  await page.getByLabel('Find an approved procedure').fill(`${label} sequencing procedure`)
  const candidate = page.locator('.testSetCandidates label').filter({ hasText: procedure.displayNumber }).first()
  await expect(candidate).toBeVisible({ timeout: 30_000 })
  await candidate.locator('input[type="checkbox"]').check()
  await page.getByRole('button', { name: 'Add — covers a change' }).click()
  const procedureRow = page.locator('.testSetRow').filter({ hasText: procedure.displayNumber }).first()
  await expect(procedureRow).toBeVisible({ timeout: 30_000 })
  await expect(procedureRow).toContainText(`${label} sequencing procedure`)

  const readinessOf = async () => {
    const response = await request.get(`${apiBase}/api/release-campaigns/${campaign.id}`)
    expect(response.ok(), await response.text()).toBeTruthy()
    return (await response.json()).readiness as {
      gates: { code: string; complete: boolean; completed: number; total: number; detail: string }[]
    }
  }

  // 7. Negative state: the first build-scoped determination is a Fail, so readiness must NOT count it.
  const failedExecution = await request.post(`${apiBase}/api/test-executions`, {
    data: {
      projectId: workspace.project.id,
      procedureRevisionId,
      softwareBuildId: build.id,
      retestOfExecutionId: null,
      outcome: 'Fail',
      configuration: 'Controlled verification environment',
      determination: 'The observed output did not meet the Case acceptance criteria.',
      evidenceReference: 'evidence/726-first-attempt.json',
      executedAt: new Date().toISOString(),
    },
  })
  expect(failedExecution.ok(), await failedExecution.text()).toBeTruthy()
  const failedExecutionId = (await failedExecution.json()).id as string
  let readiness = await readinessOf()
  const coverageBefore = readiness.gates.find(gate => gate.code === 'coverage')!
  const verificationBefore = readiness.gates.find(gate => gate.code === 'verification')!
  const evidenceBefore = readiness.gates.find(gate => gate.code === 'evidence')!
  expect(coverageBefore.complete).toBe(false)
  expect(coverageBefore.detail).toContain('exact Case-to-Procedure obligation')
  expect(verificationBefore.complete).toBe(false)
  expect(evidenceBefore.complete).toBe(false)

  // 7b. The real UI surface: Release Operations shows the same obligation the API reported.
  await page.goto(`${root}/release-readiness/operations`)
  await expect(page.getByRole('heading', { name: /Release Operations|Release campaign/i }))
    .toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('exact Case-to-Procedure obligation', { exact: false }).first())
    .toHaveCount(1)

  // 8. Positive state: a later Pass with checksummed evidence. Only then do the verification and evidence
  //    gates close; the coverage obligation closes on the latest build-scoped Pass.
  const passedExecution = await request.post(`${apiBase}/api/test-executions`, {
    data: {
      projectId: workspace.project.id,
      procedureRevisionId,
      softwareBuildId: build.id,
      retestOfExecutionId: failedExecutionId,
      outcome: 'Pass',
      configuration: 'Controlled verification environment',
      determination: 'The observed output meets the Case acceptance criteria.',
      evidenceReference: 'evidence/726-pass.json',
      executedAt: new Date(Date.now() + 1_000).toISOString(),
    },
  })
  expect(passedExecution.ok(), await passedExecution.text()).toBeTruthy()
  const passedExecutionId = (await passedExecution.json()).id as string
  const evidenceResponse = await request.post(`${apiBase}/api/evidence`, {
    multipart: {
      projectId: workspace.project.id,
      file: {
        name: '726-pass.json',
        mimeType: 'application/json',
        buffer: Buffer.from(JSON.stringify({ executionId: passedExecutionId, outcome: 'Pass' })),
      },
    },
  })
  expect(evidenceResponse.ok(), await evidenceResponse.text()).toBeTruthy()
  const evidence = await evidenceResponse.json()
  const linked = await request.post(
    `${apiBase}/api/test-executions/${passedExecutionId}/evidence/${evidence.id}`, { data: {} })
  expect(linked.ok(), await linked.text()).toBeTruthy()

  readiness = await readinessOf()
  const coverageAfter = readiness.gates.find(gate => gate.code === 'coverage')!
  const verificationAfter = readiness.gates.find(gate => gate.code === 'verification')!
  const evidenceAfter = readiness.gates.find(gate => gate.code === 'evidence')!
  expect(coverageAfter.complete).toBe(true)
  expect(coverageAfter.completed).toBe(coverageAfter.total)
  expect(verificationAfter.complete).toBe(true)
  expect(verificationAfter.completed).toBe(1)
  expect(evidenceAfter.complete).toBe(true)
  expect(evidenceAfter.completed).toBe(1)

  // The same surfaces now show the closed gates.
  await page.goto(`${root}/release-readiness/operations`)
  await expect(page.getByRole('heading', { name: /Release Operations|Release campaign/i }))
    .toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('exact Case-to-Procedure obligation', { exact: false })).toHaveCount(0)
  await expect(page.getByText('Gate complete.', { exact: true }).first()).toHaveCount(1)
  await page.goto(`${root}/software-verification/hlr/results`)
  await expect(procedureRow).toBeVisible({ timeout: 30_000 })

  await reviewerRequest.dispose()
})
