import { useCallback, useEffect, useState } from 'react'
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
  vocabulary: VerificationVocabulary | undefined
  loading: boolean
  error: string
  reload: () => Promise<void>
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
 */
export function useVerificationVocabulary(api: string, projectId: string | undefined): VerificationVocabularyState {
  const [vocabulary, setVocabulary] = useState<VerificationVocabulary>()
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  const reload = useCallback(async () => {
    if (!projectId) { setVocabulary(undefined); setError(''); return }
    setLoading(true)
    try {
      const read = await apiRequest<VerificationVocabulary>(`${api}/api/projects/${projectId}/verification-methods`)
      setVocabulary(read)
      setError('')
    } catch (failure) {
      setVocabulary(undefined)
      setError(operationError(failure, 'The permitted verification methods could not be loaded.'))
    } finally {
      setLoading(false)
    }
  }, [api, projectId])

  useEffect(() => { void reload() }, [reload])
  return { vocabulary, loading, error, reload }
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

/** Why verification-bearing authoring is blocked, in the reader's terms; empty when it is not blocked. */
export function verificationBlockedReason(state: VerificationVocabularyState): string {
  if (state.loading || (!state.vocabulary && !state.error)) return "Loading this project's permitted verification methods…"
  if (state.error) return `${state.error} A requirement cannot declare a verification method until this loads, so authoring is paused.`
  if (state.vocabulary && state.vocabulary.methods.length === 0)
    return 'This project permits no verification methods. Configure them in Project Configuration before authoring a requirement.'
  return ''
}
