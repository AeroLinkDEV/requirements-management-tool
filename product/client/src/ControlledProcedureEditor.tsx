import { useEffect, useMemo, useState } from 'react'
import { isVerificationProcedureKind, stateLabel, verificationArtifactApiRoot, verificationArtifactNoun, verificationArtifactWord } from './presentation'
import './ControlledRequirementEditor.css'

/**
 * One proposed procedure change, authored the way a requirement proposal is authored.
 *
 * Stage 2 of raising a package was a single `+ Add a procedure decision` button and a flat form. The
 * requirements side has had controlled authoring for a while: a proposal is introduced, modified or retired;
 * a modification searches the controlled library and locks the exact identity and next revision; and the card
 * says what identity it has taken. Authoring a procedure change is the same job on a different artifact, so
 * it is the same editor — this is {@link ControlledRequirementEditor} over procedures, sharing its stylesheet
 * so the two cannot drift apart visually either.
 */

export type ProcedureChangeKind = 'Introduce' | 'Modify' | 'Retire'

export type ProcedureProposal = {
  key: string
  kind: ProcedureChangeKind
  baseNumber: string
  /** The revision this proposal will become. Introduce has none until check-in assigns the identity. */
  revision: number
  title: string
  objective: string
  preconditions: string
  steps: string
  expectedResult: string
  rationale: string
}

type ExistingProcedure = {
  id: string
  /** `SYSTP-000001.02`, or just the base number for a procedure with no controlled revision yet. */
  displayNumber: string
  /** Null until the procedure has a controlled revision, which is why the next revision is derived and not assumed. */
  revision: number | null
  title: string
  level: string
  state: string
}

/** The controlled identity inside a display number, which is what a proposal locks onto. */
const baseNumberOf = (displayNumber: string) => displayNumber.split('.')[0]

const kindOptions: { value: ProcedureChangeKind; label: string }[] = [
  { value: 'Introduce', label: 'Introduce a new procedure' },
  { value: 'Modify', label: 'Modify an existing procedure' },
  { value: 'Retire', label: 'Retire an existing procedure' },
]

