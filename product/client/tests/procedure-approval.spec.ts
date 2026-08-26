import { expect, test } from '@playwright/test'
import { apiBase, apiLogin, showcaseSeed } from './auth'

/**
 * Only an approved procedure revision can be executed.
 *
 * This journey used to create a procedure through the direct-create route, sign it with a procedure-level
 * approval, and then run it. Both of those are gone: a procedure is introduced, modified or retired by a test
 * change request, and the package's review is what approves the work — materialisation writes the revision as
 * Approved on that authority, so a separate signature on the revision would approve the same work twice.
 *
 * The rule underneath survived all of that, and is the only place it is covered: a revision that is not
 * Approved cannot have an execution recorded against it. So the Draft is found rather than made, and nothing
 * here approves anything.
 */
test('an unapproved procedure revision cannot have an execution recorded against it', async ({ request }) => {
  await apiLogin(request)
  const showcase = await showcaseSeed(request)

  const draftsResponse = await request.get(
    `${apiBase}/api/test-procedures?projectId=${showcase.projectId}&artifactKind=Procedure&state=Draft&page=1&pageSize=1`)
  expect(draftsResponse.ok(), await draftsResponse.text()).toBeTruthy()
  const drafts = await draftsResponse.json()
  // The build carries an unapproved procedure revision. If it ever stops carrying one this fails loudly
  // rather than passing vacuously, because a vacuous pass here would hide the gate coming off.
  expect(drafts.items.length, 'the showcase build carries no Draft procedure revision to exercise the gate')
    .toBeGreaterThan(0)
  const draft = drafts.items[0]
  expect(draft.state).toBe('Draft')

  const blocked = await request.post(`${apiBase}/api/test-executions`, {
    data: {
      projectId: showcase.projectId,
      procedureRevisionId: draft.revisionId,
      softwareBuildId: null,
      retestOfExecutionId: null,
      outcome: 'Pass',
      configuration: 'Controlled integration rig',
      determination: 'Observed result satisfies the expected result.',
      evidenceReference: 'evidence/procedure-execution-gate.json',
      executedAt: new Date().toISOString(),
    },
  })
  expect(blocked.status(), await blocked.text()).toBe(400)
  expect((await blocked.json()).error).toContain('approved')

  // And an approved one is accepted, so the refusal above is the state talking rather than the endpoint
  // refusing everything.
  const approvedResponse = await request.get(
    `${apiBase}/api/test-procedures?projectId=${showcase.projectId}&artifactKind=Procedure&state=Approved&page=1&pageSize=1`)
  expect(approvedResponse.ok(), await approvedResponse.text()).toBeTruthy()
  const approved = (await approvedResponse.json()).items[0]
  expect(approved.state).toBe('Approved')

  const recorded = await request.post(`${apiBase}/api/test-executions`, {
    data: {
      projectId: showcase.projectId,
      procedureRevisionId: approved.revisionId,
      softwareBuildId: null,
      retestOfExecutionId: null,
      outcome: 'Pass',
      configuration: 'Controlled integration rig',
      determination: 'Observed result satisfies the approved expected result.',
      evidenceReference: 'evidence/procedure-execution-gate.json',
      executedAt: new Date().toISOString(),
    },
  })
  expect(recorded.ok(), await recorded.text()).toBeTruthy()
})
