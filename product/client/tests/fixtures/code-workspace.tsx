import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import CodeWorkspace, { type CodeWorkspacePage } from '../../src/CodeWorkspace'
import ArtifactCodeRelationships from '../../src/ArtifactCodeRelationships'
import { exactTraceArtifactPath } from '../../src/routing'
import '../../src/index.css'

function Fixture() {
  const [page, setPage] = useState<CodeWorkspacePage>('mergeRequests')
  const [targetId, setTargetId] = useState('exact-one')
  if (new URLSearchParams(location.search).get('mode') === 'artifact') return <>
    <button onClick={() => setTargetId('exact-two')}>Open another exact revision</button>
    <ArtifactCodeRelationships api="" projectId="project-one" releaseId="release-one" targetKind="RequirementRevision" targetId={targetId} />
  </>
  return <CodeWorkspace api="" projectId="project-one" releaseId="release-one" readOnly={false}
    page={page} onPage={setPage} onBack={() => {}}
    traceArtifactHref={target => exactTraceArtifactPath({ programId: 'program-one', projectId: 'project-one', releaseId: 'release-one' }, target)} />
}
createRoot(document.getElementById('root')!).render(<Fixture />)
