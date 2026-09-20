import { useEffect, useState } from 'react'

export type MetadataObservation<T> = { configurationVersion: number; checkedAt: string;
  observation: { succeeded: boolean; detail: string; completeness: string; nextPage?: string; value?: T } }
export type MergeRequest = { iid: number; title: string; state: string; draft: boolean; webUrl: string;
  sourceBranch?: string; targetBranch?: string; mergeCommitSha?: string; squashMergeCommitSha?: string;
  mergedAt?: string; approvals?: { known: boolean; detail: string; approvedBy: { id: number; username: string; name: string }[] } }
export type CodeRelationship = { id: string; relationshipKind: string; version: number; isActive: boolean;
  targetKind: string; targetIdentityId: string; targetDisplaySnapshot: string; meaning: string;
  sourceSnapshotId?: string; commitSha?: string; path?: string; startLine?: number; endLine?: number;
  mergeRequestIid?: number; recordedBy: string; recordedAt: string; withdrawalRationale?: string;
  mergeRequestUrlSnapshot?: string; mergeRequestTitleSnapshot?: string;
  capabilities: { canWithdraw: boolean; canReAdd: boolean } }
export type CodePage<T> = { page: number; pageSize: number; total: number; items: T[]; metadataCheckedAt?: string; metadataReused?: boolean }
export type RegisteredMergeRequest = { instanceBaseUrl: string; remoteProjectId: number; mergeRequestIid: number;
  relationshipCount: number; metadataKnown: boolean; metadata?: MergeRequest }
export type InspectedMergeRequest = { metadataKnown: boolean; metadata?: MergeRequest; metadataCheckedAt?: string;
  mergeRequests: CodeRelationship[]; files: CodeRelationship[] }
export type TreeEntry = { path: string; name: string; kind: string; mode?: string }
export type TreePage = { commitSha: string; entries: TreeEntry[]; nextCursor?: string }

/** Query identity is checked during render too, before an effect can clear a previous page's payload. */
export function useCodeRead<T>(url: string | undefined, refresh = 0) {
  const [result, setResult] = useState<{ url: string; refresh: number; value?: T; error?: string }>()
  useEffect(() => {
    if (!url) return
    const controller = new AbortController()
    void (async () => {
      try {
        const response = await fetch(url, { signal: controller.signal })
        const value = await response.json()
        if (!response.ok) throw new Error(value.error ?? 'This information is unavailable. Refresh to retry.')
        if (!controller.signal.aborted) setResult({ url, refresh, value })
      } catch (failure) {
        if (!controller.signal.aborted) setResult({ url, refresh, error: failure instanceof Error ? failure.message : 'Information unavailable.' })
      }
    })()
    return () => controller.abort()
  }, [url, refresh])
  const current = result?.url === url && result?.refresh === refresh ? result : undefined
  return { value: current?.value, error: current?.error, loading: !!url && !current }
}

export const mergeRequestState = (item?: MergeRequest) => !item ? 'Unknown' : item.draft ? `Draft · ${item.state}` : item.state
export const codeQuery = (values: Record<string, string | number | undefined>) => new URLSearchParams(
  Object.entries(values).filter(([, value]) => value !== undefined).map(([key, value]) => [key, String(value)]),
).toString()
