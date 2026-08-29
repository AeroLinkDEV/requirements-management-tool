import { useCallback, useEffect, useMemo, useState } from 'react'
import type { AuthUser } from './IdentityCenter'
import PortalHeader from './PortalHeader'
import { apiRequest, operationError } from './apiClient'
import './PersonnelCenter.css'

/**
 * Who is on the project, what base roles they perform, and who holds each Project Leadership position —
 * with who stands behind them.
 *
 * #816 split the ideas the old page conflated. A person performs one or more base project roles; a
 * Project Leadership position is a separate, singular elevation that carries additional authority, and
 * every position can have one standing backup who answers the same authority while designated. The page
 * keeps those three questions apart: the leadership cards, the roster, and the person's identity details.
 *
 * Two hard lines from the owner decisions shape the markup. Generic Reviewer/Approver are workflow-stage
 * meanings, not jobs, so new role pickers never offer them. And a person's global identity (display name,
 * email) is edited only by a global administrator, with no effect on historical records.
 */

type LeadershipPerson = { userId: string; userName: string; displayName: string }
type LeadershipPrimary = { person: LeadershipPerson; assignedAt: string; eligibilityValid: boolean } | null
type LeadershipBackup = { person: LeadershipPerson; namedAt: string; eligibilityValid: boolean } | null
type LeadershipPosition = {
  position: string
  requiredBaseRole: string
  primary: LeadershipPrimary
  backup: LeadershipBackup
}
type Member = {
  userId: string
  userName: string
  displayName: string
  email: string
  accountDisabled: boolean
  roles: string[]
  endedRoles: string[]
  backsUp: string[]
  joinedAt: string
  leftAt?: string
  isCurrent: boolean
}
type Personnel = { projectId: string; canManage: boolean; positions: LeadershipPosition[]; members: Member[] }
type Candidate = { userId: string; userName: string; displayName: string; email: string }

type LeadershipResponse = { positions: LeadershipPosition[] }

const roleLabels: Record<string, string> = {
  ProjectEngineer: 'Project Engineer',
  ProgramManager: 'Program Manager',
  EngineeringManager: 'Engineering Manager',
  ConfigurationManager: 'Configuration Manager',
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

/**
 * The base roles new assignment offers — the jobs a person performs. Leadership positions are not in this
 * list because elevation happens on the leadership cards, and the generic legacy values (Engineer, Reviewer,
 * Approver, TestEngineer, TestLead) stay readable on historical chips but are not jobs the page grants.
 */
const baseRoleChoices: { name: string; roles: string[]; hint?: string }[] = [
  {
    name: 'Engineering',
    roles: ['SystemEngineer', 'SoftwareEngineer'],
    hint: 'Carries the authority to author controlled content',
  },
  { name: 'Verification', roles: ['SystemTestEngineer', 'SoftwareTestEngineer'] },
  {
    name: 'Leadership eligibility',
    hint: 'Eligibility for the matching Project Leadership position; the elevation itself happens on the leadership card',
    roles: ['ProjectEngineer', 'EngineeringManager', 'ProgramManager', 'ConfigurationManager'],
  },
  {
    name: 'Independent assurance',
    roles: ['SoftwareQualityAnalyst', 'Airworthiness'],
    hint: 'Reads everything; gains no engineering write authority',
  },
]

const initials = (name: string) =>
  name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase() ?? '').join('') || '?'

const formatDate = (value?: string) =>
  value ? new Date(value).toLocaleDateString(undefined, { day: '2-digit', month: 'short', year: 'numeric' }) : ''

function Avatar({ name, tone }: { name: string; tone?: string }) {
  return <span className={`personAvatar${tone ? ` ${tone}` : ''}`} aria-hidden="true">{initials(name)}</span>
}

