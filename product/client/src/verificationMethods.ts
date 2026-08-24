import { useCallback, useEffect, useRef, useState } from 'react'
import { apiRequest, operationError } from './apiClient'

export type NonConformingVerificationMethod = {
  value: string
  changeCount: number
  revisionCount: number
  totalCount: number
  examples: string[]
}

export type VerificationVocabulary = {
  persisted: boolean
  version: number
  methods: string[]
  canManage: boolean
  nonConforming: NonConformingVerificationMethod[]
}

export type VerificationVocabularyState = {
  /**
   * The project this vocabulary is the authority for. A caller that scopes a write by project must compare
   * this against the project it is writing to, rather than assuming the two agree.
   */
  projectId: string | undefined
  vocabulary: VerificationVocabulary | undefined
  loading: boolean
  error: string
  reload: () => Promise<void>
}

type ScopedVocabulary = {
  projectId: string | undefined
  vocabulary: VerificationVocabulary | undefined
  loading: boolean
  error: string
}

/**
 * The project's permitted verification methods (#701).
 *
 * There is deliberately no client-side default. The server is the only authority for what a project
 * permits, and a hard-coded list here would be a second one — silently correct until a programme configures
 * Similarity, and then silently wrong in a screen an author trusts. When the vocabulary has not arrived the
 * caller is told so and says so; it does not quietly substitute the four methods AeroLink happens to ship
 * with. A refusal at submission names the permitted values either way, so nothing here can widen what review
 * accepts — but an honest empty state is what stops an author picking a method the project has withdrawn.
 *
 * Two things make the authority project-safe rather than merely eventually-correct.
 *
 * State carries the project that produced it, and is only presented as current when that project is still
 * the one being asked about. Effects run after render, so a component switched from project A to project B
 * would otherwise spend a frame showing A's permitted methods and A's version as though they were B's — long
 * enough for Project Configuration to send A's edit state to B's endpoint.
 *
 * Every request takes a ticket, and only the newest ticket may write state. Without that, a slow answer for
 * A arriving after a fast answer for B would overwrite B with A, and nothing later would correct it because
 * no further request is in flight.
 */
export function useVerificationVocabulary(api: string, projectId: string | undefined): VerificationVocabularyState {
  const [scoped, setScoped] = useState<ScopedVocabulary>({
    projectId: undefined, vocabulary: undefined, loading: false, error: '',
  })
  const ticketRef = useRef(0)

  const reload = useCallback(async () => {
    const requested = projectId
    const ticket = ++ticketRef.current
    if (!requested) {
      setScoped({ projectId: undefined, vocabulary: undefined, loading: false, error: '' })
      return
    }
    setScoped({ projectId: requested, vocabulary: undefined, loading: true, error: '' })
    try {
      const read = await apiRequest<VerificationVocabulary>(`${api}/api/projects/${requested}/verification-methods`)
      if (ticketRef.current !== ticket) return
      setScoped({ projectId: requested, vocabulary: read, loading: false, error: '' })
    } catch (failure) {
      if (ticketRef.current !== ticket) return
      setScoped({
        projectId: requested,
        vocabulary: undefined,
        loading: false,
        error: operationError(failure, 'The permitted verification methods could not be loaded.'),
      })
    }
  }, [api, projectId])

  useEffect(() => { void reload() }, [reload])

  // The synchronous half of the guard. Between a projectId change and the effect that acts on it, the stored
  // answer belongs to the previous project; presenting it would be presenting the wrong project's authority.
  const current: ScopedVocabulary = scoped.projectId === projectId
    ? scoped
    : { projectId, vocabulary: undefined, loading: !!projectId, error: '' }
  return { projectId, vocabulary: current.vocabulary, loading: current.loading, error: current.error, reload }
}

/**
 * The options an authoring select offers: the configured vocabulary, with the value already on the record
 * kept at the front when the vocabulary no longer permits it.
 *
 * A historical requirement that says "Testing" must keep saying "Testing" while an author reads it. Dropping
 * the stored value from the list would make the select show a different method than the record holds and
 * write that substitution back the moment anything else on the form changed — a silent rewrite of controlled
 * data caused by nothing more than opening a screen. The value is offered, marked, and refused at submission
 * until somebody corrects it deliberately.
 */
export function authoringOptions(methods: string[] | undefined, current: string): string[] {
  const permitted = methods ?? []
  if (!current || permitted.includes(current)) return permitted
  return [current, ...permitted]
}

/**
 * Whether a proposal that has to declare a verification method can be created yet.
 *
 * The vocabulary arrives asynchronously, and until it does there is no authoritative first method to start a
 * proposal on. Creating one anyway gave it `verificationMethod: ""` while the select — whose `value` matched
 * no option — displayed the browser's fallback first entry. The author saw a method the payload did not
 * carry, and the submission was refused for a blank field they had been shown a value for. Authoring waits
 * instead.
 */
export function canDeclareVerificationMethod(state: VerificationVocabularyState): boolean {
  return !state.loading && !state.error && !!state.vocabulary && state.vocabulary.methods.length > 0
}

/**
 * The method a new verification-bearing proposal starts on: the project's first configured value, and never
 * a guess. Blank only when {@link canDeclareVerificationMethod} is false, which is exactly when authoring is
 * blocked from creating one.
 */
export function firstPermittedMethod(state: VerificationVocabularyState): string {
  return canDeclareVerificationMethod(state) ? state.vocabulary!.methods[0] : ''
}

/**
 * What a change-kind transition should do about the verification method, or why it must not happen.
 *
 * Retirement is deliberately available while the vocabulary is still loading — a retirement declares no
 * method, so nothing about it needs the project's permitted set. That left a way round the gate: create a
 * retirement, then change its type to Modify, and the parents' handlers carried the blank method straight
 * into a verification-bearing proposal — exactly the state disabling the Add buttons exists to prevent.
 *
 * The rule lives here so both authoring surfaces answer it identically, and it is applied inside the
 * handlers rather than only on the control, because a disabled option is an affordance and not an
 * invariant.
 *
 * A card that already carries a value keeps it, whatever the vocabulary says. That value may be a
 * historical spelling the project no longer permits, and replacing it on a type change would be the silent
 * rewrite of controlled data this whole issue exists to stop; it stays visible and is refused at submission
 * until somebody corrects it deliberately.
 */
export type KindChangeDecision =
  | { allowed: true; verificationMethod: string }
  | { allowed: false; reason: string }

export function decideKindChange(
  state: VerificationVocabularyState,
  input: { level: string; toKind: string; currentMethod: string },
): KindChangeDecision {
  const { level, toKind, currentMethod } = input
  // An ICD has no verification artifact, and a retirement declares nothing: neither needs the vocabulary.
  if (level === 'Interface' || toKind === 'Retire') return { allowed: true, verificationMethod: currentMethod }
  if (currentMethod.trim()) return { allowed: true, verificationMethod: currentMethod }
  if (!canDeclareVerificationMethod(state)) return { allowed: false, reason: verificationBlockedReason(state) }
  return { allowed: true, verificationMethod: firstPermittedMethod(state) }
}

/** Why verification-bearing authoring is blocked, in the reader's terms; empty when it is not blocked. */
export function verificationBlockedReason(state: VerificationVocabularyState): string {
  if (state.loading || (!state.vocabulary && !state.error)) return "Loading this project's permitted verification methods…"
  if (state.error) return `${state.error} A requirement cannot declare a verification method until this loads, so authoring is paused.`
  if (state.vocabulary && state.vocabulary.methods.length === 0)
    return 'This project permits no verification methods. Configure them in Project Configuration before authoring a requirement.'
  return ''
}
