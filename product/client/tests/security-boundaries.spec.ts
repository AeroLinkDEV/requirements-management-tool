import { expect, test } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { apiBase, apiLogin } from './auth'

test('mutation authentication and Program-scoped discovery prevent direct-object access',async({request,playwright})=>{
 const anonymous=await playwright.request.newContext();const deniedSeed=await anonymous.post(`${apiBase}/api/showcase/seed`);expect(deniedSeed.status()).toBe(401);await anonymous.dispose()
 await apiLogin(request);const suffix=Date.now().toString().slice(-7);const created=await request.post(`${apiBase}/api/workspaces`,{data:{programName:`Restricted ${suffix}`,programCode:`RX${suffix}`,projectName:'Restricted Project',softwareProduct:'Restricted Product',initialRelease:'1.0',initialReleaseIsReleased:false}});expect(created.ok(),await created.text()).toBeTruthy();const workspace=await created.json()
 const outsider=await playwright.request.newContext();const login=await outsider.post(`${apiBase}/api/auth/login`,{data:{userName:'systems.reviewer',password:'AeroLink!2026'}});expect(login.ok(),await login.text()).toBeTruthy();const discovery=await outsider.get(`${apiBase}/api/search?projectId=${workspace.project.id}&query=SCR`);expect(discovery.status()).toBe(403)
 const unknown=randomUUID()
 const denied:[string,Awaited<ReturnType<typeof outsider.post>>][]=[
  ['build',await outsider.post(`${apiBase}/api/builds`,{data:{projectId:workspace.project.id,releaseId:workspace.release.id,baselineId:unknown,buildNumber:'OUTSIDER-BUILD',description:'Unauthorized',recordedBy:'outsider'}})],
  ['release',await outsider.post(`${apiBase}/api/releases`,{data:{projectId:workspace.project.id,version:'2.0',predecessorReleaseId:null}})],
  ['trace',await outsider.post(`${apiBase}/api/trace-links`,{data:{projectId:workspace.project.id,sourceRevisionId:unknown,targetRevisionId:randomUUID(),type:'DerivedFrom',rationale:'Unauthorized'}})],
  ['procedure',await outsider.post(`${apiBase}/api/test-procedures`,{data:{projectId:workspace.project.id,baseNumber:'CLIENT-SUPPLIED',title:'Unauthorized',objective:'Unauthorized',preconditions:'None',steps:'None',expectedResult:'None',requirementRevisionIds:[],level:'System'}})],
  ['evidence',await outsider.post(`${apiBase}/api/evidence`,{multipart:{projectId:workspace.project.id,file:{name:'unauthorized.txt',mimeType:'text/plain',buffer:Buffer.from('unauthorized')}}})]
 ]
 for(const [name,response] of denied)expect(response.status(),`${name}: ${await response.text()}`).toBe(403)
 await outsider.dispose()
})
