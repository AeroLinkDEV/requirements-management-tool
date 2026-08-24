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
