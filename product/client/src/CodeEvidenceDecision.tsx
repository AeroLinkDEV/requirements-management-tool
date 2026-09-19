import { PersonName } from './People'
import { stateLabel } from './presentation'

export type CodeEvidence = {
  state: string; countsAsImplementation: boolean; selectorVersion: number; evidenceSetId: string;
  disposition?: string; noCodeChangeRationale?: string; recordedBy?: string; recordedAt?: string;
  invalidationRationale?: string; sourceSnapshotId?: string; sourceSelectionEventId?: string;
  supersededLegacyRecordId?: string;
  contributions: { id: string; kind: string; repositoryPath: string; mergeRequestIid?: number;
    mergeRequestTitle?: string; mergeRequestUrl?: string; commitSha: string; path?: string;
    startLine?: number; endLine?: number; mergeResultSha?: string; mergeResultKind?: string;
    mergedAt?: string; providerObservedAt?: string }[];
}

export default function CodeEvidenceDecision({ evidence }: { evidence: CodeEvidence }) {
  return <div className="codeEvidenceDecision">
    {!evidence.countsAsImplementation && <p className="missingCallout">
      {evidence.state === 'SourceChanged' ? 'The build source changed. Confirm implementation evidence against the selected source again.'
        : evidence.state === 'Invalidated' ? 'This recorded decision is retained in history and no longer satisfies the build gate.'
          : 'This evidence identity cannot currently satisfy the build gate.'}
      {evidence.invalidationRationale && <span> {evidence.invalidationRationale}</span>}
    </p>}
    {evidence.disposition === 'NoCodeChangeRequired' && <p className="noCodeRationale">{evidence.noCodeChangeRationale}</p>}
    {evidence.contributions.map(item => <section className="mergeEvidence" key={item.id}>
      <div><small>{item.kind === 'MergeRequest' ? 'RECORDED MERGE REQUEST' : 'RECORDED FILE'}</small>
        {item.kind === 'MergeRequest' && item.mergeRequestUrl
          ? <a href={item.mergeRequestUrl} target="_blank" rel="noreferrer">!{item.mergeRequestIid} · {item.mergeRequestTitle || 'Merge request'} ↗</a>
          : <b>{item.path}</b>}
        <span>{item.repositoryPath}{item.startLine ? ` · Lines ${item.startLine}–${item.endLine}` : ''}</span>
      </div>
      <div><small>EXACT BUILD SOURCE</small><code>{item.commitSha}</code>
        {item.mergeResultSha && <><small>{stateLabel(item.mergeResultKind ?? 'Merge result')}</small><code>{item.mergeResultSha}</code></>}
        {item.providerObservedAt && <span>Observed {new Date(item.providerObservedAt).toLocaleString()}</span>}
      </div>
    </section>)}
    <footer>Recorded decision · {evidence.recordedBy && <PersonName userName={evidence.recordedBy} />}
      {evidence.recordedAt && <> · {new Date(evidence.recordedAt).toLocaleString()}</>}
    </footer>
  </div>
}
