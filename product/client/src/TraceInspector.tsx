import type { ReactNode } from 'react'
import ExactArtifactLink from './ExactArtifactLink'

/** Presentation only. Each caller supplies its own authoritative relationship projection. */
export function TraceInspector({ loading, error, unavailable, summary, digitalThreadHref, onOpenThread, children }: {
  loading?: boolean; error?: string; unavailable?: string
  summary?: { label: string; count: number }[]
  digitalThreadHref?: string; onOpenThread?: () => void; children: ReactNode
}) {
  return <div className="inspectorBody traceInspector">
    {loading ? <p className="inspectorNote" role="status">Loading trace…</p>
      : error ? <p className="inspectorNote warn" role="alert">{error}</p>
        : unavailable ? <p className="inspectorNote warn">{unavailable}</p>
          : <>
            {summary && <div className="traceSummary">{summary.map(item => <article key={item.label}>
              <b>{item.count}</b><span>{item.label}</span>
            </article>)}</div>}
            {digitalThreadHref ? <ExactArtifactLink className="openDigitalThread" href={digitalThreadHref}>Open complete Digital Thread →</ExactArtifactLink>
              : onOpenThread && <button className="openDigitalThread" onClick={onOpenThread}>Open complete Digital Thread →</button>}
            {children}
          </>}
  </div>
}

export function TraceGroup({ title, count, empty, children }: {
  title: string; count: number; empty: string; children: ReactNode
}) {
  return <section aria-label={title}><h3>{title} <small>({count})</small></h3>
    {count ? children : <div className="traceEmpty"><span>{empty}</span></div>}
  </section>
}

export function TraceRelation({ label, href, onOpen, linkTitle, title, detail, attention, className = '', children }: {
  label: string; href?: string; onOpen?: () => void; title?: string | null
  linkTitle?: string; detail?: string; attention?: boolean; className?: string; children?: ReactNode
}) {
  return <article className={`traceRelation${attention ? ' attention' : ''} ${className}`}>
    <div className="traceRelationTarget"><ExactArtifactLink className="linkedArtifactText" href={href} onOpen={onOpen} title={linkTitle}><b>{label}</b></ExactArtifactLink></div>
    {title && <p>{title}</p>}{detail && <small>{detail}</small>}{children}
  </article>
}
