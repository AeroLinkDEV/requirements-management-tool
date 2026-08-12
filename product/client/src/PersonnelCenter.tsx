import { useCallback, useEffect, useMemo, useState } from 'react'
import type { AuthUser } from './IdentityCenter'
import PortalHeader from './PortalHeader'
import { apiRequest, operationError } from './apiClient'
import './PersonnelCenter.css'

/**
 * Who is on a Project, what position they hold, and who acts for them.
 *
 * Positions come in two shapes and the page shows both, because they answer different questions. A singular
 * position — Project Engineer, Configuration Manager, each discipline's Lead — has one holder, and the useful
 * fact about it is whether anybody holds it at all. A discipline has many members, and the useful fact is who
 * leads it. A roster alphabetised by surname answers neither.
 */

type Person = { userId: string; userName: string; displayName: string }
type Position = { role: string; holder: Person | null; heldSince?: string; backup: Person | null }
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
type Personnel = { projectId: string; canManage: boolean; positions: Position[]; members: Member[] }
type Candidate = { userId: string; userName: string; displayName: string; email: string }

/** The label a person recognises, against the enum name the server stores. */
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

/**
 * The roles offered when adding somebody, grouped the way the domain groups them.
 *
 * Presenting sixteen values as one flat list invites the mistake the model exists to prevent: an independent
 * assurance role sitting beside an engineering one, indistinguishable, when the whole point is that one reads
 * and the other writes. `Administrator` is deliberately absent — granting it stays with the global account.
 */
const roleGroups: { name: string; hint?: string; roles: string[] }[] = [
  { name: 'Project positions', hint: 'One person holds each', roles: ['ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager', 'ProjectEngineeringLead'] },
  { name: 'Engineering', hint: 'Carries the authority to author controlled content', roles: ['SystemEngineer', 'SoftwareEngineer', 'SystemEngineeringLead', 'SoftwareEngineeringLead'] },
  { name: 'Verification', roles: ['SystemTestEngineer', 'SoftwareTestEngineer', 'SystemTestLead', 'SoftwareTestLead'] },
  { name: 'Control authority', roles: ['Reviewer', 'Approver'] },
  { name: 'Independent assurance', hint: 'Reads everything; gains no engineering write authority', roles: ['SoftwareQualityAnalyst', 'Airworthiness'] },
]

