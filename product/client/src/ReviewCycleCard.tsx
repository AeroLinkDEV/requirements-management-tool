import type { ReactNode } from 'react'
import { PersonName } from './People'
import { demoPerson } from './PeopleRegistry'
import { stateLabel } from './presentation'

export type ReviewCycleStep = {
  position: number
  approverId: string
  approverName: string
  authority: string
  stageName: string
  rationale?: string
  state: string
}

export type ReviewCycleSummary = {
  sequence: number
  mode: string
  state: string
  closureReason?: string
  steps: ReviewCycleStep[]
}

const approverRole = (step: ReviewCycleStep) =>
  step.stageName || step.authority || demoPerson(step.approverId)?.role || 'Reviewer'

const approvalStanding = (cycle: ReviewCycleSummary, step: ReviewCycleStep) => {
  if (step.state === 'Approved') return 'Approved'
  if (cycle.mode === 'Parallel' || step.state === 'Active') return 'Awaiting approval'
  const ahead = cycle.steps.filter(other => other.position < step.position && other.state !== 'Approved').length
  if (ahead <= 1) return 'Next in line for approval'
  return `Waiting on ${ahead} earlier approvals`
}

/** The shared, read-only part of a requirements or test change request review cycle. */
export default function ReviewCycleCard({ cycle, children }: { cycle?: ReviewCycleSummary; children?: ReactNode }) {
  if (!cycle) {
    return <section className="workspaceCard reviewCycleCard">
      <div className="workspaceTitle">
        <div><h2>Review cycle</h2><p>No cycle recorded</p></div>
      </div>
      <p className="workspaceEmpty">This controlled record has no review cycle evidence.</p>
      {children}
    </section>
  }

  return <section className="workspaceCard reviewCycleCard">
    <div className="workspaceTitle">
      <div><h2>Review cycle {cycle.sequence}</h2><p>{stateLabel(cycle.state)}</p></div>
    </div>
    <div className="approvalPath">
      {cycle.steps.map(step => (
        <div className={`approvalStep ${step.state.toLowerCase()}`} key={step.position}>
          <span>{step.state === 'Approved' ? '✓' : step.state === 'Returned' ? '↩' : step.position + 1}</span>
          <div>
            <b><PersonName userName={step.approverId} displayName={step.approverName} /></b>
            <small>{approverRole(step)} · {approvalStanding(cycle, step)}</small>
            {step.rationale && <small className="stepRationale">{step.rationale}</small>}
          </div>
        </div>
      ))}
    </div>
    {cycle.closureReason && <div className="closure"><b>Closure reason</b><p>{cycle.closureReason}</p></div>}
    {children}
  </section>
}
