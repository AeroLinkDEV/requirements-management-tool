import { expect, test } from '@playwright/test'
import { apiBase, apiLogin } from './auth'

test('mutation authentication and Program-scoped discovery prevent direct-object access',async({request,playwright})=>{
 const anonymous=await playwright.request.newContext();const deniedSeed=await anonymous.post(`${apiBase}/api/showcase/seed`);expect(deniedSeed.status()).toBe(401);await anonymous.dispose()
 await apiLogin(request);const suffix=Date.now().toString().slice(-7);const created=await request.post(`${apiBase}/api/workspaces`,{data:{programName:`Restricted ${suffix}`,programCode:`RX${suffix}`,projectName:'Restricted Project',softwareProduct:'Restricted Product',initialRelease:'1.0',initialReleaseIsReleased:false}});expect(created.ok(),await created.text()).toBeTruthy();const workspace=await created.json()
 const outsider=await playwright.request.newContext();const login=await outsider.post(`${apiBase}/api/auth/login`,{data:{userName:'systems.reviewer',password:'AeroLink!2026'}});expect(login.ok(),await login.text()).toBeTruthy();const discovery=await outsider.get(`${apiBase}/api/search?projectId=${workspace.project.id}&query=SCR`);expect(discovery.status()).toBe(403);await outsider.dispose()
})
