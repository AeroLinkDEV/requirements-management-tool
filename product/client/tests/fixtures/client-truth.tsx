import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import PersonPicker from '../../src/PersonPicker'
import SoftwareBuildsLanding from '../../src/SoftwareBuildsLanding'
import { decodeWorkspaces } from '../../src/workspaceContext'
import '../../src/index.css'
import '../../src/App.css'

function PickerFixture() {
  const [projectId, setProjectId] = useState('project-a')
  const [api, setApi] = useState('')
  const [authority, setAuthority] = useState('SystemEngineer')
  const [roles, setRoles] = useState(['SystemEngineer'])
  const [mounted, setMounted] = useState(true)
  const [selected, setSelected] = useState({ userId: '', name: '' })
  return <>
    <button onClick={() => setProjectId(value => value === 'project-a' ? 'project-b' : 'project-a')}>Change project</button>
    <button onClick={() => setApi(value => value ? '' : '/alternate')}>Change API</button>
    <button onClick={() => setAuthority(value => value === 'SystemEngineer' ? 'Reviewer' : 'SystemEngineer')}>Change authority</button>
    <button onClick={() => setRoles(value => value[0] === 'SystemEngineer' ? ['Reviewer'] : ['SystemEngineer'])}>Change roles</button>
    <button onClick={() => setMounted(false)}>Unmount picker</button>
    {mounted && <PersonPicker api={api} projectId={projectId} authority={authority} allowedRoles={roles}
      excludeUserNames={['excluded']} value={selected.userId} name="" index={0} onSelect={setSelected} />}
    <output aria-label="Selected reviewer">{selected.userId}</output>
  </>
}

// Raw server-shaped data must cross the production decoder before reaching the real card.
const workspaces = decodeWorkspaces([{
  program: { id: 'program', name: 'Example', code: '' },
  projects: [{ project: { id: 'project', name: 'Example', softwareProduct: 'Example product' }, releases: [
    { id: 'without-readiness', version: '1.0', isReleased: true, releasedWithoutReadiness: true },
    { id: 'with-readiness', version: '1.1', isReleased: true, releasedWithoutReadiness: false },
    { id: 'legacy', version: '1.2', isReleased: true },
  ] }],
}])
const project = workspaces[0].projects[0]

createRoot(document.getElementById('root')!).render(new URLSearchParams(location.search).has('picker')
  ? <PickerFixture />
  : <SoftwareBuildsLanding user={{ id: 'reviewer', userName: 'reviewer', displayName: 'Reviewer', email: '',
    isAdministrator: false, mustChangePassword: false, programs: [] }} releases={project.releases}
    projectName={project.project.name} softwareProduct={project.project.softwareProduct}
    onOpenBuild={() => {}} onProjectOverview={() => {}} onImportedBaselines={() => {}}
    onPersonnel={() => {}} onProjectConfiguration={() => {}} onSignOut={() => {}} />)
