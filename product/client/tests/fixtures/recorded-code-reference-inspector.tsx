import { createRoot } from 'react-dom/client'
import ChangeRequestInspector from '../../src/ChangeRequestInspector'
import '../../src/index.css'

createRoot(document.getElementById('root')!).render(
  <ChangeRequestInspector
    api=""
    id="root-change-request"
    kind="ChangeRequest"
    projectId="11111111-1111-4111-8111-111111111111"
    releaseId="22222222-2222-4222-8222-222222222222"
    href="#exact-change-request"
    onClose={() => undefined}
    onOpen={() => undefined}
  />,
)
