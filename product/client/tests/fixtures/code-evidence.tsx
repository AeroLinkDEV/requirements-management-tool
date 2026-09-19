import { createRoot } from 'react-dom/client'
import CodeTraceabilityCenter from '../../src/CodeTraceabilityCenter'
import '../../src/index.css'

createRoot(document.getElementById('root')!).render(<CodeTraceabilityCenter api="" projectId="project-one"
  releaseId="release-one" readOnly={!new URLSearchParams(location.search).has('editable')} onBack={() => {}} embedded />)
