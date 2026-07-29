import { expect, test } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { apiBase, apiLogin, showcaseSeed } from './auth'

test('mutation authentication and Program-scoped discovery prevent direct-object access',async({request,playwright})=>{
 const anonymous=await playwright.request.newContext();const deniedSeed=await anonymous.post(`${apiBase}/api/showcase/seed`);expect(deniedSeed.status()).toBe(401)
 for(const path of ['/API/dashboard',`/Api/scrs?projectId=${randomUUID()}`]){const response=await anonymous.get(`${apiBase}${path}`);expect(response.status(),`${path}: ${await response.text()}`).toBe(401)}
 await anonymous.dispose()
 await apiLogin(request);const suffix=Date.now().toString().slice(-7);const created=await request.post(`${apiBase}/api/workspaces`,{data:{programName:`Restricted ${suffix}`,programCode:`RX${suffix}`,projectName:'Restricted Project',softwareProduct:'Restricted Product',initialRelease:'1.0',initialReleaseIsReleased:false}});expect(created.ok(),await created.text()).toBeTruthy();const workspace=await created.json()
 const usersResponse=await request.get(`${apiBase}/api/admin/users`);expect(usersResponse.ok(),await usersResponse.text()).toBeTruthy();const users=await usersResponse.json();const systems=users.find((x:{userName:string})=>x.userName==='systems.reviewer');const assurance=users.find((x:{userName:string})=>x.userName==='assurance.reviewer');expect(systems).toBeTruthy();expect(assurance).toBeTruthy()
 const outsider=await playwright.request.newContext();const login=await outsider.post(`${apiBase}/api/auth/login`,{data:{userName:'systems.reviewer',password:'AeroLink!2026'}});expect(login.ok(),await login.text()).toBeTruthy();const discovery=await outsider.get(`${apiBase}/api/search?projectId=${workspace.project.id}&query=SCR`);expect(discovery.status()).toBe(403)
 const directory=await outsider.get(`${apiBase}/api/directory?programId=${workspace.program.id}`);expect(directory.status(),await directory.text()).toBe(403)
 const unknown=randomUUID()
 const denied:[string,Awaited<ReturnType<typeof outsider.post>>][]=[
  ['build',await outsider.post(`${apiBase}/api/builds`,{data:{projectId:workspace.project.id,releaseId:workspace.release.id,baselineId:unknown,buildNumber:'OUTSIDER-BUILD',description:'Unauthorized',recordedBy:'outsider'}})],
  ['release',await outsider.post(`${apiBase}/api/releases`,{data:{projectId:workspace.project.id,version:'2.0',predecessorReleaseId:null}})],
  ['trace',await outsider.post(`${apiBase}/api/trace-links`,{data:{projectId:workspace.project.id,sourceRevisionId:unknown,targetRevisionId:randomUUID(),type:'DerivedFrom',rationale:'Unauthorized'}})],
  ['procedure',await outsider.post(`${apiBase}/api/test-procedures`,{data:{projectId:workspace.project.id,baseNumber:'CLIENT-SUPPLIED',title:'Unauthorized',objective:'Unauthorized',preconditions:'None',steps:'None',expectedResult:'None',requirementRevisionIds:[],level:'System'}})],
  ['evidence',await outsider.post(`${apiBase}/api/evidence`,{multipart:{projectId:workspace.project.id,file:{name:'unauthorized.txt',mimeType:'text/plain',buffer:Buffer.from('unauthorized')}}})]
 ]
 for(const [name,response] of denied)expect(response.status(),`${name}: ${await response.text()}`).toBe(403)

 for(const id of [systems.id,assurance.id]){const grant=await request.post(`${apiBase}/api/admin/users/${id}/memberships`,{data:{programId:workspace.program.id,role:'Engineer'}});expect(grant.ok(),await grant.text()).toBeTruthy()}
 const invalidDelegation=await outsider.post(`${apiBase}/api/delegations`,{data:{programId:workspace.program.id,delegatorUserId:systems.id,delegateUserId:assurance.id,role:'ConfigurationManager',startsAt:new Date(Date.now()-60_000).toISOString(),endsAt:new Date(Date.now()+3_600_000).toISOString(),reason:'Attempted privilege escalation.'}})
 expect(invalidDelegation.status(),await invalidDelegation.text()).toBe(403)
 const scopedAdminGrant=await request.post(`${apiBase}/api/admin/users/${systems.id}/memberships`,{data:{programId:workspace.program.id,role:'Administrator'}});expect(scopedAdminGrant.ok(),await scopedAdminGrant.text()).toBeTruthy()
 const refreshedIdentity=await outsider.get(`${apiBase}/api/auth/me`);expect(refreshedIdentity.ok(),await refreshedIdentity.text()).toBeTruthy();expect((await refreshedIdentity.json()).isAdministrator).toBe(false)
 const systemAdministration=await outsider.get(`${apiBase}/api/admin/users`);expect(systemAdministration.status(),await systemAdministration.text()).toBe(403)
 await outsider.dispose()
})

