export type RecordedCodeRelationship = {
  id: string
  relationshipKind: 'MergeRequest' | 'File'
  version: number
  isActive: boolean
  releaseId: string
  releaseVersion: string
  meaning: 'Implements' | 'Addresses' | 'RelatedContext'
  recordedBy: string
  recordedAt: string
  reAddedBy?: string | null
  reAddedAt?: string | null
  targetKind: 'RequirementRevision' | 'ChangeRequestRevision' | 'ProblemReportRevision' | 'RequirementProposal'
  targetIdentityId: string
  targetOwnerIdentityId?: string | null
  targetRevisionNumber?: number | null
  targetStableIdentity: string
  targetDisplaySnapshot: string
  instanceBaseUrl: string
  remoteProjectId: number
  repositoryPathSnapshot: string
  sourceSnapshotId?: string | null
  sourceSelectionEventId?: string | null
  mergeRequestIid?: number | null
  mergeRequestId?: number | null
  mergeRequestUrlSnapshot?: string | null
  mergeRequestTitleSnapshot?: string | null
  commitSha?: string | null
  path?: string | null
  startLine?: number | null
  endLine?: number | null
  fileMergeRequestIid?: number | null
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)

const requiredText = (value: unknown): value is string => typeof value === 'string' && value.length > 0
const requiredGuid = (value: unknown): value is string =>
  typeof value === 'string'
  && value !== '00000000-0000-0000-0000-000000000000'
  && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
const nonNegativeInteger = (value: unknown): value is number => Number.isInteger(value) && (value as number) >= 0
const optionalText = (value: unknown): value is string | null | undefined =>
  value === null || value === undefined || typeof value === 'string'
const optionalGuid = (value: unknown): value is string | null | undefined =>
  value === null || value === undefined || requiredGuid(value)
const optionalNumber = (value: unknown): value is number | null | undefined =>
  value === null || value === undefined || (typeof value === 'number' && Number.isFinite(value))

/** Validates the stored Code-reference DTO without deriving a target or filling missing source snapshots. */
export const readRecordedCodeRelationship = (raw: unknown): RecordedCodeRelationship | undefined => {
  if (!isRecord(raw)) return undefined
  const targetKinds = ['RequirementRevision', 'ChangeRequestRevision', 'ProblemReportRevision', 'RequirementProposal']
  if (!requiredGuid(raw.id)
    || !['MergeRequest', 'File'].includes(String(raw.relationshipKind))
    || !Number.isInteger(raw.version) || (raw.version as number) < 1
    || typeof raw.isActive !== 'boolean'
    || !requiredGuid(raw.releaseId) || !requiredText(raw.releaseVersion)
    || !['Implements', 'Addresses', 'RelatedContext'].includes(String(raw.meaning))
    || !requiredText(raw.recordedBy) || !requiredText(raw.recordedAt)
    || !optionalText(raw.reAddedBy) || !optionalText(raw.reAddedAt)
    || !targetKinds.includes(String(raw.targetKind))
    || !requiredGuid(raw.targetIdentityId)
    || !optionalText(raw.targetOwnerIdentityId) || !optionalNumber(raw.targetRevisionNumber)
    || !requiredText(raw.targetStableIdentity) || !requiredText(raw.targetDisplaySnapshot)
    || !requiredText(raw.instanceBaseUrl)
    || typeof raw.remoteProjectId !== 'number' || !Number.isSafeInteger(raw.remoteProjectId) || raw.remoteProjectId <= 0
    || !requiredText(raw.repositoryPathSnapshot)
    || !optionalGuid(raw.sourceSnapshotId) || !optionalGuid(raw.sourceSelectionEventId)
    || !optionalNumber(raw.mergeRequestIid) || !optionalNumber(raw.mergeRequestId)
    || !optionalText(raw.mergeRequestUrlSnapshot) || !optionalText(raw.mergeRequestTitleSnapshot)
    || !optionalText(raw.commitSha) || !optionalText(raw.path)
    || !optionalNumber(raw.startLine) || !optionalNumber(raw.endLine)
    || !optionalNumber(raw.fileMergeRequestIid)) return undefined

  if (raw.targetStableIdentity !== `${raw.targetKind}:${raw.targetIdentityId}`) return undefined
  if (raw.targetKind === 'RequirementRevision'
    && (!requiredGuid(raw.targetOwnerIdentityId) || !nonNegativeInteger(raw.targetRevisionNumber))) return undefined
  if (raw.targetKind === 'ChangeRequestRevision'
    && (raw.targetOwnerIdentityId != null || !nonNegativeInteger(raw.targetRevisionNumber))) return undefined
  if (raw.targetKind === 'ProblemReportRevision'
    && (!requiredGuid(raw.targetOwnerIdentityId) || !nonNegativeInteger(raw.targetRevisionNumber))) return undefined
  if (raw.targetKind === 'RequirementProposal'
    && (!requiredGuid(raw.targetOwnerIdentityId) || raw.targetRevisionNumber != null)) return undefined
  if (raw.relationshipKind === 'File'
    && (!requiredGuid(raw.sourceSnapshotId)
      || !requiredText(raw.commitSha) || !/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/i.test(raw.commitSha)
      || !requiredText(raw.path) || !safePathSegments(raw.repositoryPathSnapshot) || !safePathSegments(raw.path)
      || !isHttpsUrl(raw.instanceBaseUrl)
      || !validLineRange(raw.startLine, raw.endLine))) return undefined

  return raw as unknown as RecordedCodeRelationship
}