export default function PersonnelCenter({
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
  const [data, setData] = useState<Personnel | null>(null)
  const [leadership, setLeadership] = useState<LeadershipPosition[]>([])
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [addOpen, setAddOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [chosenPerson, setChosenPerson] = useState<Candidate | null>(null)
  const [chosenRoles, setChosenRoles] = useState<string[]>([])
  const [createAccountOpen, setCreateAccountOpen] = useState(false)
  const [newAccount, setNewAccount] = useState({ displayName: '', userName: '', email: '', temporaryPassword: '' })
  const [detailsFor, setDetailsFor] = useState<string | null>(null)
  const [identityDraft, setIdentityDraft] = useState({ displayName: '', email: '' })
  const [picker, setPicker] = useState<{ kind: 'primary' | 'backup'; position: string } | null>(null)
  const [pickerChoice, setPickerChoice] = useState('')

  const load = useCallback(async () => {
    try {
      setError('')
      const [personnel, leadershipData] = await Promise.all([
        apiRequest<Personnel>(`${api}/api/projects/${projectId}/personnel`),
        apiRequest<LeadershipResponse>(`${api}/api/projects/${projectId}/leadership`),
      ])
      setData(personnel)
      setLeadership(leadershipData.positions)
    } catch (failure) {
      setError(operationError(failure, 'The project personnel could not be loaded.'))
    }
  }, [api, projectId])

  useEffect(() => { void load() }, [load])

  const memberCount = useMemo(() => data?.members.filter(member => member.isCurrent).length ?? 0, [data])
  const currentMembers = useMemo(() => (data?.members ?? []).filter(member => member.isCurrent), [data])
  const memberByUser = useMemo(() => {
    const map = new Map<string, Member>()
    for (const member of data?.members ?? []) map.set(member.userId, member)
    return map
  }, [data])
  const leadershipByPosition = useMemo(() => {
    const map = new Map<string, LeadershipPosition>()
    for (const position of leadership) map.set(position.position, position)
    return map
  }, [leadership])
  // Primary holders and standing backups are tracked separately: a backup is not the primary holder, and
  // conflating them misrepresents who actually holds the position's authority (#816).
  const primaryPositionsHeldBy = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const position of leadership) {
      if (position.primary) map.set(position.primary.person.userId, [...(map.get(position.primary.person.userId) ?? []), position.position])
    }
    return map
  }, [leadership])
  const canManage = data?.canManage ?? false
  const isGlobalAdmin = user.isAdministrator
  const vacancies = leadership.filter(position => !position.primary).length

  const run = async (operation: () => Promise<void>, success: string, fallback: string) => {
    setBusy(true)
    setError('')
    setNotice('')
    try {
      await operation()
      await load()
      setNotice(success)
    } catch (failure) {
      setError(operationError(failure, fallback))
    } finally {
      setBusy(false)
    }
  }

  const loadCandidates = useCallback(async (term: string) => {
    try {
      setCandidates(await apiRequest<Candidate[]>(`${api}/api/projects/${projectId}/personnel/candidates?search=${encodeURIComponent(term)}`))
    } catch (failure) {
      setError(operationError(failure, 'The list of people who could be added could not be loaded.'))
    }
  }, [api, projectId])

  useEffect(() => {
    if (!addOpen) return
    const timer = setTimeout(() => void loadCandidates(search), 180)
    return () => clearTimeout(timer)
  }, [addOpen, search, loadCandidates])

  const openAdd = () => {
    setAddOpen(true)
    setChosenPerson(null)
    setChosenRoles([])
    setNotice('')
    setError('')
    setSearch('')
    void loadCandidates('')
  }

  const addPerson = () => {
    if (!chosenPerson || chosenRoles.length === 0) return
    void run(async () => {
      await apiRequest(`${api}/api/projects/${projectId}/personnel`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: chosenPerson.userId, roles: chosenRoles }),
      })
      setAddOpen(false)
      setChosenPerson(null)
      setChosenRoles([])
    }, 'Added to the project with the selected roles.', 'That person could not be added.')
  }

  const addRole = (userId: string, role: string) => run(
    async () => {
      await apiRequest(`${api}/api/projects/${projectId}/personnel`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId, roles: [role] }),
      })
    },
    `${label(role)} added.`,
    'That role could not be added.',
  )

  const endRole = (member: Member, role: string) => run(
    () => apiRequest(`${api}/api/projects/${projectId}/personnel/${member.userId}/roles/${role}`, { method: 'DELETE' }),
    `${label(role)} ended for ${member.displayName}.`,
    'That role could not be ended.',
  )

  const assignPrimary = (position: string, holderUserId: string) => run(
    async () => {
      const result = await apiRequest<{ replaced?: string; previousBackupContinues?: boolean }>(
        `${api}/api/projects/${projectId}/leadership/${position}/primary`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ holderUserId }),
      })
      setPicker(null)
      setPickerChoice('')
      if (result.replaced && result.previousBackupContinues) setNotice('Leader replaced. The existing backup remains attached to the position.')
      else if (result.replaced) setNotice('Leader replaced.')
      else setNotice('Leader assigned.')
    },
    'Leader assigned.',
    'That assignment could not be recorded.',
  )

  const assignBackup = (position: string, backupUserId: string) => run(
    async () => {
      const active = leadershipByPosition.get(position)
      const hasBackup = Boolean(active?.backup)
      await apiRequest(`${api}/api/projects/${projectId}/leadership/${position}/backup`, hasBackup
        ? { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ backupUserId }) }
        : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ backupUserId }) })
      setPicker(null)
      setPickerChoice('')
    },
    'Backup assigned.',
    'That backup could not be named.',
  )

  const removeBackup = (position: string) => run(
    () => apiRequest(`${api}/api/projects/${projectId}/leadership/${position}/backup`, { method: 'DELETE' }),
    'Backup removed.',
    'That backup could not be removed.',
  )

  const saveIdentity = (userId: string) => run(
    async () => {
      await apiRequest(`${api}/api/admin/users/${userId}/identity`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(identityDraft),
      })
    },
    'Current identity updated. Historical records were not changed.',
    'That identity change could not be recorded.',
  )

  const createLocalAccount = () => run(
    async () => {
      const created = await apiRequest<{ id: string; userName: string; displayName: string; email: string }>(
        `${api}/api/admin/users`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          userName: newAccount.userName, displayName: newAccount.displayName,
          email: newAccount.email, temporaryPassword: newAccount.temporaryPassword,
        }),
      })
      setCreateAccountOpen(false)
      setNewAccount({ displayName: '', userName: '', email: '', temporaryPassword: '' })
      setChosenPerson({ userId: created.id, userName: created.userName, displayName: created.displayName, email: created.email })
      await loadCandidates(search)
    },
    'Account created. Now choose the project roles.',
    'That account could not be created.',
  )

  const openDetails = (member: Member) => {
    setDetailsFor(member.userId)
    setIdentityDraft({ displayName: member.displayName, email: member.email })
  }
  const detailsMember = detailsFor ? memberByUser.get(detailsFor) : undefined
  const detailsLeadership = detailsFor ? (primaryPositionsHeldBy.get(detailsFor) ?? []) : []

  const openPicker = (kind: 'primary' | 'backup', position: string) => {
    setPicker({ kind, position })
    setPickerChoice('')
  }

  const pickerPosition = picker ? leadershipByPosition.get(picker.position) : undefined
  const pickerRequiredRole = pickerPosition?.requiredBaseRole ?? ''
  const pickerEligible = currentMembers.filter(member => member.roles.includes(pickerRequiredRole))
  const pickerIneligible = currentMembers.filter(member => !member.roles.includes(pickerRequiredRole))

  return (
    <div className="personnelPage">
      <PortalHeader user={user} onSignOut={onSignOut}/>
      <main className="personnelMain">
        <nav className="personnelBreadcrumb" aria-label="Breadcrumb">
          <button type="button" onClick={onBackToBuilds}>Software Builds</button>
          <span aria-hidden="true">/</span>
          <strong>Personnel</strong>
        </nav>

        <header className="personnelHeading">
          <div>
            <h1>Personnel</h1>
            <p>
              Who is on {projectName}, which base roles they perform, who holds each Project Leadership
              position, and who stands behind them. Adding somebody here is what gives them access.
            </p>
          </div>
          <div className="personnelActions">
            {canManage && <button type="button" className="primaryAction" onClick={openAdd}>+ Add Person to Project</button>}
            <button type="button" onClick={onBackToBuilds}><span aria-hidden="true">←</span> Software Builds</button>
          </div>
        </header>

        {!canManage && data && (
          <p className="personnelReadOnly">
            You are viewing this roster. The Program Manager or Project Engineer can change it.
          </p>
        )}
        {error && <p className="personnelError" role="alert">{error}</p>}
        {notice && <p className="personnelNotice" role="status">{notice}</p>}

        {!data ? (
          <p className="personnelLoading">Loading the project roster…</p>
        ) : (
          <>
            <section className="personnelSection" aria-labelledby="project-leadership">
              <header>
                <h2 id="project-leadership">Project Leadership</h2>
                <p>
                  {memberCount} {memberCount === 1 ? 'person' : 'people'} on this project
                  {vacancies > 0 && ` · ${vacancies} position${vacancies === 1 ? '' : 's'} unfilled`} · Each
                  position carries elevated authority and can have one standing backup with the same authority.
                </p>
              </header>
              <div className="positionGrid">
                {leadership.map(position => {
                  const required = label(position.requiredBaseRole)
                  const primaryValid = position.primary?.eligibilityValid !== false
                  const backupValid = position.backup?.eligibilityValid !== false
                  return (
                    <article key={position.position} className={`positionCard${position.primary ? '' : ' vacant'}`} data-position={position.position}>
                      <span className="positionName">{label(position.position)}</span>
                      {position.primary ? (
                        <div className="positionHolder">
                          <Avatar name={position.primary.person.displayName}/>
                          <span className="positionWho">
                            <button type="button" className="personLink" onClick={() => {
                              const member = memberByUser.get(position.primary!.person.userId)
                              if (member) openDetails(member)
                            }}>{position.primary.person.displayName}</button>
                            <span>Held since {formatDate(position.primary.assignedAt)}</span>
                            <span className="statusPill covered">Elevated authority</span>
                          </span>
                        </div>
                      ) : (
                        <div className="positionHolder">
                          <span className="personAvatar vacant" aria-hidden="true">?</span>
                          <span className="positionWho vacantWho">
                            Nobody assigned
                            <span>Requires the {required} role. Anything this position signs cannot be completed.</span>
                          </span>
                        </div>
                      )}
                      {position.primary && !primaryValid && (
                        <p className="leadershipWarning" role="status">
                          The holder no longer meets the {required} eligibility. Their authority is suspended.
                        </p>
                      )}
                      <div className="positionFoot">
                        {position.backup ? (
                          <>
                            <span className={`statusPill ${backupValid ? 'covered' : 'ended'}`}>
                              Backup {position.backup.person.displayName}
                            </span>
                            {canManage && (
                              <>
                                <button type="button" className="linkAction" disabled={busy} onClick={() => openPicker('backup', position.position)}>Change backup</button>
                                <button type="button" className="linkAction" disabled={busy} onClick={() => removeBackup(position.position)}>Remove backup</button>
                              </>
                            )}
                          </>
                        ) : (
                          <>
                            <span className="statusPill muted">No backup assigned</span>
                            {canManage && (
                              <button type="button" className="linkAction" disabled={busy} onClick={() => openPicker('backup', position.position)}>Assign backup</button>
                            )}
                          </>
                        )}
                      </div>
                      {position.backup && !backupValid && (
                        <p className="leadershipWarning" role="status">
                          The backup no longer meets the {required} eligibility. Their authority is suspended.
                        </p>
                      )}
                      <div className="positionFoot">
                        {canManage && (
                          <button type="button" className="linkAction" disabled={busy} onClick={() => openPicker('primary', position.position)}>
                            {position.primary ? 'Replace leader' : 'Assign leader'}
                          </button>
                        )}
                      </div>
                    </article>
                  )
                })}
              </div>
            </section>

            <section className="personnelSection" aria-labelledby="assurance">
              <header>
                <h2 id="assurance">Independent assurance</h2>
                <p>Reads everything on the project. Deliberately holds no engineering write authority and is not Project Leadership.</p>
              </header>
              <div className="assuranceRow">
                {['SoftwareQualityAnalyst', 'Airworthiness'].map(role => {
                  const holders = currentMembers.filter(member => member.roles.includes(role))
                  return (
                    <article key={role} className={`assuranceCard${holders.length === 0 ? ' vacant' : ''}`} data-assurance={role}>
                      <span className="positionName">{label(role)}</span>
                      {holders.length === 0 ? (
                        <p className="assuranceEmpty">Nobody holds this. Anything requiring it cannot be signed.</p>
                      ) : (
                        <ul>{holders.map(holder => (
                          <li key={holder.userId}><Avatar name={holder.displayName}/> {holder.displayName}</li>
                        ))}</ul>
                      )}
                    </article>
                  )
                })}
              </div>
            </section>

            <section className="personnelSection" aria-labelledby="roster">
              <header>
                <h2 id="roster">Roster</h2>
                <p>Everyone listed as current can open this project.</p>
              </header>
              <div className="rosterTableWrap">
                <table className="rosterTable">
                  <thead>
                    <tr>
                      <th scope="col">Person</th>
                      <th scope="col">Email</th>
                      <th scope="col">Base roles</th>
                      <th scope="col">Project Leadership</th>
                      <th scope="col">Joined</th>
                      <th scope="col">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.members.map(member => {
                      const held = primaryPositionsHeldBy.get(member.userId) ?? []
                      return (
                        <tr key={member.userId} className={member.isCurrent ? undefined : 'departed'} data-member={member.userName}>
                          <td>
                            <div className="rosterWho">
                              <Avatar name={member.displayName}/>
                              <span className="rosterName">
                                <button type="button" className="personLink" disabled={!member.isCurrent} onClick={() => openDetails(member)}>{member.displayName}</button>
                                <span>{member.userName}</span>
                              </span>
                            </div>
                          </td>
                          <td>{member.email || <span className="roleChip ended">No email</span>}</td>
                          <td>
                            <div className="roleChips">
                              {member.roles.map(role => (
                                <span key={role} className="roleChip">
                                  {label(role)}
                                  {canManage && member.isCurrent && (
                                    <button
                                      type="button"
                                      className="chipEnd"
                                      aria-label={`End ${label(role)} for ${member.displayName}`}
                                      disabled={busy}
                                      onClick={() => endRole(member, role)}
                                    >×</button>
                                  )}
                                </span>
                              ))}
                              {member.roles.length === 0 && <span className="roleChip ended">No current role</span>}
                            </div>
                          </td>
                          <td>
                            <div className="roleChips">
                              {held.map(positionName => (
                                <span key={positionName} className="roleChip lead">{label(positionName)}</span>
                              ))}
                              {member.backsUp.map(role => (
                                <span key={`backup-${role}`} className="roleChip backup">Backup · {label(role)}</span>
                              ))}
                              {held.length === 0 && member.backsUp.length === 0 && <span className="roleChip ended">—</span>}
                            </div>
                          </td>
                          <td className="rosterDate">{formatDate(member.joinedAt)}</td>
                          <td>
                            {member.accountDisabled
                              ? <span className="statusPill ended">Disabled</span>
                              : member.isCurrent
                                ? <span className="statusPill active">Active</span>
                                : <span className="statusPill muted">Left {formatDate(member.leftAt)}</span>}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            </section>
          </>
        )}

        {addOpen && (
          <div className="addPersonPanel" role="dialog" aria-modal="false" aria-labelledby="add-person-heading">
            <h2 id="add-person-heading">Add a person to this project</h2>
            <p className="addPersonLede">Search active AeroLink accounts, choose the base roles they perform, and add them once.</p>
            <div className="addPersonField">
              <label htmlFor="add-person-search">Search the directory</label>
              <input
                id="add-person-search"
                type="search"
                value={search}
                placeholder="Name, username or email"
                onChange={event => setSearch(event.target.value)}
              />
            </div>
            <div className="directoryResults" role="listbox" aria-label="Directory results">
              {candidates.map(candidate => (
                <button
                  key={candidate.userId}
                  type="button"
                  role="option"
                  aria-selected={chosenPerson?.userId === candidate.userId}
                  className={`directoryResult${chosenPerson?.userId === candidate.userId ? ' chosen' : ''}`}
                  onClick={() => setChosenPerson(candidate)}
                >
                  <Avatar name={candidate.displayName}/>
                  <span>{candidate.displayName}<span>{candidate.userName} · {candidate.email || 'No email'}</span></span>
                </button>
              ))}
              {candidates.length === 0 && (
                <p className="addPersonHint">
                  No active account matches this search. {isGlobalAdmin
                    ? 'Create a local person/account below, then continue with the role selection.'
                    : 'An AeroLink administrator must create the account before the person can be added.'}
                </p>
              )}
            </div>
            {chosenPerson && (
              <fieldset className="addPersonRoles">
                <legend>Base roles for {chosenPerson.displayName}</legend>
                {baseRoleChoices.map(group => (
                  <div key={group.name} className="roleGroup">
                    <b>{group.name}</b>
                    {group.roles.map(role => (
                      <label key={role} className="roleChoice">
                        <input
                          type="checkbox"
                          checked={chosenRoles.includes(role)}
                          onChange={event => setChosenRoles(current =>
                            event.target.checked ? [...current, role] : current.filter(x => x !== role))}
                        />
                        {label(role)}
                      </label>
                    ))}
                    {group.hint && <p className="addPersonHint">{group.hint}</p>}
                  </div>
                ))}
                {chosenRoles.length === 0 && <p className="addPersonWarning">Choose at least one base role.</p>}
              </fieldset>
            )}
            <div className="addPersonActions">
              <button type="button" className="primaryAction" disabled={!chosenPerson || chosenRoles.length === 0 || busy} onClick={addPerson}>Add to project</button>
              <button type="button" onClick={() => setAddOpen(false)}>Cancel</button>
            </div>
            {isGlobalAdmin && (
              createAccountOpen ? (
                <fieldset className="createAccountForm">
                  <legend>Create local person/account</legend>
                  <label>Display name<input value={newAccount.displayName} onChange={event => setNewAccount(current => ({ ...current, displayName: event.target.value }))} /></label>
                  <label>Username<input value={newAccount.userName} onChange={event => setNewAccount(current => ({ ...current, userName: event.target.value }))} /></label>
                  <label>Email<input type="email" value={newAccount.email} onChange={event => setNewAccount(current => ({ ...current, email: event.target.value }))} /></label>
                  <label>Temporary password<input type="password" value={newAccount.temporaryPassword} onChange={event => setNewAccount(current => ({ ...current, temporaryPassword: event.target.value }))} /></label>
                  <p className="addPersonHint">They must change it at first sign-in. Creating the account grants no project authority — the roles above do.</p>
                  <div className="addPersonActions">
                    <button type="button" className="primaryAction" disabled={!newAccount.userName || !newAccount.displayName || !newAccount.temporaryPassword || busy} onClick={createLocalAccount}>Create account</button>
                    <button type="button" onClick={() => setCreateAccountOpen(false)}>Cancel</button>
                  </div>
                </fieldset>
              ) : (
                <button type="button" className="linkAction" onClick={() => setCreateAccountOpen(true)}>Create local person/account</button>
              )
            )}
          </div>
        )}

        {detailsMember && (
          <div className="addPersonPanel" role="dialog" aria-modal="false" aria-labelledby="person-details-heading">
            <h2 id="person-details-heading">{detailsMember.displayName}</h2>
            <p className="addPersonLede">{detailsMember.userName} · {detailsMember.email || 'No email'}</p>
            <dl className="personDetails">
              <div><dt>Account</dt><dd>{detailsMember.accountDisabled ? 'Disabled' : 'Active'}</dd></div>
              <div><dt>Base roles</dt><dd>{detailsMember.roles.length ? detailsMember.roles.map(label).join(', ') : 'None'}</dd></div>
              <div><dt>Project Leadership</dt><dd>{detailsLeadership.length ? detailsLeadership.map(label).join(', ') : 'None'}</dd></div>
              <div><dt>Standing backup for</dt><dd>{detailsMember.backsUp.length ? detailsMember.backsUp.map(label).join(', ') : 'Nothing'}</dd></div>
            </dl>
            {isGlobalAdmin && detailsMember.isCurrent && (
              <fieldset className="createAccountForm">
                <legend>Current identity (global administrator)</legend>
                <label>Display name<input value={identityDraft.displayName} onChange={event => setIdentityDraft(current => ({ ...current, displayName: event.target.value }))} /></label>
                <label>Email<input type="email" value={identityDraft.email} onChange={event => setIdentityDraft(current => ({ ...current, email: event.target.value }))} /></label>
                <p className="addPersonHint">Changing this updates the current account only. Historical records keep the names they were signed with.</p>
                <button type="button" className="primaryAction" disabled={busy} onClick={() => saveIdentity(detailsMember.userId)}>Save identity</button>
              </fieldset>
            )}
            {canManage && detailsMember.isCurrent && (
              <fieldset className="addPersonRoles">
                <legend>Base roles on this project</legend>
                <div className="roleChips">
                  {detailsMember.roles.map(role => (
                    <span key={role} className="roleChip">
                      {label(role)}
                      <button type="button" className="chipEnd" aria-label={`End ${label(role)}`} disabled={busy} onClick={() => endRole(detailsMember, role)}>×</button>
                    </span>
                  ))}
                </div>
                <label htmlFor={`add-role-${detailsMember.userId}`}>Add a base role</label>
                <select
                  id={`add-role-${detailsMember.userId}`}
                  value=""
                  onChange={event => { if (event.target.value) addRole(detailsMember.userId, event.target.value) }}
                >
                  <option value="">Select a base role…</option>
                  {baseRoleChoices.flatMap(group => group.roles)
                    .filter(role => !detailsMember.roles.includes(role))
                    .map(role => <option key={role} value={role}>{label(role)}</option>)}
                </select>
                <p className="addPersonHint">Ending a role that a leadership position requires suspends that authority until the role is restored.</p>
              </fieldset>
            )}
            <div className="addPersonActions">
              <button type="button" onClick={() => setDetailsFor(null)}>Close</button>
            </div>
          </div>
        )}

        {picker && pickerPosition && (
          <div className="addPersonPanel" role="dialog" aria-modal="false" aria-labelledby="picker-heading">
            <h2 id="picker-heading">{picker.kind === 'primary' ? (pickerPosition.primary ? 'Replace leader' : 'Assign leader') : 'Standing backup'}</h2>
            <p className="addPersonLede">
              {pickerPosition.position} · Requires the {label(pickerRequiredRole)} role. People without it are
              listed but cannot be chosen.
            </p>
            <div className="directoryResults" role="listbox" aria-label="Eligible people">
              {pickerEligible.filter(member => !(picker.kind === 'backup' && member.userId === pickerPosition.primary?.person.userId))
                .map(member => (
                  <button
                    key={member.userId}
                    type="button"
                    role="option"
                    aria-selected={pickerChoice === member.userId}
                    className={`directoryResult${pickerChoice === member.userId ? ' chosen' : ''}`}
                    onClick={() => setPickerChoice(member.userId)}
                  >
                    <Avatar name={member.displayName}/>
                    <span>{member.displayName}<span>{member.userName}</span></span>
                  </button>
                ))}
              {pickerIneligible.map(member => (
                <button key={member.userId} type="button" className="directoryResult ineligible" disabled
                  aria-label={`${member.displayName}: requires the ${label(pickerRequiredRole)} role`}>
                  <Avatar name={member.displayName}/>
                  <span>{member.displayName}<span>Requires the {label(pickerRequiredRole)} role</span></span>
                </button>
              ))}
            </div>
            {picker.kind === 'primary' && pickerPosition.backup && (
              <p className="addPersonWarning">
                {pickerPosition.backup.person.displayName} is the standing backup. Naming somebody else leaves
                that backup attached to the position.
              </p>
            )}
            <div className="addPersonActions">
              <button
                type="button"
                className="primaryAction"
                disabled={!pickerChoice || busy}
                onClick={() => picker.kind === 'primary'
                  ? assignPrimary(picker.position, pickerChoice)
                  : assignBackup(picker.position, pickerChoice)}
              >Confirm</button>
              <button type="button" onClick={() => setPicker(null)}>Cancel</button>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}
