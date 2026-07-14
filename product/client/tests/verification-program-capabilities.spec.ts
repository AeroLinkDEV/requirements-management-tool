import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, openNavigationGroup } from './auth'

test('verification actions follow authority in the selected Program',async({page,request,playwright})=>{
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
  const createProcedure=async(projectId:string,title:string,approve=true)=>{
    const createdResponse=await request.post(`${apiBase}/api/test-procedures`,{data:{
      projectId,
      baseNumber:'SERVER-ALLOCATED',
      title,
      objective:'Verify Program-scoped frontend authority.',
      preconditions:'Controlled configuration available.',
      steps:'Exercise the approved behavior.',
      expectedResult:'The expected behavior is observed.',
      requirementRevisionIds:[],
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
  const testProcedure=await createProcedure(testWorkspace.project.id,'Test-authority approved procedure')
  const approvalProcedure=await createProcedure(approvalWorkspace.project.id,'Approval-only approved procedure')
  await createProcedure(approvalWorkspace.project.id,'Approval-only draft procedure',false)
  for(const [workspace,procedure] of [[testWorkspace,testProcedure],[approvalWorkspace,approvalProcedure]]){
    const recorded=await request.post(`${apiBase}/api/test-executions`,{data:{
      projectId:workspace.project.id,
      procedureRevisionId:procedure.revisionId,
      softwareBuildId:null,
      retestOfExecutionId:null,
      outcome:'Pass',
      executedBy:'ignored-client-identity',
      configuration:'Controlled verification environment',
      determination:'The observed result satisfies the approved expected result.',
      evidenceReference:'evidence/program-scope.json',
      executedAt:new Date().toISOString(),
    }})
    expect(recorded.ok(),await recorded.text()).toBeTruthy()
  }
  await reviewerRequest.dispose()

  await page.goto('/')
  await page.getByLabel('Username').fill(userName)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:/Command Center/})).toBeVisible()

  await page.getByLabel('Active program').selectOption({label:`Test Authority ${suffix}`})
  await openNavigationGroup(page,'VERIFICATION')
  await page.getByRole('link',{name:'System Verification'}).click()
  await expect(page.getByRole('heading',{name:'Verification & Evidence'})).toBeVisible()
  await expect(page.getByRole('button',{name:/New Test Procedure/})).toBeEnabled()
  await page.getByRole('button',{name:/Test procedures/}).click()
  const testRow=page.locator('.procedureRow').filter({hasText:'Test-authority approved procedure'})
  await expect(testRow.getByRole('button',{name:'Record result'})).toBeVisible()
  await testRow.getByRole('button',{name:'Record result'}).click()
  await expect(page.getByLabel('Executed by / human determination owner')).toHaveAttribute('readonly','')
  await expect(page.getByLabel('Evidence reference')).toHaveAttribute('required','')
  await page.getByLabel('Outcome').selectOption('Blocked')
  await expect(page.getByLabel('Evidence reference')).not.toHaveAttribute('required','')
  await page.locator('.resultForm').getByRole('button',{name:'Cancel'}).click()
  await page.getByRole('button',{name:/Execution history/}).click()
  await expect(page.getByText('Upload evidence',{exact:true})).toBeVisible()

  await page.getByLabel('Active program').selectOption({label:`Approval Authority ${suffix}`})
  await expect(page.getByRole('button',{name:/New Test Procedure/})).toBeDisabled()
  await page.getByRole('button',{name:/Test procedures/}).click()
  const draftRow=page.locator('.procedureRow').filter({hasText:'Approval-only draft procedure'})
  await expect(draftRow.getByRole('button',{name:'Review & approve'})).toBeVisible()
  const approvedRow=page.locator('.procedureRow').filter({hasText:'Approval-only approved procedure'})
  await expect(approvedRow.getByRole('button',{name:'Record result'})).toHaveCount(0)
  await expect(approvedRow.getByText(/Test Engineer authority is required/)).toBeVisible()
  await page.getByRole('button',{name:/Execution history/}).click()
  await expect(page.getByText('Upload evidence',{exact:true})).toHaveCount(0)
})
