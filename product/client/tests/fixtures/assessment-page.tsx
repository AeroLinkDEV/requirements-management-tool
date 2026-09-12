import { createRoot } from 'react-dom/client'
import TestChangeRequestPage from '../../src/TestChangeRequestPage'
import type { TestDiscipline } from '../../src/TestResultsWorkspace'
import '../../src/index.css'

const discipline = new URLSearchParams(location.search).get('discipline') as TestDiscipline || 'System'
createRoot(document.getElementById('root')!).render(<TestChangeRequestPage api="" releaseId="build"
  releases={[{ id: 'build', version: '1.6', isReleased: false }]} packageId="assessment" discipline={discipline}
  currentUser="reader" onBack={() => {}} onOpenRequirementRevision={() => {}} onOpenTestChangeRequest={() => {}} />)
