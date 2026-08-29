import { useCallback, useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import type { AuthUser } from './IdentityCenter'
import PortalHeader from './PortalHeader'
import { apiRequest, operationError } from './apiClient'
import {
  authorityLabel,
  authorityToken,
  baseRoleAuthorities,
  leadershipAuthorities,
  parseAuthorityToken,
} from './workflowAuthorities'
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
/**
 * The required authority a stage demands, as recorded. `LegacyRoleDemand` only ever arrives from a row
 * written before the cutover — it is displayed as historical, never offered as a modern choice.
 */
type RequiredAuthority = {
  kind: 'BaseRole' | 'LeadershipPosition' | 'LegacyRoleDemand'
  role?: string | null
  position?: string | null
}
type Stage = {
  position: number
  name: string
  kind: 'Review' | 'Approval'
  requiredRole?: string
  authorityKind?: 'BaseRole' | 'LeadershipPosition' | null
  isLegacy?: boolean
  requiredAuthority?: RequiredAuthority
  required: Resolved
}
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

const subjectLabels: Record<string, string> = {
  System: 'System Change Request',
  Software: 'Software Change Request',
  SystemTest: 'System Test Change Request',
  HighLevelSoftwareCase: 'HLR Test Case Change Request',
  LowLevelSoftwareCase: 'LLR Test Case Change Request',
  // Legacy subject names are readable only for historical records returned by compatibility APIs.
  HighLevelSoftwareTest: 'Historical HLR Test Procedure Change Request',
  LowLevelSoftwareTest: 'Historical LLR Test Procedure Change Request',
}

const label = (role: string) => authorityLabel(role)

/**
 * One row being edited. The authority is kept as an unselected state until the author actually chooses:
 * there is no default, because silently picking Reviewer (or anything else) is exactly the conflation the
 * cutover removed. `authorityKind === ''` means "not chosen yet".
 */
type EditableStage = {
  name: string
  kind: 'Review' | 'Approval'
  authorityKind: '' | 'BaseRole' | 'LeadershipPosition'
  authorityValue: string
}

const stageComplete = (stage: EditableStage) =>
  Boolean(stage.name.trim()) && parseAuthorityToken(`${stage.authorityKind}:${stage.authorityValue}`) !== null

/** What a saved stage shows in the editor. A legacy row is deliberately left UNSELECTED: the new version must record explicit modern authority, never a forwarded copy of the old demand. */
const savedAuthority = (stage: Stage): { authorityKind: EditableStage['authorityKind']; authorityValue: string } =>
  stage.requiredAuthority?.kind === 'BaseRole' && stage.requiredAuthority.role
    ? { authorityKind: 'BaseRole', authorityValue: stage.requiredAuthority.role }
    : stage.requiredAuthority?.kind === 'LeadershipPosition' && stage.requiredAuthority.position
      ? { authorityKind: 'LeadershipPosition', authorityValue: stage.requiredAuthority.position }
      : { authorityKind: '', authorityValue: '' }

// What the right-hand column says about a stage: a name, a count, or nobody.
function whoCanSign(required: Resolved) {
  if (required.blocking) return 'Nobody holds this'
  const parts: string[] = []
  if (required.singular && required.holders.length === 1) parts.push(required.holders[0])
  else if (required.holders.length) parts.push(`${required.holders.length} eligible`)
  if (required.backups.length) parts.push(`backup ${required.backups.join(', ')}`)
  if (required.delegates?.length) parts.push(`delegate ${required.delegates.join(', ')}`)
  return parts.length ? parts.join(' · ') : 'No eligible signer listed'
}

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
      Each row is one required minimum sign-off: the <strong>required project authority</strong> names who may
      act — a base project role, or the one accountable Project Leadership position (with its standing backup) —
      and the <strong>signature</strong> records what signing means, Review or Approval. Add rows to increase the
      minimum; authors may add extra eligible Program participants when submitting a Draft. This policy applies to
      new submissions and Drafts when sent, while an InReview or Approved record stays frozen on the version it
      started under.
    </p>
    <ol className="configurationRows">
      {stages.map((stage, index) => <li key={index} className="configurationRow">
        <span className="configurationRowNumber">{index + 1}</span>
        <label>Stage name
          <input value={stage.name} aria-label={`Stage name ${index + 1}`} onChange={event => setStages(items => items.map((item, i) => i === index ? { ...item, name: event.target.value } : item))} required />
        </label>
        <label>Signature
          <select value={stage.kind} aria-label={`Signature ${index + 1}`} onChange={event => setStages(items => items.map((item, i) => i === index ? { ...item, kind: event.target.value as EditableStage['kind'] } : item))}>
            <option value="Review">Review</option>
            <option value="Approval">Approval</option>
          </select>
        </label>
        <label>Required project authority
          <select
            value={`${stage.authorityKind}:${stage.authorityValue}`}
            aria-label={`Required project authority ${index + 1}`}
            onChange={event => {
              const parsed = parseAuthorityToken(event.target.value)
              setStages(items => items.map((item, i) => i === index ? {
                ...item,
                authorityKind: parsed?.kind ?? '',
                authorityValue: parsed?.value ?? '',
              } : item))
            }}
          >
            <option value=":">Choose authority…</option>
            <optgroup label="Base project roles">
              {baseRoleAuthorities.map(role => (
                <option value={authorityToken('BaseRole', role)} key={`BaseRole:${role}`}>{label(role)}</option>
              ))}
            </optgroup>
            <optgroup label="Project Leadership">
              {leadershipAuthorities.map(position => (
                <option value={authorityToken('LeadershipPosition', position)} key={`LeadershipPosition:${position}`}>
                  {`${label(position)} — leadership position`}
                </option>
              ))}
            </optgroup>
          </select>
        </label>
        <button type="button" className="configurationRemove" disabled={stages.length === 1 || saving} onClick={() => setStages(items => items.filter((_, i) => i !== index))}>
          Remove
        </button>
      </li>)}
    </ol>
    <div className="configurationEditorActions">
      <button type="button" onClick={() => setStages(items => [...items, { name: '', kind: 'Review', authorityKind: '', authorityValue: '' }])} disabled={saving}>+ Add required sign-off</button>
      <span>{stages.length} minimum sign-off{stages.length === 1 ? '' : 's'} · {dirty ? 'Unsaved changes' : 'No changes'}</span>
      <div>
        <button type="button" onClick={onCancel} disabled={saving}>Cancel</button>
        <button type="button" className="primaryConfigAction" onClick={onSave}
          disabled={saving || !dirty || stages.some(stage => !stageComplete(stage))}>
          {saving ? 'Saving…' : 'Save and activate'}
        </button>
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
  embedded = false,
}: {
  user: AuthUser
  api: string
  projectId: string
  projectName: string
  onBackToBuilds: () => void
  onSignOut: () => void
  embedded?: boolean
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
  useEffect(() => {
    if (data && !data.artifacts.some(item => item.subject === selected))
      setSelected(data.artifacts[0]?.subject ?? '')
  }, [data, selected])

  const artifact = useMemo(
    () => data?.artifacts.find(item => item.subject === selected) ?? null,
    [data, selected],
  )
  useEffect(() => {
    if (!artifact || editing) return
    setDraftStages((artifact.stages ?? []).map(stage => ({
      name: stage.name,
      kind: stage.kind,
      ...savedAuthority(stage),
    })))
  }, [artifact, editing])
  const blockedTotal = useMemo(
    () => (data?.artifacts ?? []).reduce((total, item) => total + item.blockingStages, 0),
    [data],
  )
  const savedStages = useMemo(() => (artifact?.stages ?? []).map(stage => ({
    name: stage.name,
    kind: stage.kind,
    ...savedAuthority(stage),
  })), [artifact])
  const dirty = editing && JSON.stringify(draftStages) !== JSON.stringify(savedStages)

  const beginEdit = () => {
    if (!artifact) return
    setSaveError('')
    setSaveSuccess('')
    // A legacy stage loads UNSELECTED on purpose: revising a legacy configuration writes a new explicit
    // version, and copying the old demand forward would smuggle it past the cutover.
    const existing = (artifact.stages ?? []).map(stage => ({
      name: stage.name,
      kind: stage.kind,
      ...savedAuthority(stage),
    }))
    setDraftStages(existing.length ? existing : [{ name: '', kind: 'Review', authorityKind: '', authorityValue: '' }])
    setEditing(true)
  }

  const save = async () => {
    if (!artifact || draftStages.length === 0) return
    const parsed = draftStages.map(stage => ({
      stage,
      authority: parseAuthorityToken(`${stage.authorityKind}:${stage.authorityValue}`),
    }))
    if (parsed.some(item => !item.authority)) return
    setSaving(true)
    setSaveError('')
    setSaveSuccess('')
    try {
      await apiRequest(`${api}/api/projects/${projectId}/approval-configuration/${artifact.subject}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          stages: parsed.map(({ stage, authority }) => ({
            name: stage.name,
            kind: stage.kind,
            requiredAuthority: authority!.kind === 'BaseRole'
              ? { kind: 'BaseRole', role: authority!.value }
              : { kind: 'LeadershipPosition', position: authority!.value },
          })),
        }),
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

  const ApprovalMain = embedded ? 'div' : 'main'
  return (
    <div className={`approvalConfigPage${embedded ? ' approvalConfigEmbedded' : ''}`}>
      {!embedded && <PortalHeader user={user} onSignOut={onSignOut}/>}
      <ApprovalMain className="approvalConfigMain">
        {!embedded && <nav className="approvalConfigBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onBackToBuilds}>Software Builds</button>
          <span aria-hidden="true">/</span>
          <strong>Approval configuration</strong>
        </nav>}

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
            {!embedded && <button type="button" onClick={onBackToBuilds}><span aria-hidden="true">←</span> Software Builds</button>}
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
                          <th scope="col">Required project authority</th>
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
                            <td>
                              {stage.isLegacy
                                ? <span className="legacyAuthority" title="Recorded before the authority split; kept exactly as it was stored">{stage.required.role}</span>
                                : stage.required.role}
                            </td>
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
      </ApprovalMain>
    </div>
  )
}
