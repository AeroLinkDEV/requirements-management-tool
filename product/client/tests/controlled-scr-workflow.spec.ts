import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'
import { apiBase, login, openNewSoftwareChangeRequest, selectProgram } from './auth'

async function openPageFromPalette(page:Page,label:string){
  await page.getByRole('button',{name:/Search & navigate/}).click()
  const palette=page.getByRole('dialog',{name:'Quick navigation'})
  await palette.getByPlaceholder(/Search pages/).fill(label)
  await palette.getByRole('link',{name:new RegExp(label)}).click()
}

test('author creates, edits, submits, and sequentially approves an SCR', async ({ page }) => {
  test.setTimeout(60_000)
  await login(page, 'admin', { openProject: false })
  const suffix=Date.now().toString().slice(-7),programName=`Browser Workflow ${suffix}`
  if(await page.getByLabel('Program name').count()){
    await page.getByLabel('Program name').fill(programName)
    await page.getByLabel('Program code').fill(`BW${suffix}`)
    await page.getByLabel('Project name').fill('Workflow Software')
    await page.getByLabel('Software product').fill('Workflow Management Software')
    await page.getByLabel('Initial release or baseline').fill('1.0')
    await page.getByRole('button', { name: /Create program workspace/ }).click()
  }else{
    const created=await page.request.post(`${apiBase}/api/workspaces`,{data:{programName,programCode:`BW${suffix}`,projectName:'Workflow Software',softwareProduct:'Workflow Management Software',initialRelease:'1.0',initialReleaseIsReleased:false}})
    expect(created.ok(),await created.text()).toBeTruthy()
    await selectProgram(page,programName)
  }

  await openPageFromPalette(page,'Software Verification')
  await expect(page.getByText('Procedure authoring waits for governed requirement materialization')).toBeVisible()
  await expect(page.getByText(/no immutable requirement revisions yet/)).toBeVisible()
  await expect(page.getByText(/Existing inherited procedures remain visible/)).toBeVisible()
  await expect(page.getByText(/Requirement materialization is not exposed/)).toBeVisible()
  await expect(page.getByRole('button',{name:'New Test Procedure'})).toBeDisabled()
  await expect(page.getByRole('button',{name:'New Test Procedure'})).toHaveAttribute(
    'title',
    'Materialize the software build requirements before creating a procedure.',
  )
  await page.getByRole('button',{name:'Command Center'}).click()

  await openNewSoftwareChangeRequest(page,'HLR')
  await expect(page.getByRole('navigation', { name: 'Change authoring progress' })).toBeVisible()
  // The author chooses the first change; the editor no longer assumes one.
  await page.getByRole('button',{name:'+ Introduce HLR'}).click()
  await expect(page.getByLabel('Identifier')).toHaveValue(/^HLR-\d{6}$/)
  await expect(page.getByLabel('Identifier')).not.toBeEditable()
  await expect(page.getByLabel('Revision')).toHaveValue('00')
  await expect(page.getByLabel('Revision')).not.toBeEditable()
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).toHaveValue('Software HLR')
  await expect(page.getByRole('textbox',{name:'Level',exact:true})).not.toBeEditable()
  // Change type is chosen, not reported — the identifier above it is what is server-issued and fixed.
  await expect(page.getByLabel('Change type')).toHaveValue('Introduce')
  await expect(page.getByLabel('Change type')).toBeEditable()
  await expect(page.getByText('SWR-000001')).toHaveCount(0)
  await page.getByLabel('Title').fill('Introduce controlled browser workflow')
  await page.getByLabel('Problem').fill('The workflow is not yet controlled end to end.')
  await page.getByLabel('Analysis', { exact: true }).fill('SCR content, reviewers, and history must remain attributable.')
  await page.getByLabel('Solution').fill('Add an ordered and auditable approval workflow.')
  await page.getByLabel('Requirement statement').fill('The software shall enforce ordered SCR approval.')
  await page.getByRole('button', { name: 'Save SWCR Draft' }).click()

  await expect(page.getByRole('heading', { name: 'Introduce controlled browser workflow' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Download DOCX' })).toHaveAttribute('href', /\/api\/scrs\/.+\/download\?format=docx/)
  await expect(page.getByRole('link', { name: 'Download PDF' })).toHaveAttribute('href', /\/api\/scrs\/.+\/download\?format=pdf/)
  await page.getByRole('button', { name: 'Check out & edit' }).click()
  await page.getByLabel('Title').fill('Introduce controlled approval workflow')
  await expect(page.getByText('Known downstream context',{exact:true})).toBeVisible()
  await expect(page.locator('.editorColumns aside select')).toHaveCount(0)
  await page.getByRole('button', { name: 'Save & check in' }).click()
  await expect(page.getByRole('heading', { name: 'Introduce controlled approval workflow' })).toBeVisible()

  await page.getByRole('button', { name: 'Configure & Submit Review' }).click()
  await expect(page.getByText('No reviewers selected')).toBeVisible()
  await page.getByRole('button', { name: '+ Add approver' }).click()
  await page.getByLabel('Approver 1 search').fill('AeroLink Administrator')
  await page.getByRole('button', { name: /AeroLink Administrator.*Administrator/ }).click()
  await page.getByRole('button', { name: 'Submit for Review' }).click()
  // The lifecycle state is spelled for a reader; the raw enum stays available to tooling as data-state.
  await expect(page.getByText('In review', { exact: true }).first()).toBeVisible()
  await expect(page.locator('[data-state="InReview"]').first()).toBeVisible()

  await expect(page.getByText('AeroLink Administrator is the active reviewer.')).toBeVisible()
  await page.getByRole('button', { name: 'Review & electronically approve' }).click()
  await page.getByLabel('Re-enter your password').fill('AeroLink!2026')
  await page.getByRole('button', { name: 'Sign & approve' }).click()
  await expect(page.getByText('Approved', { exact: true }).first()).toBeVisible()
  await expect(page.getByText('Scr Approved')).toBeVisible()

  // Once approved, the only thing you can do to a change request is supersede it. The action that does that
  // is Revise, and it must be where Check out & edit was — the same position holding whatever applies now,
  // rather than a differently-worded button in the rail that nobody found.
  await expect(page.getByRole('button', { name: 'Revise', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: /Check out & edit/ })).toHaveCount(0)
  await expect(page.getByText(/This approved revision is immutable/)).toBeVisible()
})
