import { expect } from '@playwright/test'
import type { APIRequestContext, Locator, Page } from '@playwright/test'
export const apiBase=process.env.AEROLINK_E2E_API_BASE??'http://127.0.0.1:5082'

export type ShowcaseSeed = {
  programId: string
  projectId: string
  activeReleaseId: string
  releasedBaselineId: string
}

export type LoginOptions = {
  openProject?: boolean
  /** Stable server identity to open after signing in. */
  projectId?: string
  /** Stable server identity of the build to open after selecting the project. */
  releaseId?: string
}

let cachedShowcase: ShowcaseSeed | undefined

function configuredShowcase(): ShowcaseSeed | undefined {
  const raw = process.env.AEROLINK_SHOWCASE_SEED
  if (!raw) return undefined
  try {
    const value = JSON.parse(raw) as Partial<ShowcaseSeed>
    if ([value.programId, value.projectId, value.activeReleaseId, value.releasedBaselineId]
      .every(item => typeof item === 'string' && item.length > 0)) return value as ShowcaseSeed
  } catch {
    // A missing or malformed optional seed must not turn an authentication helper into a second auth path.
  }
  return undefined
}

type WorkspaceSummary = {
  program: { id: string; name: string; code?: string }
  projects: { project: { id: string; name?: string; softwareProduct?: string }; releases: { id: string; version?: string; isReleased: boolean }[] }[]
}

async function discoverFmsTarget(page: Page): Promise<Pick<ShowcaseSeed, 'programId' | 'projectId' | 'activeReleaseId'> | undefined> {
  const configured = configuredShowcase()
  if (configured) return configured
  // This request uses the page's already-authenticated cookie. In particular, it must not call apiLogin:
  // ordinary-role journeys deliberately remain ordinary while opening their authorized Project.
  try {
    const response = await page.request.get(`${apiBase}/api/workspaces`)
    if (!response.ok()) return undefined
    const workspaces = await response.json() as WorkspaceSummary[]
    const programs = workspaces.filter(item => item.program.code === 'FMSLIVE')
    if (programs.length !== 1 || programs[0].projects.length !== 1) return undefined
    const project = programs[0].projects[0]
    const inWork = project.releases.filter(item => !item.isReleased)
    if (inWork.length !== 1) return undefined
    return { programId: programs[0].program.id, projectId: project.project.id, activeReleaseId: inWork[0].id }
  } catch {
    // Isolated component fixtures may deliberately leave the API unavailable; their strict DOM fallback below
    // still proves that a single fixture identity is present.
    return undefined
  }
}

