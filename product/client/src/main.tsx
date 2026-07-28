import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import './DesignSystem.css'
import './Density.css'
import WorkspaceErrorBoundary from './WorkspaceErrorBoundary.tsx'
import { installProtectedFetch } from './protectedFetch.ts'

installProtectedFetch()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <WorkspaceErrorBoundary>
      <App />
    </WorkspaceErrorBoundary>
  </StrictMode>,
)
