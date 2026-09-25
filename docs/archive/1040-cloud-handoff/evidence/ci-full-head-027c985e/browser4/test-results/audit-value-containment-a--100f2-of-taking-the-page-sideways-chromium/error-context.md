# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: audit-value-containment.spec.ts >> a long unbroken audit value wraps instead of taking the page sideways
- Location: tests\audit-value-containment.spec.ts:14:1

# Error details

```
Error: expect(locator).toBeVisible() failed

Locator: getByRole('heading', { name: /Create your first program|Projects/ })
Expected: visible
Timeout: 15000ms
Error: element(s) not found

Call log:
  - Expect "toBeVisible" with timeout 15000ms
  - waiting for getByRole('heading', { name: /Create your first program|Projects/ })

```

```yaml
- main:
  - text: AeroLink
  - paragraph: CONTROLLED ENGINEERING WORKSPACE
  - heading "Requirements, Verification, Changes, Evidence, Document, and more in one connected record" [level=1]
  - text: WORKSPACE ORIGIN http://127.0.0.1:5174 API ORIGIN http://127.0.0.1:5082 ◈
  - heading "Welcome back" [level=2]
  - paragraph: Sign in to your on-premises AeroLink workspace.
  - text: Username
  - textbox "Username": admin
  - text: Password
  - textbox "Password":[REDACTED]
  - button "Reveal typed characters": ◎
  - button "Authenticating…" [disabled]
```

# Test source

