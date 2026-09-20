import { useState } from 'react'
import ArtifactCodeRelationships from './ArtifactCodeRelationships'
import { codeQuery, useCodeRead, type CodePage } from './codeWorkspaceData'

type Props = { api: string; projectId: string; releaseId: string; reportId: string; snapshotId?: string }
export default function ProblemReportCodeRelationships(props: Props) {
  return <SnapshotRelationships key={`${props.projectId}/${props.releaseId}/${props.reportId}/${props.snapshotId}`} {...props} />
}
function SnapshotRelationships({ api, projectId, releaseId, reportId, snapshotId }: Props) {
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState('')
  const snapshots = useCodeRead<CodePage<{ exactIdentityId: string; display: string; eventType: string; occurredAt: string; available: boolean }>>(
    snapshotId ? undefined : `${api}/api/projects/${projectId}/code/targets?${codeQuery({ releaseId, reportId, targetKind: 'ProblemReportRevision', page, pageSize: 25 })}`)
  return <section>{!snapshotId && <>
    <p>Code relationships name an immutable PR snapshot. Choose the recorded event to inspect.</p>
    <label>Recorded Problem Report snapshot<select value={selected} onChange={event => setSelected(event.target.value)}>
      <option value="">Choose a snapshot</option>{snapshots.value?.items.map(item => <option key={item.exactIdentityId} value={item.exactIdentityId} disabled={!item.available}>
        {item.display} · {item.eventType} · {new Date(item.occurredAt).toLocaleString()}{!item.available ? ' · Unavailable' : ''}
      </option>)}</select></label>
    {snapshots.loading && <p>Loading snapshots…</p>}{snapshots.error && <p role="alert">{snapshots.error}</p>}
    <div className="codePagination"><button disabled={page <= 1} onClick={() => { setPage(value => value - 1); setSelected('') }}>Previous snapshots</button>
      <span>Page {page}</span><button disabled={!snapshots.value || page * 25 >= snapshots.value.total}
        onClick={() => { setPage(value => value + 1); setSelected('') }}>Next snapshots</button></div>
  </>}
    {(snapshotId || selected) && <ArtifactCodeRelationships {...{ api, projectId, releaseId }} targetKind="ProblemReportRevision"
      targetId={snapshotId || selected} readOnly={!!snapshotId} />}
  </section>
}
