# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: managed-documentation-center.spec.ts >> controlled relationship links use canonical targets and exact browser routes
- Location: tests\managed-documentation-center.spec.ts:5:1

# Error details

```
Error: {"error":"That canonical relationship is already active on this document revision."}

expect(received).toBeTruthy()

Received: false
```

# Test source

```ts
  1   | import { expect, test } from '@playwright/test'
  2   | import { readFile } from 'node:fs/promises'
  3   | import { apiBase, apiLogin, login, showcaseSeed } from './auth'
  4   | 
  5   | test('controlled relationship links use canonical targets and exact browser routes', async ({ page, request }) => {
  6   |   test.setTimeout(240_000)
  7   |   const showcase = await showcaseSeed(request)
  8   |   await apiLogin(request, 'software.author')
  9   |   // Keep this relationship-only fixture outside build-targeted PR queues so it cannot perturb
  10  |   // their independently paged first-page assertions when the full browser suite shares a seed.
  11  |   const reportResponse=await request.post(`${apiBase}/api/problem-reports`,{data:{category: 'CodeFunctional', projectId:showcase.projectId,title:`Document relationship target ${Date.now()}`,problem:'Prove canonical Problem Report navigation.'}})
  12  |   expect(reportResponse.ok(),await reportResponse.text()).toBeTruthy()
  13  |   const report=await reportResponse.json()
  14  |   const documentsResponse=await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}`)
  15  |   expect(documentsResponse.ok(),await documentsResponse.text()).toBeTruthy()
  16  |   const document=(await documentsResponse.json()).items.find((item:{acronym:string})=>item.acronym==='SDP')
  17  |   expect(document).toBeTruthy()
  18  |   const created={id:document.id}
  19  |   const meanings:Record<string,string>={ChangeRequest:'MotivatedBy',ProblemReport:'AddressesProblem',TestChangeRequest:'VerificationImpact',Release:'RelatedBuild'}
  20  |   const linked:{type:string;id:string}[]=[]
  21  |   for(const artifactType of Object.keys(meanings)){
  22  |     const optionsResponse=await request.get(`${apiBase}/api/managed-documents/link-options?projectId=${showcase.projectId}&artifactType=${artifactType}`)
  23  |     expect(optionsResponse.ok(),await optionsResponse.text()).toBeTruthy()
  24  |     const options=(await optionsResponse.json() as {items:{id:string}[]}).items
  25  |     if(artifactType==='ProblemReport')options.unshift({id:report.id})
  26  |     expect(options.length,`Expected a showcase ${artifactType} target`).toBeGreaterThan(0)
  27  |     const detail=await (await request.get(`${apiBase}/api/managed-documents/${created.id}`)).json()
  28  |     const revision=detail.revisions.find((item:{state:string})=>['Draft','Returned'].includes(item.state))
  29  |     expect(revision).toBeTruthy()
  30  |     const response=await request.post(`${apiBase}/api/managed-documents/${created.id}/links`,{data:{revisionId:revision.id,artifactType,artifactId:options[0].id,displayNumber:'FORGED-CLIENT-LABEL',relationship:meanings[artifactType],expectedVersion:revision.version}})
> 31  |     expect(response.ok(),await response.text()).toBeTruthy()
      |                                                 ^ Error: {"error":"That canonical relationship is already active on this document revision."}
  32  |     linked.push({type:artifactType,id:options[0].id})
  33  |   }
  34  | 
  35  |   await login(page,'software.author',{openProject:false})
  36  |   await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${created.id}`)
  37  |   await page.getByRole('button',{name:'Links'}).click()
  38  |   for(const target of linked){
  39  |     const row=page.locator('.mdLinks > div').filter({has:page.locator('span').filter({hasText:new RegExp(`^${target.type} ·`)})})
  40  |     await expect(row).toBeVisible()
  41  |     const href=await row.getByRole('link').getAttribute('href')
  42  |     expect(href).not.toContain('FORGED-CLIENT-LABEL')
  43  |     if(target.type==='ChangeRequest')expect(href).toMatch(new RegExp(`/change-requests/${target.id}$`))
  44  |     if(target.type==='ProblemReport')expect(href).toMatch(new RegExp(`/problem-reports/${target.id}$`))
  45  |     if(target.type==='TestChangeRequest')expect(href).toMatch(new RegExp(`/coverage/${target.id}$`))
  46  |     if(target.type==='Release')expect(href).toMatch(new RegExp(`/releases/${target.id}/command-center$`))
  47  |   }
  48  |   const tcr=linked.find(item=>item.type==='TestChangeRequest')!
  49  |   await page.locator('.mdLinks > div').filter({has:page.locator('span').filter({hasText:/^TestChangeRequest ·/})}).getByRole('link').click()
  50  |   await expect(page).toHaveURL(new RegExp(`/coverage/${tcr.id}$`))
  51  |   await expect(page.getByRole('dialog')).toBeVisible()
  52  | })
  53  | 
  54  | test('managed Word documents remain one Project-wide register across build navigation', async ({ page }) => {
  55  |   test.setTimeout(240_000)
  56  |   await login(page, 'software.author')
  57  | 
  58  |   await page.getByRole('link', { name: 'Documentation Center' }).click()
  59  |   await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  60  |   await expect(page.getByRole('heading', { name: 'Documentation Center' })).toBeVisible()
  61  |   const [trustDownload] = await Promise.all([
  62  |     page.waitForEvent('download'),
  63  |     page.getByRole('button', { name: 'Download connector trust' }).click(),
  64  |   ])
  65  |   expect(trustDownload.suggestedFilename()).toMatch(/^aerolink-.+-trust\.json$/)
  66  |   const trustPath = await trustDownload.path()
  67  |   expect(trustPath).toBeTruthy()
  68  |   const trust = JSON.parse(await readFile(trustPath!, 'utf8'))
  69  |   expect(trust.protocolVersion).toBe('aerolink-connector-launch-v1')
  70  |   expect(trust.profileVersion).toBe('aerolink-ooxml-safe-v1')
  71  |   expect(trust.publicKeyFingerprint).toMatch(/^[0-9a-f]{64}$/)
  72  |   await expect(page.getByText('7 of 7 matching records')).toBeVisible()
  73  |   await expect(page.locator('.mdMetrics').getByText('4', { exact: true })).toBeVisible()
  74  | 
  75  |   await page.getByRole('button', { name: /SDP SDP-000001/ }).click()
  76  |   await expect(page).toHaveURL(/documentation-center\/[0-9a-f-]+$/)
  77  |   await page.reload({ waitUntil: 'load' })
  78  |   await expect(page.getByRole('heading', { name: 'FMS Software Development Plan' })).toBeVisible()
  79  |   await expect(page.locator('.mdIdentity').getByText(/Draft SDP-000001\.01/)).toBeVisible()
  80  |   await expect(page.getByText('Document steward')).toBeVisible()
  81  |   await expect(page.getByText('Responsible owner')).toBeVisible()
  82  |   await expect(page.getByText('Revision initiated by')).toBeVisible()
  83  |   await expect(page.getByText('Contributors')).toBeVisible()
  84  | 
  85  |   await expect(page.getByText('Add GitLab merge-request traceability and desktop connector responsibilities.')).toBeVisible()
  86  |   await page.getByRole('button', { name: 'Edit formal scope' }).click()
  87  |   const summaryEditor = page.locator('.mdInlineForm')
  88  |   await summaryEditor.getByLabel('Formal revision scope')[REDACTED literal-browser-fill]
  89  |   await summaryEditor.getByLabel('Reason for correction')[REDACTED literal-browser-fill]
  90  |   await summaryEditor.getByRole('button', { name: 'Record formal scope correction' }).click()
  91  |   await expect(page.getByText(/formal revision scope for SDP-000001\.01 was revised/i)).toBeVisible()
  92  |   await page.reload({ waitUntil: 'load' })
  93  |   await expect(page.getByText('Add GitLab traceability and preserve immutable check-in evidence.')).toBeVisible()
  94  |   await page.getByRole('button', { name: 'Versions' }).click()
  95  |   await expect(page.locator('.mdVersions').getByText('Most recent checked-in draft.', { exact: true })).toBeVisible()
  96  | 
  97  |   await page.getByRole('button', { name: 'Review & release' }).click()
  98  |   await expect(page.getByRole('heading', { name: 'Electronic signatures for SDP-000001.01' })).toBeVisible()
  99  |   await expect(page.getByText('No signatures are recorded for this exact revision.')).toBeVisible()
  100 | 
  101 |   await page.getByRole('button', { name: /Back to Software Builds/ }).click()
  102 |   await page.getByRole('button', { name: 'Open build 1.5' }).click()
  103 |   await page.goto(page.url().replace(/command-center$/, 'documentation-center'))
  104 |   await expect(page).toHaveURL(/\/programs\/[0-9a-f-]+\/projects\/[0-9a-f-]+\/documentation-center$/)
  105 |   await expect(page.getByText('7 of 7 matching records')).toBeVisible()
  106 |   await expect(page.getByRole('button', { name: '+ New document' })).toBeVisible()
  107 |   await expect(page.locator('.mdList').getByText(/\.01 · (Draft|In Review|Returned)/)).toHaveCount(4)
  108 | })
  109 | 
  110 | test('the Project register loads bounded pages while direct document URLs remain reachable', async ({ page, request }) => {
  111 |   const showcase = await showcaseSeed(request)
  112 |   await apiLogin(request, 'software.author')
  113 |   const response = await request.get(`${apiBase}/api/managed-documents?projectId=${showcase.projectId}&pageSize=100`)
  114 |   expect(response.ok(), await response.text()).toBeTruthy()
  115 |   const realItems = (await response.json()).items
  116 |   const direct = realItems.find((item:{acronym:string}) => item.acronym === 'SDP')
  117 |   const template = realItems.find((item:{id:string}) => item.id !== direct.id)
  118 |   expect(direct).toBeTruthy(); expect(template).toBeTruthy()
  119 | 
  120 |   await page.route('**/api/managed-documents?*', async route => {
  121 |     const url = new URL(route.request().url())
  122 |     if (url.searchParams.get('cursor') === 'mock-next') {
  123 |       await route.fulfill({ json: { totalCount: 51, pageSize: 50, hasMore: false, nextCursor: null, items: [{ ...template, id: crypto.randomUUID(), documentNumber: 'DOC-999999', title: 'Last paged document' }] } })
  124 |       return
  125 |     }
  126 |     const items = Array.from({ length: 50 }, (_, index) => ({ ...template, id: crypto.randomUUID(), documentNumber: `DOC-${String(index + 1).padStart(6, '0')}`, title: `Paged document ${index + 1}` }))
  127 |     await route.fulfill({ json: { totalCount: 51, pageSize: 50, hasMore: true, nextCursor: 'mock-next', items } })
  128 |   })
  129 |   await page.route('**/history/audit?*', async route => {
  130 |     const cursor = new URL(route.request().url()).searchParams.get('cursor')
  131 |     const items = cursor
```