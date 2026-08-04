import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, showcaseSeed } from './auth'

test('draft updates preserve controlled identities and normalize new proposals on the server',async({request})=>{
  await apiLogin(request)
  const showcase=await showcaseSeed(request)
  const authoritativeResponse=await request.get(`${apiBase}/api/authoring/requirements?projectId=${showcase.projectId}&scope=System&limit=1`)
  expect(authoritativeResponse.ok(),await authoritativeResponse.text()).toBeTruthy();const authoritative=(await authoritativeResponse.json())[0]

  const draftRequest={baseNumber:'CLIENT-IGNORED',projectId:showcase.projectId,targetReleaseId:showcase.activeReleaseId,title:'Controlled identity normalization probe',problem:'Prove client input cannot corrupt controlled identities.',analysis:'Exercise update normalization.',solution:'Allocate and preserve all identities on the server.',authorId:'ignored.client.actor',type:'System',requirementChanges:[{baseNumber:'CLIENT-IGNORED',revision:77,level:'System',kind:'Introduce',statement:'The system shall preserve server-issued identifiers.',rationale:'Controlled identity integrity.',verificationMethod:'Test',richText:'The system shall preserve server-issued identifiers.',attributesJson:'{}',impactDispositionJson:'{}',isDerived:false}]}
  const created=await request.post(`${apiBase}/api/scr-drafts`,{data:draftRequest});expect(created.ok(),await created.text()).toBeTruthy();const scr=await created.json();expect(scr.requirementChanges).toHaveLength(1)
  const original=scr.requirementChanges[0];expect(original.baseNumber).toMatch(/^SYSR-\d{6}$/);expect(original.revision).toBe(0)

  const checkout=await request.post(`${apiBase}/api/controlled-editing/checkout`,{data:{artifactType:'SCR',artifactId:scr.id,leaseMinutes:15}});expect(checkout.ok(),await checkout.text()).toBeTruthy();const lock=await checkout.json()
  const draft={title:scr.title,problem:scr.problem,analysis:scr.analysis,solution:scr.solution}
  const tamperedSave=await request.put(`${apiBase}/api/controlled-editing/sessions/${lock.id}/autosave`,{data:{expectedVersion:lock.version,draftJson:JSON.stringify({...draft,requirementChanges:[{...original,revision:99}]}),leaseMinutes:15}});expect(tamperedSave.ok(),await tamperedSave.text()).toBeTruthy();const tamperedLock=await tamperedSave.json()
  const tampered=await request.post(`${apiBase}/api/controlled-editing/sessions/${lock.id}/check-in`,{data:{expectedVersion:tamperedLock.version}})
  expect(tampered.status(),await tampered.text()).toBe(400);expect((await tampered.json()).error).toContain('controlled identity')

  const latestDraft={...draft,requirementChanges:[original,{baseNumber:'ATTACKER-CHOICE',revision:42,level:'System',kind:'Introduce',statement:'The system shall allocate another controlled identifier.',rationale:'Second controlled proposal.',verificationMethod:'Inspection',richText:'',attributesJson:'{}',impactDispositionJson:'{}',isDerived:false},{baseNumber:authoritative.baseNumber,revision:9000,level:'System',kind:'Modify',statement:`${authoritative.statement} Updated under controlled change.`,rationale:'Exercise authoritative revision allocation.',verificationMethod:authoritative.verificationMethod,richText:'',attributesJson:'{}',impactDispositionJson:'{}',isDerived:false}]}
  const latestSave=await request.put(`${apiBase}/api/controlled-editing/sessions/${lock.id}/autosave`,{data:{expectedVersion:tamperedLock.version,draftJson:JSON.stringify(latestDraft),leaseMinutes:15}});expect(latestSave.ok(),await latestSave.text()).toBeTruthy();const latestLock=await latestSave.json()
  const saved=await request.post(`${apiBase}/api/controlled-editing/sessions/${lock.id}/check-in`,{data:{expectedVersion:latestLock.version}});expect(saved.ok(),await saved.text()).toBeTruthy()
  const updatedResponse=await request.get(`${apiBase}/api/change-requests/${scr.id}`);expect(updatedResponse.ok(),await updatedResponse.text()).toBeTruthy();const updated=await updatedResponse.json();expect(updated.requirementChanges).toHaveLength(3)
  expect(updated.requirementChanges).toContainEqual(expect.objectContaining({baseNumber:original.baseNumber,revision:original.revision,level:original.level,kind:original.kind}))
  const introduced=updated.requirementChanges.find((item:{statement:string})=>item.statement==='The system shall allocate another controlled identifier.');expect(introduced.baseNumber).toMatch(/^SYSR-\d{6}$/);expect(introduced.baseNumber).not.toBe(original.baseNumber);expect(introduced.revision).toBe(0)
  expect(updated.requirementChanges).toContainEqual(expect.objectContaining({baseNumber:authoritative.baseNumber,revision:authoritative.nextRevision,level:'System',kind:'Modify'}))

  const baselinedResponse=await request.get(`${apiBase}/api/requirements?projectId=${showcase.projectId}&baselineId=${showcase.releasedBaselineId}&scope=System&page=1&pageSize=2`)
  expect(baselinedResponse.ok(),await baselinedResponse.text()).toBeTruthy();const baselined=(await baselinedResponse.json()).items;expect(baselined).toHaveLength(2)
  const trace=await request.post(`${apiBase}/api/trace-links`,{data:{projectId:showcase.projectId,sourceRevisionId:baselined[0].revisionId,targetRevisionId:baselined[1].revisionId,type:'DerivedFrom',rationale:'Controlled-history deletion probe.'}})
  expect(trace.ok(),await trace.text()).toBeTruthy();const deletion=await request.delete(`${apiBase}/api/trace-links/${(await trace.json()).id}`)
  expect(deletion.status(),await deletion.text()).toBe(409);expect((await deletion.json()).code).toBe('controlled_trace_history')
})
