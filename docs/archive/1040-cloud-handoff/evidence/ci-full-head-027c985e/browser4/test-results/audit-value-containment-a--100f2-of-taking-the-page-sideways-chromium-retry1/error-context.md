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

Locator: getByRole('heading', { name: 'Projects', exact: true })
Expected: visible
Timeout: 15000ms
Error: element(s) not found

Call log:
  - Expect "toBeVisible" with timeout 15000ms
  - waiting for getByRole('heading', { name: 'Projects', exact: true })

```

```yaml
- text: ▲
- paragraph: AEROLINK CONTROLLED WORKSPACE
- heading "Establishing your secure session" [level=1]
- text: Confirming identity, authority, and active program context…
```

# Test source

```ts
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
  76  |   await expect(page.getByRole('heading',{name:/Create your first program|Projects/})).toBeVisible()
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
> 133 |     await expect(page.getByRole('heading',{name:'Projects', exact:true})).toBeVisible()
      |                                                                           ^ Error: expect(locator).toBeVisible() failed
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
  177 |   }
  178 |   const group=page.locator('.navGroup').filter({has:page.locator('summary').filter({hasText:currentName[name]??name})})
  179 |   if(await group.getAttribute('open')===null)await group.locator('summary').click()
  180 |   const engineeringScope=name==='SOFTWARE ENGINEERING'?'Software':name==='SYSTEMS ENGINEERING'?'System':''
  181 |   if(engineeringScope){
  182 |     const scopeButton=group.getByRole('group',{name:'Requirements scope'}).getByRole('button',{name:engineeringScope})
  183 |     if(await scopeButton.getAttribute('aria-pressed')!=='true')await scopeButton.click()
  184 |   }
  185 | }
  186 | 
  187 | export async function openNewSystemChangeRequest(page:Page){
  188 |   await openNavigationGroup(page,'SYSTEMS ENGINEERING')
  189 |   await page.getByRole('link',{name:'System Change Requests'}).click()
  190 |   await page.getByRole('button',{name:'+ New System Change Request'}).click()
  191 | }
  192 | 
  193 | export async function openNewSoftwareChangeRequest(page:Page,level:'HLR'|'LLR'='HLR'){
  194 |   await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  195 |   await page.getByRole('link',{name:'Software Change Requests'}).click()
  196 |   if(level==='LLR')await page.getByRole('button',{name:/^LLR Low-level requirements$/}).click()
  197 |   await page.getByRole('button',{name:`+ New ${level} Change Request`}).click()
  198 | }
  199 | 
  200 | /**
  201 |  * Waits for a surface to have painted, instead of sleeping for a fixed period.
  202 |  *
  203 |  * The design and contrast audits visited each surface and then slept one second before measuring. Thirteen
  204 |  * surfaces in two densities is twenty-six seconds of a thirty-five second test spent waiting on a timer that
  205 |  * was neither long enough to be a guarantee nor short enough to be cheap.
  206 |  *
  207 |  * `networkidle` is not usable here: System Operations reloads every 2.5 seconds and the Integration Command
  208 |  * Center every 5, so those surfaces are never idle by that definition. Every surface does render a `main`, so
  209 |  * the signal is that element carrying real text.
  210 |  *
  211 |  * Both waits swallow their timeout on purpose. A surface that never paints is a genuine failure, and the audit
  212 |  * that follows reports it as one — `crashed` names the surface, where a timeout here would only name this
  213 |  * helper.
  214 |  */
  215 | export async function surfacePainted(page: Page, minimumCharacters = 60) {
  216 |   await page.locator('main').first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {})
  217 |   await page.waitForFunction(
  218 |     minimum => ((document.querySelector('main')?.textContent) ?? '').trim().length >= minimum,
  219 |     minimumCharacters,
  220 |     { timeout: 5_000 },
  221 |   ).catch(() => {})
  222 | }
  223 | 
  224 | /**
  225 |  * Waits until the document stops growing, for the measurements that compare one layout against another.
  226 |  *
  227 |  * `surfacePainted` is the right signal for "is there something to audit", but not for "how tall is it": the
  228 |  * verification workspace keeps loading datasets after its first paint, and a height sampled mid-load made
  229 |  * compact look taller than comfortable. Two consecutive equal readings is a settled layout; the poll interval
  230 |  * is a poll, not a guess at how long rendering takes.
  231 |  */
  232 | export async function layoutSettled(page: Page, timeoutMs = 15_000) {
  233 |   const height = () => page.evaluate(() => document.documentElement.scrollHeight)
```