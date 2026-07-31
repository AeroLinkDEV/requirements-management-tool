import './VerificationLanding.css'

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
export default function VerificationLanding({ scope, buildName, onOpen }: {
  scope: 'System' | 'Software'
  buildName: string
  onOpen: (view: 'testingCoverage' | 'testResults', level?: 'HighLevel' | 'LowLevel') => void
}) {
  const pairs: { level?: 'HighLevel' | 'LowLevel'; title: string; note: string }[] = scope === 'System'
    ? [{ title: 'System', note: 'Verification of the system requirements this build carries.' }]
    : [
        { level: 'HighLevel', title: 'Software HLR', note: 'Verification of the high-level software requirements.' },
        { level: 'LowLevel', title: 'Software LLR', note: 'Verification of the low-level software requirements.' },
      ]

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
            <button type="button" onClick={() => onOpen('testingCoverage', pair.level)}>
              <b>Testing Coverage</b>
              <span>What the requirements are tested by, and which test change requests nobody has picked up.</span>
              <i>Open Testing Coverage →</i>
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