export async function login(page:Page,userName='admin',options:LoginOptions={}){
  await page.goto('/')
  // A journey may change users without creating a new BrowserContext. The shell redirects an already
  // authenticated session straight to Projects, so clear that session before looking for the login form.
  const signOut=page.getByRole('button',{name:'Sign out'})
  const username=page.getByLabel('Username')
  await expect(signOut.or(username)).toBeVisible()
  if(await signOut.isVisible().catch(()=>false)){
    await signOut.click()
    await expect(username).toBeVisible()
  }
  await username.fill(userName)
  await page.getByLabel('Password').fill('AeroLink!2026')
  await page.getByRole('button',{name:/Sign in securely/}).click()
  await expect(page.getByRole('heading',{name:/Create your first program|Projects/})).toBeVisible()
  if(options.openProject!==false&&await page.getByRole('heading',{name:'Projects'}).count()){
    // The display name is intentionally non-unique. Use the exact server identity supplied by global setup;
    // the strict fallback preserves old isolated fixtures without silently picking an arbitrary duplicate.
    const seed = await discoverFmsTarget(page)
    const projectId = options.projectId ?? seed?.projectId
    const project = projectId
      ? page.locator(`[data-project-card][data-project-id="${projectId}"]`)
      : page.getByRole('link',{name:'Open FMS Product Development'})
    await expect(project, projectId
      ? `Project card ${projectId} must be present exactly once`
      : 'FMS Product Development must identify exactly one project when no showcase seed is configured')
      .toHaveCount(1)
    await project.click()
    await expect(page.getByRole('heading',{name:'Software Builds'})).toBeVisible()
    const releaseId = options.releaseId ?? seed?.activeReleaseId
    const build = releaseId
      ? page.locator(`[data-build-card][data-build-id="${releaseId}"]`)
          .getByRole('button', { name: /^Open build / })
      : page.getByRole('button',{name:'Open build 1.6'})
    await expect(build, releaseId
      ? `Build card ${releaseId} must be present exactly once`
      : 'Build 1.6 must identify exactly one build when no showcase seed is configured')
      .toHaveCount(1)
    await build.click()
    await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
  }
}
export async function apiLogin(request:APIRequestContext,userName='admin'){
  const response=await request.post(`${apiBase}/api/auth/login`,{data:{userName,password:'AeroLink!2026'}})
  expect(response.ok(),await response.text()).toBeTruthy()
}
export async function showcaseSeed(request:APIRequestContext){
  if(cachedShowcase)return cachedShowcase
  const prepared=process.env.AEROLINK_SHOWCASE_SEED
  if(prepared){cachedShowcase=JSON.parse(prepared) as ShowcaseSeed;return cachedShowcase}
  await apiLogin(request)
  // A fresh production database materializes the complete 1,250-requirement showcase plus controlled
  // procedures, executions, evidence and upgrade records — and since #724/#725/#728 the seed request also
  // bootstraps dormant procedures, their change-control packages and controlled procedure documents, so its
  // duration has grown toward this budget. Three production-lane runs on 2026-08-25 aborted at exactly
  // 240s on four-core runners (issue #759) while the same seed completed earlier the same day, so the
  // request now carries a 480s budget: still far inside the production job's own 20-minute timeout, and
  // a genuine wedge still times out — with twice the evidence retained.
  const response=await request.post(`${apiBase}/api/showcase/seed`,{timeout:480_000})
  const body=await response.text()
  expect(response.ok(),body).toBeTruthy()
  cachedShowcase=JSON.parse(body) as ShowcaseSeed
  return cachedShowcase
}
export async function selectProgram(page:Page,label:string, target?: { projectId?: string; releaseId?: string }){
  const seed = await discoverFmsTarget(page)
  if (label === 'Flight Management System Live Program' && !target && seed) {
    // Keep the selection visible and user-driven: the stable ids select the intended cards, while the
    // current session remains the caller's session. A direct route would prove identity resolution but
    // would skip the visual project/build selection contract.
    await page.goto('/')
    await expect(page.getByRole('heading',{name:'Projects', exact:true})).toBeVisible()
    const project = page.locator(`[data-project-card][data-project-id="${seed.projectId}"]`)
    await expect(project, `Project card ${seed.projectId} must be present exactly once`).toHaveCount(1)
    await project.click()
    await expect(page.getByRole('heading',{name:'Software Builds'})).toBeVisible()
    const build = page.locator(`[data-build-card][data-build-id="${seed.activeReleaseId}"]`)
      .getByRole('button', { name: /^Open build / })
    await expect(build, `Build card ${seed.activeReleaseId} must be present exactly once`).toHaveCount(1)
    await build.click()
    await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
    return
  }
  const response=await page.request.get(`${apiBase}/api/workspaces`)
  const body=await response.text()
  expect(response.ok(),body).toBeTruthy()
  const workspaces=JSON.parse(body) as {
    program:{id:string;name:string};
    projects:{project:{id:string};releases:{id:string;isReleased:boolean}[]}[]
  }[]
  const matches=workspaces.filter(item=>item.program.name===label)
  expect(matches,`Program context "${label}" must identify exactly one program`).toHaveLength(1)
  const workspace=matches[0]
  const projects=target?.projectId
    ? workspace.projects.filter(item=>item.project.id===target.projectId)
    : workspace.projects
  expect(projects,`Project in program "${label}" must identify exactly one project`).toHaveLength(1)
  const project=projects[0]
  const releases=target?.releaseId
    ? project.releases.filter(item=>item.id===target.releaseId)
    : project.releases.filter(item=>!item.isReleased)
  const selectedReleases=releases.length>0 ? releases : project.releases
  expect(selectedReleases,`Build in project "${project.project.id}" must identify exactly one target`).toHaveLength(1)
  const release=selectedReleases[0]
  await page.goto(`/programs/${workspace.program.id}/projects/${project.project.id}/releases/${release.id}/command-center`)
  await expect(page.getByRole('heading',{name:'Command Center'})).toBeVisible()
}
export async function openNavigationGroup(page:Page,name:string){
  const currentName:{[key:string]:string}={
    'ENGINEERING':'REQUIREMENTS',
    'SYSTEMS ENGINEERING':'REQUIREMENTS',
    'SOFTWARE ENGINEERING':'REQUIREMENTS',
    'VERIFICATION':'VERIFICATION',
    'ASSURANCE':'VERIFICATION',
    'RELEASE & CONFIGURATION':'RELEASE',
  }
  const group=page.locator('.navGroup').filter({has:page.locator('summary').filter({hasText:currentName[name]??name})})
  if(await group.getAttribute('open')===null)await group.locator('summary').click()
  const engineeringScope=name==='SOFTWARE ENGINEERING'?'Software':name==='SYSTEMS ENGINEERING'?'System':''
  if(engineeringScope){
    const scopeButton=group.getByRole('group',{name:'Requirements scope'}).getByRole('button',{name:engineeringScope})
    if(await scopeButton.getAttribute('aria-pressed')!=='true')await scopeButton.click()
  }
}

