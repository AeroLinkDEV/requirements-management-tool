import { formatEvidentiaryDateTime } from './presentation'
import {
  recordedCodeSourceHref,
  recordedCodeSourceTitle,
  recordedCodeTargetLabel,
  readRecordedCodeRelationship,
  type RecordedCodeRelationship,
} from './recordedCodeRelationship'
import './RecordedCodeReference.css'

function RecordedCodeReferenceUnavailable() {
  return (
    <article className="recordedCodeReference">
      <div className="recordedCodeReferenceHead">
        <b>Recorded Code reference unavailable</b>
        <span>Not accepted implementation evidence</span>
      </div>
      <span className="recordedCodeReferenceUnavailable" role="status">
        Its exact target or stored source snapshot cannot be safely interpreted.
      </span>
    </article>
  )
}

/** Renders an untrusted API collection without assuming it is an array or that its entries have IDs. */
export function RecordedCodeReferenceList({ references }: { references: unknown }) {
  if (references === undefined || references === null) return null
  if (!Array.isArray(references)) return <RecordedCodeReferenceUnavailable />

  return <>
    {references.map((reference: unknown, index: number) => (
      <RecordedCodeReferenceCard key={index} reference={reference} />
    ))}
  </>
}

export function RecordedCodeReferenceCard({ reference: rawReference }: { reference: unknown }) {
  const reference: RecordedCodeRelationship | undefined = readRecordedCodeRelationship(rawReference)
  if (!reference) return <RecordedCodeReferenceUnavailable />

  const href = recordedCodeSourceHref(reference)
  const title = recordedCodeSourceTitle(reference)
  return (
    <article className="recordedCodeReference">
      <div className="recordedCodeReferenceHead">
        <b>Reference recorded</b>
        <span>Not accepted implementation evidence</span>
      </div>
      <p className="recordedCodeReferenceTarget">{recordedCodeTargetLabel(reference)}</p>
      {title && <p className="recordedCodeReferenceTitle">{title}</p>}
      {href
        ? <a href={href} target="_blank" rel="noreferrer noopener">Open stored GitLab reference ↗</a>
        : <span className="recordedCodeReferenceUnavailable">No safe external source link is recorded.</span>}
      <small>
        {reference.meaning} · Build {reference.releaseVersion || 'version unavailable'} · Recorded by {reference.recordedBy} · {formatEvidentiaryDateTime(reference.recordedAt)}
      </small>
    </article>
  )
}