```ts
  1   | import { expect } from '@playwright/test'
  2   | import type { APIRequestContext, Locator, Page } from '@playwright/test'
  3   | export const apiBase=process.env.AEROLINK_E2E_API_BASE??'http://127.0.0.1:5082'
  4   | 
  5   | export type ShowcaseSeed = {
  6   |   programId: string
  7   |   projectId: string
  8   |   activeReleaseId: string
  9   |   releasedBaselineId: string
  10  | }
  11  | 
  12  | export type LoginOptions = {
  13  |   openProject?: boolean
  14  |   /** Stable server identity to open after signing in. */
  15  |   projectId?: string
  16  |   /** Stable server identity of the build to open after selecting the project. */
  17  |   releaseId?: string
  18  | }
  19  | 
  20  | let cachedShowcase: ShowcaseSeed | undefined
  21  | 
  22  | function configuredShowcase(): ShowcaseSeed | undefined {
  23  |   const raw = process.env.AEROLINK_SHOWCASE_SEED
  24  |   if (!raw) return undefined
  25  |   try {
  26  |     const value = JSON.parse(raw) as Partial<ShowcaseSeed>
  27  |     if ([value.programId, value.projectId, value.activeReleaseId, value.releasedBaselineId]
  28  |       .every(item => typeof item === 'string' && item.length > 0)) return value as ShowcaseSeed
  29  |   } catch {
  30  |     // A missing or malformed optional seed must not turn an authentication helper into a second auth path.
  31  |   }
  32  |   return undefined
  33  | }
  34  | 
  35  | type WorkspaceSummary = {
  36  |   program: { id: string; name: string; code?: string }
  37  |   projects: { project: { id: string; name?: string; softwareProduct?: string }; releases: { id: string; version?: string; isReleased: boolean }[] }[]
  38  | }
  39  | 
  40  | async function discoverFmsTarget(page: Page): Promise<Pick<ShowcaseSeed, 'programId' | 'projectId' | 'activeReleaseId'> | undefined> {
  41  |   const configured = configuredShowcase()
  42  |   if (configured) return configured
  43  |   // This request uses the page's already-authenticated cookie. In particular, it must not call apiLogin:
  44  |   // ordinary-role journeys deliberately remain ordinary while opening their authorized Project.
  45  |   try {
  46  |     const response = await page.request.get(`${apiBase}/api/workspaces`)
  47  |     if (!response.ok()) return undefined
  48  |     const workspaces = await response.json() as WorkspaceSummary[]
  49  |     const programs = workspaces.filter(item => item.program.code === 'FMSLIVE')
  50  |     if (programs.length !== 1 || programs[0].projects.length !== 1) return undefined
  51  |     const project = programs[0].projects[0]
  52  |     const inWork = project.releases.filter(item => !item.isReleased)
  53  |     if (inWork.length !== 1) return undefined
  54  |     return { programId: programs[0].program.id, projectId: project.project.id, activeReleaseId: inWork[0].id }
  55  |   } catch {
  56  |     // Isolated component fixtures may deliberately leave the API unavailable; their strict DOM fallback below
  57  |     // still proves that a single fixture identity is present.
  58  |     return undefined
  59  |   }
  60  | }
  61  | 
  62  | export async function login(page:Page,userName='admin',options:LoginOptions={}){
  63  |   await page.goto('/')
  64  |   // A journey may change users without creating a new BrowserContext. The shell redirects an already
  65  |   // authenticated session straight to Projects, so clear that session before looking for the login form.
  66  |   const signOut=page.getByRole('button',{name:'Sign out'})
  67  |   const username=page.getByLabel('Username')
  68  |   await expect(signOut.or(username)).toBeVisible()
  69  |   if(await signOut.isVisible().catch(()=>false)){
  70  |     await signOut.click()
  71  |     await expect(username).toBeVisible()
  72  |   }
  73  |   await username.fill(userName)
  74  |   await page.getByLabel('Password')[REDACTED literal-browser-fill]
  75  |   await page.getByRole('button',{name:/Sign in securely/}).click()
> 76  |   await expect(page.getByRole('heading',{name:/Create your first program|Projects/})).toBeVisible()
      |                                                                                       ^ Error: expect(locator).toBeVisible() failed
  77  |   if(options.openProject!==false&&await page.getByRole('heading',{name:'Projects'}).count()){
  78  |     // The display name is intentionally non-unique. Use the exact server identity supplied by global setup;
  79  |     // the strict fallback preserves old isolated fixtures without silently picking an arbitrary duplicate.
  80  |     const seed = await discoverFmsTarget(page)
  81  |     const projectId = options.projectId ?? seed?.projectId
  82  |     const project = projectId
  83  |       ? page.locator(`[data-project-card][data-project-id="${projectId}"]`)
  84  |       : page.getByRole('link',{name:'Open FMS Product Development'})
  85  |     await expect(project, projectId
  86  |       ? `Project card ${projectId} must be present exactly once`
  87  |       : 'FMS Product Development must identify exactly one project when no showcase seed is configured')
  88  |       .toHaveCount(1)
  89  |     await project.click()
  90  |     await expect(page.getByRole('heading',{name:'Software Builds'})).toBeVisible()
  91  |     const releaseId = options.releaseId ?? seed?.activeReleaseId
  92  |     const build = releaseId
  93  |       ? page.locator(`[data-build-card][data-build-id="${releaseId}"]`)
  94  |           .getByRole('button', { name: /^Open build / })
  95  |       : page.getByRole('button',{name:'Open build 1.6'})
  96  |     await expect(build, releaseId
  97  |       ? `Build card ${releaseId} must be present exactly once`
  98  |       : 'Build 1.6 must identify exactly one build when no showcase seed is configured')
  99  |       .toHaveCount(1)
  100 |     await build.click()
  101 |     await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
  102 |   }
  103 | }
  104 | export async function apiLogin(request:APIRequestContext,userName='admin'){
  105 |   const response=await request.post(`${apiBase}/api/auth/login`,{data:{userName,password:'AeroLink!2026'}})
  106 |   expect(response.ok(),await response.text()).toBeTruthy()
  107 | }
  108 | export async function showcaseSeed(request:APIRequestContext){
  109 |   if(cachedShowcase)return cachedShowcase
  110 |   const prepared=process.env.AEROLINK_SHOWCASE_SEED
  111 |   if(prepared){cachedShowcase=JSON.parse(prepared) as ShowcaseSeed;return cachedShowcase}
  112 |   await apiLogin(request)
  113 |   // A fresh production database materializes the complete 1,250-requirement showcase plus controlled
  114 |   // procedures, executions, evidence and upgrade records — and since #724/#725/#728 the seed request also
  115 |   // bootstraps dormant procedures, their change-control packages and controlled procedure documents, so its
  116 |   // duration has grown toward this budget. Three production-lane runs on 2026-08-25 aborted at exactly
  117 |   // 240s on four-core runners (issue #759) while the same seed completed earlier the same day, so the
  118 |   // request now carries a 480s budget: still far inside the production job's own 20-minute timeout, and
  119 |   // a genuine wedge still times out — with twice the evidence retained.
  120 |   const response=await request.post(`${apiBase}/api/showcase/seed`,{timeout:480_000})
  121 |   const body=await response.text()
  122 |   expect(response.ok(),body).toBeTruthy()
  123 |   cachedShowcase=JSON.parse(body) as ShowcaseSeed
  124 |   return cachedShowcase
  125 | }
  126 | export async function selectProgram(page:Page,label:string, target?: { projectId?: string; releaseId?: string }){
  127 |   const seed = await discoverFmsTarget(page)
  128 |   if (label === 'Flight Management System Live Program' && !target && seed) {
  129 |     // Keep the selection visible and user-driven: the stable ids select the intended cards, while the
  130 |     // current session remains the caller's session. A direct route would prove identity resolution but
  131 |     // would skip the visual project/build selection contract.
  132 |     await page.goto('/')
  133 |     await expect(page.getByRole('heading',{name:'Projects', exact:true})).toBeVisible()
  134 |     const project = page.locator(`[data-project-card][data-project-id="${seed.projectId}"]`)
  135 |     await expect(project, `Project card ${seed.projectId} must be present exactly once`).toHaveCount(1)
  136 |     await project.click()
  137 |     await expect(page.getByRole('heading',{name:'Software Builds'})).toBeVisible()
  138 |     const build = page.locator(`[data-build-card][data-build-id="${seed.activeReleaseId}"]`)
  139 |       .getByRole('button', { name: /^Open build / })
  140 |     await expect(build, `Build card ${seed.activeReleaseId} must be present exactly once`).toHaveCount(1)
  141 |     await build.click()
  142 |     await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
  143 |     return
  144 |   }
  145 |   const response=await page.request.get(`${apiBase}/api/workspaces`)
  146 |   const body=await response.text()
  147 |   expect(response.ok(),body).toBeTruthy()
  148 |   const workspaces=JSON.parse(body) as {
  149 |     program:{id:string;name:string};
  150 |     projects:{project:{id:string};releases:{id:string;isReleased:boolean}[]}[]
  151 |   }[]
  152 |   const matches=workspaces.filter(item=>item.program.name===label)
  153 |   expect(matches,`Program context "${label}" must identify exactly one program`).toHaveLength(1)
  154 |   const workspace=matches[0]
  155 |   const projects=target?.projectId
  156 |     ? workspace.projects.filter(item=>item.project.id===target.projectId)
  157 |     : workspace.projects
  158 |   expect(projects,`Project in program "${label}" must identify exactly one project`).toHaveLength(1)
  159 |   const project=projects[0]
  160 |   const releases=target?.releaseId
  161 |     ? project.releases.filter(item=>item.id===target.releaseId)
  162 |     : project.releases.filter(item=>!item.isReleased)
  163 |   const selectedReleases=releases.length>0 ? releases : project.releases
  164 |   expect(selectedReleases,`Build in project "${project.project.id}" must identify exactly one target`).toHaveLength(1)
  165 |   const release=selectedReleases[0]
  166 |   await page.goto(`/programs/${workspace.program.id}/projects/${project.project.id}/releases/${release.id}/command-center`)
  167 |   await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
  168 | }
  169 | export async function openNavigationGroup(page:Page,name:string){
  170 |   const currentName:{[key:string]:string}={
  171 |     'ENGINEERING':'REQUIREMENTS',
  172 |     'SYSTEMS ENGINEERING':'REQUIREMENTS',
  173 |     'SOFTWARE ENGINEERING':'REQUIREMENTS',
  174 |     'VERIFICATION':'VERIFICATION',
  175 |     'ASSURANCE':'VERIFICATION',
  176 |     'RELEASE & CONFIGURATION':'RELEASE',
```