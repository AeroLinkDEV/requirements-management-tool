import './VerificationLanding.css'
import { LadderCapability, ladderAllows } from './projectLadder'
import type { ProjectLadderProjection } from './projectLadder'
import { verificationArtifactNoun } from './presentation'

/**
 * The two questions a build's verification work splits into.
 *
 * "What is tested, and what has nobody picked up?" and "what did we run, and what happened?" are asked by
 * different people at different points and were previously four tabs inside one workspace, where the answer
 * to either meant knowing which tab held it. They are pages now, and this is the fork between them.
 *
 * Software has two of each, because HLR and LLR test work is planned, done and approved separately. System
 * has one pair. Nothing is computed here on purpose: a chooser that waited on counts would make the reader
 * wait to be shown two links they were always going to be shown.
 */
export default function VerificationLanding({ scope, buildName, ladder, onOpen }: {
  scope: 'System' | 'Software'
  buildName: string
  ladder: ProjectLadderProjection | null
  onOpen: (view: 'testChangeRequests' | 'testingCoverage' | 'testResults', level?: 'HighLevel' | 'LowLevel') => void
}) {
  const pairs: { level?: 'HighLevel' | 'LowLevel'; title: string; note: string }[] = scope === 'System'
    ? (ladderAllows(ladder, 'System', LadderCapability.Verification)
      ? [{ title: 'System', note: 'Verification of the system requirements this build carries.' }]
      : [])
    : [
        ladderAllows(ladder, 'HighLevel', LadderCapability.Verification)
          ? { level: 'HighLevel' as const, title: 'Software HLR', note: 'Verification of the high-level software requirements.' }
          : null,
        ladderAllows(ladder, 'LowLevel', LadderCapability.Verification)
          ? { level: 'LowLevel' as const, title: 'Software LLR', note: 'Verification of the low-level software requirements.' }
          : null,
      ].filter((pair): pair is { level: 'HighLevel' | 'LowLevel'; title: string; note: string } => pair !== null)

  return (
    <main className="verificationLanding">
      <header>
        <p className="eyebrow">VERIFICATION / {scope.toUpperCase()}</p>
        <h1>Verification</h1>
        <p>The test work {buildName} carries, in the two halves it is actually done in.</p>
      </header>

      {pairs.map(pair => (
        <section key={pair.title} aria-label={pair.title}>
          <div className="landingGroupHead">
            <b>{pair.title}</b>
            <span>{pair.note}</span>
          </div>
          <div className="landingCards">
            {/* Two cards, because these became two pages. The card said "Change Requests" and opened the
                assessments page, which is the confusion that made the register impossible to find. */}
            <button type="button" onClick={() => onOpen('testChangeRequests', pair.level)}>
              <b>Change Requests</b>
              <span>The test change requests controlling this build&apos;s {verificationArtifactNoun(scope === 'System' ? 'System' : pair.level).toLowerCase()}, and where each one has got to.</span>
              <i>Open Change Requests →</i>
            </button>
            <button type="button" onClick={() => onOpen('testingCoverage', pair.level)}>
              <b>Downstream Assessments</b>
              <span>Approved changes still waiting for a test conclusion, and what this build&apos;s {verificationArtifactNoun(scope === 'System' ? 'System' : pair.level).toLowerCase()}s cover.</span>
              <i>Open Downstream Assessments →</i>
            </button>
            <button type="button" onClick={() => onOpen('testResults', pair.level)}>
              <b>Test Results</b>
              <span>What this build has to run, and the determination somebody recorded against each one.</span>
              <i>Open Test Results →</i>
            </button>
          </div>
        </section>
      ))}
    </main>
  )
}