const targetKindLabel: Record<RecordedCodeRelationship['targetKind'], string> = {
  RequirementRevision: 'exact requirement revision',
  ChangeRequestRevision: 'exact change request revision',
  ProblemReportRevision: 'exact Problem Report snapshot',
  RequirementProposal: 'exact requirement proposal',
}

const isHttpsUrl = (value: string | null | undefined): value is string => {
  if (!value) return false
  try {
    const url = new URL(value)
    const pathSegments = url.pathname.split('/').filter(Boolean)
    return url.protocol === 'https:' && !url.username && !url.password
      && !url.search && !url.hash && !url.pathname.includes('\\')
      && pathSegments.every(segment => segment !== '.' && segment !== '..')
  } catch {
    return false
  }
}

const safePathSegments = (value: string | null | undefined): string[] | undefined => {
  if (!value || value.includes('\\')) return undefined
  const segments = value.split('/')
  if (segments.some(segment => !segment || segment === '.' || segment === '..')) return undefined
  return segments
}

const validLineRange = (start: unknown, end: unknown): boolean => {
  if (start == null && end == null) return true
  return Number.isInteger(start) && Number.isInteger(end)
    && (start as number) > 0 && (end as number) >= (start as number)
}

/** Build an external link only from the immutable GitLab URL/commit/path snapshots on the relationship. */
export const recordedCodeSourceHref = (rawReference: unknown): string | undefined => {
  const exact = readRecordedCodeRelationship(rawReference)
  if (!exact) return undefined
  const reference = exact
  if (reference.relationshipKind === 'MergeRequest')
    return isHttpsUrl(reference.mergeRequestUrlSnapshot) ? reference.mergeRequestUrlSnapshot : undefined

  if (!isHttpsUrl(reference.instanceBaseUrl) || !reference.commitSha) return undefined
  const repository = safePathSegments(reference.repositoryPathSnapshot)
  const path = safePathSegments(reference.path)
  if (!repository || !path) return undefined
  const base = reference.instanceBaseUrl.replace(/\/+$/, '')
  const lines = reference.startLine == null ? '' : `#L${reference.startLine}${reference.endLine === reference.startLine ? '' : `-L${reference.endLine}`}`
  return `${base}/${repository.map(encodeURIComponent).join('/')}/-/blob/${encodeURIComponent(reference.commitSha)}/${path.map(encodeURIComponent).join('/')}${lines}`
}

export const recordedCodeTargetLabel = (reference: RecordedCodeRelationship): string => {
  const revision = reference.targetRevisionNumber == null ? '' : ` · revision ${reference.targetRevisionNumber}`
  return `${targetKindLabel[reference.targetKind]}: ${reference.targetDisplaySnapshot}${revision}`
}

export const recordedCodeSourceLabel = (reference: RecordedCodeRelationship): string =>
  reference.relationshipKind === 'MergeRequest'
    ? `GitLab merge request${reference.mergeRequestIid == null ? '' : ` !${reference.mergeRequestIid}`}`
    : `${reference.path ?? 'Recorded file'}${reference.startLine == null ? '' : ` · line ${reference.startLine}${reference.endLine == null || reference.endLine === reference.startLine ? '' : `–${reference.endLine}`}`}`

export const recordedCodeSourceTitle = (reference: RecordedCodeRelationship): string | null =>
  reference.relationshipKind === 'MergeRequest' ? reference.mergeRequestTitleSnapshot ?? null : reference.path ?? null
