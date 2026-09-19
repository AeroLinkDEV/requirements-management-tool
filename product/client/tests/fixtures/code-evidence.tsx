import { createRoot } from 'react-dom/client'
import CodeTraceabilityCenter from '../../src/CodeTraceabilityCenter'
import '../../src/index.css'

createRoot(document.getElementById('root')!).render(<CodeTraceabilityCenter api="" projectId="project-one"
  releaseId="release-one" readOnly={true} onBack={() => {}} embedded />)