const singularPositions = ['ProjectEngineer', 'ProgramManager', 'EngineeringManager', 'ConfigurationManager', 'ProjectEngineeringLead']
const disciplineLeads: { lead: string; discipline: string; member: string }[] = [
  { lead: 'SystemEngineeringLead', discipline: 'System Engineering', member: 'SystemEngineer' },
  { lead: 'SoftwareEngineeringLead', discipline: 'Software Engineering', member: 'SoftwareEngineer' },
  { lead: 'SystemTestLead', discipline: 'System Test', member: 'SystemTestEngineer' },
  { lead: 'SoftwareTestLead', discipline: 'Software Test', member: 'SoftwareTestEngineer' },
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
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const [addOpen, setAddOpen] = useState(false)
  const [candidates, setCandidates] = useState<Candidate[]>([])
  const [chosenPerson, setChosenPerson] = useState('')
  const [chosenRole, setChosenRole] = useState('SystemEngineer')
  const [backupFor, setBackupFor] = useState<string | null>(null)
  const [chosenBackup, setChosenBackup] = useState('')

  const load = useCallback(async () => {
    try {
      setError('')
      setData(await apiRequest<Personnel>(`${api}/api/projects/${projectId}/personnel`))
    } catch (failure) {
      setError(operationError(failure, 'The project personnel could not be loaded.'))
    }
  }, [api, projectId])

  useEffect(() => { void load() }, [load])

  const positionsByRole = useMemo(() => {
    const map = new Map<string, Position>()
    for (const position of data?.positions ?? []) map.set(position.role, position)
    return map
  }, [data])

  const memberCount = useMemo(() => data?.members.filter(member => member.isCurrent).length ?? 0, [data])
  const vacancies = useMemo(
    () => (data?.positions ?? []).filter(position => !position.holder).map(position => position.role),
    [data],
  )
  const currentMembers = useMemo(() => (data?.members ?? []).filter(member => member.isCurrent), [data])

  const openAdd = async () => {
    setAddOpen(true)
    setNotice('')
    setError('')
    try {
      setCandidates(await apiRequest<Candidate[]>(`${api}/api/projects/${projectId}/personnel/candidates`))
    } catch (failure) {
      setError(operationError(failure, 'The list of people who could be added could not be loaded.'))
    }
  }

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

  const addPerson = () => run(
    async () => {
      await apiRequest(`${api}/api/projects/${projectId}/personnel`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userId: chosenPerson, role: chosenRole }),
      })
      setAddOpen(false)
      setChosenPerson('')
    },
    'Added to the project.',
    'That person could not be added.',
  )

  const endRole = (member: Member, role: string) => run(
    () => apiRequest(`${api}/api/projects/${projectId}/personnel/${member.userId}/roles/${role}`, { method: 'DELETE' }),
    `${label(role)} ended for ${member.displayName}.`,
    'That position could not be ended.',
  )

  const nameBackup = (role: string) => run(
    async () => {
      await apiRequest(`${api}/api/projects/${projectId}/personnel/backups`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ backupUserId: chosenBackup, role }),
      })
      setBackupFor(null)
      setChosenBackup('')
    },
    'Backup named.',
    'That backup could not be named.',
  )

  const removeBackup = (role: string) => run(
    () => apiRequest(`${api}/api/projects/${projectId}/personnel/backups/${role}`, { method: 'DELETE' }),
    'Backup removed.',
    'That backup could not be removed.',
  )

  const canManage = data?.canManage ?? false

  const positionCard = (role: string, subtitle?: string) => {
    const position = positionsByRole.get(role)
    const vacant = !position?.holder
    return (
      <article key={role} className={`positionCard${vacant ? ' vacant' : ''}${position?.backup ? ' covered' : ''}`} data-position={role}>
        <span className="positionName">{label(role)}</span>
        {position?.holder ? (
          <div className="positionHolder">
            <Avatar name={position.holder.displayName}/>
            <span className="positionWho">
              {position.holder.displayName}
              <span>{subtitle ?? (position.heldSince ? `Held since ${formatDate(position.heldSince)}` : position.holder.userName)}</span>
            </span>
          </div>
        ) : (
          <div className="positionHolder">
            <span className="personAvatar vacant" aria-hidden="true">?</span>
            <span className="positionWho vacantWho">
              Nobody assigned
              <span>Nothing this position signs can be completed</span>
            </span>
          </div>
        )}
        <div className="positionFoot">
          {position?.backup ? (
            <>
              <span className="statusPill covered">Backup {position.backup.displayName}</span>
              {canManage && <button type="button" className="linkAction" disabled={busy} onClick={() => removeBackup(role)}>Remove</button>}
            </>
          ) : (
            <>
              <span className="statusPill muted">No backup named</span>
              {canManage && position?.holder && (
                <button type="button" className="linkAction" disabled={busy} onClick={() => { setBackupFor(role); setChosenBackup('') }}>Name one</button>
              )}
            </>
          )}
        </div>
        {backupFor === role && (
          <div className="backupPicker">
            <label htmlFor={`backup-${role}`}>Standing backup</label>
            <select id={`backup-${role}`} value={chosenBackup} onChange={event => setChosenBackup(event.target.value)}>
              <option value="">Select a person…</option>
              {currentMembers
                .filter(member => member.userId !== position?.holder?.userId)
                .map(member => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}
            </select>
            <p>They will be able to act as {label(role)} at any time, until removed.</p>
            <div className="backupPickerActions">
              <button type="button" className="primaryAction" disabled={!chosenBackup || busy} onClick={() => nameBackup(role)}>Name backup</button>
              <button type="button" onClick={() => setBackupFor(null)}>Cancel</button>
            </div>
          </div>
        )}
      </article>
    )
  }

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
              Who is on {projectName}, what their position authorises, and who acts for them. Adding somebody
              here is what gives them access to the project.
            </p>
          </div>
          <div className="personnelActions">
            {canManage && <button type="button" className="primaryAction" onClick={openAdd}>+ Add person</button>}
            <button type="button" onClick={onBackToBuilds}><span aria-hidden="true">←</span> Software Builds</button>
          </div>
        </header>

        {!canManage && data && (
          <p className="personnelReadOnly">
            You are viewing this roster. The Program Manager, Project Engineer or Project Engineering Lead can change it.
          </p>
        )}
        {error && <p className="personnelError" role="alert">{error}</p>}
        {notice && <p className="personnelNotice" role="status">{notice}</p>}

        {!data ? (
          <p className="personnelLoading">Loading the project roster…</p>
        ) : (
          <>
            <section className="personnelSection" aria-labelledby="project-positions">
              <header>
                <h2 id="project-positions">Project positions</h2>
                <p>
                  {memberCount} {memberCount === 1 ? 'person' : 'people'} on this project
                  {vacancies.length > 0 && ` · ${vacancies.length} position${vacancies.length === 1 ? '' : 's'} unfilled`}
                </p>
              </header>
              <div className="positionGrid">{singularPositions.map(role => positionCard(role))}</div>
            </section>

            <section className="personnelSection" aria-labelledby="disciplines">
              <header>
                <h2 id="disciplines">Disciplines</h2>
                <p>Each has one lead. Everyone else in the discipline works under them.</p>
              </header>
              <div className="positionGrid">
                {disciplineLeads.map(({ lead, discipline, member }) => {
                  const headcount = currentMembers.filter(person => person.roles.includes(member)).length
                  return (
                    <div key={lead} className="disciplineWrap">
                      {positionCard(lead, `Lead · ${headcount} ${headcount === 1 ? 'engineer' : 'engineers'}`)}
                      <span className="disciplineTag">{discipline}</span>
                    </div>
                  )
                })}
              </div>
            </section>

            <section className="personnelSection" aria-labelledby="assurance">
              <header>
                <h2 id="assurance">Independent assurance</h2>
                <p>Reads everything on the project. Deliberately holds no engineering write authority.</p>
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
                      <th scope="col">Position on this project</th>
                      <th scope="col">Joined</th>
                      <th scope="col">Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.members.map(member => (
                      <tr key={member.userId} className={member.isCurrent ? undefined : 'departed'} data-member={member.userName}>
                        <td>
                          <div className="rosterWho">
                            <Avatar name={member.displayName}/>
                            <span className="rosterName">{member.displayName}<span>{member.userName}</span></span>
                          </div>
                        </td>
                        <td>
                          <div className="roleChips">
                            {member.roles.map(role => (
                              <span key={role} className={`roleChip${singularPositions.includes(role) || role.endsWith('Lead') ? ' lead' : ''}`}>
                                {label(role)}
                                {canManage && (
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
                            {member.backsUp.map(role => (
                              <span key={`backup-${role}`} className="roleChip backup">Backup · {label(role)}</span>
                            ))}
                            {member.roles.length === 0 && <span className="roleChip ended">No current position</span>}
                          </div>
                        </td>
                        <td className="rosterDate">{formatDate(member.joinedAt)}</td>
                        <td>
                          {member.isCurrent
                            ? <span className="statusPill active">Active</span>
                            : <span className="statusPill muted">Left {formatDate(member.leftAt)}</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          </>
        )}

        {addOpen && (
          <div className="addPersonPanel" role="dialog" aria-modal="false" aria-labelledby="add-person-heading">
            <h2 id="add-person-heading">Add someone to this project</h2>
            <p className="addPersonLede">They will be able to open {projectName} as soon as you save.</p>
            <div className="addPersonField">
              <label htmlFor="add-person-who">Person</label>
              <select id="add-person-who" value={chosenPerson} onChange={event => setChosenPerson(event.target.value)}>
                <option value="">Select a person…</option>
                {candidates.map(candidate => (
                  <option key={candidate.userId} value={candidate.userId}>{candidate.displayName} · {candidate.userName}</option>
                ))}
              </select>
              {candidates.length === 0 && <p className="addPersonHint">Everyone with an active account is already on this project. New accounts are created by an administrator.</p>}
            </div>
            <div className="addPersonField">
              <label htmlFor="add-person-role">Position</label>
              <select id="add-person-role" value={chosenRole} onChange={event => setChosenRole(event.target.value)}>
                {roleGroups.map(group => (
                  <optgroup key={group.name} label={group.name}>
                    {group.roles.map(role => <option key={role} value={role}>{label(role)}</option>)}
                  </optgroup>
                ))}
              </select>
              {roleGroups.find(group => group.roles.includes(chosenRole))?.hint && (
                <p className="addPersonHint">{roleGroups.find(group => group.roles.includes(chosenRole))?.hint}</p>
              )}
              {singularPositions.concat(disciplineLeads.map(item => item.lead)).includes(chosenRole)
                && positionsByRole.get(chosenRole)?.holder && (
                <p className="addPersonWarning">
                  {label(chosenRole)} is held by {positionsByRole.get(chosenRole)?.holder?.displayName}. End their
                  position before assigning it to somebody else.
                </p>
              )}
            </div>
            <div className="addPersonActions">
              <button type="button" className="primaryAction" disabled={!chosenPerson || busy} onClick={addPerson}>Add to project</button>
              <button type="button" onClick={() => setAddOpen(false)}>Cancel</button>
            </div>
          </div>
        )}
      </main>
    </div>
  )
}
