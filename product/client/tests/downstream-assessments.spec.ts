import { expect, test } from '@playwright/test'
import { apiLogin, login, openNavigationGroup, showcaseSeed } from './auth'

test('downstream assessment actions follow authority and submit without a form-navigation no-op', async ({page,request,browser}) => {
  const showcase=await showcaseSeed(request)
  await apiLogin(request)
  const apiResponse=await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/downstream-assessments?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}`)
  expect(apiResponse.ok(),await apiResponse.text()).toBeTruthy()
  const assessments=await apiResponse.json() as {sourceChangeRequestNumber:string;state:string;outcome:string}[]
  const seededActionable=assessments.find(row=>row.sourceChangeRequestNumber==='SRCR-00031.00'&&row.state==='Open'&&row.outcome==='Pending')
  expect(seededActionable,'The seeded SRCR-00031.00 assessment must remain actionable for this authority journey').toBeTruthy()

  const unauthorizedContext=await browser.newContext()
  const unauthorized=await unauthorizedContext.newPage()
  await login(unauthorized,'systems.reviewer')
  await openNavigationGroup(unauthorized,'SOFTWARE ENGINEERING')
  await unauthorized.getByRole('link',{name:'Software Change Requests'}).click()
  const unauthorizedQueue=unauthorized.locator('.downstreamQueue')
  // Every row offers the same one control whoever is reading. What the reader may do is decided inside.
  await expect(unauthorizedQueue.getByRole('button',{name:'Change required'})).toHaveCount(0)
  const unauthorizedAssessment=unauthorizedQueue.locator('.downstreamAssessment').filter({hasText:seededActionable!.sourceChangeRequestNumber}).first()
  await expect(unauthorizedAssessment).toBeVisible()
  await unauthorizedAssessment.getByRole('button',{name:'Open assessment'}).click()
  const unauthorizedDrawer=unauthorized.getByRole('dialog',{name:/downstream impact/})
  await expect(unauthorizedDrawer).toContainText('Software engineering authority is required')
  await expect(unauthorizedDrawer.getByRole('button',{name:'Change required'})).toHaveCount(0)
  await unauthorizedContext.close()

  // Establish the approver's browser session and queue before the engineer makes the irreversible
  // submission. If this login/setup fails, the assessment is still Open/Pending for a retry.
  const approverContext=await browser.newContext()
  const approverPage=await approverContext.newPage()
  await login(approverPage,'software.lead')
  await openNavigationGroup(approverPage,'SOFTWARE ENGINEERING')
  await approverPage.getByRole('link',{name:'Software Change Requests'}).click()

  await login(page)
  await openNavigationGroup(page,'SOFTWARE ENGINEERING')
  await page.getByRole('link',{name:'Software Change Requests'}).click()

  await expect(page.getByRole('heading',{name:'Downstream change assessments'})).toBeVisible()
  const queue=page.locator('.downstreamQueue')
  await expect(queue.getByText('SRCR-00031.00')).toBeVisible()
  await expect(queue.getByText('HLR assessment').first()).toBeVisible()
  await expect(queue).toContainText('One Draft may answer several assessments')
  await expect(page.getByRole('heading',{name:'Software Change Requests'})).toBeVisible()
  // One entry control, worded identically on every row whatever state the assessment is in.
  const entryControls=await queue.locator('.downstreamActions button').allTextContents()
  expect(entryControls.length).toBeGreaterThan(0)
  expect([...new Set(entryControls)]).toEqual(['Open assessment'])

  const assessment=queue.locator('.downstreamAssessment').filter({hasText:'SRCR-00031.00'}).first()
  await assessment.getByRole('button',{name:'Open assessment'}).click()
  const workbench=page.getByRole('dialog',{name:'SRCR-00031.00 downstream impact'})
  // Straight to the conclusions. There is no claim to make first — answering is what takes it on.
  await expect(workbench.getByRole('button',{name:'No change required'})).toBeVisible()
  await workbench.getByRole('button',{name:'No change required'}).click()
  const noChangeDialog=page.getByRole('dialog',{name:'Record no-change conclusion for SRCR-00031.00'})
  await expect(noChangeDialog).toBeVisible()
  await expect(noChangeDialog.getByRole('button',{name:'Record no-change conclusion'})).toBeDisabled()
  await noChangeDialog.getByLabel('Decision rationale').fill('No software requirement change is required for this build.')
  await noChangeDialog.getByRole('button',{name:'Record no-change conclusion'}).click()
  await expect(noChangeDialog).toBeHidden()
  const approver=workbench.getByLabel(/Approver for SRCR-00031\.00/)
  await approver.fill('software.lead')
  await workbench.locator('.personSuggestions button[data-user-name="software.lead"]').click()
  await workbench.getByRole('button',{name:'Send for approval'}).click()
  // The queue's answer reads the same before and after sending — a decided no-change conclusion is pending
  // approval either way — so what proves the submission landed is the drawer handing the assessment over:
  // the approver picker goes, and the approver it is now with is named.
  await expect(workbench.locator('.personName[title="software.lead"]')).toBeVisible()
  await expect(workbench.getByRole('button',{name:'Send for approval'})).toBeHidden()
  await expect(workbench).toContainText('HLR Assessment Complete – No HLRCR Required Pending Approval')
  await page.reload()
  const persisted=page.locator('.downstreamAssessment').filter({hasText:'SRCR-00031.00'}).first()
  await expect(persisted).toContainText('HLR Assessment Complete – No HLRCR Required Pending Approval')
  const persistedWorkbench=page.getByRole('dialog',{name:'SRCR-00031.00 downstream impact'})
  await expect(persistedWorkbench.locator('.personName[title="software.lead"]')).toBeVisible()

  // Refresh the already-authenticated approver queue after submission so its drawer reflects the
  // persisted InReview state. The refresh is intentionally retained as a post-mutation durability check.
  await approverPage.reload()
  const approvalAssessment=approverPage.locator('.downstreamAssessment').filter({hasText:'SRCR-00031.00'}).first()
  await approvalAssessment.getByRole('button',{name:'Open assessment'}).click()
  const approvalWorkbench=approverPage.getByRole('dialog',{name:'SRCR-00031.00 downstream impact'})
  await approvalWorkbench.getByRole('button',{name:'Return'}).click()
  const returnDialog=approverPage.getByRole('dialog',{name:'Return SRCR-00031.00 assessment'})
  await expect(returnDialog).toBeVisible()
  await expect(returnDialog.getByRole('button',{name:'Return assessment'})).toBeDisabled()
  await returnDialog.getByLabel('Decision rationale').fill('Clarify the software-level allocation before approval.')
  await returnDialog.getByRole('button',{name:'Return assessment'}).click()
  await expect(returnDialog).toBeHidden()
  // Returned to its engineer, so it is decided and unapproved — which is what the label says. The reviewer's
  // reason for sending it back is stated inside the drawer rather than in the queue's one-line answer.
  await expect(approvalWorkbench).toContainText('HLR Assessment Complete – No HLRCR Required Pending Approval')
  await expect(approvalWorkbench).toContainText('Clarify the software-level allocation before approval.')
  await approverContext.close()
})

test('an assessment deep link explains impact and a required change creates and links the correct Draft HLRCR',async({page,request})=>{
  test.setTimeout(90_000)
  const showcase=await showcaseSeed(request)
  await apiLogin(request)
  const response=await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/downstream-assessments?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&targetLevel=HighLevel`)
  expect(response.ok(),await response.text()).toBeTruthy()
  const rows=await response.json()
  // Undecided, not merely open: the drawer offers a first conclusion only where none has been recorded, so
  // an assessment another journey already answered is not a candidate for recording one.
  const candidates=rows.filter((row:{state:string;outcome:string;capabilities:{canAssign:boolean;canEdit:boolean}})=>
    row.state==='Open'&&row.outcome==='Pending'&&(row.capabilities.canAssign||row.capabilities.canEdit))
  expect(candidates.length,'An actionable HLR assessment').toBeGreaterThan(0)
  let candidate=candidates[0]
  for(const row of candidates){
    const impacts=await Promise.all(row.sourceChanges.map((change:{displayNumber:string})=>request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/authoring/impact?projectId=${showcase.projectId}&baseNumber=${change.displayNumber.replace(/\.\d{2}$/,'')}`).then(result=>result.json())))
    if(impacts.every(impact=>impact.derivedRequirements.length===0)){candidate=row;break}
  }

  await login(page)
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/software/change-requests?level=HLR&assessment=${candidate.id}`)
  const drawer=page.getByRole('dialog',{name:`${candidate.sourceChangeRequestNumber} downstream impact`})
  await expect(drawer).toBeVisible({timeout:30_000})
  await expect(drawer.getByRole('heading',{name:'Source change request'})).toBeVisible()
  await expect(drawer.getByText(candidate.sourceProblem).first()).toBeVisible()
  await page.reload()
  await expect(drawer).toBeVisible()
  expect(page.url()).toContain(`assessment=${candidate.id}`)
  if(candidate.sourceChanges.length)await expect(drawer.getByRole('heading',{name:'Approved requirement changes'})).toBeVisible()
  const emptyTrace=drawer.getByText('No current downward requirement trace is recorded for the changed requirements.')
  if(await emptyTrace.count())await expect(emptyTrace).toBeVisible()
  await drawer.getByRole('button',{name:new RegExp(candidate.sourceChangeRequestNumber)}).click()
  await expect(page).toHaveURL(new RegExp(`/systems/change-requests/${candidate.sourceChangeRequestId}$`))
  await page.goBack()
  await expect(drawer).toBeVisible()
  await drawer.getByRole('button',{name:'Close downstream assessment'}).click()
  await expect(page).not.toHaveURL(/assessment=/)

  const assessment=page.locator('.downstreamAssessment').filter({hasText:candidate.sourceChangeRequestNumber}).first()
  await assessment.getByRole('button',{name:'Open assessment'}).click()
  const decisionWorkbench=page.getByRole('dialog',{name:`${candidate.sourceChangeRequestNumber} downstream impact`})
  await decisionWorkbench.getByRole('button',{name:'Change required',exact:true}).click()
  await expect(decisionWorkbench).toContainText('HLR Assessment Complete – Draft HLRCR Required')
  await decisionWorkbench.getByRole('button',{name:'Create Draft HLRCR'}).click()
  await expect(page.getByRole('heading',{name:'Create HLR Change Request'})).toBeVisible()
  await page.getByLabel('Title').fill(`Implement ${candidate.sourceChangeRequestNumber} downstream impact`)
  let failLinkOnce=true
  await page.route('**/api/downstream-assessments/*/change-requests',route=>{
    if(failLinkOnce){failLinkOnce=false;return route.fulfill({status:500,contentType:'application/json',body:JSON.stringify({error:'Simulated recoverable link outage.'})})}
    return route.continue()
  })
  await page.getByRole('button',{name:'Save HLRCR Draft'}).click()
  await expect(page).toHaveURL(/\/software\/change-requests\/[0-9a-f-]+$/i)
  const createdId=page.url().split('/').pop()!
  const recovery=page.getByRole('alert').filter({hasText:'Downstream assessment link needs attention'})
  await expect(recovery).toBeVisible()
  await recovery.getByRole('button',{name:'Retry assessment link'}).click()
  await expect(recovery).toBeHidden()
  const persisted=await (await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/downstream-assessments?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&targetLevel=HighLevel`)).json()
  const updated=persisted.find((row:{id:string})=>row.id===candidate.id)
  expect(updated.outcome).toBe('ChangeRequestsLinked')
  const linked=updated.linkedChangeRequests.find((link:{changeRequestId:string})=>link.changeRequestId===createdId)
  expect(linked).toMatchObject({state:'Draft',title:`Implement ${candidate.sourceChangeRequestNumber} downstream impact`})
})

/**
 * A concluded assessment states what was concluded, and offers only what is still open to do.
 *
 * The drawer used to render the same conclusion controls whatever state the assessment was in, so one already
 * answered showed "No change required" and "Change required" both live and indistinguishable from a
 * first-time answer. There was no way to say "that answer was wrong" other than pressing one of them, which
 * left nothing behind to show the question had ever been answered differently. Reopening is that statement,
 * made deliberately and kept.
 *
 * The subject is an assessment an earlier journey in this file already answered — which is the state under
 * test, and means this journey does not compete for the showcase's finite supply of unanswered ones.
 */
test('a concluded assessment states its conclusion, and correcting it is an act of its own',async({page,request,browser})=>{
  test.setTimeout(150_000)
  const showcase=await showcaseSeed(request)
  await apiLogin(request)
  const rows=await (await request.get(`${process.env.AEROLINK_E2E_API_BASE}/api/downstream-assessments?projectId=${showcase.projectId}&releaseId=${showcase.activeReleaseId}&targetLevel=HighLevel`)).json()
  const subject=rows.find((row:{outcome:string;capabilities:{canReopen:boolean}})=>row.capabilities.canReopen&&row.outcome!=='Pending')
  expect(subject,'An assessment this engineer has already concluded').toBeTruthy()
  const number:string=subject.sourceChangeRequestNumber

  await login(page)
  await page.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/software/change-requests?level=HLR&assessment=${subject.id}`)
  const drawer=page.getByRole('dialog',{name:`${number} downstream impact`})
  await expect(drawer).toBeVisible({timeout:30_000})

  // Answered: the conclusion is stated outright with its author, and neither answer is offered again.
  const conclusion=drawer.locator('.recordedConclusion')
  await expect(conclusion).toContainText(/HLR requirement change is required/)
  await expect(conclusion).toContainText('Recorded by')
  await expect(drawer.getByRole('button',{name:'No change required'})).toHaveCount(0)
  await expect(drawer.getByRole('button',{name:'Change required',exact:true})).toHaveCount(0)

  // Correcting it is a separate act, it needs a reason, and it keeps what it withdrew.
  await drawer.getByRole('button',{name:'Reopen assessment'}).click()
  const reopen=page.getByRole('dialog',{name:`Reopen the ${number} assessment`})
  await expect(reopen.getByRole('button',{name:'Reopen assessment'})).toBeDisabled()
  await reopen.getByLabel('Reason for withdrawing the conclusion').fill('A second reading shows the timing wording is not covered by any current HLR.')
  await reopen.getByRole('button',{name:'Reopen assessment'}).click()
  await expect(reopen).toBeHidden()

  // Undecided again, and that is the one state offering both answers.
  await expect(drawer.locator('.recordedConclusion')).toHaveCount(0)
  await expect(drawer.getByRole('button',{name:'No change required'})).toBeVisible()
  await expect(drawer.getByRole('button',{name:'Change required',exact:true})).toBeVisible()
  const withdrawn=drawer.locator('.withdrawnConclusions')
  await expect(withdrawn).toContainText('timing wording is not covered')
  await expect(withdrawn).toContainText('Withdrawn by')

  // It survives a reload, because it is a record rather than a screen state.
  await page.reload()
  const reloaded=page.getByRole('dialog',{name:`${number} downstream impact`})
  await expect(reloaded.locator('.withdrawnConclusions')).toContainText('timing wording is not covered',{timeout:30_000})

  await reloaded.getByRole('button',{name:'No change required'}).click()
  const noChange=page.getByRole('dialog',{name:`Record no-change conclusion for ${number}`})
  await noChange.getByLabel('Decision rationale').fill('The approved System wording is already satisfied by the current HLR set.')
  await noChange.getByRole('button',{name:'Record no-change conclusion'}).click()
  await expect(noChange).toBeHidden()
  await expect(reloaded.locator('.recordedConclusion')).toContainText('already satisfied by the current HLR set')

  const approver=reloaded.getByLabel(new RegExp(`Approver for ${number.replace('.','\\.')}`))
  await expect(approver).toBeVisible({timeout:30_000})
  await approver.fill('software.lead')
  await reloaded.locator('.personSuggestions button[data-user-name="software.lead"]').click()
  await reloaded.getByRole('button',{name:'Send for approval'}).click()
  await expect(reloaded).toContainText('HLR Assessment Complete – No HLRCR Required Pending Approval')
  // With the approver holding it, the engineer can neither answer it again nor withdraw it behind them.
  await expect(reloaded.getByRole('button',{name:'Reopen assessment'})).toHaveCount(0)
  await expect(reloaded.getByRole('button',{name:'No change required'})).toHaveCount(0)

  const approverContext=await browser.newContext()
  const approverPage=await approverContext.newPage()
  await login(approverPage,'software.lead')
  await approverPage.goto(`/programs/${showcase.programId}/projects/${showcase.projectId}/releases/${showcase.activeReleaseId}/software/change-requests?level=HLR&assessment=${subject.id}`)
  const approval=approverPage.getByRole('dialog',{name:`${number} downstream impact`})
  await expect(approval).toBeVisible({timeout:30_000})
  await approval.getByRole('button',{name:'Approve'}).click()
  await expect(approval).toContainText('HLR Assessment Complete – No HLRCR Required')
  // Approved: the conclusion is stated with both hands that touched it, and the only act left is a
  // deliberate withdrawal.
  await expect(approval.locator('.recordedConclusion')).toContainText('Approved by')
  await expect(approval.getByRole('button',{name:'No change required'})).toHaveCount(0)
  await expect(approval.getByRole('button',{name:'Change required',exact:true})).toHaveCount(0)
  await expect(approval.getByRole('button',{name:'Reopen assessment'})).toBeVisible()
  await approverContext.close()
})