export async function openNewSystemChangeRequest(page:Page){
  await openNavigationGroup(page,'SYSTEMS ENGINEERING')
  await page.getByRole('link',{name:'System Change Requests'}).click()
  await page.getByRole('button',{name:'+ New System Change Request'}).click()
}

export async function openNewSoftwareChangeRequest(page:Page,level:'HLR'|'LLR'='HLR'){
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()
  if(level==='LLR')await page.getByRole('button',{name:/^LLR Low-level requirements$/}).click()
  await page.getByRole('button',{name:`+ New ${level} Change Request`}).click()
}

/**
 * Moves a Create New Project walkthrough past the Features step (#1113), keeping every feature. The
 * walkthrough reaches it straight after Starting point, so each journey that clicks through setup calls this
 * where that step now sits; asserting the heading fails loudly if the step order changes again.
 */
export async function continuePastFeatures(page: Page) {
  await expect(page.getByRole('heading', { name: "Choose the project's features", level: 2 })).toBeVisible()
  await page.getByRole('button', { name: 'Continue' }).click()
}

/**
 * Waits for a surface to have painted, instead of sleeping for a fixed period.
 *
 * The design and contrast audits visited each surface and then slept one second before measuring. Thirteen
 * surfaces in two densities is twenty-six seconds of a thirty-five second test spent waiting on a timer that
 * was neither long enough to be a guarantee nor short enough to be cheap.
 *
 * `networkidle` is not usable here: System Operations reloads every 2.5 seconds and the Integration Command
 * Center every 5, so those surfaces are never idle by that definition. Every surface does render a `main`, so
 * the signal is that element carrying real text.
 *
 * Both waits swallow their timeout on purpose. A surface that never paints is a genuine failure, and the audit
 * that follows reports it as one — `crashed` names the surface, where a timeout here would only name this
 * helper.
 */
export async function surfacePainted(page: Page, minimumCharacters = 60) {
  await page.locator('main').first().waitFor({ state: 'visible', timeout: 15_000 }).catch(() => {})
  await page.waitForFunction(
    minimum => ((document.querySelector('main')?.textContent) ?? '').trim().length >= minimum,
    minimumCharacters,
    { timeout: 5_000 },
  ).catch(() => {})
}

/**
 * Waits until the document stops growing, for the measurements that compare one layout against another.
 *
 * `surfacePainted` is the right signal for "is there something to audit", but not for "how tall is it": the
 * verification workspace keeps loading datasets after its first paint, and a height sampled mid-load made
 * compact look taller than comfortable. Two consecutive equal readings is a settled layout; the poll interval
 * is a poll, not a guess at how long rendering takes.
 */
export async function layoutSettled(page: Page, timeoutMs = 15_000) {
  const height = () => page.evaluate(() => document.documentElement.scrollHeight)
  let previous = -1
  let stable = 0
  const started = Date.now()
  while (Date.now() - started < timeoutMs && stable < 2) {
    const current = await height()
    stable = current === previous ? stable + 1 : 0
    previous = current
    if (stable < 2) await page.waitForTimeout(120)
  }
}