export default function ControlledProcedureEditor({
  api, projectId, releaseId, scope, artifactKind, levelLabel, item, index, onChange, onRemove,
}: {
  api: string
  projectId: string
  releaseId: string
  /** The discipline's scope as the procedure list endpoint names it. */
  scope: string
  /** The exact package artifact key; software Procedure packages must search procedures, not Cases. */
  artifactKind?: string
  levelLabel: string
  item: ProcedureProposal
  index: number
  onChange: (key: keyof ProcedureProposal, value: string | number) => void
  onRemove: () => void
}) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<ExistingProcedure[]>([])
  const [lookupBusy, setLookupBusy] = useState(false)
  const [lookupError, setLookupError] = useState('')
  const artifactWord = verificationArtifactWord(scope, artifactKind)
  const artifactNoun = verificationArtifactNoun(scope, artifactKind)
  const artifactApiRoot = verificationArtifactApiRoot(scope, artifactKind)

  // An Introduce proposal has no existing identity to find, and a proposal that has already locked one is
  // not looking for another.
  const identityLocked = item.kind === 'Introduce' || item.baseNumber.trim().length > 0

  useEffect(() => {
    if (item.kind === 'Introduce') { setResults([]); return }
    const term = query.trim()
    if (term.length < 2) { setResults([]); setLookupError(''); return }
    let cancelled = false
    setLookupBusy(true)
    // Debounced, so typing an identifier does not fire a request per keystroke.
    const timer = window.setTimeout(() => {
      fetch(`${api}${artifactApiRoot}?projectId=${projectId}&releaseId=${releaseId}&scope=${scope}` +
        (isVerificationProcedureKind(artifactKind) ? '&artifactKind=Procedure' : '') +
        `&search=${encodeURIComponent(term)}&page=1&pageSize=8`)
        .then(response => response.ok ? response.json() : Promise.reject(new Error(String(response.status))))
        .then((body: { items: ExistingProcedure[] }) => {
          if (cancelled) return
          setResults(body.items ?? [])
          setLookupError('')
        })
        .catch(() => { if (!cancelled) { setResults([]); setLookupError(`The controlled ${artifactNoun.toLowerCase()} library could not be searched.`) } })
        .finally(() => { if (!cancelled) setLookupBusy(false) })
    }, 180)
    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [api, artifactApiRoot, artifactNoun, item.kind, projectId, query, releaseId, scope, artifactKind])

  const selectExisting = (selected: ExistingProcedure) => {
    onChange('baseNumber', baseNumberOf(selected.displayNumber))
    // The next revision, locked from what the library actually carries rather than typed by hand. A procedure
    // with no controlled revision yet becomes revision 00, not NaN.
    onChange('revision', (selected.revision ?? -1) + 1)
    if (item.kind === 'Modify') {
      // Carried forward so the engineer corrects the procedure rather than retyping it.
      onChange('title', selected.title)
    }
    setQuery('')
    setResults([])
  }

  const displayNumber = useMemo(() => item.kind === 'Introduce'
    ? `New ${levelLabel} ${artifactWord}`
    : item.baseNumber
      ? `${item.baseNumber}.${String(item.revision).padStart(2, '0')}`
      : `Select an existing controlled ${artifactNoun.toLowerCase()}`, [artifactNoun, item.baseNumber, item.kind, item.revision, levelLabel, artifactWord])

  return (
    <article className={`controlledEditor ${identityLocked ? 'identityLocked' : 'identityPending'}`}
      data-procedure-proposal={index}>
      <header>
        <div>
          <span>PROPOSAL {index + 1}</span>
          <h3>{displayNumber}</h3>
        </div>
        <div>
          <i>{levelLabel}</i>
          <i>{item.kind}</i>
          <button type="button" onClick={onRemove}>Remove</button>
        </div>
      </header>

      {!identityLocked && item.kind !== 'Introduce' && (
        <section className="proposalLookup" aria-label={`Select ${artifactNoun.toLowerCase()} for proposal ${index + 1}`}>
          <div>
              <b>{item.kind === 'Modify' ? `Select the ${artifactNoun.toLowerCase()} to modify` : `Select the ${artifactNoun.toLowerCase()} to retire`}</b>
              <span>
                Search by {artifactNoun.toLowerCase()} identifier or words in its title. AeroLink will lock the exact identity and
              next revision.
            </span>
          </div>
          <label>
            Find controlled {artifactNoun.toLowerCase()}
            <input
              aria-label={`Find controlled ${artifactNoun.toLowerCase()} ${index + 1}`}
              value={query}
              onChange={event => setQuery(event.target.value)}
              placeholder={`Search identifier or ${artifactNoun.toLowerCase()} title`}
              autoComplete="off"
            />
          </label>
          {lookupBusy && <small className="lookupStatus">Searching…</small>}
          {lookupError && <small className="lookupStatus error">{lookupError}</small>}
          {!lookupBusy && query.trim().length >= 2 && !results.length && !lookupError && (
            <small className="lookupStatus">No permitted {artifactNoun.toLowerCase()}s match that identifier or title.</small>
          )}
          {!!results.length && (
            <div className="proposalLookupResults">
              {results.map(result => (
                <button type="button" key={result.id} onClick={() => selectExisting(result)}>
                  <span>
                    <b>{result.displayNumber}</b>
                    <small>{stateLabel(result.level)} · {stateLabel(result.state)}</small>
                  </span>
                  <p>{result.title}</p>
                  <em>Use revision {String((result.revision ?? -1) + 1).padStart(2, '0')} →</em>
                </button>
              ))}
            </div>
          )}
        </section>
      )}

      <div className="proposalIdentity">
        <label>
          Identifier
          <input
            aria-label={`Identifier ${index + 1}`}
            value={item.kind === 'Introduce'
              ? 'Provisional — assigned at check-in'
              : item.baseNumber || 'Awaiting controlled selection'}
            readOnly
          />
        </label>
        <label>
          Revision
          <input aria-label={`Revision ${index + 1}`}
            value={item.kind === 'Introduce' ? 'Pending' : String(item.revision).padStart(2, '0')} readOnly />
        </label>
        <label>
          Change type
          <select aria-label={`Change type ${index + 1}`} value={item.kind}
            onChange={event => {
              onChange('kind', event.target.value)
              // Changing what the proposal does resets the identity it had selected, because the identity was
              // chosen for a different act.
              onChange('baseNumber', '')
              onChange('revision', 0)
            }}>
            {kindOptions.map(option => <option key={option.value} value={option.value}>{option.label.replace(/procedure/gi, artifactNoun.toLowerCase())}</option>)}
          </select>
        </label>
      </div>

      {item.kind === 'Introduce' && (
        <label className="proposalField">
          {artifactNoun} number
          <input aria-label={`${artifactNoun} number ${index + 1}`} value={item.baseNumber}
            onChange={event => onChange('baseNumber', event.target.value)}
            placeholder={`The controlled number this ${artifactNoun.toLowerCase()} will carry`} />
        </label>
      )}

      {item.kind !== 'Retire' && (
        <>
          <label className="proposalField">
            Title
            <input aria-label={`Title ${index + 1}`} value={item.title}
              onChange={event => onChange('title', event.target.value)} />
          </label>
          <label className="proposalField">
            Objective
            <textarea aria-label={`Objective ${index + 1}`} value={item.objective}
              onChange={event => onChange('objective', event.target.value)}
              placeholder={`What this ${artifactNoun.toLowerCase()} sets out to demonstrate`} />
          </label>
          <label className="proposalField">
            Preconditions
            <textarea aria-label={`Preconditions ${index + 1}`} value={item.preconditions}
              onChange={event => onChange('preconditions', event.target.value)}
              placeholder="What must be true before it runs" />
          </label>
          <label className="proposalField">
            Steps
            <textarea aria-label={`Steps ${index + 1}`} value={item.steps}
              onChange={event => onChange('steps', event.target.value)}
              placeholder="What the operator does, in order" />
          </label>
          <label className="proposalField">
            Expected result
            <textarea aria-label={`Expected result ${index + 1}`} value={item.expectedResult}
              onChange={event => onChange('expectedResult', event.target.value)}
              placeholder="What must be observed for a pass" />
          </label>
        </>
      )}

      <label className="proposalField">
        Rationale
        <textarea aria-label={`Rationale ${index + 1}`} value={item.rationale}
          onChange={event => onChange('rationale', event.target.value)}
          placeholder={item.kind === 'Retire'
            ? `Why this ${artifactNoun.toLowerCase()} is being withdrawn`
            : 'Why this change is necessary'} />
      </label>
    </article>
  )
}
