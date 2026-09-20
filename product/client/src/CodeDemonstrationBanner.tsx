import { useState } from 'react'
import type { CodeSource } from './CodeSourcePanel'
import { useCodeRead } from './codeWorkspaceData'

type Observation = {
  configurationId: string; configurationVersion: number; remoteProjectId: number;
  checkedAt: string; cache: { reused: boolean };
  observation: { succeeded: boolean };
}

/** A bounded connection observation, separate from the register/tree's own refresh state. */
export default function CodeDemonstrationBanner({ api, projectId, binding }: {
  api: string; projectId: string; binding: NonNullable<CodeSource['demonstration']>;
}) {
  const [refresh, setRefresh] = useState(0)
  const probe = useCodeRead<Observation>(`${api}/api/projects/${projectId}/repository/merge-requests?page=1&pageSize=1`, refresh)
  const observed = probe.value
  const matches = observed?.configurationId === binding.configurationId
    && observed.configurationVersion === binding.configurationVersion && observed.remoteProjectId === binding.remoteProjectId
  const succeeded = matches && observed.observation?.succeeded === true && Number.isFinite(Date.parse(observed.checkedAt))
  const state = probe.loading ? 'checking' : succeeded ? (observed.cache?.reused ? 'cached' : 'live') : 'unavailable'
  return <section className="codeDemoBanner" aria-label="Synthetic demonstration">
    <div className="codeDemoBannerHeading"><strong>Synthetic demonstration content{succeeded ? ' · live GitLab metadata' : ''}</strong>
      <span className={`codeStatusBadge codeStatusBadge--${state}`}>{state === 'live' ? 'Live' : state === 'cached' ? 'Checked · cached' : state === 'checking' ? 'Checking' : 'Unavailable'}</span>
    </div>
    <p className="codeDemoBannerStatus" role="status">{probe.loading ? 'GitLab metadata not yet checked.'
      : observed && !matches ? 'Repository configuration changed; refresh the build source before checking again.'
        : succeeded ? <>Connection checked <time dateTime={observed.checkedAt}>{new Date(observed.checkedAt).toLocaleString()}</time>
          {observed.cache?.reused ? ' · cached observation (up to 15 seconds).' : ' · fresh observation.'}</>
          : 'GitLab metadata unavailable.'}</p>
    <div className="codeDemoBannerFooter"><p>This connection check does not refresh the displayed merge requests or directory.</p>
      <button disabled={probe.loading} onClick={() => setRefresh(value => value + 1)}>Check GitLab connection</button></div>
  </section>
}
