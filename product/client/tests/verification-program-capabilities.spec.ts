import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, firstSectionId, openNavigationGroup } from './auth'

test('verification actions follow authority in the selected Program',async({page,request,playwright})=>{
  test.setTimeout(90_000)
  await apiLogin(request)
  const suffix=Date.now().toString().slice(-7)
  const makeWorkspace=async(label:string)=>{
    const response=await request.post(`${apiBase}/api/workspaces`,{data:{
      programName:`${label} ${suffix}`,
      programCode:`${label.slice(0,2).toUpperCase()}${suffix}`,
      projectName:`${label} Verification`,
      softwareProduct:`${label} Product`,
      initialRelease:'1.0',
      initialReleaseIsReleased:false,
    }})
    expect(response.ok(),await response.text()).toBeTruthy()
    return response.json()
  }
  const testWorkspace=await makeWorkspace('Test Authority')
  const approvalWorkspace=await makeWorkspace('Approval Authority')
  const prepareExactRequirement=async(workspace:any,label:string)=>{
    const impacts=JSON.stringify({trace:'Not Affected',verification:'Not Affected',documents:'Not Affected',baseline:'Not Affected',collaboration:'Not Affected'})
    const draftResponse=await request.post(`${apiBase}/api/scr-drafts`,{data:{
      projectId:workspace.project.id,targetReleaseId:workspace.release.id,type:'System',
      title:`${label} exact verification target`,problem:'A controlled target is required.',
      analysis:'Procedure authoring must bind to a materialized revision.',solution:'Introduce one exact revision.',
      requirementChanges:[{level:'System',kind:'Introduce',targetSectionId:await firstSectionId(request,workspace.project.id),statement:`The ${label.toLowerCase()} product shall expose an exact verification target.`,rationale:'Capability qualification.',verificationMethod:'Test',impactDispositionJson:impacts}],
    }})
    expect(draftResponse.ok(),await draftResponse.text()).toBeTruthy()
    const draft=await draftResponse.json()
    const submitted=await request.post(`${apiBase}/api/scrs/${draft.id}/submit`,{data:{approvers:[{userId:'admin',name:'Ignored'}]}})
    expect(submitted.ok(),await submitted.text()).toBeTruthy()
    const approved=await request.post(`${apiBase}/api/scrs/${draft.id}/approve`,{data:{password:'AeroLink!2026',meaning:'Approved for exact verification applicability.'}})
    expect(approved.ok(),await approved.text()).toBeTruthy()
    const baselineResponse=await request.post(`${apiBase}/api/baselines`,{data:{baseNumber:`SW-99.${Date.now().toString().slice(-2)}`,revision:0,projectId:workspace.project.id,releaseId:workspace.release.id,name:`${label} materialized software build`}})
    expect(baselineResponse.ok(),await baselineResponse.text()).toBeTruthy()
    const baseline=await baselineResponse.json()
    for(const [path,data] of [
      [`selections`,{scrId:draft.id}],
      [`freeze`,{}],
      [`materialize-requirements`,{}],
    ] as const){
      const response=await request.post(`${apiBase}/api/baselines/${baseline.id}/${path}`,{data})
      expect(response.ok(),await response.text()).toBeTruthy()
    }
    const requirementsResponse=await request.get(`${apiBase}/api/requirements?projectId=${workspace.project.id}&baselineId=${baseline.id}&scope=System&includeRetired=false&page=1&pageSize=10`)
    expect(requirementsResponse.ok(),await requirementsResponse.text()).toBeTruthy()
    const requirementRevisionId=(await requirementsResponse.json()).items[0].revisionId as string
    const buildResponse=await request.post(`${apiBase}/api/builds`,{data:{
      projectId:workspace.project.id,
      releaseId:workspace.release.id,
      baselineId:baseline.id,
      buildNumber:`${label.replaceAll(' ','-')}-${suffix}`,
      description:`${label} verification fixture`,
    }})
    expect(buildResponse.ok(),await buildResponse.text()).toBeTruthy()
    return {requirementRevisionId,buildId:(await buildResponse.json()).id as string}
  }
  const testTarget=await prepareExactRequirement(testWorkspace,'Test authority')
  const approvalTarget=await prepareExactRequirement(approvalWorkspace,'Approval authority')

  const usersResponse=await request.get(`${apiBase}/api/admin/users`)
  expect(usersResponse.ok(),await usersResponse.text()).toBeTruthy()
  const users=await usersResponse.json()
  const reviewer=users.find((user:{userName:string})=>user.userName==='systems.reviewer')
  expect(reviewer).toBeTruthy()

  const userName=`verification.scope.${suffix}`
  const password='ScopedTest!2026'
  const accountResponse=await request.post(`${apiBase}/api/admin/users`,{data:{
    userName,
    displayName:'Program Scoped Verifier',
    email:`${userName}@example.test`,
    temporaryPassword:password,
  }})
  expect(accountResponse.ok(),await accountResponse.text()).toBeTruthy()
  const account=await accountResponse.json()
  for(const [userId,programId,role] of [
    [account.id,testWorkspace.program.id,'TestEngineer'],
    [account.id,approvalWorkspace.program.id,'Approver'],
    [reviewer.id,testWorkspace.program.id,'Approver'],
    [reviewer.id,approvalWorkspace.program.id,'Approver'],
  ]){
    const grant=await request.post(`${apiBase}/api/admin/users/${userId}/memberships`,{data:{programId,role}})
    expect(grant.ok(),await grant.text()).toBeTruthy()
  }

  const reviewerRequest=await playwright.request.newContext()
  const reviewerLogin=await reviewerRequest.post(`${apiBase}/api/auth/login`,{data:{userName:'systems.reviewer',password:'AeroLink!2026'}})
  expect(reviewerLogin.ok(),await reviewerLogin.text()).toBeTruthy()
  const createProcedure=async(projectId:string,requirementRevisionId:string,title:string,approve=true,approverId='systems.reviewer')=>{
    const createdResponse=await request.post(`${apiBase}/api/test-procedures`,{data:{
      projectId,
      baseNumber:'SERVER-ALLOCATED',
      title,
      objective:'Verify Program-scoped frontend authority.',
      preconditions:'Controlled configuration available.',
      steps:'Exercise the approved behavior.',
      expectedResult:'The expected behavior is observed.',
      requirementRevisionIds:[requirementRevisionId],
      approverId,
      level:'System',
    }})
    expect(createdResponse.ok(),await createdResponse.text()).toBeTruthy()
    const created=await createdResponse.json()
    if(approve){
      const approved=await reviewerRequest.post(`${apiBase}/api/test-procedures/${created.revisionId}/approve`,{data:{
        password:'AeroLink!2026',
        meaning:'Approved independently for capability-surface validation.',
      }})
      expect(approved.ok(),await approved.text()).toBeTruthy()
    }
    return created
  }
  const testProcedure=await createProcedure(testWorkspace.project.id,testTarget.requirementRevisionId,'Test-authority approved procedure')
  const approvalProcedure=await createProcedure(approvalWorkspace.project.id,approvalTarget.requirementRevisionId,'Approval-only approved procedure')
  await createProcedure(approvalWorkspace.project.id,approvalTarget.requirementRevisionId,'Approval-only draft procedure',false,userName)
  for(const [workspace,procedure,buildId] of [[testWorkspace,testProcedure,testTarget.buildId],[approvalWorkspace,approvalProcedure,approvalTarget.buildId]]){
    const recorded=await request.post(`${apiBase}/api/test-executions`,{data:{
      projectId:workspace.project.id,
      procedureRevisionId:procedure.revisionId,
      softwareBuildId:buildId,
      retestOfExecutionId:null,
      outcome:'Pass',
      executedBy:'ignored-client-identity',
      configuration:'Controlled verification environment',
      determination:'The observed result satisfies the approved expected result.',
      evidenceReference:'evidence/program-scope.json',
      executedAt:new Date().toISOString(),
    }})
    expect(recorded.ok(),await recorded.text()).toBeTruthy()
    // Put the procedure in the build's test set as the administrator. Choosing what a build runs is a lead's
    // decision and neither of the accounts under test holds that authority — which is the point of this
    // journey, so the set is prepared here rather than through the page.
    const included=await request.post(`${apiBase}/api/releases/${workspace.release.id}/test-sets/System/procedures`,{
      data:{procedureRevisionIds:[procedure.revisionId],reason:'Chosen',note:'Prepared for the capability journey.'},
    })
    expect(included.ok(),await included.text()).toBeTruthy()
  }
  await reviewerRequest.dispose()

  await page.goto('/')
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:'Replace temporary password'})).toBeVisible()
  const rotatedPassword=`Rotated-${password}`
  await page.getByLabel('Temporary password').fill(password)
  await page.getByLabel('New password',{exact:true}).fill(rotatedPassword)
  await page.getByLabel('Confirm new password').fill(rotatedPassword)
  await page.getByRole('button',{name:/Change password securely/}).click()
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(rotatedPassword)
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:'Projects'})).toBeVisible()
  await page.goto(`/programs/${testWorkspace.program.id}/projects/${testWorkspace.project.id}/releases/${testWorkspace.release.id}/command-center`)
  await expect(page.getByRole('heading',{name:/Command Center/})).toBeVisible()

  // Test Engineer authority in this Program: procedures can be written, and results recorded against the
  // build's test set.
  await openNavigationGroup(page,'ASSURANCE')
  await page.getByRole('link',{name:'System Testing Coverage'}).click()
  await expect(page.getByRole('heading',{name:'Testing Coverage'})).toBeVisible({timeout:30_000})
  await expect(page.getByRole('button',{name:/New test procedure/})).toBeEnabled()

  await page.getByRole('link',{name:'System Test Results'}).click()
  await expect(page.getByRole('heading',{name:'Test Results'})).toBeVisible({timeout:30_000})
  const testRow=page.locator('.testSetRow').filter({hasText:'Test-authority approved procedure'})
  await expect(testRow).toBeVisible({timeout:30_000})
  await testRow.getByRole('button',{name:/Record result|Record retest/}).click()
  await expect(page.getByLabel('Executed by / human determination owner')).toHaveAttribute('readonly','')
  await expect(page.getByLabel('Evidence reference')).toHaveAttribute('required','')
  await page.getByLabel('Outcome').selectOption('Blocked')
  await expect(page.getByLabel('Evidence reference')).not.toHaveAttribute('required','')
  await page.getByRole('button',{name:'Cancel'}).click()

  // Approver authority without Test Engineer authority: a draft can be signed for, and nothing can be run.
  await page.goto(`/programs/${approvalWorkspace.program.id}/projects/${approvalWorkspace.project.id}/releases/${approvalWorkspace.release.id}/system-verification/coverage`)
  await expect(page.getByRole('heading',{name:'Testing Coverage'})).toBeVisible({timeout:30_000})
  await expect(page.getByRole('button',{name:/New test procedure/})).toBeDisabled()
  const draftRow=page.locator('.procedureLibrary .coverageRow').filter({hasText:'Approval-only draft procedure'})
  await expect(draftRow.getByRole('button',{name:'Review & approve'})).toBeVisible({timeout:30_000})

  await page.goto(`/programs/${approvalWorkspace.program.id}/projects/${approvalWorkspace.project.id}/releases/${approvalWorkspace.release.id}/system-verification/results`)
  await expect(page.getByRole('heading',{name:'Test Results'})).toBeVisible({timeout:30_000})
  // Nothing on this page offers to record a determination. The set may be empty here — choosing what a build
  // runs is a lead's job and this account is neither — so the assertion is that no recording control exists
  // at all, which is what an account without Test Engineer authority must see either way.
  await expect(page.getByRole('button',{name:'Record result'})).toHaveCount(0)
  await expect(page.getByRole('button',{name:'Record retest'})).toHaveCount(0)
})
