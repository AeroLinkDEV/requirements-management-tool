import { createRoot } from 'react-dom/client'
import { RecordedCodeReferenceCard } from '../../src/RecordedCodeReference'
import '../../src/index.css'

const query = new URLSearchParams(location.search)
const scenario = query.get('case')
const surface = query.get('surface')
const exactFile = {
  id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', relationshipKind: 'File', version: 2, isActive: true,
  releaseId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', releaseVersion: '1.6', meaning: 'RelatedContext',
  recordedBy: 'reviewer', recordedAt: '2026-09-21T12:00:00Z', targetKind: 'ProblemReportRevision',
  targetIdentityId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', targetOwnerIdentityId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
  targetRevisionNumber: 1, targetStableIdentity: 'ProblemReportRevision:cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  targetDisplaySnapshot: 'PR-97001.01', instanceBaseUrl: 'https://gitlab.example', remoteProjectId: 42,
  repositoryPathSnapshot: 'aerolink/source', sourceSnapshotId: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',
  sourceSelectionEventId: null, commitSha: 'a'.repeat(40), path: 'src/route.c', startLine: 3, endLine: 12,
}
const reference = scenario === 'unknown-target'
  ? { ...exactFile, targetKind: 'CurrentRequirement' }
  : scenario === 'unknown-relationship'
    ? { ...exactFile, relationshipKind: 'FutureGitRecord' }
    : scenario === 'missing-source-snapshot'
      ? { ...exactFile, sourceSnapshotId: null }
      : exactFile

createRoot(document.getElementById('root')!).render(
  <main data-surface={surface ?? 'shared-card'}>
    <RecordedCodeReferenceCard reference={reference} />
  </main>,
)
