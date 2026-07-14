import { expect, test } from '@playwright/test'
import { apiBase, apiLogin } from './auth'

test('test procedures require independent electronic approval before execution',async({request,playwright})=>{
  await apiLogin(request)
  const seeded=await request.post(`${apiBase}/api/showcase/seed`);expect(seeded.ok(),await seeded.text()).toBeTruthy();const showcase=await seeded.json()
  const requirementsResponse=await request.get(`${apiBase}/api/requirements?projectId=${showcase.projectId}&baselineId=${showcase.releasedBaselineId}&scope=System&page=1&pageSize=1`)
  expect(requirementsResponse.ok(),await requirementsResponse.text()).toBeTruthy();const requirements=await requirementsResponse.json();expect(requirements.items).toHaveLength(1)

  const engineer=await playwright.request.newContext();const login=await engineer.post(`${apiBase}/api/auth/login`,{data:{userName:'test.engineer',password:'AeroLink!2026'}});expect(login.ok(),await login.text()).toBeTruthy()
  const created=await engineer.post(`${apiBase}/api/test-procedures`,{data:{projectId:showcase.projectId,baseNumber:'CLIENT-IGNORED',title:'Verify independent approval control',objective:'Verify the selected exact requirement.',preconditions:'Released baseline loaded.',steps:'Exercise the required behavior.',expectedResult:'The approved behavior is observed.',requirementRevisionIds:[requirements.items[0].revisionId],level:'System'}})
  expect(created.ok(),await created.text()).toBeTruthy();const procedure=await created.json();expect(procedure.state).toBe('Draft');expect(procedure.displayNumber).toMatch(/^SYSTP-\d{8}\.00$/)

  const execution={projectId:showcase.projectId,procedureRevisionId:procedure.revisionId,softwareBuildId:null,retestOfExecutionId:null,outcome:'Pass',executedBy:'ignored.client.actor',configuration:'Controlled integration rig',determination:'Observed result satisfies the approved expected result.',evidenceReference:'evidence/procedure-approval.json',executedAt:new Date().toISOString()}
  const blocked=await engineer.post(`${apiBase}/api/test-executions`,{data:execution});expect(blocked.status(),await blocked.text()).toBe(400);expect((await blocked.json()).error).toContain('approved')

  const approved=await request.post(`${apiBase}/api/test-procedures/${procedure.revisionId}/approve`,{data:{password:'AeroLink!2026',meaning:'Approved for controlled verification use after independent review.'}})
  expect(approved.ok(),await approved.text()).toBeTruthy();const approval=await approved.json();expect(approval.state).toBe('Approved');expect(approval.contentHash).toMatch(/^[a-f0-9]{64}$/)

  const recorded=await engineer.post(`${apiBase}/api/test-executions`,{data:execution});expect(recorded.ok(),await recorded.text()).toBeTruthy()
  const signatures=await request.get(`${apiBase}/api/signatures?artifactId=${procedure.revisionId}`);expect(signatures.ok(),await signatures.text()).toBeTruthy();expect(await signatures.json()).toEqual(expect.arrayContaining([expect.objectContaining({action:'Approve',contentHash:approval.contentHash})]))
  await engineer.dispose()
})