/**
 * The first section of a Project's requirements document for one level.
 *
 * A new requirement cannot be sent for review without a section, so any journey that builds a change request
 * through the API and then submits it has to name one. Which section is not the point of those journeys, so
 * they take the first.
 */
export async function firstSectionId(request: APIRequestContext, projectId: string,
  level: 'System' | 'HighLevel' | 'LowLevel' = 'System') {
  const response = await request.get(`${apiBase}/api/authoring/sections?projectId=${projectId}&level=${level}`)
  if (!response.ok()) throw new Error(`sections ${response.status()}: ${await response.text()}`)
  const sections = await response.json()
  // A Project builds its requirements documents the first time its requirements are synchronized, so one
  // created moments ago by a journey has no sections yet. There is nothing to choose and nothing to send;
  // the API asks for a section only where sections exist.
  return sections.length ? (sections[0].id as string) : undefined
}

/**
 * Authors the Phase 1 no-upstream answer through the same controlled checkout,
 * autosave, and check-in path used by the product.  API-created fixture drafts
 * must use this path too, otherwise their direct submit bypasses the canonical
 * draft snapshot and is rejected by the review readiness gate.
 */
export async function authorNoUpstreamAnswer(
  request: APIRequestContext,
  changeRequestId: string,
  rationale: string,
): Promise<{ version: number; resultingArtifactVersion: number }> {
  const checkout = await request.post(`${apiBase}/api/controlled-editing/checkout`, {
    data: { artifactType: 'ChangeRequest', artifactId: changeRequestId, leaseMinutes: 15 },
  })
  expect(checkout.ok(), await checkout.text()).toBeTruthy()
  const lock = await checkout.json() as { id: string; version: number; draftJson: string }
  const draft = JSON.parse(lock.draftJson) as Record<string, unknown>
  draft.upstreamLinks = []
  draft.noUpstreamRationale = rationale
  const autosave = await request.put(`${apiBase}/api/controlled-editing/sessions/${lock.id}/autosave`, {
    data: { expectedVersion: lock.version, draftJson: JSON.stringify(draft), leaseMinutes: 15 },
  })
  expect(autosave.ok(), await autosave.text()).toBeTruthy()
  const saved = await autosave.json() as { version: number }
  const checkIn = await request.post(`${apiBase}/api/controlled-editing/sessions/${lock.id}/check-in`, {
    data: { expectedVersion: saved.version },
  })
  expect(checkIn.ok(), await checkIn.text()).toBeTruthy()
  const result = await checkIn.json() as { resultingArtifactVersion: number }
  expect(result.resultingArtifactVersion).toEqual(expect.any(Number))
  return { ...result, version: result.resultingArtifactVersion }
}

/**
 * Chooses a Problem Report category in the picker that replaced the four-kind select.
 *
 * Driven by the label a person actually reads rather than the enum name, so a spec fails when the
 * vocabulary somebody is shown changes and not merely when an identifier is renamed.
 */
export async function chooseCategory(scope: Locator | Page, label: string) {
  const picker = scope.locator('.catPicker').first()
  await picker.locator('.catCurrent').click()
  // Scoped to the picker's own menu. Every <option> of every <select> on the form carries the option
  // role too — the impact matrix alone contributes two dozen — so an unscoped role query is ambiguous,
  // and .first() then depends on DOM order rather than on what was asked for.
  await picker.locator('.catMenu').getByRole('option', { name: label, exact: false }).first().click()
  // Proven, not assumed: a silent no-op here surfaces much later as a lifecycle button that never appears.
  await expect(picker.locator('.catCurrent')).toContainText(label)
}

/**
 * Writes into a rich authored field.
 *
 * Every Problem Report narrative field holds typed structure internally, while document-like editing opens
 * directly on its first paragraph. The block primitive stays hidden from ordinary authoring; typing and
 * Enter continuation are the authoring interaction.
 */
export async function writeRichField(scope: Locator | Page, label: string, text: string) {
  const body = scope.getByRole("textbox", { name: `${label} paragraph 1` })
  await body.fill(text)
}
