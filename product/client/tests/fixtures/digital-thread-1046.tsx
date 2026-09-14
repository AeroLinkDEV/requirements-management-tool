import { createRoot } from 'react-dom/client'
import { useEffect, useState } from 'react'
import '../../src/index.css'
import DigitalThreadNetwork from '../../src/DigitalThreadNetwork'
import DigitalThreadPage, { type DigitalThreadPageProps } from '../../src/DigitalThreadPage'
import type { NetworkNode, NetworkProjection } from '../../src/changeNetworkPresentation'

// Synthetic server-shaped input. Counts, ordering and named topology model the owner's images;
// this is not an export of HOME and does not assert its other relationships or historical facts.
const nodes: NetworkNode[] = []
const add = (id: string, kind: string, displayNumber: string, level?: string, title = 'FMS 1.6 change package: oceanic navigation and route sequencing', state = 'Draft') => {
  nodes.push({ id, kind, displayNumber, level, title, state, buildVersion: '1.6', revision: 0 })
}
for (const n of [2, 5, 6, 7, 8, 9, 10]) add(`pr-${n}`, 'ProblemReport', `PR-${String(n).padStart(5, '0')}.00`, undefined,
  n === 6 ? 'Flight-plan sequencing mode is lost after a warm restart' : 'Intermittent position-source disagreement during route capture', 'Open')
const systems = [31, 32, 33, 34, 35, 36, 41, ...Array.from({ length: 39 }, (_, i) => 53 + i)]
for (const n of systems) add(`sys-${n}`, 'ChangeRequest', `SRCR-${String(n).padStart(5, '0')}.00`, 'System',
  n === 33 ? '[EXPLORATORY QA] Detect prolonged disagreement between independent position sources' : 'Introduce round-robin routing capability for oceanic flight plans', n % 3 ? 'Draft' : 'Approved')
const hlrs = [76, 77, 82, 87, 103, 120, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, ...Array.from({ length: 26 }, (_, i) => 135 + i)]
for (const n of hlrs) add(`hlr-${n}`, 'ChangeRequest', `HLRCR-${String(n).padStart(5, '0')}.00`, 'HighLevel',
  n === 122 ? 'prolonged disagreement' : 'Implement bounded route sequencing and independent position-source integrity checks')
for (let i = 0; i < 40; i++) add(`llr-${i}`, 'ChangeRequest', `LLRCR-${String(78 + i).padStart(5, '0')}.00`, 'LowLevel')
for (const n of [1, 2, 34, 35, 36, 46, 47, 48, 49, 50]) add(`case-${n}`, 'TestChangeRequest', `HLRTCCR-${String(n).padStart(6, '0')}.00`, 'Case', '')
for (const n of [1, 2, 3, 4, 5, 13, 37, 38, 39]) add(`proc-${n}`, 'TestChangeRequest', `SYSTPCR-${String(n).padStart(6, '0')}.00`, 'Procedure', '', 'Approved')
for (const node of nodes.filter(n => n.kind === 'TestChangeRequest')) node.verification = {
  hasControlledNumber: true, controlledNumber: node.displayNumber.slice(0, -3), controlledRevision: 0,
  outcome: 'ChangeRequired', artifactKind: node.level!, discipline: node.level === 'Procedure' ? 'System' : 'Software',
  originKind: 'ChangeRequest', originReferenceId: node.id === 'proc-4' ? 'sys-33' : 'synthetic-other-source',
  sourceDisplayNumber: node.id === 'proc-4' ? 'SRCR-00033.00' : 'HLRCR-00128.00',
}
const edge = (fromId: string, toId: string, relation: string) => ({ fromId, toId, relation,
  fromKind: nodes.find(n => n.id === fromId)!.kind, toKind: nodes.find(n => n.id === toId)!.kind,
  provenance: [], isSuspect: false })
const projection: NetworkProjection = {
  projectId: 'synthetic-1046-project', releaseId: 'synthetic-1046-build', nodes,
  orderedLevels: ['System', 'HighLevel', 'LowLevel'], truncated: false,
  edges: [edge('pr-6', 'hlr-128', 'ProblemReportResolution'), edge('pr-6', 'hlr-134', 'ProblemReportResolution'),
    edge('pr-6', 'case-35', 'ProblemReportResolution'), edge('pr-6', 'case-36', 'ProblemReportResolution'),
    edge('sys-33', 'hlr-122', 'Upstream'), edge('sys-33', 'proc-4', 'CoveredByTestChangeRequest')],
}
// The real Page owns routed selections and supplies scopeKey. A standalone Network is an
// intentional diagnostic control, not equivalent coverage of that caller lifecycle.
const nativeFetch = window.fetch.bind(window)
window.fetch = async (input, init) => {
  const url = String(input)
  if (!url.startsWith('/fixture-api/')) return nativeFetch(input, init)
  const body = url.includes('/change-requests/network?') ? projection
    : url.includes('/build-context?') ? { effectiveBaselineId: null, inheritedBaseline: false }
    : url.includes('/baselines?') ? [] : undefined
  return body === undefined ? new Response('Unexpected fixture read', { status: 500 }) : Response.json(body)
}
function PageHarness() {
  const readRoute = (): Parameters<DigitalThreadPageProps['onRoute']>[0] => {
    const focalId = new URLSearchParams(location.search).get('focal') ?? undefined
    return { view: 'network', focalId, focalKind: focalId ? 'change-request' : undefined }
  }
  const [route, setRoute] = useState(readRoute)
  useEffect(() => {
    const pop = () => setRoute(readRoute())
    window.addEventListener('popstate', pop)
    return () => window.removeEventListener('popstate', pop)
  }, [])
  return <DigitalThreadPage api="/fixture-api" projectId={projection.projectId} releaseId={projection.releaseId}
    buildLabel="Build 1.6" {...route} onRoute={next => {
      (window as any).__1046?.push({ kind: 'route', t: performance.now(), previous: route, next })
      const url = new URL(location.href)
      if (next.focalId) url.searchParams.set('focal', next.focalId)
      else url.searchParams.delete('focal')
      history.pushState(null, '', url)
      setRoute(next)
    }} traceArtifactHref={node => `#exact-${node.id}`} />
}
createRoot(document.getElementById('root')!).render(<>
  <header style={{ height: 150, padding: 20 }}>Synthetic #1046 reproduction — FMS-shaped, not HOME data</header>
  <main style={{ marginLeft: 280, height: 'calc(100vh - 180px)', minHeight: 0 }}>
    {new URLSearchParams(location.search).has('page') ? <PageHarness /> : <DigitalThreadNetwork projection={projection} buildLabel="Build 1.6" hrefFor={node => `#exact-${node.id}`} onOpenChange={() => undefined} />}
  </main>
</>)
