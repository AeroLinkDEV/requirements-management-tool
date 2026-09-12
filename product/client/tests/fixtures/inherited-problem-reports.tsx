import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import InheritedProblemReports, { type ProblemReportSource } from '../../src/InheritedProblemReports'
import '../../src/ProblemReportPicker.css'
import '../../src/index.css'

function Fixture() {
  const [selected, setSelected] = useState(['manual'])
  const [sources, setSources] = useState<ProblemReportSource[]>([
    { id: 'source-a', kind: 'ChangeRequest', displayNumber: 'SRCR-00001.02' },
    { id: 'source-b', kind: 'ChangeRequest', displayNumber: 'SRCR-00002.01' },
    { id: 'case-source', kind: 'TestChangeRequest', displayNumber: 'HLRTCCR-00003.00' },
  ])
  return <main><h1>Upstream Problem Reports</h1>
    <button onClick={() => setSources(current => current.filter(source => source.id !== 'source-a'))}>Remove first source</button>
    <button onClick={() => setSelected(current => [...new Set([...current, 'manual-2'])])}>Select another manual PR</button>
    <output aria-label="Direct selections">{selected.join(',')}</output>
    <InheritedProblemReports api="" projectId="project" releaseId="build" sources={sources}
      selected={selected} onChange={setSelected} />
  </main>
}
createRoot(document.getElementById('root')!).render(<Fixture />)
