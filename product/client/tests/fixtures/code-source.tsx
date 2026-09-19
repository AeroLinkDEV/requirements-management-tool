import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import CodeSourcePanel, { type CodeSource } from '../../src/CodeSourcePanel'
import '../../src/index.css'

function Fixture() {
  const [releaseId, setReleaseId] = useState('release-a')
  const [source, setSource] = useState<CodeSource>()
  return <><button onClick={() => setReleaseId('release-b')}>Switch build</button>
    <output aria-label="Parent source">{source?.releaseId ?? 'No parent source'}</output>
    <CodeSourcePanel api="" projectId="project-one" releaseId={releaseId} readOnly={false} onSource={value => setSource(value)} /></>
}
createRoot(document.getElementById('root')!).render(<Fixture />)
