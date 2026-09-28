import { createRoot } from 'react-dom/client'
import ReviewWorkflowCenter from '../../src/ReviewWorkflowCenter'
import '../../src/index.css'
import '../../src/App.css'

createRoot(document.getElementById('root')!).render(
  <div className="workspaceView">
    <ReviewWorkflowCenter api="" projectId="fixture-project" onBack={() => {}} />
  </div>,
)
