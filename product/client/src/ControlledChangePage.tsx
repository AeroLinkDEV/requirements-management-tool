import type { FormEventHandler, ReactNode } from 'react'

/** Canonical page header for requirement and verification change requests. */
export function ControlledChangePage({
  backLabel,
  onBack,
  eyebrow,
  title,
  description,
  allocation,
  state,
  stateCode,
  version,
  docxHref,
  pdfHref,
  error,
  saved,
  children,
}: {
  backLabel: string
  onBack: () => void
  eyebrow: string
  title: string
  description: string
  allocation: string
  state: string
  stateCode?: string
  version: number
  docxHref: string
  pdfHref: string
  error?: string
  saved?: string
  children: ReactNode
}) {
  return <main className="scrPage">
    <header className="scrHeader">
      <div>
        <button className="back" type="button" onClick={onBack}>← {backLabel}</button>
        <p className="eyebrow">{eyebrow}</p>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      <div className="headerState">
        <span className={`stateBadge ${(stateCode ?? state).toLowerCase()}`} data-state={stateCode ?? state}>{allocation} · {state}</span>
        <small>Record version {version}</small>
        <div className="scrPublicationTools">
          <span>Professional controlled publication</span>
          <a href={docxHref}>Download DOCX</a>
          <a href={pdfHref}>Download PDF</a>
        </div>
      </div>
    </header>
    {error && <div className="workspaceError" role="alert">{error}</div>}
    {saved && <div className="workspaceSaved" role="status">✓ {saved}</div>}
    {children}
  </main>
}

/** The canonical main-column/right-rail arrangement. */
export function ControlledChangeReadLayout({ children }: { children: ReactNode }) {
  return <div className="workspaceGrid">{children}</div>
}

/** The exact Problem / Analysis / Solution card used on both record types. */
export function ControlledChangeCaseCard({
  actions,
  note,
  fields,
}: {
  actions?: ReactNode
  note?: ReactNode
  fields: { key: 'P' | 'A' | 'S'; label: string; value: ReactNode }[]
}) {
  return <section className="workspaceCard">
    <div className="workspaceTitle">
      <div><h2>Change case</h2><p>Problem, analysis, and proposed solution</p></div>
      {actions}
    </div>
    {note}
    <div className="pasView">
      {fields.map(field => <article key={field.key}>
        <span>{field.key}</span>
        <div><b>{field.label}</b>{field.value}</div>
      </article>)}
    </div>
  </section>
}

export function ControlledStatusCard({
  displayNumber,
  fields,
  children,
}: {
  displayNumber: string
  fields: { label: string; value: ReactNode; data?: { name: string; value: string } }[]
  children?: ReactNode
}) {
  return <section className="workspaceCard controlStatusCard">
    <div className="workspaceTitle"><div><h2>Control status</h2><p>{displayNumber}</p></div></div>
    <dl>
      {fields.map(field => <div key={field.label}>
        <dt>{field.label}</dt>
        <dd {...(field.data ? { [`data-${field.data.name}`]: field.data.value } : {})}>{field.value}</dd>
      </div>)}
    </dl>
    {children}
  </section>
}

export type AuthoringStage = { href: string; label: string; status: string; complete: boolean; active?: boolean }

/** The same two-stage progress rail for requirement changes and procedure changes. */
export function ControlledChangeAuthoringStages({ stages }: { stages: AuthoringStage[] }) {
  return <nav className="workspaceStages" aria-label="Checked-out authoring progress">
    {stages.map((stage, index) => <a
      key={stage.href}
      href={stage.href}
      className={stage.complete ? 'complete' : stage.active ? 'active' : ''}
    >
      <span>{index + 1}</span>
      <div><b>{stage.label}</b><small>{stage.status}</small></div>
    </a>)}
  </nav>
}

/** The canonical sticky checkout bar. */
export function ControlledChangeAuthoringActions({
  summary,
  detail,
  busy,
  saving = false,
  canSave,
  canCheckIn,
  checkInBlockedReason,
  onDiscard,
  onSave,
  checkInLabel = 'Save & check in',
}: {
  summary: string
  detail: string
  busy: boolean
  saving?: boolean
  canSave: boolean
  canCheckIn: boolean
  /**
   * Why check-in is unavailable, in words, whenever it is. A greyed control that says nothing leaves the
   * reader to guess between "the page is busy", "I lack authority" and "something I typed is wrong" — and
   * they are three different actions. Required in practice rather than by the type, because the only correct
   * value when `canCheckIn` is false is a sentence.
   */
  checkInBlockedReason?: string
  onDiscard: () => void
  onSave: () => void
  checkInLabel?: string
}) {
  return <div className="workspaceActions stickyWorkspaceActions">
    <div><b>{summary}</b><span>{detail}</span></div>
    {!canCheckIn && checkInBlockedReason && !busy && !saving
      && <p className="checkInBlockedReason" role="status">{checkInBlockedReason}</p>}
    <button type="button" className="outline" onClick={onDiscard} disabled={busy}>Discard checkout</button>
    <button type="button" className="outline" onClick={onSave} disabled={busy || saving || !canSave}>Save</button>
    <button type="submit" disabled={busy || saving || !canCheckIn}>{busy ? 'Checking in…' : checkInLabel}</button>
  </div>
}

/** A form wrapper whose markup is shared even though its stage bodies are artifact-specific. */
export function ControlledChangeAuthoringForm({
  stages,
  onSubmit,
  children,
  actions,
}: {
  stages: AuthoringStage[]
  onSubmit: FormEventHandler<HTMLFormElement>
  children: ReactNode
  actions: ReactNode
}) {
  return <form onSubmit={onSubmit} className="workspaceStack">
    <ControlledChangeAuthoringStages stages={stages} />
    {children}
    {actions}
  </form>
}
