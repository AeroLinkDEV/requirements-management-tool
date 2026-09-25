# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: managed-documentation-center.spec.ts >> managed Word documents remain one Project-wide register across build navigation
- Location: tests\managed-documentation-center.spec.ts:54:1

# Error details

```
Error: expect(locator).toBeVisible() failed

Locator: getByText('7 of 7 matching records')
Expected: visible
Timeout: 15000ms
Error: element(s) not found

Call log:
  - Expect "toBeVisible" with timeout 15000ms
  - waiting for getByText('7 of 7 matching records')

```

```yaml
- complementary:
  - text: AeroLink
  - 'group "Instance: AEROLINK (Undeclared) Source: unknown Mode: UNKNOWN"': AEROLINK
  - button "Search & navigate Ctrl K"
  - text: ACTIVE CONTEXT
  - strong: Flight Management System Live Program
  - text: FMS Product Development Project-wide
  - button "← Back to Software Builds"
  - navigation "Primary navigation":
    - link "Command Center":
      - /url: "#"
    - link "My Work":
      - /url: "#"
    - link "Team Work":
      - /url: "#"
    - group: REQUIREMENTS ›
    - group: VERIFICATION ›
    - group: CODE ›
    - link "Documentation Center":
      - /url: /programs/896ec195-d496-4331-99b2-1bb85e85881d/projects/309276a0-e227-4145-9ec6-a473919ca4e6/documentation-center
      - text: DOCUMENTATION CENTER
    - link "Problem Reports":
      - /url: "#"
      - text: PROBLEM REPORTS
    - group: RELEASE ›
  - img "Software Requirements Author, Software Lead"
  - text: Software Requirements Author software.author
  - button "Sign out"
  - button "Open workspace display settings": Aa Workspace display comfortable density
- navigation "Breadcrumb":
  - text: Flight Management System Live Program FMS Product Development
  - strong: Documentation Center
- text: Project-wide
- button "Copy link to this page": Copy link
- main:
  - button "← Software Builds"
  - paragraph: PROJECT / CONTROLLED DOCUMENTATION
  - heading "Documentation Center" [level=1]
  - paragraph: Project-wide Microsoft Word lifecycle documents, controlled from checkout through approved release.
  - button "Download connector trust"
  - button "+ New document"
  - status:
    - text: Connector trust manifest downloaded. Run the connector with --enroll and select this file before opening Word.
    - button "×"
  - region "Documentation summary": Controlled documents 12 Current released 7 In work 8 In review / returned 4 Checked out 0
  - complementary:
    - heading "Document register" [level=2]
    - text: 12 of 12 matching records
    - button "Refresh"
    - text: Search
    - textbox "Search":
      - /placeholder: Number, acronym, title
    - text: Status
    - combobox "Status":
      - option "All" [selected]
      - option "Draft"
      - option "In review"
      - option "Returned"
      - option "Released"
    - button "Apply filters"
    - button "ARP ARP-000001 Stewardship transfer 409652 Not released ARP-000001.00 · Draft":
      - text: ARP ARP-000001 Stewardship transfer 409652
      - emphasis: Not released
      - emphasis: ARP-000001.00 · Draft
    - button "IBP IBP-000001 Integrity block 404497 Not released IBP-000001.00 · Draft":
      - text: IBP IBP-000001 Integrity block 404497
      - emphasis: Not released
      - emphasis: IBP-000001.00 · Draft
    - button "ICD ICD-000001 FMS Navigation Interface Control Document ICD-000001.00 · Released ICD-000001.01 · Returned":
      - text: ICD ICD-000001 FMS Navigation Interface Control Document
      - emphasis: ICD-000001.00 · Released
      - emphasis: ICD-000001.01 · Returned
    - button "ORP ORP-000001 Ordered document review 399984 Not released ORP-000001.00 · In Review":
      - text: ORP ORP-000001 Ordered document review 399984
      - emphasis: Not released
      - emphasis: ORP-000001.00 · In Review
    - button "PSAC PSAC-000001 FMS Plan for Software Aspects of Certification PSAC-000001.00 · Released":
      - text: PSAC PSAC-000001 FMS Plan for Software Aspects of Certification
      - emphasis: PSAC-000001.00 · Released
    - button "RIP RIP-000001 Exact review intent 396431 Not released RIP-000001.00 · In Review":
      - text: RIP RIP-000001 Exact review intent 396431
      - emphasis: Not released
      - emphasis: RIP-000001.00 · In Review
    - button "SAS SAS-000001 FMS Software Accomplishment Summary SAS-000001.00 · Released SAS-000001.01 · Draft":
      - text: SAS SAS-000001 FMS Software Accomplishment Summary
      - emphasis: SAS-000001.00 · Released
      - emphasis: SAS-000001.01 · Draft
    - button "SCMP SCMP-000001 FMS Software Configuration Management Plan SCMP-000001.00 · Released":
      - text: SCMP SCMP-000001 FMS Software Configuration Management Plan
      - emphasis: SCMP-000001.00 · Released
    - button "SDP SDP-000001 FMS Software Development Plan SDP-000001.00 · Released SDP-000001.01 · Draft":
      - text: SDP SDP-000001 FMS Software Development Plan
      - emphasis: SDP-000001.00 · Released
      - emphasis: SDP-000001.01 · Draft
    - button "SQAP SQAP-000001 FMS Software Quality Assurance Plan SQAP-000001.00 · Released":
      - text: SQAP SQAP-000001 FMS Software Quality Assurance Plan
      - emphasis: SQAP-000001.00 · Released
    - button "SVP SVP-000001 FMS Software Verification Plan SVP-000001.00 · Released SVP-000001.01 · In Review":
      - text: SVP SVP-000001 FMS Software Verification Plan
      - emphasis: SVP-000001.00 · Released
      - emphasis: SVP-000001.01 · In Review
    - button "WDP WDP-000001 Withdraw revision 406396 Not released":
      - text: WDP WDP-000001 Withdraw revision 406396
      - emphasis: Not released
  - article:
    - paragraph: ASSIGNMENT RECOVERY PLAN
    - heading "Stewardship transfer 409652" [level=2]
    - text: ARP-000001 Draft ARP-000001.00 · Draft
    - navigation "Document record sections":
      - button "overview"
      - button "versions"
      - button "Review & release"
      - button "links"
      - button "audit"
    - heading "Project document accountability" [level=3]
    - term: Document steward
    - definition: Daniel Reyes
    - term: Document created by
    - definition: admin
    - heading "Current released revision" [level=3]
    - paragraph: This Project document has not been released yet.
    - heading "Active successor revision" [level=3]
    - term: Status
    - definition: Draft
    - term: Responsible owner
    - definition: Rina Shah
    - term: Revision initiated by
    - definition: admin
    - term: Contributors
    - definition: admin
    - term: Applicability
    - definition: Project-wide
    - term: Formal revision scope
    - definition: Prove the controlled browser reassignment path.
    - term: Scope evidence
    - definition:
      - code: v1 · 3282c28518dd…
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
  31  |     expect(response.ok(),await response.text()).toBeTruthy()
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
> 72  |   await expect(page.getByText('7 of 7 matching records')).toBeVisible()
      |                                                           ^ Error: expect(locator).toBeVisible() failed
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
  132 |       ? [{ id: crypto.randomUUID(), eventType: 'PagedAuditEvent51', actorId: 'software.author', detail: 'Final retained event', occurredAt: new Date().toISOString() }]
  133 |       : Array.from({ length: 50 }, (_, index) => ({ id: crypto.randomUUID(), eventType: `PagedAuditEvent${index + 1}`, actorId: 'software.author', detail: `Retained event ${index + 1}`, occurredAt: new Date().toISOString() }))
  134 |     await route.fulfill({ json: { pageSize: 50, hasMore: !cursor, nextCursor: cursor ? null : 'mock-audit-next', items } })
  135 |   })
  136 | 
  137 |   await login(page, 'software.author', { openProject: false })
  138 |   await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/documentation-center/${direct.id}`)
  139 |   await expect(page.getByRole('heading', { name: direct.title })).toBeVisible()
  140 |   await expect(page.getByText('50 of 51 matching records')).toBeVisible()
  141 |   await page.getByRole('button', { name: 'Load more documents' }).click()
  142 |   await expect(page.getByText('51 of 51 matching records')).toBeVisible()
  143 |   await expect(page.getByRole('button', { name: /Last paged document/ })).toBeVisible()
  144 |   await page.getByRole('button', { name: 'Audit' }).click()
  145 |   await expect(page.getByRole('heading', { name: 'Complete retained evidence' })).toBeVisible()
  146 |   await page.getByRole('button', { name: 'Load more Audit' }).click()
  147 |   await expect(page.getByText('Paged Audit Event51')).toBeVisible()
  148 | })
  149 | 
  150 | test('review signature dialog exposes and submits the exact frozen intent', async ({ page, request }) => {
  151 |   test.setTimeout(180_000)
  152 |   const showcase = await showcaseSeed(request)
  153 |   await apiLogin(request, 'software.author')
  154 |   const suffix = Date.now().toString().slice(-6)
  155 |   const createdResponse = await request.post(`${apiBase}/api/managed-documents`, { data: {
  156 |     projectId: showcase.projectId,
  157 |     acronym: 'RIP',
  158 |     documentType: 'Review Integrity Plan',
  159 |     title: `Exact review intent ${suffix}`,
  160 |     ownerId: 'software.author',
  161 |     formalChangeSummary: 'Bind the browser decision to exact controlled evidence.',
  162 |     operationKey: crypto.randomUUID(),
  163 |   } })
  164 |   expect(createdResponse.ok(), await createdResponse.text()).toBeTruthy()
  165 |   const created = await createdResponse.json()
  166 |   const detailResponse = await request.get(`${apiBase}/api/managed-documents/${created.id}`)
  167 |   expect(detailResponse.ok(), await detailResponse.text()).toBeTruthy()
  168 |   const detail = await detailResponse.json()
  169 |   const revision = detail.revisions.find((item:{id:string}) => item.id === created.revisionId)
  170 |   const working = revision.attachments.find((item:{id:string}) => item.id === revision.currentWorkingAttachmentId)
  171 |   const submitResponse = await request.post(`${apiBase}/api/managed-documents/revisions/${revision.id}/submit`, { data: {
  172 |     reviewers: [
```