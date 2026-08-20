import { useCallback, useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import type { AuthUser } from './IdentityCenter'
import PortalHeader from './PortalHeader'
import { apiRequest, operationError } from './apiClient'
import './ApprovalConfigurationCenter.css'

/**
 * What each artifact requires before it can be released, resolved to the people who could actually sign it.
 *
 * A review procedure names authorities rather than people so it survives somebody changing jobs. The cost is
 * that it can quietly require a position nobody holds, and nothing says so until an author submits and the
 * review stops at a stage with no one to sign it. Reading the procedure and the roster together is the only
 * way to answer that in advance, and neither page can do it alone.
 *
 * This page edits the same versioned ReviewWorkflow aggregate used by review submission. A revision retires
 * the prior active policy for future Draft submissions while preserving the version an in-flight review began under.
 */

type Resolved = {
  role: string
  singular: boolean
  holders: string[]
  backups: string[]
  delegates?: string[]
  blocking: boolean
}
type Stage = { position: number; name: string; kind: 'Review' | 'Approval'; requiredRole?: string; required: Resolved }
type Artifact = {
  subject: string
  configured: boolean
  name: string | null
  version: number | null
  mode: string | null
  stages: Stage[] | null
  blockingStages: number
}
type Configuration = { projectId: string; canManage: boolean; artifacts: Artifact[] }

type EditableStage = { name: string; kind: 'Review' | 'Approval'; requiredRole: string }

const subjectLabels: Record<string, string> = {
  System: 'System Change Request',
  Software: 'Software Change Request',
  SystemTest: 'System Test Change Request',
  HighLevelSoftwareTest: 'HLR Test Change Request',
  LowLevelSoftwareTest: 'LLR Test Change Request',
}

const roleLabels: Record<string, string> = {
  ProjectEngineer: 'Project Engineer',
  ProgramManager: 'Program Manager',
  EngineeringManager: 'Engineering Manager',
  ConfigurationManager: 'Configuration Manager',
  ProjectEngineeringLead: 'Project Engineering Lead',
  SystemEngineeringLead: 'System Engineering Lead',
  SoftwareEngineeringLead: 'Software Engineering Lead',
  SystemTestLead: 'System Test Lead',
  SoftwareTestLead: 'Software Test Lead',
  SystemEngineer: 'System Engineer',
  SoftwareEngineer: 'Software Engineer',
  SystemTestEngineer: 'System Test Engineer',
  SoftwareTestEngineer: 'Software Test Engineer',
  SoftwareQualityAnalyst: 'Software Quality Assurance',
  Airworthiness: 'Airworthiness',
  Engineer: 'Engineer',
  Reviewer: 'Reviewer',
  Approver: 'Approver',
  TestEngineer: 'Test Engineer',
  TestLead: 'Test Lead',
  Administrator: 'Administrator',
}
const label = (role: string) => roleLabels[role] ?? role

/** What the right-hand column says about a stage: a name, a count, or nobody. */
function whoCanSign(required: Resolved) {
  if (required.blocking) return 'Nobody holds this'
  const parts: string[] = []
  if (required.singular && required.holders.length === 1) parts.push(required.holders[0])
  else if (required.holders.length) parts.push(`${required.holders.length} eligible`)
  if (required.backups.length) parts.push(`backup ${required.backups.join(', ')}`)
  if (required.delegates?.length) parts.push(`delegate ${required.delegates.join(', ')}`)
  return parts.length ? parts.join(' · ') : 'No eligible signer listed'
}

// Keep the editor's role choices in lockstep with the display vocabulary. Every current ProgramRole is listed
// once in roleLabels, so adding a role to this page cannot silently make it display-only.
const configurableRoles = Object.keys(roleLabels)

function ConfigurationEditor({
  stages,
  setStages,
  dirty,
  saving,
  onSave,
  onCancel,
}: {
  stages: EditableStage[]
  setStages: Dispatch<SetStateAction<EditableStage[]>>
  dirty: boolean
  saving: boolean
  onSave: () => void
  onCancel: () => void
}) {
  return <div className="configurationEditor">
    <p className="configurationEditorIntro">
      Each row is one required minimum sign-off. Add rows to increase the minimum; authors may add extra eligible
      Program participants when submitting a Draft. This policy applies to new submissions and Drafts when sent,
      while an InReview or Approved record stays frozen on the version it started under.
    </p>
    <ol className="configurationRows">
      {stages.map((stage, index) => <li key={index} className="configurationRow">
        <span className="configurationRowNumber">{index + 1}</span>
        <label>Stage name
          <input value={stage.name} onChange={event => setStages(items => items.map((item, i) => i === index ? { ...item, name: event.target.value } : item))} required />
        </label>
        <label>Signature
          <select value={stage.kind} onChange={event => setStages(items => items.map((item, i) => i === index ? { ...item, kind: event.target.value as EditableStage['kind'] } : item))}>
            <option value="Review">Review</option>
            <option value="Approval">Approval</option>
          </select>
        </label>
        <label>Required Program role
          <select value={stage.requiredRole} onChange={event => setStages(items => items.map((item, i) => i === index ? { ...item, requiredRole: event.target.value } : item))}>
            {configurableRoles.map(role => <option value={role} key={role}>{label(role)}</option>)}
          </select>
        </label>
        <button type="button" className="configurationRemove" disabled={stages.length === 1 || saving} onClick={() => setStages(items => items.filter((_, i) => i !== index))}>
          Remove
        </button>
      </li>)}
    </ol>
    <div className="configurationEditorActions">
      <button type="button" onClick={() => setStages(items => [...items, { name: '', kind: 'Review', requiredRole: 'Reviewer' }])} disabled={saving}>+ Add required sign-off</button>
      <span>{stages.length} minimum sign-off{stages.length === 1 ? '' : 's'} · {dirty ? 'Unsaved changes' : 'No changes'}</span>
      <div>
        <button type="button" onClick={onCancel} disabled={saving}>Cancel</button>
        <button type="button" className="primaryConfigAction" onClick={onSave} disabled={saving || !dirty || stages.some(stage => !stage.name.trim())}>{saving ? 'Saving…' : 'Save and activate'}</button>
      </div>
    </div>
  </div>
}

export default function ApprovalConfigurationCenter({
  user,
  api,
  projectId,
  projectName,
  onBackToBuilds,
  onSignOut,
}: {
  user: AuthUser
  api: string
  projectId: string
  projectName: string
  onBackToBuilds: () => void
  onSignOut: () => void
}) {
  const [data, setData] = useState<Configuration | null>(null)
  const [error, setError] = useState('')
  const [selected, setSelected] = useState<string>('System')
  const [editing, setEditing] = useState(false)
  const [draftStages, setDraftStages] = useState<EditableStage[]>([])
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState('')
  const [saveSuccess, setSaveSuccess] = useState('')

  const load = useCallback(async () => {
    try {
      setError('')
      setData(await apiRequest<Configuration>(`${api}/api/projects/${projectId}/approval-configuration`))
    } catch (failure) {
      setError(operationError(failure, 'The approval configuration could not be loaded.'))
    }
  }, [api, projectId])

  useEffect(() => { void load() }, [load])

  const artifact = useMemo(
    () => data?.artifacts.find(item => item.subject === selected) ?? null,
    [data, selected],
  )
  useEffect(() => {
    if (!artifact || editing) return
    setDraftStages((artifact.stages ?? []).map(stage => ({
      name: stage.name,
      kind: stage.kind,
      requiredRole: stage.requiredRole ?? stage.required.role,
    })))
  }, [artifact, editing])
  const blockedTotal = useMemo(
    () => (data?.artifacts ?? []).reduce((total, item) => total + item.blockingStages, 0),
    [data],
  )
  const savedStages = useMemo(() => (artifact?.stages ?? []).map(stage => ({
    name: stage.name,
    kind: stage.kind,
    requiredRole: stage.requiredRole ?? stage.required.role,
  })), [artifact])
  const dirty = editing && JSON.stringify(draftStages) !== JSON.stringify(savedStages)

  const beginEdit = () => {
    if (!artifact) return
    setSaveError('')
    setSaveSuccess('')
    setDraftStages((artifact.stages ?? [{ position: 0, name: 'Required review', kind: 'Review', requiredRole: 'Reviewer', required: { role: 'Reviewer', singular: false, holders: [], backups: [], blocking: false } }]).map(stage => ({
      name: stage.name,
      kind: stage.kind,
      requiredRole: stage.requiredRole ?? stage.required.role,
    })))
    setEditing(true)
  }

  const save = async () => {
    if (!artifact || draftStages.length === 0) return
    setSaving(true)
    setSaveError('')
    setSaveSuccess('')
    try {
      await apiRequest(`${api}/api/projects/${projectId}/approval-configuration/${artifact.subject}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ stages: draftStages }),
      })
      setEditing(false)
      setSaveSuccess(`${subjectLabels[artifact.subject] ?? artifact.subject} configuration saved. New submissions will use the next active policy version.`)
      await load()
    } catch (failure) {
      setSaveError(operationError(failure, 'The approval configuration could not be saved.'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="approvalConfigPage">
      <PortalHeader user={user} onSignOut={onSignOut}/>
      <main className="approvalConfigMain">
        <nav className="approvalConfigBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onBackToBuilds}>Software Builds</button>
          <span aria-hidden="true">/</span>
          <strong>Approval configuration</strong>
        </nav>

        <header className="approvalConfigHeading">
          <div>
            <h1>Approval configuration</h1>
            <p>
              What each artifact in {projectName} requires before it can be released, and who on the project
              could sign it today. Save a versioned minimum sign-off policy for each artifact type; it applies to
              new submissions and Drafts when sent, while reviews already running keep the version they started under.
            </p>
          </div>
          <div className="approvalConfigActions">
            <button type="button" onClick={onBackToBuilds}><span aria-hidden="true">←</span> Software Builds</button>
          </div>
        </header>

        {error && <p className="approvalConfigError" role="alert">{error}</p>}
        {saveError && <p className="approvalConfigError" role="alert">{saveError}</p>}
        {saveSuccess && <p className="approvalConfigSuccess" role="status">{saveSuccess}</p>}

        {!data ? (
          <p className="approvalConfigLoading">Loading the project's approval configuration…</p>
        ) : (
          <>
            {blockedTotal > 0 && (
              <p className="approvalConfigAlarm" role="status">
                <strong>{blockedTotal} stage{blockedTotal === 1 ? '' : 's'} cannot be signed.</strong> A procedure
                requires a position nobody on this project holds, and work submitted under it will stop there.
              </p>
            )}

            <div className="approvalConfigLayout">
              <nav className="artifactList" aria-label="Artifact types">
                <h2>Artifact type</h2>
                {editing && <p className="artifactNavigationNotice" role="status">Finish or cancel this artifact's edits before selecting another artifact type.</p>}
                <ul>
                  {data.artifacts.map(item => (
                    <li key={item.subject}>
                      <button
                        type="button"
                        className={item.subject === selected ? 'selected' : undefined}
                        aria-current={item.subject === selected ? 'true' : undefined}
                        data-artifact={item.subject}
                        disabled={editing || saving}
                        aria-disabled={editing || saving ? 'true' : undefined}
                        title={editing || saving ? 'Finish or cancel the current configuration first' : undefined}
                        onClick={() => { if (!editing && !saving) setSelected(item.subject) }}
                      >
                        <span className="artifactName">{subjectLabels[item.subject] ?? item.subject}</span>
                        <span className="artifactMeta">
                          {item.configured
                            ? <>v{item.version} · {item.blockingStages > 0
                                ? <span className="pill blocked">Cannot complete</span>
                                : <span className="pill active">Active</span>}</>
                            : <span className="pill muted">Not configured</span>}
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
                <p className="artifactListNote">
                  Controlled documents are absent on purpose: their reviewers are chosen per document by the
                  author when a draft is ready, not fixed for the project.
                </p>
              </nav>

              <section className="procedurePanel" aria-live="polite">
                {!artifact ? null : !artifact.configured ? (
                  <div className="procedureEmpty">
                    <h2>{subjectLabels[artifact.subject] ?? artifact.subject}</h2>
                    {!editing && <p>
                      No procedure is recorded, so an author selects their own reviewers at submission and
                      nothing checks the result against a written rule. This is not a blocked state — a rule
                      nobody has written down yet does not stop work.
                    </p>}
                    {data.canManage && !editing && <button type="button" className="primaryConfigAction" onClick={beginEdit}>Configure this artifact</button>}
                    {data.canManage && editing && <ConfigurationEditor
                      stages={draftStages}
                      setStages={setDraftStages}
                      dirty={dirty}
                      saving={saving}
                      onSave={() => void save()}
                      onCancel={() => setEditing(false)}
                    />}
                  </div>
                ) : (
                  <>
                    <header className="procedureHeader">
                      <div>
                        <h2>{subjectLabels[artifact.subject] ?? artifact.subject}</h2>
                        <p>{artifact.name} · version {artifact.version} · {artifact.mode?.toLowerCase()}</p>
                      </div>
                      <div className="procedureHeaderActions">
                        {artifact.blockingStages > 0
                          ? <span className="pill blocked large">Cannot complete</span>
                          : <span className="pill active large">Can complete</span>}
                        {data.canManage && !editing && <button type="button" className="primaryConfigAction" onClick={beginEdit}>Edit configuration</button>}
                      </div>
                    </header>

                    {!editing ? <table className="stageTable">
                      <thead>
                        <tr>
                          <th scope="col" className="stageNumberCell">#</th>
                          <th scope="col">Stage</th>
                          <th scope="col">Signature</th>
                          <th scope="col">Required position</th>
                          <th scope="col">Who can sign today</th>
                        </tr>
                      </thead>
                      <tbody>
                        {(artifact.stages ?? []).map(stage => (
                          <tr key={stage.position} className={stage.required.blocking ? 'blockingStage' : undefined} data-stage={stage.position}>
                            <td className="stageNumberCell">{stage.position + 1}</td>
                            <td className="stageNameCell">{stage.name}</td>
                            <td>
                              <span className={`pill ${stage.kind === 'Approval' ? 'approval' : 'review'}`}>
                                {stage.kind === 'Approval' ? 'Approval' : 'Review'}
                              </span>
                            </td>
                            <td>{label(stage.required.role)}</td>
                            <td className={stage.required.blocking ? 'nobody' : undefined}>{whoCanSign(stage.required)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table> : <ConfigurationEditor
                      stages={draftStages}
                      setStages={setDraftStages}
                      dirty={dirty}
                      saving={saving}
                      onSave={() => void save()}
                      onCancel={() => setEditing(false)}
                    />}

                    {!editing && <p className="stageKindNote">
                      A <strong>review</strong> examines the content. An <strong>approval</strong> acknowledges
                      the artifact is done and releasing. Stages recorded before the distinction existed read as
                      reviews, which is what they were. The configured rows are the minimum; authors may add
                      additional eligible Program participants, and those extra signers remain part of the frozen cycle.
                    </p>}

                    {artifact.blockingStages > 0 && (
                      <div className="blockingNotice">
                        <span aria-hidden="true">!</span>
                        <p>
                          {artifact.blockingStages === 1 ? 'One stage' : `${artifact.blockingStages} stages`} cannot
                          be signed, because nobody on this project holds the position required and no backup is
                          named for it. Assign somebody on the Personnel page, or change what the stage requires
                          on Review Workflows.
                        </p>
                      </div>
                    )}
                  </>
                )}
              </section>
            </div>
          </>
        )}
      </main>
    </div>
  )
}