test('Program scope protects dashboards, signatures, impacts, directories, and baseline views',async({request,playwright})=>{
 await apiLogin(request)
 const showcase=await showcaseSeed(request)
 const draftsResponse=await request.get(`${apiBase}/api/scrs?projectId=${showcase.projectId}&state=Draft&page=1&pageSize=200`);expect(draftsResponse.ok(),await draftsResponse.text()).toBeTruthy();const drafts=await draftsResponse.json();expect(drafts.items.length).toBeGreaterThan(0);const signedArtifact=drafts.items.find((x:any)=>x.authorId==='systems.author'||x.authorId==='software.author');expect(signedArtifact).toBeTruthy()
 const author=await playwright.request.newContext();const authorLogin=await author.post(`${apiBase}/api/auth/login`,{data:{userName:signedArtifact.authorId,password:'AeroLink!2026'}});expect(authorLogin.ok(),await authorLogin.text()).toBeTruthy();const submitted=await author.post(`${apiBase}/api/scrs/${signedArtifact.id}/submit`,{data:{actorId:signedArtifact.authorId,expectedVersion:null,approvers:[{userId:'admin',name:'AeroLink Administrator'}],mode:'Sequential'}});expect(submitted.ok(),await submitted.text()).toBeTruthy();await author.dispose()
 const approved=await request.post(`${apiBase}/api/scrs/${signedArtifact.id}/approve`,{data:{password:'AeroLink!2026',meaning:'Approved as a tenant-isolation signature probe.',expectedVersion:null}});expect(approved.ok(),await approved.text()).toBeTruthy()
 const campaignsResponse=await request.get(`${apiBase}/api/release-campaigns?projectId=${showcase.projectId}`);expect(campaignsResponse.ok(),await campaignsResponse.text()).toBeTruthy();const campaigns=await campaignsResponse.json();expect(campaigns.length).toBeGreaterThan(0)
 const campaignResponse=await request.get(`${apiBase}/api/release-campaigns/${campaigns[0].id}`);expect(campaignResponse.ok(),await campaignResponse.text()).toBeTruthy();const campaign=await campaignResponse.json();expect(campaign.impacts.length).toBeGreaterThan(0)

 const suffix=Date.now().toString().slice(-7);const scopedWorkspaceResponse=await request.post(`${apiBase}/api/workspaces`,{data:{programName:`Scoped Only ${suffix}`,programCode:`SO${suffix}`,projectName:'Scoped Project',softwareProduct:'Scoped Product',initialRelease:'1.0',initialReleaseIsReleased:false}});expect(scopedWorkspaceResponse.ok(),await scopedWorkspaceResponse.text()).toBeTruthy();const scopedWorkspace=await scopedWorkspaceResponse.json()
 const userName=`scope.only.${suffix}`;const password='ScopeOnly!2026';const createdUserResponse=await request.post(`${apiBase}/api/admin/users`,{data:{userName,displayName:'Scope Only User',email:`${userName}@example.test`,temporaryPassword:password}});expect(createdUserResponse.ok(),await createdUserResponse.text()).toBeTruthy();const createdUser=await createdUserResponse.json()
 const grant=await request.post(`${apiBase}/api/admin/users/${createdUser.id}/memberships`,{data:{programId:scopedWorkspace.program.id,role:'Engineer'}});expect(grant.ok(),await grant.text()).toBeTruthy()
 const scoped=await playwright.request.newContext();const login=await scoped.post(`${apiBase}/api/auth/login`,{data:{userName,password}});expect(login.ok(),await login.text()).toBeTruthy();const rotatedPassword='ScopeOnly-Rotated!2026';const rotation=await scoped.post(`${apiBase}/api/auth/password`,{data:{currentPassword:password,newPassword:rotatedPassword}});expect(rotation.status(),await rotation.text()).toBe(204);const relogin=await scoped.post(`${apiBase}/api/auth/login`,{data:{userName,password:rotatedPassword}});expect(relogin.ok(),await relogin.text()).toBeTruthy()

 const impact=await scoped.put(`${apiBase}/api/impact-dispositions/${campaign.impacts[0].id}`,{data:{state:'Addressed',rationale:'Unauthorized cross-Program mutation.'}});expect(impact.status(),await impact.text()).toBe(403)
 const directory=await scoped.get(`${apiBase}/api/directory?programId=${showcase.programId}`);expect(directory.status(),await directory.text()).toBe(403)
 const dashboard=await scoped.get(`${apiBase}/api/dashboard`);expect(dashboard.ok(),await dashboard.text()).toBeTruthy();const dashboardBody=await dashboard.json();expect(dashboardBody.system.total+dashboardBody.software.total).toBe(0)
 const signatures=await scoped.get(`${apiBase}/api/signatures?artifactId=${signedArtifact.id}`);expect(signatures.ok(),await signatures.text()).toBeTruthy();expect(await signatures.json()).toEqual([])
 for(const path of [`/api/traceability?projectId=${scopedWorkspace.project.id}&baselineId=${showcase.releasedBaselineId}&page=1&pageSize=10`,`/api/verification-coverage?projectId=${scopedWorkspace.project.id}&baselineId=${showcase.releasedBaselineId}`]){const response=await scoped.get(`${apiBase}${path}`);expect(response.status(),`${path}: ${await response.text()}`).toBe(400);expect((await response.json()).code).toBe('baseline_project_mismatch')}
 await scoped.dispose()
})
