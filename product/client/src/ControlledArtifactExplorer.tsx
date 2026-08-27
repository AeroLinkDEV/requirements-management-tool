import type { ReactNode } from 'react'

/**
 * The canonical controlled-artifact explorer frame.
 *
 * Requirements and test procedures are different records, but reading either record is the same page:
 * heading and controlled outputs, one command bar, a document rail, a paged result set, and the same
 * persistent inspector.  Keeping that structure here is what prevents the Verification page from becoming
 * a separately styled approximation of the Requirements page again.
 */
export function ControlledArtifactExplorerHeader({
  back,
  eyebrow,
  title,
}: {
  back?: { label: string; onClick: () => void }
  eyebrow: string
  title: string
}) {
  return <header className="reqHeader">
    <div>
      {back && <button className="back" type="button" onClick={back.onClick}>← {back.label}</button>}
      <p className="eyebrow">{eyebrow}</p>
      <h1>{title}</h1>
    </div>
  </header>
}

/** The exact three-column frame used by both controlled artifact explorers. */
export function ControlledArtifactExplorerLayout({
  inspecting,
  resizableKey,
  children,
}: {
  inspecting: boolean
  resizableKey: string
  children: ReactNode
}) {
  return <div
    className={inspecting ? 'reqLayout inspecting' : 'reqLayout'}
    data-resizable-layout="horizontal"
    data-resizable-key={resizableKey}
  >
    {children}
  </div>
}

/**
 * The inspector chrome is deliberately shared. Only the body of each tab is artifact-specific.
 */
export function ControlledArtifactInspector({
  artifactType,
  displayNumber,
  subtitle = 'Controlled current revision',
  closeLabel,
  onClose,
  tabs,
  activeTab,
  onTab,
  children,
}: {
  artifactType: string
  displayNumber: string
  subtitle?: string
  closeLabel: string
  onClose: () => void
  tabs: { id: string; label: ReactNode }[]
  activeTab: string
  onTab: (id: string) => void
  children: ReactNode
}) {
  return <aside className="requirementInspector" aria-label={`${displayNumber} detail`}>
    <div className="inspectorTop">
      <div>
        <span>{artifactType}</span>
        <h2>{displayNumber}</h2>
        <p>{subtitle}</p>
      </div>
      <button type="button" className="inspectorClose" aria-label={closeLabel} onClick={onClose}>×</button>
    </div>
    <div className="inspectorTabs" role="tablist" aria-label={`${displayNumber} detail sections`}>
      {tabs.map(tab => <button
        type="button"
        key={tab.id}
        role="tab"
        aria-selected={activeTab === tab.id}
        tabIndex={activeTab === tab.id ? 0 : -1}
        className={activeTab === tab.id ? 'active' : ''}
        onClick={() => onTab(tab.id)}
      >{tab.label}</button>)}
    </div>
    {children}
  </aside>
}

export function ControlledArtifactInspectorEmpty({
  title,
  description,
}: {
  title: string
  description: string
}) {
  return <aside className="requirementInspector" aria-label={`${title} detail`}>
    <div className="procedureInspectorEmpty">
      <span aria-hidden="true">≡</span>
      <b>Select a {title.toLowerCase()}</b>
      <p>{description}</p>
    </div>
  </aside>
}
